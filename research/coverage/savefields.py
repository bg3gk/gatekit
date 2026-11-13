"""The game's own member names of saved components, from the macOS build's symbols.

    python research/coverage/savefields.py <Component or part of a model name>
    python research/coverage/savefields.py --build        rebuild the table (reference/macos-symbols/savefields.json)
    python research/coverage/savefields.py --stats

A component that goes into a savegame is copied into a "model" (`ls::model::game::<ns>::vN::<Name>`),
and the code that visits a model names each member in its own type:
`ls::model::Spec<Model>::_Field_m_<Name>`. The visitor's template argument list gives the members
in declaration order. So for a saved component the game itself tells the names and the order of
what it keeps - found by a worker on 2026-11-12 (the symbol base's `savemodel` command gives the
sizes, versions and the Save/Load functions; this gives the names).

What this is and is not: the names are the MODEL's members. A model is usually the component
member for member, but a component may hold more than it saves, and a newer model version may
have added members. Names from here are "source": "game" with that said in the evidence; offsets
are not given - they come from the Windows side or live samples.
The order is the order of SAVING, which is not always the order in memory (seen in
esv::itemwall: the model saves Caster before TargetPosition, the component holds them the other
way round) - never derive an offset from a member's position in the list.
"""
import json
import re
import sys
from collections import defaultdict
from pathlib import Path

HERE = Path(__file__).resolve().parent
SYMBOLS = HERE.parent / "reference" / "macos-symbols" / "symbols-demangled.tsv"
TABLE = HERE.parent / "reference" / "macos-symbols" / "savefields.json"
FIELD = re.compile(r"ls::model::Spec<(ls::model::[\w:]+)>::_Field_m_(\w+)")


def build() -> dict:
    """{model: [member names in the order the longest visitor lists them]}"""
    best: dict[str, list[str]] = {}
    with open(SYMBOLS, encoding="utf-8", errors="replace") as lines:
        for line in lines:
            if "_Field_m_" not in line:
                continue
            per_model = defaultdict(list)
            for model, name in FIELD.findall(line):
                if name not in per_model[model]:
                    per_model[model].append(name)
            for model, names in per_model.items():
                if len(names) > len(best.get(model, [])):
                    best[model] = names
    TABLE.write_text(json.dumps(best, indent=1, sort_keys=True), encoding="utf-8")
    return best


def load() -> dict:
    return json.loads(TABLE.read_text(encoding="utf-8")) if TABLE.exists() else build()


def find(text: str) -> dict:
    """Models whose name ends with the component's own name (namespaces differ: the model of
    esv::script::light::EffectParametersComponent is ls::model::game::script::light::v0::...)."""
    table = load()
    last = text.split("::")[-1]
    words = [w for w in text.split("::")[:-1] if w not in ("esv", "ecl", "eoc", "ls")]
    out = {}
    for model, names in table.items():
        if model.split("::")[-1] == last or (last.lower() in model.lower() and "::" not in text):
            if all(word in model for word in words):
                out[model] = names
    return out


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print(__doc__)
    elif sys.argv[1] == "--build":
        table = build()
        print(f"{len(table)} models, {sum(len(v) for v in table.values())} members")
    elif sys.argv[1] == "--stats":
        table = load()
        print(f"{len(table)} models, {sum(len(v) for v in table.values())} members")
    else:
        found = find(sys.argv[1])
        if not found:
            print("no saved model with member names for", sys.argv[1])
        for model, names in sorted(found.items()):
            print(model)
            for number, name in enumerate(names):
                print(f"   {number}: m_{name}")
