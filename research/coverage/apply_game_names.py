"""Gives Script Extender's component fields the names the game itself uses.

Source of the names: research/coverage/work/fieldmeta-check.json (the coverage session's
comparison of Script Extender's layouts with the game's own replicated-field names, read from
the macOS build's `ls::FieldMeta<..., &Class::m_Name>` symbols). Every field whose verdict is
"different name" is renamed in the headers; the old name stays usable from Lua through
Script Extender's own `[[bg3::legacy(OldName)]]` attribute, the way its author renames fields.

    python research/coverage/apply_game_names.py <tree>            report only
    python research/coverage/apply_game_names.py <tree> --apply    edit the headers

<tree> is the source tree, e.g. se-fork/bg3se-v34. C++ code that uses a renamed member has to
be fixed by hand afterwards (the compiler lists the places).
"""
import json
import re
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
CHECK = HERE / "work" / "fieldmeta-check.json"


def find_struct(text: str, engine_name: str):
    """(start, end) of the body of the struct that declares the component `engine_name`."""
    marker = re.search(r'DEFINE_(?:\w+_)?COMPONENT\w*\([^)]*"' + re.escape(engine_name) + r'"\)', text)
    if not marker:
        return None
    start = text.rfind("{", 0, marker.start())
    depth, pos = 0, start
    while pos < len(text):
        if text[pos] == "{":
            depth += 1
        elif text[pos] == "}":
            depth -= 1
            if depth == 0:
                return start, pos
        pos += 1
    return None


def main() -> int:
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    tree = Path(sys.argv[1]).resolve()
    apply = "--apply" in sys.argv[2:]
    headers = sorted((tree / "BG3Extender" / "GameDefinitions").rglob("*.h"))
    texts = {path: path.read_text(encoding="utf-8", newline="") for path in headers}
    check = json.loads(CHECK.read_text(encoding="utf-8"))

    done, problems = [], []
    for component, fields in sorted(check.items()):
        for field in fields:
            if field["verdict"] != "different name":
                continue
            old, new = field["extender"], field["game"]
            where = None
            for path, text in texts.items():
                span = find_struct(text, component)
                if span:
                    where = (path, span)
                    break
            if not where:
                problems.append(f"{component}: struct not found ({old} -> {new})")
                continue
            path, (start, end) = where
            body = texts[path][start:end]
            if re.search(r"\b" + re.escape(new) + r"\b\s*(\{[^{}]*\})?\s*;", body):
                problems.append(f"{component}: a member named {new} already exists")
                continue
            member = re.search(r"(?m)^(?P<indent>[ \t]*)(?P<attrs>(?:\[\[[^\]]*\]\][ \t]*)*)(?P<type>[^\n;=]*?[\w>\]\*&][ \t]+)"
                               + re.escape(old) + r"(?P<tail>[ \t]*(?:\{[^{}\n]*\})?[ \t]*;)", body)
            if not member:
                problems.append(f"{component}: member {old} not found in {path.name}")
                continue
            if "bg3::legacy" in member.group("attrs"):
                problems.append(f"{component}: {old} already carries a legacy name; rename by hand")
                continue
            line = (member.group("indent") + member.group("attrs") + f"[[bg3::legacy({old})]] "
                    + member.group("type") + new + member.group("tail"))
            texts[path] = texts[path][:start] + body[:member.start()] + line + body[member.end():] + texts[path][end:]
            done.append((component, old, new, path.name))

    for component, old, new, name in done:
        print(f"{'renamed' if apply else 'would rename'}  {component}: {old} -> {new}   ({name})")
    for problem in problems:
        print("SKIPPED ", problem)
    print(f"{len(done)} field(s), {len(problems)} skipped")
    if apply:
        for path, text in texts.items():
            if text != path.read_text(encoding="utf-8", newline=""):
                path.write_text(text, encoding="utf-8", newline="")
    return 0


if __name__ == "__main__":
    sys.exit(main())
