"""Pseudo-code of the game and of the Toolkit's engine DLLs, made with Ghidra and kept in SQLite.

    python decomp.py run bg3.exe            # analyse and decompile (hours for the game executable)
    python decomp.py run EoCClient.dll      # a Toolkit DLL, found in the Toolkit folder
    python decomp.py load                   # put finished output into decomp.sqlite
    python decomp.py code ecl::TimelineSystem::TimelineSystem
    python decomp.py code bg3.exe+0x1D6FA90
    python decomp.py grep "ShowWeapon"      # search names and pseudo-code
    python decomp.py dotnet                 # the editor's managed assemblies to C# (needs ilspycmd)
    python decomp.py csharp "ShowWeapon"    # search that C#
    python decomp.py status

Needs Ghidra and the JDK it asks for (JDK 25 for Ghidra 12; see README). Nothing is written into the repository: projects, output
and the database live in the work folder (BG3_DECOMP_WORK, default %LOCALAPPDATA%\\GateKit\\decomp).
The pseudo-code is derived from the game's binaries: keep it to yourself.
"""
import argparse
import json
import os
import pathlib
import re
import shutil
import sqlite3
import subprocess
import sys
import time
import zlib

def local_folder():
    """%LOCALAPPDATA%\\GateKit; the folder's former name is used while only that one exists."""
    base = pathlib.Path(os.environ.get("LOCALAPPDATA", pathlib.Path.home()))
    old = base / "GwynervaDevKit"
    return old if old.exists() and not (base / "GateKit").exists() else base / "GateKit"

HERE = pathlib.Path(__file__).resolve().parent
LOCAL = local_folder()
WORK = pathlib.Path(os.environ.get("BG3_DECOMP_WORK", LOCAL / "decomp"))
TOOLS = pathlib.Path(os.environ.get("BG3_DECOMP_TOOLS", LOCAL / "tools"))
GAME = pathlib.Path(os.environ.get("BG3_GAME", r"C:\Program Files (x86)\Steam\steamapps\common\Baldurs Gate 3"))
TOOLKIT = pathlib.Path(os.environ.get("BG3_TOOLKIT", r"C:\Program Files (x86)\Steam\steamapps\common\Baldurs Gate 3 Toolkit"))
SYMBOLS = HERE.parent / "symbols.py"

SCHEMA = """
CREATE TABLE IF NOT EXISTS binaries (id INTEGER PRIMARY KEY, name TEXT UNIQUE, loaded TEXT, functions INTEGER, failed INTEGER);
CREATE TABLE IF NOT EXISTS functions (
    id INTEGER PRIMARY KEY, binary_id INTEGER, rva INTEGER, name TEXT, signature TEXT, size INTEGER,
    thunk INTEGER, lines INTEGER, error TEXT
);
CREATE TABLE IF NOT EXISTS code (function_id INTEGER PRIMARY KEY, text BLOB);       -- zlib
CREATE TABLE IF NOT EXISTS calls (binary_id INTEGER, caller INTEGER, callee INTEGER);  -- rva -> rva
CREATE VIRTUAL TABLE IF NOT EXISTS code_fts USING fts5(name, code, content='', contentless_delete=1, tokenize="unicode61 tokenchars '_'");
-- C# sources of the Toolkit's managed assemblies (the editor), decompiled with ILSpy.
CREATE TABLE IF NOT EXISTS managed_files (id INTEGER PRIMARY KEY, assembly TEXT, path TEXT, lines INTEGER);
CREATE TABLE IF NOT EXISTS managed_code (file_id INTEGER PRIMARY KEY, text BLOB);    -- zlib
CREATE VIRTUAL TABLE IF NOT EXISTS managed_fts USING fts5(path, code, content='', contentless_delete=1, tokenize="unicode61 tokenchars '_'");
CREATE INDEX IF NOT EXISTS managed_assembly ON managed_files(assembly);
-- Where each ECS system is created, read from the pseudo-code (python decomp.py systems --rebuild).
CREATE TABLE IF NOT EXISTS system_factories (
    binary_id INTEGER, system TEXT, factory INTEGER, constructor INTEGER, vtable INTEGER, note TEXT
);
CREATE INDEX IF NOT EXISTS system_factories_name ON system_factories(system);
-- The per-frame entry of a system: the function pointer its factory stores (the engine's
-- SystemRegistrationHelper::SystemUpdate<System>) and the update function that one calls.
CREATE TABLE IF NOT EXISTS system_updates (binary_id INTEGER, system TEXT, thunk INTEGER, updater INTEGER, note TEXT);
CREATE INDEX IF NOT EXISTS system_updates_name ON system_updates(system);
-- Toolkit function <-> game function, paired by their pseudo-code (python decomp.py match).
-- score: weight of shared features over weight of all; margin: lead over the runner-up.
CREATE TABLE IF NOT EXISTS code_matches (
    toolkit_binary INTEGER, toolkit_rva INTEGER, game_binary INTEGER, game_rva INTEGER, score REAL, margin REAL
);
CREATE INDEX IF NOT EXISTS code_matches_game ON code_matches(game_binary, game_rva);
-- Global FixedStrings of the game (python decomp.py fixedstrings --rebuild). The engine keeps one
-- registry entry per string literal; kind 'entry' is that entry (the text pointer), 'value' the
-- FixedString it is resolved into at start-up (entry + 0x18), 'pointer' a global that holds the
-- address of the value.
CREATE TABLE IF NOT EXISTS fixed_strings (binary_id INTEGER, rva INTEGER, kind TEXT, text TEXT, registered_in INTEGER);
CREATE INDEX IF NOT EXISTS fixed_strings_rva ON fixed_strings(binary_id, rva);
CREATE INDEX IF NOT EXISTS fixed_strings_text ON fixed_strings(text);
CREATE INDEX IF NOT EXISTS code_matches_toolkit ON code_matches(toolkit_binary, toolkit_rva);
CREATE INDEX IF NOT EXISTS functions_rva ON functions(binary_id, rva);
CREATE INDEX IF NOT EXISTS functions_name ON functions(name);
CREATE INDEX IF NOT EXISTS calls_caller ON calls(binary_id, caller);
CREATE INDEX IF NOT EXISTS calls_callee ON calls(binary_id, callee);
"""


def find_tools():
    ghidra = next(iter(sorted(TOOLS.glob("ghidra_*"), reverse=True)), None)
    jdk = next(iter(sorted(TOOLS.glob("jdk-*"), reverse=True)), None)
    if not ghidra or not (ghidra / "support" / "launch.bat").exists():
        sys.exit(f"Ghidra not found under {TOOLS} (unpack a ghidra_*_PUBLIC release there, or set BG3_DECOMP_TOOLS)")
    return ghidra, jdk


def locate(name):
    for candidate in (GAME / "bin" / name, TOOLKIT / name, pathlib.Path(name)):
        if candidate.is_file():
            return candidate
    sys.exit(f"binary not found: {name}")


