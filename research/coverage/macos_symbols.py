"""Reads the symbol table of the macOS build of the game and writes it out as plain text.

    python research/coverage/macos_symbols.py                  summary + write the list
    python research/coverage/macos_symbols.py --grep unsheath   mangled names containing a text

The macOS executable (se-fork/reference/macos-game/Baldur's Gate 3.app/Contents/MacOS/...,
fetched from the user's own Steam library) is a Mach-O file that keeps its symbol table:
the mangled C++ names of functions and globals, with their addresses. This script needs no
macOS tools: it parses the file itself (the universal header, the arm64 slice, LC_SYMTAB).

Output: se-fork/reference/macos-symbols/symbols.tsv - address, type letter as `nm` prints it
(T/t code, D/d data, B/b zero-filled data, S/s other sections, U undefined; capital =
external), mangled name. Demangling is a separate step (see demangle()).
"""
import struct
import sys
from collections import Counter
from pathlib import Path

HERE = Path(__file__).resolve().parent
REFERENCE = HERE.parent / "reference"
BINARY = REFERENCE / "macos-game" / "Baldur's Gate 3.app" / "Contents" / "MacOS" / "Baldur's Gate 3"
OUT = REFERENCE / "macos-symbols"

FAT_MAGIC, FAT_MAGIC_64 = 0xCAFEBABE, 0xCAFEBABF
MH_MAGIC_64 = 0xFEEDFACF
CPU_ARM64, CPU_X86_64 = 0x0100000C, 0x01000007
LC_SYMTAB, LC_SEGMENT_64 = 0x2, 0x19
N_STAB, N_TYPE, N_EXT = 0xE0, 0x0E, 0x01
N_UNDF, N_ABS, N_SECT = 0x0, 0x2, 0xE


def slices(data) -> dict[int, tuple[int, int]]:
    """{cpu type: (offset, size)} of a universal file; a thin file is one slice at 0."""
    magic = struct.unpack_from(">I", data, 0)[0]
    if magic in (FAT_MAGIC, FAT_MAGIC_64):
        count = struct.unpack_from(">I", data, 4)[0]
        found = {}
        for index in range(count):
            if magic == FAT_MAGIC:
                cpu, _, offset, size, _ = struct.unpack_from(">IIIII", data, 8 + 20 * index)
            else:
                cpu, _, offset, size, _, _ = struct.unpack_from(">IIQQII", data, 8 + 32 * index)
            found[cpu] = (offset, size)
        return found
    cpu = struct.unpack_from("<I", data, 4)[0]
    return {cpu: (0, len(data))}


def read_symbols(path: Path = BINARY, cpu: int = CPU_ARM64):
    """Yields (address, type letter, mangled name) of one architecture's symbol table."""
    import mmap
    with open(path, "rb") as handle:
        data = mmap.mmap(handle.fileno(), 0, access=mmap.ACCESS_READ)
        available = slices(data)
        if cpu not in available:
            raise SystemExit(f"no slice for cpu 0x{cpu:X}; the file has {[hex(c) for c in available]}")
        base, _ = available[cpu]
        magic, _, _, _, ncmds, _, _, _ = struct.unpack_from("<IiiIIIII", data, base)
        if magic != MH_MAGIC_64:
            raise SystemExit("not a 64-bit Mach-O slice")
        sections = []  # (segment name, section name) by 1-based index
        symtab = None
        at = base + 32
        for _ in range(ncmds):
            command, size = struct.unpack_from("<II", data, at)
            if command == LC_SEGMENT_64:
                segment = data[at + 8:at + 24].rstrip(b"\0").decode()
                nsects = struct.unpack_from("<I", data, at + 64)[0]
                for index in range(nsects):
                    name = data[at + 72 + 80 * index:at + 88 + 80 * index].rstrip(b"\0").decode()
                    sections.append((segment, name))
            elif command == LC_SYMTAB:
                symtab = struct.unpack_from("<IIII", data, at + 8)
            at += size
        if symtab is None:
            raise SystemExit("the file has no symbol table")
        symoff, nsyms, stroff, strsize = symtab
        strings = base + stroff
        for index in range(nsyms):
            strx, kind, sect, _, value = struct.unpack_from("<IBBHQ", data, base + symoff + 16 * index)
            if kind & N_STAB:
                continue  # debugger entries
            end = data.find(b"\0", strings + strx, strings + strsize)
            name = data[strings + strx:end].decode("utf-8", "replace")
            what = kind & N_TYPE
            if what == N_UNDF:
                letter = "U"
            elif what == N_ABS:
                letter = "A"
            elif what == N_SECT and 0 < sect <= len(sections):
                segment, section = sections[sect - 1]
                letter = "T" if section == "__text" else "B" if section in ("__bss", "__common") else \
                    "D" if segment.startswith("__DATA") else "S"
            else:
                letter = "?"
            yield value, letter if kind & N_EXT else letter.lower(), name


def main() -> int:
    if not BINARY.exists():
        raise SystemExit(f"not there: {BINARY}")
    if "--grep" in sys.argv:
        wanted = sys.argv[sys.argv.index("--grep") + 1]
        for address, letter, name in read_symbols():
            if wanted in name:
                print(f"{address:016X} {letter} {name}")
        return 0
    OUT.mkdir(parents=True, exist_ok=True)
    kinds, total, cxx = Counter(), 0, 0
    with open(OUT / "symbols.tsv", "w", encoding="utf-8", newline="\n") as out:
        for address, letter, name in read_symbols():
            out.write(f"{address:016X}\t{letter}\t{name}\n")
            kinds[letter] += 1
            total += 1
            cxx += name.startswith("__Z")
    print(f"{total} symbols, {cxx} of them C++ (mangled); by type: {dict(kinds.most_common())}")
    print(f"written to {OUT / 'symbols.tsv'}")
    return 0


if __name__ == "__main__":
    sys.exit(main())


def demangle(names: list[str]) -> list[str]:
    """Readable C++ names for mangled ones, through the GNU demangler that ships with Ghidra
    (GPL/DemanglerGnu); one name per line in, one per line out."""
    import os
    import subprocess
    tools = Path(os.environ.get("BG3_DECOMP_TOOLS") or Path(os.environ["LOCALAPPDATA"]) / "GateKit" / "tools")
    found = sorted(tools.glob("ghidra_*/GPL/DemanglerGnu/os/win_x86_64/demangler_gnu_v2_41.exe"))
    if not found:
        raise SystemExit(f"no Ghidra demangler under {tools}")
    # Mach-O prefixes every C symbol with one more underscore than the mangling has.
    text = "\n".join(name[1:] if name.startswith("__Z") else name for name in names) + "\n"
    out = subprocess.run([str(found[-1])], input=text.encode(), capture_output=True).stdout.decode("utf-8", "replace")
    lines = out.splitlines()
    return lines if len(lines) == len(names) else names


def write_demangled() -> None:
    """symbols-demangled.tsv: address, type, readable name - for the C++ symbols only."""
    rows = [(address, letter, name) for address, letter, name in read_symbols() if name.startswith("__Z")]
    readable = demangle([name for _, _, name in rows])
    with open(OUT / "symbols-demangled.tsv", "w", encoding="utf-8", newline="\n") as out:
        for (address, letter, _), name in zip(rows, readable):
            out.write(f"{address:016X}\t{letter}\t{name}\n")
    print(f"{len(rows)} C++ names demangled into {OUT / 'symbols-demangled.tsv'}")


if __name__ == "__main__" and "--demangle" in sys.argv:
    write_demangled()
