"""Compares Script Extender's field names with the game's own member names.

    python research/coverage/fieldmeta_check.py

The macOS build names replicated members (`ls::FieldMeta<Type Class::*, &Class::m_Name>`,
collected in se-fork/reference/macos-symbols/fieldmeta.json); the Windows build gives the
offset of the same members (`ls::FieldMeta<Type Class::*,OFFSET>` among its type names). Where
the type identifies the member uniquely, the two give "the member at OFFSET is called NAME" -
a fact that can be held against the field Script Extender declares at that offset.

Writes work/fieldmeta-check.json: per component, per offset: game name, Script Extender name,
and whether they are the same word. A different word is not necessarily an error (the
extender's names are its author's), but an offset where the extender has no field boundary is.
"""
import json
import re
import sqlite3
from collections import Counter, defaultdict
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
import oldrepo  # noqa: E402
FIELDMETA = HERE.parent / "reference" / "macos-symbols" / "fieldmeta.json"


def normalise(type_text: str) -> str:
    """A C++ type spelled the MSVC way or the clang way, reduced to comparable form."""
    text = re.sub(r"\b(struct|class|enum) ", "", type_text)
    text = re.sub(r"\s+", "", text)
    return text.replace("unsignedchar", "uchar").replace("unsignedint", "uint").replace("unsigned__int64", "ulong") \
        .replace("unsignedlonglong", "ulong").replace("unsignedlong", "ulong").replace("__int64", "long") \
        .replace("longlong", "long")


def main() -> None:
    names = json.loads(FIELDMETA.read_text(encoding="utf-8"))
    base = sqlite3.connect(oldrepo.symbols_db())
    offsets = defaultdict(dict)
    for (text,) in base.execute("SELECT DISTINCT type FROM type_names WHERE type LIKE '%FieldMeta%'"):
        for match in re.finditer(r"FieldMeta<(.+?) (?:struct |class )?([\w:]+)::\*\s*,\s*(\d+)>", text):
            offsets[match.group(2)][int(match.group(3))] = normalise(match.group(1))
    extender = defaultdict(dict)
    for engine_class, name, offset in base.execute(
            "SELECT t.engine_class, f.name, f.offset FROM se_fields f JOIN se_types t ON t.id = f.type_id "
            "WHERE t.engine_class IS NOT NULL AND f.offset IS NOT NULL"):
        extender[engine_class].setdefault(offset, name)
    out, tally = {}, Counter()
    for component, by_offset in offsets.items():
        members = names.get(component) or {}
        by_type = defaultdict(list)
        for name, type_text in members.items():
            by_type[normalise(type_text)].append(name)
        rows = []
        for offset, type_text in sorted(by_offset.items()):
            candidates = by_type.get(type_text, [])
            game = candidates[0][2:] if len(candidates) == 1 and candidates[0].startswith("m_") else None
            theirs = extender.get(component, {}).get(offset)
            if component not in extender:
                verdict = "not in Script Extender"
            elif game is None:
                verdict = "game name ambiguous"
            elif theirs is None:
                verdict = "NO FIELD AT THIS OFFSET"
            elif theirs.lower() == game.lower():
                verdict = "same name"
            else:
                verdict = "different name"
            tally[verdict] += 1
            rows.append({"offset": offset, "game": game, "extender": theirs, "verdict": verdict})
        out[component] = rows
    (HERE / "work" / "fieldmeta-check.json").write_text(json.dumps(out, indent=1), encoding="utf-8")
    print(dict(tally))
    for component, rows in sorted(out.items()):
        for row in rows:
            if row["verdict"] in ("NO FIELD AT THIS OFFSET", "different name"):
                print(f"  {row['verdict']:24} {component} +0x{row['offset']:X}: game {row['game']}, extender {row['extender']}")


if __name__ == "__main__":
    main()