def run(args):
    ghidra, jdk = find_tools()
    binary = locate(args.binary)
    work = WORK / binary.name
    work.mkdir(parents=True, exist_ok=True)
    out = work / "functions.jsonl"
    log = work / "ghidra.log"
    env = dict(os.environ)
    if jdk:
        env["JAVA_HOME"] = str(jdk)
        env["PATH"] = str(jdk / "bin") + os.pathsep + env["PATH"]
    # Ghidra's own settings, cache and temporary files go next to the output, so that a run does
    # not depend on (or leave anything in) the user profile.
    user = WORK / "ghidra-user"
    for sub in ("settings", "cache", "tmp"):
        (user / sub).mkdir(parents=True, exist_ok=True)
    vmargs = (f"-Dapplication.settingsdir={user / 'settings'} -Dapplication.cachedir={user / 'cache'}"
              f" -Djava.io.tmpdir={user / 'tmp'}")
    launch = [str(ghidra / "support" / "launch.bat"), "fg", "jdk", "Ghidra-Headless", args.memory, vmargs,
              "ghidra.app.util.headless.AnalyzeHeadless", str(work), "project"]
    scripts = ["-scriptPath", str(HERE / "ghidra")]
    project_exists = any(work.glob("project.rep")) and not args.fresh
    if project_exists:
        cmd = launch + ["-process", binary.name, "-noanalysis"] + scripts
        if binary.parent == GAME / "bin" and not args.range:
            # names found since the first run (python symbols.py build after decomp.py match)
            names = work / "names.tsv"
            r = subprocess.run([sys.executable, str(SYMBOLS), "export", "--format", "ghidra", "--dll", binary.name,
                                "--out", str(names)], capture_output=True, text=True)
            print(r.stderr.strip() or r.stdout.strip(), file=sys.stderr)
            if names.exists():
                cmd += ["-preScript", "ApplyNames.java", str(names), "update"]
    else:
        # Ghidra's launcher is a batch file and breaks on "Program Files (x86)": import a copy.
        copy = work / binary.name
        if not copy.exists() or copy.stat().st_size != binary.stat().st_size:
            shutil.copyfile(binary, copy)
        cmd = launch + ["-import", str(copy), "-overwrite"] + scripts
        if binary.parent == GAME / "bin":
            names = work / "names.tsv"
            r = subprocess.run([sys.executable, str(SYMBOLS), "export", "--format", "ghidra", "--dll", binary.name,
                                "--out", str(names)], capture_output=True, text=True)
            print(r.stderr.strip() or r.stdout.strip(), file=sys.stderr)
            if names.exists():
                cmd += ["-preScript", "ApplyNames.java", str(names)]
        elif binary.stat().st_size > 50_000_000:
            # no names to apply, but the script also switches off the analyzers a large image cannot afford
            names = work / "names.tsv"
            names.write_text("", encoding="utf-8")
            cmd += ["-preScript", "ApplyNames.java", str(names)]
    post = ["-postScript", "ExportDecomp.java", str(out)]
    if args.range:
        first, _, last = args.range.partition("-")
        post += [first] + ([last] if last else [])
    cmd += post + ["-max-cpu", str(args.cpu), "-log", str(log)]
    print("running:", " ".join(f'"{c}"' if " " in c else c for c in cmd), file=sys.stderr)
    started = time.time()
    with open(work / "console.log", "a", encoding="utf-8", errors="replace") as console:
        console.write(f"\n==== {time.strftime('%Y-%m-%d %H:%M:%S')} {' '.join(cmd)}\n")
        console.flush()
        # stdin is closed so that a launcher question (for example a missing JDK) fails instead of waiting
        code = subprocess.run(cmd, env=env, stdin=subprocess.DEVNULL, stdout=console, stderr=subprocess.STDOUT).returncode
    (work / "last_run.json").write_text(json.dumps({"seconds": round(time.time() - started), "exit": code,
                                                    "finished": time.strftime("%Y-%m-%d %H:%M:%S")}), encoding="utf-8")
    print(f"{binary.name}: exit {code} after {time.time() - started:.0f} s; output {out}", file=sys.stderr)
    return code


THIRD_PARTY = ("System.", "Microsoft.", "Xceed.", "Google.", "Newtonsoft.", "Magick.", "WeifenLuo.", "Cyotek.", "Emoji.",
               "Typography.", "ScintillaNET", "SciLexer", "Office", "DocumentFormat.", "p4api", "Steamworks", "SFGLib",
               "FreeImageNET", "DirectXTexNet", "Skybound", "SmartPropertyGrid", "GDEXSharp", "Xceed")


def dotnet(args):
    """Decompiles the Toolkit's own managed assemblies to C# with ILSpy and loads them into the database."""
    ilspy = next(iter(sorted(TOOLS.glob("ilspycmd/*/tools/*/any/ilspycmd.dll"), reverse=True)), None)
    if not ilspy:
        sys.exit(f"ilspycmd not found under {TOOLS / 'ilspycmd'}: unpack the ilspycmd package from nuget.org there")
    runner = [str(pathlib.Path(args.dotnet) / "dotnet.exe")] if args.dotnet else ["dotnet"]
    env = dict(os.environ)
    if args.dotnet:
        env["DOTNET_ROOT"] = str(args.dotnet)
    out_root = WORK / "dotnet"
    done = failed = 0
    names = []
    for dll in sorted(TOOLKIT.glob("*.dll")) + sorted(TOOLKIT.glob("*.exe")):
        if dll.name.startswith(THIRD_PARTY) or (args.only and args.only.lower() not in dll.name.lower()):
            continue
        with open(dll, "rb") as f:
            head = f.read(4096)
        pe = int.from_bytes(head[0x3C:0x40], "little")
        if head[:2] != b"MZ" or pe + 24 + 240 > len(head):
            continue
        magic = int.from_bytes(head[pe + 24:pe + 26], "little")
        clr = pe + 24 + (112 if magic == 0x20B else 96) + 14 * 8
        if int.from_bytes(head[clr:clr + 4], "little") == 0:
            continue                                  # not a .NET assembly
        target = out_root / dll.stem
        names.append(dll.stem)
        if target.exists() and (any(target.rglob("*.cs")) or (target / "ilspy-error.txt").exists()) and not args.fresh:
            continue
        target.mkdir(parents=True, exist_ok=True)
        try:
            r = subprocess.run(runner + [str(ilspy), "-p", "-o", str(target), str(dll)], env=env, stdin=subprocess.DEVNULL,
                               capture_output=True, text=True, errors="replace", timeout=args.timeout)
        except subprocess.TimeoutExpired:
            failed += 1
            (target / "ilspy-error.txt").write_text(f"gave up after {args.timeout} s", encoding="utf-8")
            print(f"  {dll.name}: gave up after {args.timeout} s (mixed C++/CLI assemblies can take very long)", file=sys.stderr)
            continue
        if r.returncode or not any(target.rglob("*.cs")):
            failed += 1
            (target / "ilspy-error.txt").write_text((r.stdout + r.stderr)[-4000:], encoding="utf-8")
            print(f"  {dll.name}: not decompiled ({(r.stderr or r.stdout).strip().splitlines()[-1][:120] if (r.stderr or r.stdout).strip() else 'no output'})", file=sys.stderr)
        else:
            done += 1
            print(f"  {dll.name}: {sum(1 for _ in target.rglob('*.cs'))} files", file=sys.stderr)
    db = database()
    db.executescript("DELETE FROM managed_code; DELETE FROM managed_files; DELETE FROM managed_fts;")
    files = lines = 0
    for name in sorted(d.name for d in out_root.iterdir() if d.is_dir()):
        for path in sorted((out_root / name).rglob("*.cs")):
            text = path.read_text(encoding="utf-8-sig", errors="replace")
            rel = path.relative_to(out_root / name).as_posix()
            cur = db.execute("INSERT INTO managed_files (assembly, path, lines) VALUES (?,?,?)", (name, rel, text.count("\n")))
            db.execute("INSERT INTO managed_code VALUES (?,?)", (cur.lastrowid, zlib.compress(text.encode("utf-8"), 6)))
            db.execute("INSERT INTO managed_fts (rowid, path, code) VALUES (?,?,?)", (cur.lastrowid, f"{name}/{rel}", text))
            files += 1
            lines += text.count("\n")
    db.commit()
    print(f"{len(names)} managed assemblies, {done} decompiled now, {failed} failed; {files} C# files, {lines} lines in the database")


def csharp(args):
    """Search the decompiled C# of the editor, or print one file."""
    db = database(readonly=True)
    if args.file:
        row = db.execute("SELECT c.text FROM managed_files f JOIN managed_code c ON c.file_id = f.id WHERE f.assembly || '/' || f.path = ?"
                         " OR f.path = ? LIMIT 1", (args.text, args.text)).fetchone()
        print(zlib.decompress(row[0]).decode("utf-8") if row else "no such file")
        return
    query = args.text if args.raw else " ".join('"' + w.replace('"', '""') + '"' for w in args.text.split())
    rows = db.execute("SELECT f.id, f.assembly, f.path FROM managed_fts JOIN managed_files f ON f.id = managed_fts.rowid"
                      " WHERE managed_fts MATCH ? LIMIT ?", (query, args.limit)).fetchall()
    words = [w.lower() for w in re.findall(r"\w+", args.text)]
    for fid, assembly, path in rows:
        print(f"{assembly}/{path}")
        if args.lines:
            text = zlib.decompress(db.execute("SELECT text FROM managed_code WHERE file_id = ?", (fid,)).fetchone()[0]).decode("utf-8")
            shown = 0
            for n, line in enumerate(text.splitlines(), 1):
                if any(w in line.lower() for w in words):
                    print(f"    {n}: {line.strip()[:200]}")
                    shown += 1
                    if shown >= args.lines:
                        break
    if not rows:
        print("nothing found", file=sys.stderr)


def database(readonly=False):
    path = WORK / "decomp.sqlite"
    if readonly and not path.exists():
        sys.exit(f"{path} does not exist; run:  python decomp.py run <binary>  then  python decomp.py load")
    db = sqlite3.connect(path)
    if not readonly:
        db.executescript("PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF;" + SCHEMA)
    return db


