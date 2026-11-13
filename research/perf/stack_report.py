"""Names and tables for what research/perf/stack_sampler.py recorded.

    python research/perf/stack_report.py stacks.json [--run run.json --state client:LoadModule | --from S --to S]
                                [--top 40] [--thread TID] [--wall] [--tree NAME] [--json out.json]

Addresses inside the game's executable are named from the symbols database (symbols/symbols.sqlite):
the Toolkit function a game function was matched with, or - where there is no match - the
function names the game keeps as text for its own log lines and that the function refers to
(shown in quotes: the function mentions that name, it need not be that function). Everything
else is `module+offset`.

Weights are processor seconds: every stack counts for the processor time its thread used since
the previous look. --wall counts looks instead (only those where every thread was walked), which
shows what a thread waits on. --state takes the window from a run recorded by
startup_profile.py (side:State, the first time that state was entered); --from/--to are seconds
after process creation.

Tables: threads; functions by inclusive time (anywhere on the stack); functions by own time
(top of the stack); categories (rules in CATEGORIES, first rule that matches any frame from the
outermost inwards). --tree NAME prints who calls and what is called by functions matching NAME.
"""
from __future__ import annotations

import argparse
import bisect
import collections
import json
import os
import re
import sqlite3
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]
DATABASE = Path(os.environ.get("BG3_SYMBOLS_DB", REPO / "symbols" / "symbols.sqlite"))

# First rule whose pattern is found in a stack's names (outermost frame first) names the category.
CATEGORIES: list[tuple[str, str]] = []


class Names:
    """Function starts and names for one executable of the game."""

    def __init__(self, dll: str) -> None:
        self.starts: list[int] = []
        self.names: dict[int, str] = {}
        if not DATABASE.exists():
            return
        db = sqlite3.connect(f"file:{DATABASE}?mode=ro", uri=True)
        row = db.execute("SELECT id FROM dlls WHERE name = ?", (dll,)).fetchone()
        if not row:
            return
        dll_id = row[0]
        self.starts = [r[0] for r in db.execute("SELECT rva FROM functions WHERE dll_id = ? ORDER BY rva", (dll_id,))]
        hints: dict[int, list[str]] = collections.defaultdict(list)
        qualified = re.compile(r"^[A-Za-z_]\w*(::[~\w<>]+)+$")
        for rva, text in db.execute("SELECT r.func_rva, s.text FROM string_refs r JOIN strings s ON s.id = r.string_id "
                                    "WHERE s.dll_id = ? AND s.text LIKE '%::%' AND length(s.text) < 120", (dll_id,)):
            if qualified.match(text) and len(hints[rva]) < 3:
                hints[rva].append(text)
        for rva, texts in hints.items():
            self.names[rva] = '"' + " | ".join(texts) + '"'
        for rva, name, relation in db.execute(
                "SELECT m.game_rva, s.full_name, m.relation FROM matches m JOIN symbols s ON s.dll_id = m.toolkit_dll "
                "AND s.rva = m.toolkit_rva WHERE m.game_dll = ? ORDER BY m.evidence", (dll_id,)):
            self.names[rva] = name if relation in (None, "same") else f"{name} [{relation}]"
        db.close()

    def function(self, rva: int) -> int:
        index = bisect.bisect_right(self.starts, rva) - 1
        return self.starts[index] if index >= 0 else rva


def read_exports(path: str) -> tuple[list[int], list[str]]:
    """Exported names of a DLL, sorted by address: the nearest one below an address is a fair
    guess at the function for system libraries (which export most of what matters here)."""
    import struct
    try:
        data = Path(path).read_bytes()
        pe = struct.unpack_from("<I", data, 0x3C)[0]
        sections_count = struct.unpack_from("<H", data, pe + 6)[0]
        optional = pe + 24
        optional_size = struct.unpack_from("<H", data, pe + 20)[0]
        export_rva = struct.unpack_from("<I", data, optional + 112)[0]
        sections = [struct.unpack_from("<IIII", data, optional + optional_size + 40 * i + 8) for i in range(sections_count)]

        def offset(rva: int) -> int:
            for size, address, raw_size, raw in sections:
                if address <= rva < address + max(size, raw_size):
                    return raw + rva - address
            raise ValueError
        if not export_rva:
            return [], []
        base = offset(export_rva)
        count_functions, count_names, functions, names, ordinals = struct.unpack_from("<IIIII", data, base + 20)
        found = []
        for index in range(count_names):
            name_at = offset(struct.unpack_from("<I", data, offset(names) + 4 * index)[0])
            ordinal = struct.unpack_from("<H", data, offset(ordinals) + 2 * index)[0]
            rva = struct.unpack_from("<I", data, offset(functions) + 4 * ordinal)[0]
            found.append((rva, data[name_at:data.index(b"\0", name_at)].decode("ascii", "replace")))
        found.sort()
        return [rva for rva, _ in found], [name for _, name in found]
    except (OSError, ValueError, IndexError, __import__("struct").error):
        return [], []


