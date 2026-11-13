"""What a freshly created component holds, read from the macOS build's own code.

    <venv python> research/coverage/macos_defaults.py show <engine component name>    disassembly + summary
    <venv python> research/coverage/macos_defaults.py build                           every component -> defaults.json

(needs capstone: %LOCALAPPDATA%\\GateKit\\venv\\Scripts\\python.exe)

The macOS executable names, for every one of the 2647 component types, the function
`ecs::ComponentOps<T>::DefaultConstructComponents(...)`: it fills `count` new components with
their default values. Its machine code therefore shows, without running the game:

  - whether the type holds anything at all: for an empty struct (a tag) the function stores
    nothing; for a component with data it stores its defaults (or zero-fills it);
  - the stride it walks the buffer with - the size of the type on ARM64;
  - which offsets get which constants: field boundaries and default values.

This is ARM64 code of the same game version as the Windows build (4.1.1.7631656); the layout
of plain data components is the same on both, pointers and all being 8 bytes.

Output of `build`: se-fork/reference/macos-symbols/defaults.json (derived from the game, not
committed): { component: { address, size, instructions, stores: [...], calls, verdict } }.
"""
import json
import mmap
import re
import struct
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import macos_symbols  # noqa: E402

OUT = macos_symbols.OUT / "defaults.json"
OPS = re.compile(r"^ecs::ComponentOps<(.+)>::DefaultConstructComponents\(")


class Image:
    """The arm64 slice of the executable: bytes by virtual address, and the symbols around one."""

    def __init__(self, path: Path = macos_symbols.BINARY):
        self.handle = open(path, "rb")
        self.data = mmap.mmap(self.handle.fileno(), 0, access=mmap.ACCESS_READ)
        self.base, _ = macos_symbols.slices(self.data)[macos_symbols.CPU_ARM64]
        self.segments = []
        _, _, _, _, ncmds, _, _, _ = struct.unpack_from("<IiiIIIII", self.data, self.base)
        at = self.base + 32
        for _ in range(ncmds):
            command, size = struct.unpack_from("<II", self.data, at)
            if command == macos_symbols.LC_SEGMENT_64:
                vmaddr, vmsize, fileoff, filesize = struct.unpack_from("<QQQQ", self.data, at + 24)
                self.segments.append((vmaddr, vmsize, fileoff, filesize))
            at += size

    def read(self, address: int, size: int) -> bytes:
        for vmaddr, vmsize, fileoff, filesize in self.segments:
            if vmaddr <= address < vmaddr + filesize:
                start = self.base + fileoff + (address - vmaddr)
                return self.data[start:start + size]
        return b""


def functions() -> tuple[dict[str, int], list[int]]:
    """({component: address of its DefaultConstructComponents}, sorted addresses of all code symbols)."""
    wanted, starts = {}, set()
    for line in open(macos_symbols.OUT / "symbols-demangled.tsv", encoding="utf-8"):
        address, kind, name = line.rstrip("\n").split("\t", 2)
        if kind in "tT":
            starts.add(int(address, 16))
            match = OPS.match(name)
            if match:
                wanted[match.group(1)] = int(address, 16)
    for line in open(macos_symbols.OUT / "symbols.tsv", encoding="utf-8"):  # C functions too
        address, kind, _ = line.split("\t", 2)
        if kind in "tT":
            starts.add(int(address, 16))
    return wanted, sorted(starts)


def disassemble(image: Image, address: int, limit: int):
    import capstone
    engine = capstone.Cs(capstone.CS_ARCH_ARM64, capstone.CS_MODE_ARM)
    engine.detail = False
    return list(engine.disasm(image.read(address, limit), address))


STORE = re.compile(r"^(str|strb|strh|stp|stur|sturb|sturh)$")


# Every DefaultConstructComponents starts with the same storage bookkeeping (about 200
# instructions that find the page and the slot; it stores a counter and a pair of pointers).
# What it writes through the pointer to the new component comes after that and is what we want.
BOOKKEEPING = (re.compile(r"^str w\d+, \[x\d+, #0x14\]$"), re.compile(r"^stp x\d+, x\d+, \[x8\]$"))


def summarise(instructions) -> dict:
    """What the function writes into the new component: its stores (after the bookkeeping),
    the calls it makes, and a verdict - "empty" (an empty struct: a tag), "stores" (writes
    defaults itself) or "calls" (hands the memory to a constructor or memset)."""
    stores, calls = [], []
    for ins in instructions:
        if STORE.match(ins.mnemonic):
            text = f"{ins.mnemonic} {ins.op_str}"
            if "[sp" in text or "x29" in text or any(rule.match(text) for rule in BOOKKEEPING):
                continue
            stores.append(text)
        elif ins.mnemonic == "bl":
            calls.append(ins.op_str.lstrip("#"))
        elif ins.mnemonic == "dc":  # dc zva: zero a cache block
            stores.append(f"{ins.mnemonic} {ins.op_str}")
    return {"instructions": len(instructions), "stores": stores[:32], "calls": calls[:12], "ncalls": len(calls),
            "verdict": "stores" if stores else "undecided"}


def build() -> dict:
    image = Image()
    wanted, starts = functions()
    import bisect
    out = {}
    for component, address in wanted.items():
        nxt = starts[bisect.bisect_right(starts, address)] if bisect.bisect_right(starts, address) < len(starts) else address + 64
        size = min(nxt - address, 2048)
        info = summarise(disassemble(image, address, size))
        info.update(address=f"{address:X}", size=nxt - address)
        out[component] = info
    # A function with no store of its own is "empty" when it also calls nothing beyond what the
    # bookkeeping always calls; the baseline is the smallest call count seen (a pure tag).
    baseline = min(info["ncalls"] for info in out.values())
    for info in out.values():
        if info["verdict"] == "undecided":
            info["verdict"] = "empty" if info["ncalls"] <= baseline else "calls"
    OUT.write_text(json.dumps(out), encoding="utf-8")
    return out


def main() -> int:
    if len(sys.argv) >= 3 and sys.argv[1] == "show":
        image = Image()
        wanted, starts = functions()
        import bisect
        address = wanted[sys.argv[2]]
        nxt = starts[bisect.bisect_right(starts, address)]
        instructions = disassemble(image, address, min(nxt - address, 2048))
        for ins in instructions:
            print(f"  {ins.address:X}  {ins.mnemonic:8} {ins.op_str}")
        print(summarise(instructions))
        return 0
    if len(sys.argv) >= 2 and sys.argv[1] == "build":
        out = build()
        from collections import Counter
        print(f"{len(out)} components; verdicts: {dict(Counter(v['verdict'] for v in out.values()))}")
        return 0
    print(__doc__)
    return 1


if __name__ == "__main__":
    sys.exit(main())