def load(args):
    db = database()
    for out in sorted(WORK.glob("*/functions.jsonl")):
        name = out.parent.name
        if args.binary and args.binary.lower() != name.lower():
            continue
        row = db.execute("SELECT id FROM binaries WHERE name = ?", (name,)).fetchone()
        if row:
            binary_id = row[0]
            db.execute("DELETE FROM code_fts WHERE rowid IN (SELECT id FROM functions WHERE binary_id = ?)", (binary_id,))
            db.execute("DELETE FROM code WHERE function_id IN (SELECT id FROM functions WHERE binary_id = ?)", (binary_id,))
            db.execute("DELETE FROM functions WHERE binary_id = ?", (binary_id,))
            db.execute("DELETE FROM calls WHERE binary_id = ?", (binary_id,))
        else:
            binary_id = db.execute("INSERT INTO binaries (name) VALUES (?)", (name,)).lastrowid
        count = failed = 0
        with open(out, encoding="utf-8", errors="replace") as f:
            for line in f:
                try:
                    r = json.loads(line)
                except ValueError:
                    continue                      # a line cut short by an interrupted run
                rva = int(r["rva"], 16)
                code = r.get("code")
                cur = db.execute("INSERT INTO functions (binary_id, rva, name, signature, size, thunk, lines, error)"
                                 " VALUES (?,?,?,?,?,?,?,?)", (binary_id, rva, r.get("name"), r.get("sig"), r.get("size"),
                                                               int(bool(r.get("thunk"))), code.count("\n") if code else None,
                                                               r.get("error")))
                if code:
                    db.execute("INSERT INTO code VALUES (?,?)", (cur.lastrowid, zlib.compress(code.encode("utf-8"), 6)))
                    db.execute("INSERT INTO code_fts (rowid, name, code) VALUES (?,?,?)", (cur.lastrowid, r.get("name") or "", code))
                else:
                    failed += 1
                db.executemany("INSERT INTO calls VALUES (?,?,?)", [(binary_id, rva, int(c, 16)) for c in r.get("callees", [])])
                count += 1
        db.execute("UPDATE binaries SET loaded = ?, functions = ?, failed = ? WHERE id = ?",
                   (time.strftime("%Y-%m-%d %H:%M:%S"), count, failed, binary_id))
        db.commit()
        renamed = name_exports(db, name)
        print(f"{name}: {count} functions, {failed} without pseudo-code" + (f", {renamed} named from the export table" if renamed else ""))


def name_exports(db, binary):
    """Gives functions Ghidra left as FUN_... the name their address is exported under (Ghidra's
    demangler gives up on some long decorated names). Returns how many were named."""
    path = HERE.parent / "symbols.sqlite"
    row = db.execute("SELECT id FROM binaries WHERE name = ?", (binary,)).fetchone()
    if not path.exists() or not row:
        return 0
    sym = sqlite3.connect(f"file:{path.as_posix()}?mode=ro", uri=True)
    names = dict(sym.execute(
        "SELECT s.rva, min(s.full_name) FROM symbols s JOIN dlls d ON d.id = s.dll_id WHERE d.name = ? AND s.rva IS NOT NULL"
        " AND s.kind IN ('method', 'function') GROUP BY s.rva", (binary,)))
    todo = [(names[rva], fid) for fid, rva in db.execute(
        "SELECT id, rva FROM functions WHERE binary_id = ? AND (name LIKE 'FUN!_%' ESCAPE '!' OR name LIKE 'thunk!_FUN!_%' ESCAPE '!')", (row[0],))
        if rva in names]
    db.executemany("UPDATE functions SET name = ? WHERE id = ?", todo)
    db.commit()
    return len(todo)


ADDRESS = re.compile(r"^(.+?)\+(?:0x)?([0-9a-fA-F]+)$")
THIS_OFFSET = re.compile(r"(?:this|param_1) \+ (0x[0-9a-fA-F]+|\d+)\)|(?:this|param_1)\[(0x[0-9a-fA-F]+|\d+)\]")


def class_fields(class_name):
    """{offset: 'Name [source]'} for a class, from the symbol database: fields read from the
    Toolkit's accessors (the game's offset when it was read there) and Script Extender's layout."""
    path = HERE.parent / "symbols.sqlite"
    if not path.exists() or not class_name:
        return {}
    db = sqlite3.connect(f"file:{path.as_posix()}?mode=ro", uri=True)
    fields = {}
    for name, offset, checked in db.execute(
            "SELECT f.name, f.offset, f.checked FROM se_fields f JOIN se_types t ON t.id = f.type_id"
            " WHERE t.engine_class = ? AND f.offset IS NOT NULL ORDER BY f.idx", (class_name,)):
        fields.setdefault(offset, f"{name} [SE layout{'' if checked else ', offset computed'}]")
    for name, offset, game_offset, bit in db.execute(
            "SELECT field, offset, game_offset, bit FROM accessors WHERE class = ? ORDER BY bit IS NOT NULL, field", (class_name,)):
        where = game_offset if game_offset is not None else offset
        label = f"{name} [accessor{', game offset' if game_offset is not None else ', Toolkit offset'}]"
        if bit is not None:
            fields[where] = fields.get(where, "") + ("; " if where in fields else "") + f"bit {bit} = {name}"
        elif where in fields and name + " [accessor" in fields[where]:
            continue                                   # getter and setter of the same field
        elif where not in fields or "SE layout" in fields[where]:
            fields[where] = label if where not in fields else f"{name} [accessor + SE layout]" if fields[where].startswith(name + " ") else fields[where] + f" / {label}"
    return fields


MIN_SCORE, MIN_MARGIN = 0.15, 0.10          # pairs by pseudo-code shown by default; see README


def code_match_name(binary, rva, _cache={}):
    """'~ Name [pseudo-code 0.81]' for a game function paired with a named Toolkit function by
    its pseudo-code, or None."""
    if "db" not in _cache:
        path = WORK / "decomp.sqlite"
        _cache["db"] = sqlite3.connect(f"file:{path.as_posix()}?mode=ro", uri=True) if path.exists() else None
    db = _cache["db"]
    if db is None:
        return None
    try:
        row = db.execute(
            "SELECT f.name, m.score FROM code_matches m JOIN binaries g ON g.id = m.game_binary"
            " JOIN functions f ON f.binary_id = m.toolkit_binary AND f.rva = m.toolkit_rva"
            " WHERE g.name = ? AND m.game_rva = ? AND m.score >= ? AND m.margin >= ? AND f.name NOT LIKE 'FUN!_%' ESCAPE '!'"
            " AND f.name NOT LIKE 'thunk!_FUN!_%' ESCAPE '!' ORDER BY m.score DESC LIMIT 1", (binary, rva, MIN_SCORE, MIN_MARGIN)).fetchone()
    except sqlite3.Error:
        return None
    return f"~ {row[0]} [pseudo-code {row[1]:.2f}]" if row else None


UNNAMED = re.compile(r"\bFUN_([0-9a-f]{9,16})\b")


def hint_unnamed(text, binary):
    """For every function Ghidra could not name (FUN_<address>), adds at its first mention what is
    known about it from the symbol database: a later match to a Toolkit function, or the text
    constant only that function refers to. A hint, clearly marked, not a name."""
    path = HERE.parent / "symbols.sqlite"
    if not path.exists():
        return text
    db = sqlite3.connect(f"file:{path.as_posix()}?mode=ro", uri=True)
    row = db.execute("SELECT id FROM dlls WHERE name = ?", (binary,)).fetchone()
    if not row:
        return text
    dll_id, base, seen = row[0], image_base(binary), {}

    def lookup(rva):
        exported = db.execute("SELECT min(full_name) FROM symbols WHERE dll_id = ? AND rva = ? AND kind IN ('method', 'function')",
                              (dll_id, rva)).fetchone()
        if exported and exported[0]:
            return f"= {exported[0]} [export]"
        named = db.execute(
            "SELECT s.full_name, m.method FROM matches m JOIN symbols s ON s.dll_id = m.toolkit_dll AND s.rva = m.toolkit_rva"
            " WHERE m.game_dll = ? AND m.game_rva = ? AND (m.relation IS NOT 'inlined' OR m.method = 'vtable')"
            " ORDER BY length(s.full_name) LIMIT 1", (dll_id, rva)).fetchone()
        if named:
            return f"= {named[0]} [{named[1]}]"
        only = db.execute(
            "SELECT t.text FROM string_refs r JOIN strings t ON t.id = r.string_id WHERE t.dll_id = ? AND r.func_rva = ?"
            " AND (SELECT count(*) FROM string_refs r2 WHERE r2.string_id = t.id) = 1 AND t.text NOT LIKE '%GetTypeName<%'"
            " ORDER BY length(t.text) DESC LIMIT 1", (dll_id, rva)).fetchone()
        if only:
            return 'only user of "' + only[0][:60].replace("*/", "* /") + '"'
        alike = code_match_name(binary, rva)
        if alike:
            return alike
        type_name = db.execute("SELECT type FROM type_names WHERE dll_id = ? AND func_rva = ? LIMIT 1", (dll_id, rva)).fetchone()
        return f"returns the type name {type_name[0][:80]}" if type_name else None

    def replace(m):
        address = int(m.group(1), 16)
        if address in seen:
            return m.group(0)
        seen[address] = lookup(address - base)
        return m.group(0) + (f" /* {seen[address]} */" if seen[address] else "")

    return UNNAMED.sub(replace, text)


