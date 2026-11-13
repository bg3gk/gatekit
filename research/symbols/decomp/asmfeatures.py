#!/usr/bin/env python3
"""Features of functions read straight from machine code, for pairing two builds of the same
source without decompiling either (see codematch.py for the pairing itself).

A function's features are the constants its instructions carry: immediate operands and the
displacements of memory operands that are not relative to the stack or the instruction pointer
(those are field offsets), plus the text constants it refers to. Two compilers order and select
instructions differently, but both must put the same offsets, masks, sizes and strings in.

Works on the x86-64 Windows executable (functions from symbols.sqlite) and on an x86-64 Mach-O
slice of the macOS build (functions from its symbol table). Needs capstone.
"""
import array
import bisect
import hashlib
import mmap
import multiprocessing
import struct

MASK = (1 << 63) - 1
STACK = None            # register ids, set on first use (capstone is imported lazily)


def text_hash(raw):
    return int.from_bytes(hashlib.blake2b(raw, digest_size=8).digest(), "little") & MASK


def number_hash(value):
    return (value * 0x9E3779B97F4A7C15 + 1) & MASK


def canonical(value):
    """The same constant whatever width the compiler printed it in; None for 0, 1 and -1."""
    value &= (1 << 64) - 1
    if value >= 1 << 63:
        value = (1 << 64) - value
    elif 1 << 31 <= value < 1 << 32:
        value = (1 << 32) - value
    return value if value >= 2 else None


class Image:
    """Bytes of a loaded image by address, and its read-only text constants."""

    def __init__(self, path, segments, base):
        self.file = open(path, "rb")
        self.data = mmap.mmap(self.file.fileno(), 0, access=mmap.ACCESS_READ)
        self.segments = sorted(segments)             # (address, size, file offset)
        self.starts = [s[0] for s in self.segments]
        self.base = base
        self.low = self.segments[0][0]
        self.high = max(a + n for a, n, o in self.segments)

    def offset(self, address):
        i = bisect.bisect_right(self.starts, address) - 1
        if i < 0:
            return None
        a, n, o = self.segments[i]
        return o + address - a if address < a + n else None

    def read(self, address, size):
        o = self.offset(address)
        return self.data[o:o + size] if o is not None else b""

    def text_at(self, address):
        o = self.offset(address)
        if o is None:
            return None
        end = self.data.find(b"\0", o, o + 400)
        raw = self.data[o:end] if end > o else b""
        return raw if len(raw) >= 4 and all(32 <= c < 127 for c in raw) else None


def open_pe(path):
    d = open(path, "rb").read(4096)
    pe = struct.unpack_from("<I", d, 0x3C)[0]
    nsec = struct.unpack_from("<H", d, pe + 6)[0]
    opt = struct.unpack_from("<H", d, pe + 20)[0]
    base = struct.unpack_from("<Q", d, pe + 24 + 24)[0]
    segments = []
    for i in range(nsec):
        s = pe + 24 + opt + 40 * i
        vsize, va, rsize, raw = struct.unpack_from("<IIII", d, s + 8)
        segments.append((base + va, min(vsize, rsize) or rsize, raw))
    return Image(path, segments, base)


def open_macho(path):
    """Image of a thin x86-64 Mach-O file and its functions: [(address, size, mangled name)]."""
    f = open(path, "rb")
    d = mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ)
    ncmds = struct.unpack_from("<I", d, 16)[0]
    o, segments, text_sections, symtab, sect_index = 32, [], [], None, 0
    text = None
    for _ in range(ncmds):
        cmd, size = struct.unpack_from("<II", d, o)
        if cmd == 0x19:
            vm, vmsize, fileoff, filesize = struct.unpack_from("<QQQQ", d, o + 24)
            if filesize:
                segments.append((vm, filesize, fileoff))
            for k in range(struct.unpack_from("<I", d, o + 64)[0]):
                sect_index += 1
                so = o + 72 + 80 * k
                if d[so:so + 16].rstrip(b"\0") == b"__text":
                    address, length = struct.unpack_from("<QQ", d, so + 32)
                    text = (sect_index, address, length)
        elif cmd == 0x2:
            symtab = struct.unpack_from("<IIII", d, o + 8)
        o += size
    image = Image(path, segments, 0x100000000)
    symoff, nsyms, stroff, _ = symtab
    names, data_names = {}, {}
    for i in range(nsyms):
        strx, ntype, nsect, ndesc, value = struct.unpack_from("<IBBHQ", d, symoff + 16 * i)
        if ntype & 0xe0 or (ntype & 0x0e) != 0x0e:
            continue
        end = d.find(b"\0", stroff + strx)
        name = d[stroff + strx:end].decode("latin-1")
        if nsect != text[0]:
            data_names[value] = name          # a named global: variable, constant, table
            continue
        if value not in names or len(name) < len(names[value]):
            names[value] = name
    image.data_names = data_names
    ordered = sorted(names)
    limit = text[1] + text[2]
    functions = [(a, (ordered[i + 1] if i + 1 < len(ordered) else limit) - a, names[a]) for i, a in enumerate(ordered)]
    return image, functions