class Symbolizer:
    def __init__(self, modules: list[dict]) -> None:
        self.modules = sorted(modules, key=lambda m: m["base"])
        self.bases = [m["base"] for m in self.modules]
        self.tables: dict[str, Names] = {}
        self.cache: dict[int, str] = {}
        self.exports: dict[str, tuple[list[int], list[str]]] = {}

    def name(self, address: int) -> str:
        known = self.cache.get(address)
        if known:
            return known
        index = bisect.bisect_right(self.bases, address) - 1
        text = hex(address)
        if index >= 0 and address < self.modules[index]["base"] + self.modules[index]["size"]:
            module = self.modules[index]
            file = module["path"].rsplit("\\", 1)[-1]
            rva = address - module["base"]
            if file.lower() in ("bg3_dx11.exe", "bg3.exe"):
                table = self.tables.get(file.lower())
                if table is None:
                    table = self.tables[file.lower()] = Names(file.lower())
                start = table.function(rva)
                text = f"{table.names.get(start, '')} <{file[:-4]}+0x{start:X}>".strip()
            else:
                exports = self.exports.get(module["path"])
                if exports is None:
                    exports = self.exports[module["path"]] = read_exports(module["path"])
                at = bisect.bisect_right(exports[0], rva) - 1
                text = f"{file}!{exports[1][at]}" if at >= 0 and rva - exports[0][at] < 0x4000 else file
        self.cache[address] = text
        return text


def window_of(arguments) -> tuple[float, float]:
    if arguments.state:
        run = json.loads(Path(arguments.run).read_text(encoding="utf-8"))
        side, state = arguments.state.split(":")
        for row in run["analysis"]["stages"][side]:
            if row["state"] == state and "seconds" in row:
                return row["at"], row["at"] + row["seconds"]
        sys.exit(f"the run has no finished state {arguments.state}")
    return arguments.start, arguments.end