def annotate(text, function_name):
    """Adds the field name to every line that touches `this + offset`, when the function is a
    method of a class whose fields are known."""
    class_name = function_name.rsplit("::", 1)[0] if "::" in (function_name or "") else None
    fields = class_fields(class_name)
    if not fields:
        return text
    out = []
    for line in text.splitlines():
        notes = []
        for m in THIS_OFFSET.finditer(line):
            offset = int(m.group(1) or m.group(2), 0)
            if offset in fields and fields[offset] not in notes:
                notes.append(f"+0x{offset:X} {fields[offset]}")
        out.append(line + ("   // " + " | ".join(notes) if notes else ""))
    return "\n".join(out)


def containing(db, binary, rva):
    """(binary name, start of the decompiled function that contains the address, how it was found)
    or None. A function is often split into parts; the symbol database knows which part belongs
    to which function. Without it the nearest function start before the address is taken."""
    row = db.execute("SELECT id, name FROM binaries WHERE name LIKE ? ORDER BY length(name) LIMIT 1", (binary + "%",)).fetchone()
    if not row:
        return None
    binary_id, name = row
    path = HERE.parent / "symbols.sqlite"
    if path.exists():
        sym = sqlite3.connect(f"file:{path.as_posix()}?mode=ro", uri=True)
        dll = sym.execute("SELECT id FROM dlls WHERE name = ?", (name,)).fetchone()
        if dll and sym.execute("SELECT 1 FROM sqlite_master WHERE name = 'chunks'").fetchone():
            part = sym.execute("SELECT rva, size FROM functions WHERE dll_id = ? AND rva <= ? ORDER BY rva DESC LIMIT 1", (dll[0], rva)).fetchone()
            if part and rva < part[0] + part[1]:
                parent = sym.execute("SELECT parent FROM chunks WHERE dll_id = ? AND rva = ?", (dll[0], part[0])).fetchone()
                start = parent[0] if parent else part[0]
                if db.execute("SELECT 1 FROM functions WHERE binary_id = ? AND rva = ?", (binary_id, start)).fetchone():
                    return name, start, "the address is inside this function"
    near = db.execute("SELECT rva FROM functions WHERE binary_id = ? AND rva <= ? ORDER BY rva DESC LIMIT 1", (binary_id, rva)).fetchone()
    if near and rva - near[0] < 0x20000:
        return name, near[0], "nearest function start before the address; not proven to contain it"
    return None


def arm64_to_x64(address, _cache={}):
    """RVA in the x86-64 half of the macOS executable of the function that the arm64 half has at
    this absolute address, or None. The two halves carry the same symbol names; the table is
    built once from the coverage project's symbol list of the arm64 half and the symbol table of
    the x86-64 half, and kept in the work folder."""
    if "map" not in _cache:
        import pickle
        cache = WORK / "macos-arm64-to-x64.pickle"
        listing = HERE.parent.parent / "se-fork" / "reference" / "macos-symbols" / "symbols.tsv"
        half = WORK / "bg3_macos_x64" / "bg3_macos_x64"
        table = {}
        if cache.exists():
            with open(cache, "rb") as f:
                table = pickle.load(f)
        elif listing.exists() and half.exists():
            import asmfeatures
            image, functions = asmfeatures.open_macho(str(half))
            by_name = {}
            for start, size, name in functions:
                by_name.setdefault(name, start - 0x100000000)
            with open(listing, encoding="utf-8", errors="replace") as f:
                for line in f:
                    parts = line.rstrip("\n").split("\t")
                    if len(parts) >= 3 and parts[1] in "tT" and parts[2] in by_name:
                        table[int(parts[0], 16)] = by_name[parts[2]]
            with open(cache, "wb") as f:
                pickle.dump(table, f, protocol=pickle.HIGHEST_PROTOCOL)
        _cache["map"] = table
    return _cache["map"].get(address)


def follow_jump(db, binary, rva):
    """Where a chain of `jmp rel32` stubs starting at the address ends, or None when the address
    is not such a stub. Read from the file of a game executable or Toolkit DLL."""
    row = db.execute("SELECT name FROM binaries WHERE name LIKE ? ORDER BY length(name) LIMIT 1", (binary + "%",)).fetchone()
    if not row or not row[0].lower().endswith((".exe", ".dll")):
        return None
    try:
        read_text(row[0], rva)                          # loads and caches the image
    except (OSError, SystemExit):
        return None
    pe = read_text.__defaults__[0][row[0]]
    here, hops = rva, 0
    while hops < 4:
        o = pe.off(here)
        if o is None or o + 5 > len(pe.d) or pe.d[o] != 0xE9:
            break
        here = (here + 5 + int.from_bytes(pe.d[o + 1:o + 5], "little", signed=True)) & 0xFFFFFFFF
        hops += 1
    return here if hops else None


def code(args):
    db = database(readonly=True)
    m = ADDRESS.match(args.name)
    if m:
        rva = int(m.group(2), 16)
        macos = m.group(1).lower().startswith("bg3_macos")
        if macos:
            # The decompiled macOS binary is the x86-64 half of the universal file. Addresses
            # taken from the symbol tables of the arm64 half (the coverage project's tables)
            # are different numbers for the same functions: translated through the names.
            absolute = rva if rva >= 0x100000000 else rva + 0x100000000
            rva = absolute - 0x100000000
            if not db.execute("SELECT 1 FROM functions f JOIN binaries b ON b.id = f.binary_id WHERE b.name LIKE ? AND f.rva = ?",
                              (m.group(1) + "%", rva)).fetchone():
                other = arm64_to_x64(absolute)
                if other is not None:
                    print(f"// 0x{absolute:X} is an address of the arm64 half; the same function in the decompiled x86-64 half is"
                          f" +0x{other:X}", file=sys.stderr)
                    rva = other
        for base in (0x180000000, 0x140000000):     # an absolute address, as in FUN_14189ae00
            if rva >= base and not macos:
                rva -= base
                break
        rows = db.execute("SELECT f.id, b.name, f.rva, f.name, f.signature, f.error FROM functions f JOIN binaries b ON b.id = f.binary_id"
                          " WHERE b.name LIKE ? AND f.rva = ?", (m.group(1) + "%", rva)).fetchall()
        if not rows:
            # a jump stub (5 bytes: jmp target): the function it leads to is what is wanted
            target = follow_jump(db, m.group(1), rva)
            if target is not None:
                print(f"// +0x{rva:X} is a jump to +0x{target:X}", file=sys.stderr)
                rva = target
                rows = db.execute("SELECT f.id, b.name, f.rva, f.name, f.signature, f.error FROM functions f JOIN binaries b ON b.id = f.binary_id"
                                  " WHERE b.name LIKE ? AND f.rva = ?", (m.group(1) + "%", rva)).fetchall()
        if not rows:
            inside = containing(db, m.group(1), rva)
            if inside:
                print(f"// {inside[0]}+0x{rva:X} is not a function start: {inside[2]}", file=sys.stderr)
                rows = db.execute("SELECT f.id, b.name, f.rva, f.name, f.signature, f.error FROM functions f JOIN binaries b ON b.id = f.binary_id"
                                  " WHERE b.name = ? AND f.rva = ?", (inside[0], inside[1])).fetchall()
    else:
        rows = db.execute("SELECT f.id, b.name, f.rva, f.name, f.signature, f.error FROM functions f JOIN binaries b ON b.id = f.binary_id"
                          " WHERE f.name = ? ORDER BY b.name LIMIT ?", (args.name, args.limit)).fetchall()
        if not rows:
            rows = db.execute("SELECT f.id, b.name, f.rva, f.name, f.signature, f.error FROM functions f JOIN binaries b ON b.id = f.binary_id"
                              " WHERE f.name LIKE ? ORDER BY length(f.name), b.name LIMIT ?", ("%" + args.name + "%", args.limit)).fetchall()
    if not rows:
        sys.exit("no such function")
    if len(rows) > 1 and not args.all:
        for fid, binary, rva, name, sig, error in rows:
            size = db.execute("SELECT size FROM functions WHERE id = ?", (fid,)).fetchone()[0]
            print(f"{binary}+0x{rva:X}  {size or 0:>6} bytes  {name}" + (f"   {sig}" if sig and len(sig) < 160 else ""))
        print(f"{len(rows)} functions match; pick one by its binary+0xRVA, or add --all to print them all", file=sys.stderr)
        return
    for fid, binary, rva, name, sig, error in rows:
        print(f"// {binary}+0x{rva:X}  {name}")
        blob = db.execute("SELECT text FROM code WHERE function_id = ?", (fid,)).fetchone()
        if not blob:
            print(f"// not decompiled: {error}")
            continue
        text = zlib.decompress(blob[0]).decode("utf-8")
        text = text if args.plain else name_offset_globals(name_fixed_strings(hint_unnamed(annotate(text, name), binary), binary), binary)
        print(excerpt(text, args.grep, args.context) if args.grep else text)


