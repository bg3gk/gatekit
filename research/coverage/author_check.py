"""Live check of a suspected error in one of Script Extender's own (the author's) layouts.

    python research/coverage/author_check.py <side> <engine name> <ExtName> <Field>[,<Field>...] [--max N]

For up to N holders of the component it prints the raw bytes (16 per line, with offsets) and
what Script Extender returns for the named fields of the same instance, read in the same call.
The reader then sees with their own eyes whether a field's value is where the suspicion says it
is. Nothing is changed in the game or in the fork.

Used for the list "Suspect layouts of the author's" in research/se-fork/TODO.md: a suspicion found in
code is not acted on until a run of this agrees (the user's rule: confirm live before renaming
or re-laying anything of the author's).
"""
import json
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import probe  # noqa: E402


def check(game, side: str, name: str, ext_name: str, fields: list[str], most: int = 3) -> list[dict]:
    lua = f"Probe.Mapped({json.dumps(name)}, {json.dumps(ext_name)}, {probe.lua_value(fields)}, {most})"
    return probe.call(game, side, lua, timeout=60) or []


def render(rows: list[dict]) -> str:
    out = []
    for row in rows:
        out.append(f"entity {row['Entity']}  present through Script Extender: {row['Present']}")
        raw = bytes.fromhex(row.get("Hex") or "")
        for offset in range(0, len(raw), 16):
            out.append(f"   +0x{offset:03X}  {raw[offset:offset + 16].hex(' ')}")
        for field, value in (row.get("Values") or {}).items():
            out.append(f"   {field} = {json.dumps(value, ensure_ascii=False)}")
    return "\n".join(out)


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    args = sys.argv[1:]
    most = 3
    if "--max" in args:
        most = int(args[args.index("--max") + 1])
        del args[args.index("--max"):args.index("--max") + 2]
    if len(args) < 4:
        print(__doc__)
    else:
        game = probe.attach()
        print(render(check(game, args[0], args[1], args[2], args[3].split(","), most)))