def category_of(names: list[str]) -> str:
    # Rules whose category starts with "*" win wherever on the stack they match (what the time
    # is finally spent in); the others name the outermost frame that matches (whose work it is).
    for pattern, category in CATEGORIES:  # the rules are in order of precedence: the first that matches any frame
        if any(re.search(pattern, name) for name in names):
            return category
    for name in reversed(names):          # (kept for rule files that rely on frame order; not reached otherwise)
        for pattern, category in CATEGORIES:
            if re.search(pattern, name):
                return category
    return "other"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("stacks")
    parser.add_argument("--run")
    parser.add_argument("--state")
    parser.add_argument("--from", dest="start", type=float, default=0.0)
    parser.add_argument("--to", dest="end", type=float, default=1e9)
    parser.add_argument("--top", type=int, default=40)
    parser.add_argument("--thread", type=int)
    parser.add_argument("--wall", action="store_true")
    parser.add_argument("--with-waits", action="store_true")
    parser.add_argument("--parallelism", action="store_true",
                        help="how many threads were at work at each look, and what ran while only one or two were")
    parser.add_argument("--tree")
    parser.add_argument("--depth", type=int, default=3, help="with --tree: levels of callers and callees")
    parser.add_argument("--json")
    parser.add_argument("--categories", help="a JSON file with [[pattern, category], ...] instead of the built-in rules")
    arguments = parser.parse_args()
    if arguments.categories:
        CATEGORIES[:] = [tuple(rule) for rule in json.loads(Path(arguments.categories).read_text(encoding="utf-8"))]
    elif (Path(__file__).parent / "categories.json").exists():
        CATEGORIES[:] = [tuple(rule) for rule in json.loads((Path(__file__).parent / "categories.json").read_text(encoding="utf-8"))]

    data = json.loads(Path(arguments.stacks).read_text(encoding="utf-8"))
    start, end = window_of(arguments)
    symbols = Symbolizer(data["modules"])
    all_every = data.get("allEvery", 10)
    interval = data["interval"]

    # The looks at which every thread was walked: those where an idle (0 cpu) sample exists.
    looks = sorted({sample[0] for sample in data["samples"]})
    full_looks = sorted({sample[0] for sample in data["samples"] if sample[2] <= 0})
    # A full look stands for the time until the next one.
    gaps = [b - a for a, b in zip(full_looks, full_looks[1:])]
    usual = sorted(gaps)[len(gaps) // 2] if gaps else interval * all_every
    span = {moment: min(gap, 3 * usual) for moment, gap in zip(full_looks, gaps + [usual])}
    waiting = re.compile(r"!(ZwWaitFor|NtWaitFor|ZwDelayExecution|ZwRemoveIoCompletion|NtUserGetMessage|"
                         r"ZwWaitForWorkViaWorkerFactory|NtUserMsgWaitForMultipleObjects|ZwSignalAndWait|ZwYieldExecution)")
    idle = 0.0

    per_thread: dict[int, float] = collections.Counter()
    inclusive: dict[str, float] = collections.Counter()
    own: dict[str, float] = collections.Counter()
    categories: dict[str, float] = collections.Counter()
    category_threads: dict[str, set] = collections.defaultdict(set)
    roots: dict[int, collections.Counter] = collections.defaultdict(collections.Counter)
    callers: dict[str, collections.Counter] = collections.defaultdict(collections.Counter)
    callees: dict[str, collections.Counter] = collections.defaultdict(collections.Counter)
    total = 0.0
    paths: dict[str, float] = collections.Counter()
    path_examples: dict[str, str] = {}
    for sample in data["samples"]:
        moment, tid, used, addresses = sample[:4]
        if len(sample) > 4 and start <= moment <= end and used > 0:
            # Group by folder with digits and uuids blanked, so that the kinds of file show.
            folder = re.sub(r"[0-9a-fA-F]{8}-[0-9a-fA-F-]{27}", "<uuid>", sample[4].rsplit("\\", 1)[0])
            kind = folder + "\\*" + (re.search(r"\.[A-Za-z0-9_]+$", sample[4]) or re.match("", "")).group(0)
            paths[kind] += used
            path_examples.setdefault(kind, sample[4])
        if not (start <= moment <= end) or (arguments.thread and tid != arguments.thread):
            continue
        if arguments.wall:
            if moment not in span:
                continue
            weight = span[moment]
        else:
            weight = used
        if weight <= 0 or not addresses:
            continue
        names = [symbols.name(address) for address in addresses]
        if waiting.search(names[0]) and not arguments.with_waits:
            idle += weight      # asleep, or spinning between two sleeps: not work
            continue
        total += weight
        per_thread[tid] += weight
        for name in set(names):
            inclusive[name] += weight
        own[names[0]] += weight
        category = category_of(names)
        categories[category] += weight
        category_threads[category].add(tid)
        game = [n for n in names if "<bg3" in n]
        if game:
            roots[tid][game[-1]] += weight
        if arguments.tree:
            for index, name in enumerate(names):
                if re.search(arguments.tree, name):
                    chain_up = " <- ".join(names[index + 1:index + 1 + arguments.depth])
                    chain_down = " -> ".join(reversed(names[max(0, index - arguments.depth):index]))
                    callers[name][chain_up] += weight
                    callees[name][chain_down] += weight

    unit = "s of wall time (sampled)" if arguments.wall else "s of processor time"
    print(f"window {start:.2f} .. {end:.2f} s after process start ({end - start:.2f} s); {total:.1f} {unit} at work, "
          f"{idle:.1f} waiting or yielding (left out; --with-waits keeps them); {len(looks)} looks")
    print("\nthreads:")
    for tid, weight in sorted(per_thread.items(), key=lambda item: -item[1])[:25]:
        root = roots[tid].most_common(1)[0][0] if roots[tid] else ""
        print(f"  {weight:7.2f}  {tid:6d} {data['threadNames'].get(str(tid), ''):14} {root[:110]}")
    if CATEGORIES:
        print("\ncategories:")
        for category, weight in sorted(categories.items(), key=lambda item: -item[1]):
            print(f"  {weight:7.2f}  {100 * weight / max(total, 1e-9):5.1f} %  {category}  ({len(category_threads[category])} threads)")
    print("\ninclusive (the function is somewhere on the stack):")
    for name, weight in inclusive.most_common(arguments.top):
        print(f"  {weight:7.2f}  {100 * weight / max(total, 1e-9):5.1f} %  {name[:150]}")
    print("\nown (the function is on top of the stack):")
    for name, weight in own.most_common(arguments.top):
        print(f"  {weight:7.2f}  {100 * weight / max(total, 1e-9):5.1f} %  {name[:150]}")
    if arguments.tree:
        for name in sorted(callers, key=lambda n: -inclusive[n])[:8]:
            print(f"\n{name}  ({inclusive[name]:.2f})")
            print("  called from:")
            for chain, weight in callers[name].most_common(8):
                print(f"    {weight:7.2f}  {chain[:230]}")
            print("  calls:")
            for chain, weight in callees[name].most_common(12):
                print(f"    {weight:7.2f}  {chain[:230]}")
    if arguments.parallelism:
        # Per look: the threads caught at work (used processor time and were not in a wait).
        at_work: dict[float, list[list[str]]] = collections.defaultdict(list)
        for sample in data["samples"]:
            moment, tid, used, addresses = sample[:4]
            if start <= moment <= end and used > 0 and addresses:
                names = [symbols.name(address) for address in addresses]
                if not waiting.search(names[0]):
                    at_work[moment].append(names)
        in_window = [moment for moment in looks if start <= moment <= end]
        look_gaps = {a: min(b - a, 1.0) for a, b in zip(in_window, in_window[1:])}
        histogram: dict[int, float] = collections.Counter()
        narrow: dict[str, float] = collections.Counter()
        narrow_total = 0.0
        timeline = []
        for moment in in_window[:-1]:
            count = len(at_work.get(moment, []))
            histogram[count] += look_gaps[moment]
            timeline.append((moment, count))
            if 1 <= count <= 2:
                narrow_total += look_gaps[moment]
                for names in at_work[moment]:
                    for name in set(names):
                        narrow[name] += look_gaps[moment] / count
        print("\nthreads at work at once (seconds of the window):")
        for count in sorted(histogram):
            print(f"  {count:3d} threads  {histogram[count]:6.2f} s  {'#' * int(40 * histogram[count] / max(end - start, 1e-9))}")
        print(f"\nwhat ran while only one or two threads worked ({narrow_total:.2f} s):")
        for name, weight in narrow.most_common(arguments.top):
            print(f"  {weight:7.2f}  {name[:150]}")
        print("\nthreads at work, second by second:")
        second = None
        row: list[int] = []
        for moment, count in timeline + [(1e18, 0)]:
            if second is not None and int(moment) != second:
                print(f"  {second:5d} s  avg {sum(row) / max(len(row), 1):4.1f}  " + "".join(str(min(c, 9)) for c in row))
                row = []
            second = int(moment)
            row.append(count)
    if paths:
        print(f"\nfile system calls caught in the act, by folder and extension ({sum(paths.values()):.2f} s):")
        for kind, weight in paths.most_common(arguments.top):
            print(f"  {weight:7.2f}  {kind[-150:]}    e.g. {path_examples[kind].rsplit(chr(92), 1)[-1][:60]}")
    if arguments.json:
        Path(arguments.json).write_text(json.dumps({
            "window": [start, end], "total": total, "wall": arguments.wall,
            "threads": {str(t): w for t, w in per_thread.items()}, "categories": dict(categories),
            "inclusive": inclusive.most_common(400), "own": own.most_common(400)}, indent=1), encoding="utf-8")
    return 0


if __name__ == "__main__":
    sys.exit(main())