def excerpt(text, needle, context):
    """Lines of a listing that contain the text (case does not matter), with line numbers and
    `context` lines around each; `...` between pieces."""
    lines = text.splitlines()
    wanted = [w.lower() for w in needle.split("|") if w]         # a|b: either text
    keep = set()
    for i, line in enumerate(lines):
        if any(w in line.lower() for w in wanted):
            keep.update(range(max(0, i - context), min(len(lines), i + context + 1)))
    out, last = [], None
    for i in sorted(keep):
        if last is not None and i != last + 1:
            out.append("   ...")
        out.append(f"{i + 1:5d}  {lines[i]}")
        last = i
    return "\n".join(out) if out else f"// no line contains {needle!r} ({len(lines)} lines)"


def substring_query(db, text, per_word=400):
    """An FTS5 query that finds the words as parts of longer names. The index holds whole tokens
    (GroundMaterialComponent is one token), so every token that contains a word is looked up in
    the index's vocabulary first. Words of fewer than three characters match whole tokens only."""
    db.execute("CREATE VIRTUAL TABLE IF NOT EXISTS temp.code_vocab USING fts5vocab(main, code_fts, row)")
    groups = []
    for word in re.findall(r"[0-9A-Za-z_]+", text):
        low = word.lower()
        terms = [low] if len(low) < 3 else [r[0] for r in db.execute(
            "SELECT term FROM temp.code_vocab WHERE term LIKE ? ESCAPE '!' ORDER BY doc DESC LIMIT ?",
            ("%" + low.replace("!", "!!").replace("_", "!_").replace("%", "!%") + "%", per_word))]
        if not terms:
            return None
        groups.append("(" + " OR ".join('"' + t.replace('"', '""') + '"' for t in terms) + ")")
    return " AND ".join(groups) if groups else None


def grep(args):
    db = database(readonly=True)
    if args.raw:
        query = args.text
    elif args.word:
        query = " ".join('"' + w.replace('"', '""') + '"' for w in args.text.split())
    else:
        query = substring_query(db, args.text)
        if query is None:
            print("nothing found", file=sys.stderr)
            return
    rows = db.execute("SELECT f.id, b.name, f.rva, f.name FROM code_fts JOIN functions f ON f.id = code_fts.rowid"
                      " JOIN binaries b ON b.id = f.binary_id WHERE code_fts MATCH ? AND (? IS NULL OR b.name LIKE ?) LIMIT ?",
                      (query, args.binary, (args.binary or "") + "%", args.limit)).fetchall()
    words = [w.lower() for w in re.findall(r"\w+", args.text)]
    for fid, binary, rva, name in rows:
        print(f"{binary}+0x{rva:X}  {name}")
        if args.lines:
            blob = db.execute("SELECT text FROM code WHERE function_id = ?", (fid,)).fetchone()
            shown = 0
            for line in zlib.decompress(blob[0]).decode("utf-8").splitlines() if blob else ():
                if any(w in line.lower() for w in words):
                    print("    " + line.strip()[:200])
                    shown += 1
                    if shown >= args.lines:
                        break
    if not rows:
        print("nothing found", file=sys.stderr)


SYSTEM_INDEX = r"TypeIndex<([^;]+?)>_SystemsContext"
VTABLE_SET = re.compile(r"\*\s*(?:\([^()]*\))?\s*(\w+) = &?(PTR_\w+_|PTR_\w+|\w+_vftable_?);")
TABLE_ADDRESS = re.compile(r"PTR_\w+?_(1[0-9a-f]{8})$")


def find_system_factories(db, binary):
    """Reads from the pseudo-code where each ECS system is created. The game adds a system to its
    world with one helper function that takes the system's type index; that helper is recognised
    as the function most often called with a `TypeIndex<...>_SystemsContext` argument. The
    function that makes the call is the factory; the constructor is the function it calls before
    that which stores a virtual table into its first argument. All of it is read from text
    Ghidra produced, so every row is an inference to check, not a fact."""
    binary_id = db.execute("SELECT id FROM binaries WHERE name = ?", (binary,)).fetchone()
    if not binary_id:
        sys.exit(f"{binary} is not loaded")
    binary_id = binary_id[0]

    def text_of(rva):
        row = db.execute("SELECT c.text FROM functions f JOIN code c ON c.function_id = f.id WHERE f.binary_id = ? AND f.rva = ?",
                         (binary_id, rva)).fetchone()
        return " ".join(zlib.decompress(row[0]).decode("utf-8").split()) if row else ""

    # the helper: among functions called from many places, the one whose callers pass a system index
    votes = {}
    sample = db.execute("SELECT f.rva FROM code_fts JOIN functions f ON f.id = code_fts.rowid WHERE code_fts MATCH ? AND f.binary_id = ?",
                        ('"_SystemsContext"', binary_id)).fetchall()
    use = re.compile(r"(\w+) = " + SYSTEM_INDEX + r";")
    for (rva,) in sample:
        flat = text_of(rva)
        for m in use.finditer(flat):
            for call in re.finditer(r"\b(FUN_1[0-9a-f]{8})\(([^;]*?)\);", flat[m.end():m.end() + 6000]):
                arguments = [a.strip() for a in call.group(2).split(",")]
                if len(arguments) == 3 and arguments[1] == m.group(1):
                    votes[call.group(1)] = votes.get(call.group(1), 0) + 1
    if not votes:
        return 0
    helper = max(votes, key=votes.get)
    helper_rva = int(helper[4:], 16) - 0x140000000
    call = re.compile(re.escape(helper) + r"\(([^,;]+),\s*([^,;]+),")
    updates = []
    rows, stored = [], {}                  # stored: callee -> virtual tables it writes into its first argument
    for (factory,) in db.execute("SELECT DISTINCT caller FROM calls INDEXED BY calls_callee WHERE binary_id = ? AND callee = ?", (binary_id, helper_rva)).fetchall():
        flat = text_of(factory)
        found = list(call.finditer(flat))
        for m in found:
            variable = m.group(2).strip()
            direct = re.match(SYSTEM_INDEX + "$", variable)
            assigned = list(re.finditer(re.escape(variable) + " = " + SYSTEM_INDEX + ";", flat[:m.start()]))
            system = direct.group(1) if direct else assigned[-1].group(1) if assigned else None
            if not system:
                continue
            constructor = vtable = None
            note = "" if len(found) == 1 else f"one of {len(found)} systems created in this function; constructor and table not attributed"
            if len(found) == 1:
                own = [t for v, t in VTABLE_SET.findall(flat[:m.start()])]
                for (callee,) in db.execute("SELECT DISTINCT callee FROM calls INDEXED BY calls_caller WHERE binary_id = ? AND caller = ?", (binary_id, factory)).fetchall():
                    where = flat.find("FUN_14%07x(" % callee)
                    if where < 0 or where > m.start():
                        continue
                    if callee not in stored:
                        body = text_of(callee)
                        stored[callee] = [t for v, t in VTABLE_SET.findall(body) if v == "param_1"] if "param_1 = &" in body else []
                    tables = stored[callee]
                    if tables:
                        constructor, own = callee, tables
                if own:
                    address = TABLE_ADDRESS.search(own[-1].rstrip("_"))
                    vtable = int(address.group(1), 16) - 0x140000000 if address else None
                    note = ("constructor inlined into the factory" if constructor is None else "") + ("" if address else f" table named {own[-1]}")
            rows.append((binary_id, system, factory, constructor, vtable, note.strip()))
            if len(found) == 1:
                # Function pointers the factory stores: the registration helper's update entry
                # for this system. It builds the views and calls the system's own update, unless
                # the compiler folded that update into it.
                for pointer in dict.fromkeys(re.findall(r"= (?:\([^()]*\))?(?:thunk_)?FUN_(1[0-9a-f]{8});", flat)):
                    thunk = int(pointer, 16) - 0x140000000
                    body = text_of(thunk)
                    if not body:
                        continue
                    head = body[:body.find("{")] if "{" in body else body
                    if head.count("param_") not in (2, 3) or not re.search(r"\bvoid\s+(?:thunk_)?FUN_", head):
                        continue                 # an update entry is void (system, world[, time])
                    calls = [int(c, 16) - 0x140000000 for c in re.findall(r"\b(?:thunk_)?FUN_(1[0-9a-f]{8})\(param_1,", body)
                             if int(c, 16) - 0x140000000 != thunk]
                    # callees that already carry a name (from the Toolkit) are looked up by it
                    for named in re.findall(r"(?<![\w:])((?:\w+::)+~?\w+)\(param_1,", body):
                        hit = db.execute("SELECT rva FROM functions WHERE binary_id = ? AND name = ? LIMIT 2", (binary_id, named)).fetchall()
                        if len(hit) == 1 and hit[0][0] != thunk:
                            calls.append(hit[0][0])
                    calls = list(dict.fromkeys(calls))
                    size = db.execute("SELECT size FROM functions WHERE binary_id = ? AND rva = ?", (binary_id, thunk)).fetchone()
                    small = bool(size and size[0] and size[0] <= 160)
                    if small and len(calls) == 1:
                        updates.append((binary_id, system, thunk, calls[0], "the entry builds the views and calls the update"))
                    elif small and len(calls) > 1:
                        updates += [(binary_id, system, thunk, c, f"step {i + 1} of {len(calls)} the entry calls on the system")
                                    for i, c in enumerate(calls)]
                    elif not small:
                        updates.append((binary_id, system, thunk, thunk, "the update is folded into the entry"))
                    else:
                        updates.append((binary_id, system, thunk, None, "small entry with no call on the system found: look at it"))
    db.execute("DELETE FROM system_updates WHERE binary_id = ?", (binary_id,))
    db.executemany("INSERT INTO system_updates VALUES (?,?,?,?,?)", updates)
    db.execute("DELETE FROM system_factories WHERE binary_id = ?", (binary_id,))
    db.executemany("INSERT INTO system_factories VALUES (?,?,?,?,?,?)", rows)
    db.commit()
    return len(rows)