def features(md, image, parts, strings=None, facts=None):
    """Sorted feature hashes of one function given as [(address, size)] parts. `strings` maps an
    address of the image to a text when the caller knows the texts already; otherwise a
    rip-relative reference into the image is read as a text."""
    from capstone import x86_const as x
    global STACK
    if STACK is None:
        STACK = {x.X86_REG_RSP, x.X86_REG_RBP, x.X86_REG_ESP, x.X86_REG_EBP, x.X86_REG_RIP}
    out = set()
    calls, texts, refs = [], [], []
    inside = [(a, a + n) for a, n in parts]
    for address, size in parts:
        code = image.read(address, min(size, 1 << 20))
        for ins in md.disasm(code, address):
            if ins.id == 0:
                continue
            for op in ins.operands:
                if op.type == x.X86_OP_IMM:
                    value = op.imm & ((1 << 64) - 1)
                    if image.low <= value < image.high:
                        # an address of the image: a call or jump out of the function is kept as a call
                        if (ins.id == x.X86_INS_CALL or ins.id == x.X86_INS_JMP) and not any(a <= value < b for a, b in inside):
                            calls.append(value)
                        continue
                    value = canonical(value)
                    if value is not None:
                        out.add(number_hash(value))
                elif op.type == x.X86_OP_MEM:
                    if op.mem.base == x.X86_REG_RIP:
                        target = ins.address + ins.size + op.mem.disp
                        if len(refs) < 600:
                            refs.append(target)
                        raw = strings.get(target) if strings is not None else image.text_at(target)
                        if raw:
                            out.add(text_hash(raw))
                            texts.append(bytes(raw))
                    elif op.mem.base not in STACK and op.mem.disp:
                        value = canonical(op.mem.disp)
                        if value is not None and not image.low <= op.mem.disp < image.high:
                            out.add(number_hash(value))
    if facts is not None:
        facts.append((calls, texts, refs))
    return array.array("q", sorted(out))


_state = {}


def _init(kind, path):
    from capstone import Cs, CS_ARCH_X86, CS_MODE_64
    md = Cs(CS_ARCH_X86, CS_MODE_64)
    md.detail = True
    md.skipdata = True
    _state["md"] = md
    _state["image"] = open_pe(path) if kind == "pe" else open_macho(path)[0]


def _work(batch):
    md, image = _state["md"], _state["image"]
    out = []
    for key, parts in batch:
        facts = []
        f = features(md, image, parts, facts=facts)
        out.append((key, f.tobytes(), facts[0][0], facts[0][1], facts[0][2]))
    return out


def extract(kind, path, functions, jobs, log=lambda s: None, minimum=4):
    """{key: (feature array, [call target address], [text constant])} for functions given as
    [(key, [(address, size), ...])]."""
    batches = [functions[i:i + 2000] for i in range(0, len(functions), 2000)]
    out = {}
    with multiprocessing.Pool(jobs, initializer=_init, initargs=(kind, path)) as pool:
        for n, rows in enumerate(pool.imap_unordered(_work, batches), 1):
            for key, raw, calls, texts, refs in rows:
                a = array.array("q")
                a.frombytes(raw)
                out[key] = (a, calls, texts, refs)
            if n % 25 == 0:
                log(f"  {n}/{len(batches)} batches")
    return out