def systems(args):
    db = database()
    if args.rebuild or not db.execute("SELECT 1 FROM system_factories LIMIT 1").fetchone():
        count = find_system_factories(db, args.binary)
        print(f"{count} systems found in {args.binary}", file=sys.stderr)
    rows = db.execute("SELECT b.name, s.system, s.factory, s.constructor, s.vtable, s.note FROM system_factories s JOIN binaries b ON b.id = s.binary_id"
                      " WHERE s.system LIKE ? ORDER BY s.system", ("%" + (args.name or "") + "%",)).fetchall()
    for binary, system, factory, constructor, vtable, note in rows:
        print(f"{system}\n    created in {binary}+0x{factory:X}"
              + (f", constructor {binary}+0x{constructor:X}" if constructor else "")
              + (f", virtual table {binary}+0x{vtable:X}" if vtable else "") + (f"   ({note})" if note else ""))
        for thunk, updater, why in db.execute("SELECT u.thunk, u.updater, u.note FROM system_updates u JOIN binaries b ON b.id = u.binary_id"
                                              " WHERE b.name = ? AND u.system = ?", (binary, system)):
            print(f"    per-frame update: " + (f"{binary}+0x{updater:X}" if updater is not None else "?")
                  + f"   (entry stored by the factory: {binary}+0x{thunk:X}; {why})")
    if not rows:
        print("nothing found", file=sys.stderr)


def match(args):
    """Pairs Toolkit functions with game functions by pseudo-code and stores the pairs."""
    import codematch
    db = database()
    path = (WORK / "decomp.sqlite").as_posix()
    log = lambda text: print(text, file=sys.stderr)
    started = time.time()
    feats = codematch.extract(path, WORK / "features.pickle", args.jobs, log)
    game = db.execute("SELECT id FROM binaries WHERE name = ?", (args.binary,)).fetchone()
    if not game:
        sys.exit(f"{args.binary} is not loaded")
    # Functions under 64 bytes are left out: a few numbers do not tell one small accessor from another.
    info = {fid: (b, rva) for fid, b, rva in db.execute("SELECT id, binary_id, rva FROM functions WHERE size >= 64")}
    names = dict(db.execute("SELECT id, name FROM binaries"))
    executables = {"bg3.exe", "bg3_dx11.exe"}
    game_ids = [f for f in feats if info.get(f, (None,))[0] == game[0]]
    if args.source:
        # one named build of the game itself (the macOS executable) as the source of names
        source = db.execute("SELECT id FROM binaries WHERE name = ?", (args.source,)).fetchone()
        if not source:
            sys.exit(f"{args.source} is not loaded")
        sources = {source[0]}
    else:
        sources = {b for b, n in names.items() if n not in executables and n.lower().endswith(".dll")}
    toolkit_ids = [f for f in feats if f in info and info[f][0] in sources]
    log(f"features of {len(toolkit_ids)} source and {len(game_ids)} game functions, {time.time() - started:.0f} s")
    pairs = codematch.match(feats, toolkit_ids, game_ids, 0.10, 0.05, 0.0, log=log)
    db.execute("DELETE FROM code_matches WHERE game_binary = ? AND toolkit_binary IN (%s)" % ",".join("?" * len(sources)),
               (game[0], *sources))
    db.executemany("INSERT INTO code_matches VALUES (?,?,?,?,?,?)",
                   [(info[t][0], info[t][1], game[0], info[g][1], round(score, 4), round(margin, 4)) for t, g, score, margin in pairs])
    db.commit()
    shown = [p for p in pairs if p[2] >= MIN_SCORE and p[3] >= MIN_MARGIN]
    print(f"{len(pairs)} pairs stored (score >= 0.10, margin >= 0.05); {len(shown)} pass the default thresholds"
          f" (score >= {MIN_SCORE}, margin >= {MIN_MARGIN}); {time.time() - started:.0f} s")
    symbols = HERE.parent / "symbols.sqlite"
    if not symbols.exists():
        return
    if args.source:
        return check_by_name(db, symbols, args, pairs, info)
    # The check: pairs symbols.py found through text constants alone. Pairs it found along calls
    # and virtual tables may descend from pseudo-code seeds, so they are not used here.
    sym = sqlite3.connect(f"file:{symbols.as_posix()}?mode=ro", uri=True)
    known, taken = {}, set()
    for dll, toolkit_rva, game_rva, relation in sym.execute(
            "SELECT d.name, m.toolkit_rva, m.game_rva, m.relation FROM matches m JOIN dlls d ON d.id = m.toolkit_dll"
            " JOIN dlls g ON g.id = m.game_dll WHERE g.name = ? AND m.method = 'strings'", (args.binary,)):
        taken.add(game_rva)
        if relation == "same":
            known[(dll, toolkit_rva)] = game_rva
    named = {fid for fid, in db.execute("SELECT id FROM functions WHERE name NOT LIKE 'FUN!_%' ESCAPE '!' AND name NOT LIKE 'thunk!_FUN!_%' ESCAPE '!'")}
    print("check against the pairs symbols.py found by text constants, and what is new to those:")
    for min_score, min_margin in ((0.10, 0.05), (0.15, 0.10), (0.20, 0.15), (0.30, 0.20), (0.50, 0.20)):
        right = wrong = fresh = fresh_named = total = 0
        for t, g, score, margin in pairs:
            if score < min_score or margin < min_margin:
                continue
            total += 1
            expected = known.get((names[info[t][0]], info[t][1]))
            if expected is not None:
                right += expected == info[g][1]
                wrong += expected != info[g][1]
            if info[g][1] not in taken:
                fresh += 1
                fresh_named += t in named
        print(f"  score >= {min_score:.2f}, margin >= {min_margin:.2f}: {total} pairs; of the known ones {right} agree, {wrong} differ"
              f" ({100 * right / max(1, right + wrong):.1f}%); game functions without a pair before: {fresh}, {fresh_named} of them get a name")


def unqualified(name):
    """Last component of a qualified C++ name, without template arguments and parameters."""
    depth, out = 0, []
    for ch in name:
        if ch == "<":
            depth += 1
        elif ch == ">":
            depth -= 1
        elif depth == 0:
            out.append(ch)
    return "".join(out).split("(")[0].strip().split(" ")[-1]


def check_by_name(db, symbols, args, pairs, info):
    """For a source that names its functions itself: a game function that the Toolkit already
    named (through text constants, calls or virtual tables - not pseudo-code) must get the same
    name from the source."""
    sym = sqlite3.connect(f"file:{symbols.as_posix()}?mode=ro", uri=True)
    known = {}
    for rva, name in sym.execute(
            "SELECT m.game_rva, min(s.full_name) FROM matches m JOIN symbols s ON s.dll_id = m.toolkit_dll AND s.rva = m.toolkit_rva"
            " JOIN dlls g ON g.id = m.game_dll WHERE g.name = ? AND m.relation = 'same' AND m.method IN ('strings', 'vtable', 'calls', 'vtable user')"
            " GROUP BY m.game_rva", (args.binary,)):
        known[rva] = unqualified(name)
    source_name = dict(db.execute("SELECT f.id, f.name FROM functions f JOIN binaries b ON b.id = f.binary_id WHERE b.name = ?", (args.source,)))
    print(f"check against {len(known)} game functions the Toolkit names (same class and method name counts as agreement):")
    for min_score, min_margin in ((0.10, 0.05), (0.15, 0.10), (0.20, 0.15), (0.30, 0.20), (0.50, 0.20), (0.70, 0.30)):
        total = same = method = differ = 0
        for t, g, score, margin in pairs:
            if score < min_score or margin < min_margin:
                continue
            total += 1
            expected = known.get(info[g][1])
            if expected is None:
                continue
            got = unqualified(source_name.get(t, ""))
            if got == expected:
                same += 1
            elif got.split("::")[-1] == expected.split("::")[-1]:
                method += 1
            else:
                differ += 1
        checked = same + method + differ
        print(f"  score >= {min_score:.2f}, margin >= {min_margin:.2f}: {total} pairs; of {checked} checked {same} same name,"
              f" {method} same method name in another class, {differ} differ ({100 * (same + method) / max(1, checked):.1f}% agree)")


def matches(args):
    """Lists the pairs found by pseudo-code for Toolkit functions whose name contains the text,
    or for one game address."""
    db = database(readonly=True)
    m = ADDRESS.match(args.text)
    where, params = ("m.game_rva = ?", (int(m.group(2), 16),)) if m else ("f.name LIKE ?", ("%" + args.text + "%",))
    rows = db.execute(
        "SELECT m.score, m.margin, g.name, m.game_rva, t.name, m.toolkit_rva, f.name, f.size, gf.size FROM code_matches m"
        " JOIN binaries g ON g.id = m.game_binary JOIN binaries t ON t.id = m.toolkit_binary"
        " JOIN functions f ON f.binary_id = m.toolkit_binary AND f.rva = m.toolkit_rva"
        " LEFT JOIN functions gf ON gf.binary_id = m.game_binary AND gf.rva = m.game_rva"
        f" WHERE {where} AND m.score >= ? AND m.margin >= ? ORDER BY m.score DESC LIMIT ?",
        (*params, 0.0 if args.all else MIN_SCORE, 0.0 if args.all else MIN_MARGIN, args.limit)).fetchall()
    for score, margin, game, game_rva, toolkit, toolkit_rva, name, size, game_size in rows:
        print(f"{score:.2f} +{margin:.2f}  {game}+0x{game_rva:X} ({game_size} b)  ~  {toolkit}+0x{toolkit_rva:X} ({size} b)  {name}")
    if not rows:
        print("nothing found" + ("" if args.all else " (--all also shows pairs below the default thresholds)"), file=sys.stderr)


ENTRY_LINK = re.compile(r'(_?DAT_1[0-9a-f]{8}) = &(DAT_1[0-9a-f]{8}); \2 = "((?:[^"\\]|\\.)*)";')
VALUE_OFFSET = 0x18


def image_base(binary):
    """Where Ghidra loads the binary: the game executables at 0x140000000, the DLLs at 0x180000000."""
    return 0x140000000 if binary.lower().endswith(".exe") else 0x180000000


def read_text(binary, rva, _cache={}):
    """The zero-terminated ASCII text at an address of a binary's image, or None."""
    if binary not in _cache:
        sys.path.insert(0, str(HERE.parent))
        from pe import PE
        path = locate(binary)
        _cache[binary] = PE(path.read_bytes())
    pe = _cache[binary]
    o = pe.off(rva)
    if o is None:
        return None
    end = pe.d.find(b"\0", o, o + 256)
    raw = pe.d[o:end] if end > o else b""
    return raw.decode("ascii") if raw and all(32 <= c < 127 for c in raw) else None


def find_fixed_strings(db, binary):
    """Reads the registrations of global FixedStrings out of the pseudo-code. Every string literal
    used as a FixedString has a small function that fills a registry entry on first use:

        entry.length = N;  entry.next = head;  head = &entry;  entry.text = "Literal";

    and usually stores the address of the entry's value (entry + 0x18, a FixedString the registry
    fills when the game starts) into one more global. The list head is the global most often
    written in that pattern. Returns the number of strings found."""
    binary_id = db.execute("SELECT id FROM binaries WHERE name = ?", (binary,)).fetchone()
    if not binary_id:
        sys.exit(f"{binary} is not loaded")
    binary_id = binary_id[0]
    base = image_base(binary)
    found, heads = [], {}
    for fid, rva in db.execute("SELECT f.id, f.rva FROM code_fts JOIN functions f ON f.id = code_fts.rowid"
                               " WHERE code_fts MATCH ? AND f.binary_id = ?", ('"_Init_thread_header"', binary_id)).fetchall():
        blob = db.execute("SELECT text FROM code WHERE function_id = ?", (fid,)).fetchone()
        if not blob:
            continue
        flat = " ".join(zlib.decompress(blob[0]).decode("utf-8").split())
        for m in ENTRY_LINK.finditer(flat):
            head, entry, text = m.group(1).lstrip("_"), int(m.group(2)[4:], 16) - base, m.group(3)
            heads[head] = heads.get(head, 0) + 1
            value = entry + VALUE_OFFSET
            pointers = [int(x, 16) - base for x in
                        re.findall(r"_?DAT_(1[0-9a-f]{8}) = &DAT_%x;" % (value + base), flat)]
            found.append((head, rva, entry, text, pointers))
    if not heads:
        return 0
    head = max(heads, key=heads.get)
    # Second pass, now that the list head is known: every function that writes to it, with the
    # text assignment anywhere in the function (the compiler does not always keep the order).
    found = []
    link = re.compile(r"_?%s = &DAT_(1[0-9a-f]{8});" % head)
    for fid, rva in db.execute("SELECT f.id, f.rva FROM code_fts JOIN functions f ON f.id = code_fts.rowid"
                               " WHERE code_fts MATCH ? AND f.binary_id = ?", ('"%s"' % head, binary_id)).fetchall():
        blob = db.execute("SELECT text FROM code WHERE function_id = ?", (fid,)).fetchone()
        if not blob:
            continue
        flat = " ".join(zlib.decompress(blob[0]).decode("utf-8").split())
        for m in link.finditer(flat):
            literal = re.search(r'_?DAT_%s = "((?:[^"\\]|\\.)*)";' % m.group(1), flat)
            if not literal:
                # a short text Ghidra did not recognise as a string: read it from the file
                raw = re.search(r"_?DAT_%s = (?:\([^()]*\))?&DAT_(1[0-9a-f]{8});" % m.group(1), flat)
                text = read_text(binary, int(raw.group(1), 16) - base) if raw else None
                if text is None:
                    continue                      # the text comes in as an argument: not resolved here
                literal = re.match("(.*)", text, re.S)
            entry = int(m.group(1), 16) - base
            pointers = [int(x, 16) - base for x in
                        re.findall(r"_?DAT_(1[0-9a-f]{8}) = &DAT_%x;" % (entry + VALUE_OFFSET + base), flat)]
            found.append((head, rva, entry, literal.group(1), pointers))
    rows = []
    for h, rva, entry, text, pointers in found:
        if h != head:
            continue
        rows.append((binary_id, entry, "entry", text, rva))
        rows.append((binary_id, entry + VALUE_OFFSET, "value", text, rva))
        rows += [(binary_id, pointer, "pointer", text, rva) for pointer in pointers]
    db.execute("DELETE FROM fixed_strings WHERE binary_id = ?", (binary_id,))
    db.executemany("INSERT INTO fixed_strings VALUES (?,?,?,?,?)", rows)
    db.commit()
    return len({(r[1]) for r in rows if r[2] == "entry"})


def fixedstrings(args):
    db = database()
    if args.rebuild or not db.execute("SELECT 1 FROM fixed_strings LIMIT 1").fetchone():
        count = find_fixed_strings(db, args.binary)
        print(f"{count} global FixedStrings found in {args.binary}", file=sys.stderr)
    m = ADDRESS.match(args.text or "")
    if m:
        rva = int(m.group(2), 16)
        for base in (0x180000000, 0x140000000):
            if rva >= base:
                rva -= base
                break
        rows = db.execute("SELECT b.name, s.rva, s.kind, s.text, s.registered_in FROM fixed_strings s JOIN binaries b ON b.id = s.binary_id"
                          " WHERE b.name LIKE ? AND s.rva = ?", (m.group(1) + "%", rva)).fetchall()
    else:
        rows = db.execute("SELECT b.name, s.rva, s.kind, s.text, s.registered_in FROM fixed_strings s JOIN binaries b ON b.id = s.binary_id"
                          " WHERE s.text LIKE ? ORDER BY s.text, s.kind DESC LIMIT ?", ("%" + (args.text or "") + "%", args.limit)).fetchall()
    for binary, rva, kind, text, registered in rows:
        print(f"{binary}+0x{rva:X}  {kind:<7}  \"{text}\"   (registered in {binary}+0x{registered:X})")
    if not rows:
        print("nothing found", file=sys.stderr)


OFFSET_GLOBAL = re.compile(r"\(_?DAT_(1[0-9a-f]{8}) \+ |\+ _?DAT_(1[0-9a-f]{8})\)")


def read_value(binary, rva):
    """The 8 bytes stored in the file at an address of the image, as an integer; None when the
    address has no bytes in the file (zero-filled data)."""
    read_text(binary, rva)                              # loads and caches the image
    pe = read_text.__defaults__[0][binary]
    o = pe.off(rva)
    if o is None or o + 8 > len(pe.d):
        return None
    return int.from_bytes(pe.d[o:o + 8], "little")


def name_offset_globals(text, binary):
    """Writes the stored value after a global that is added to a pointer, when the pseudo-code of
    the whole binary never assigns it: such a global is a member offset the compiler kept as
    data (`Serialize(param_2, (T *)(DAT_x + object))` reads the member at that offset)."""
    wanted = []
    for m in OFFSET_GLOBAL.finditer(text):
        address = m.group(1) or m.group(2)
        if address not in wanted:
            wanted.append(address)
    if not wanted or len(wanted) > 16:
        return text
    path = WORK / "decomp.sqlite"
    try:
        db = sqlite3.connect(f"file:{path.as_posix()}?mode=ro", uri=True)
        binary_id = db.execute("SELECT id FROM binaries WHERE name = ?", (binary,)).fetchone()[0]
    except (sqlite3.Error, TypeError):
        return text
    base = image_base(binary)
    for address in wanted:
        token = "DAT_" + address
        assigned = False
        for (blob,) in db.execute("SELECT c.text FROM code_fts JOIN functions f ON f.id = code_fts.rowid JOIN code c ON c.function_id = f.id"
                                  " WHERE code_fts MATCH ? AND f.binary_id = ? LIMIT 200", (f'"{token}" OR "_{token}"', binary_id)):
            if re.search(r"_?%s\s*(=(?!=)|\+=|-=|\+\+|--)" % token, zlib.decompress(blob).decode("utf-8")):
                assigned = True
                break
        if assigned:
            continue
        try:
            value = read_value(binary, int(address, 16) - base)
        except (OSError, SystemExit):
            return text
        if value is None:
            note = "/* never assigned; zero-filled data, so 0 */"
        else:
            low = value & 0xFFFFFFFF
            note = f"/* never assigned; in the file: 0x{low:X}" + (f" (8 bytes: 0x{value:X})" if value >> 32 else "") + " */"
        text = re.sub(r"(_?%s)\b(?! /\*)" % token, lambda m: m.group(1) + " " + note, text, count=1)
    return text


def name_fixed_strings(text, binary, _cache={}):
    """Adds the text of a global FixedString after the first mention of its global in a listing."""
    if binary not in _cache:
        path = WORK / "decomp.sqlite"
        table = {}
        if path.exists():
            try:
                db = sqlite3.connect(f"file:{path.as_posix()}?mode=ro", uri=True)
                table = {rva: (kind, value) for rva, kind, value in db.execute(
                    "SELECT s.rva, s.kind, s.text FROM fixed_strings s JOIN binaries b ON b.id = s.binary_id WHERE b.name = ?", (binary,))}
            except sqlite3.Error:
                pass
        _cache[binary] = table
    table = _cache[binary]
    if not table:
        return text
    seen = set()

    def replace(m):
        rva = int(m.group(1), 16) - image_base(binary)
        hit = table.get(rva)
        if not hit or rva in seen:
            return m.group(0)
        seen.add(rva)
        what = {"value": "FixedString", "pointer": "-> FixedString", "entry": "FixedString entry"}[hit[0]]
        return m.group(0) + f" /* {what} \"{hit[1]}\" */"

    return re.sub(r"\bDAT_(1[0-9a-f]{8})\b", replace, text)


def status(args):
    for folder in sorted(WORK.glob("*")):
        if not folder.is_dir():
            continue
        out = folder / "functions.jsonl"
        last = folder / "last_run.json"
        size = out.stat().st_size if out.exists() else 0
        info = json.loads(last.read_text(encoding="utf-8")) if last.exists() else {}
        print(f"{folder.name}: output {size / 1e6:.0f} MB" + (f", last run {info.get('seconds')} s, exit {info.get('exit')}, finished {info.get('finished')}" if info else ", running or never finished"))
    path = WORK / "decomp.sqlite"
    if path.exists():
        db = sqlite3.connect(path)
        for name, loaded, functions, failed in db.execute("SELECT name, loaded, functions, failed FROM binaries"):
            print(f"  in database: {name}: {functions} functions, {failed} without pseudo-code (loaded {loaded})")
        print(f"  {path}: {path.stat().st_size / 1e6:.0f} MB")


def main():
    for stream in (sys.stdout, sys.stderr):
        stream.reconfigure(encoding="utf-8", errors="replace")
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="command", required=True)
    x = sub.add_parser("run", help="analyse and decompile a binary with Ghidra")
    x.add_argument("binary", help="bg3.exe, bg3_dx11.exe, a Toolkit DLL name, or a path")
    x.add_argument("--memory", default="20G", help="Java heap for Ghidra (default 20G)")
    x.add_argument("--cpu", type=int, default=max(1, (os.cpu_count() or 4) - 2), help="processors Ghidra may use")
    x.add_argument("--range", help="only functions in this range of RVAs, FIRST-LAST in hex (to split or resume)")
    x.add_argument("--fresh", action="store_true", help="import and analyse again even if a project exists")
    x.set_defaults(run=run)
    x = sub.add_parser("load", help="load finished output into decomp.sqlite")
    x.add_argument("binary", nargs="?")
    x.set_defaults(run=load)
    x = sub.add_parser("code", help="pseudo-code of a function by name or binary+0xRVA")
    x.add_argument("name")
    x.add_argument("--all", action="store_true")
    x.add_argument("--plain", action="store_true", help="print the pseudo-code as Ghidra wrote it, without field names and hints")
    x.add_argument("--grep", metavar="TEXT", help="print only the lines that contain the text (a|b: either of the texts), with line numbers")
    x.add_argument("-C", "--context", type=int, default=3, help="lines around each line found by --grep (default 3)")
    x.add_argument("--limit", type=int, default=40)
    x.set_defaults(run=code)
    x = sub.add_parser("grep", help="full-text search over names and pseudo-code")
    x.add_argument("text")
    x.add_argument("--binary")
    x.add_argument("--lines", type=int, default=3, help="matching lines to show per function (0 for none)")
    x.add_argument("--raw", action="store_true", help="pass the text to SQLite FTS5 as it is")
    x.add_argument("--word", action="store_true", help="whole tokens only (faster); by default a word also matches inside longer names")
    x.add_argument("--limit", type=int, default=50)
    x.set_defaults(run=grep)
    x = sub.add_parser("systems", help="where the game creates each ECS system: factory, constructor, virtual table")
    x.add_argument("name", nargs="?", help="part of a system name")
    x.add_argument("--rebuild", action="store_true", help="read the pseudo-code again")
    x.add_argument("--binary", default="bg3.exe")
    x.set_defaults(run=systems)
    x = sub.add_parser("match", help="pair Toolkit functions with game functions by their pseudo-code")
    x.add_argument("--binary", default="bg3.exe")
    x.add_argument("--jobs", type=int, default=max(1, (os.cpu_count() or 4) - 2))
    x.add_argument("--source", help="take names from this one loaded binary (the macOS build) instead of the Toolkit DLLs")
    x.set_defaults(run=match)
    x = sub.add_parser("matches", help="pairs found by pseudo-code: part of a Toolkit function name, or bg3.exe+0xRVA")
    x.add_argument("text")
    x.add_argument("--all", action="store_true", help="also pairs below the default thresholds")
    x.add_argument("--limit", type=int, default=60)
    x.set_defaults(run=matches)
    x = sub.add_parser("fixedstrings", help="global FixedStrings of the game: by part of the text, or by address of the global")
    x.add_argument("text", nargs="?", help="part of the text, or bg3.exe+0xRVA of the global")
    x.add_argument("--rebuild", action="store_true", help="read the pseudo-code again")
    x.add_argument("--binary", default="bg3.exe")
    x.add_argument("--limit", type=int, default=60)
    x.set_defaults(run=fixedstrings)
    x = sub.add_parser("vtable", help="slots of the virtual table at binary+0xRVA (the same as symbols.py vtable)")
    x.add_argument("name")
    x.set_defaults(run=lambda a: subprocess.call([sys.executable, str(SYMBOLS), "vtable", a.name]))
    x = sub.add_parser("dotnet", help="decompile the Toolkit's managed assemblies (the editor) to C#")
    x.add_argument("--dotnet", default=os.environ.get("DOTNET_ROOT"), help="folder of a .NET 8 runtime (dotnet.exe inside)")
    x.add_argument("--only", help="only assemblies whose name contains this")
    x.add_argument("--timeout", type=int, default=900, help="seconds to give one assembly (default 900)")
    x.add_argument("--fresh", action="store_true", help="decompile again")
    x.set_defaults(run=dotnet)
    x = sub.add_parser("csharp", help="search the editor's decompiled C#, or print a file with --file")
    x.add_argument("text")
    x.add_argument("--file", action="store_true", help="the text is Assembly/path.cs: print that file")
    x.add_argument("--lines", type=int, default=3)
    x.add_argument("--raw", action="store_true")
    x.add_argument("--limit", type=int, default=40)
    x.set_defaults(run=csharp)
    sub.add_parser("status", help="what has been decompiled and loaded").set_defaults(run=status)
    args = p.parse_args()
    sys.exit(args.run(args) or 0)


if __name__ == "__main__":
    main()
