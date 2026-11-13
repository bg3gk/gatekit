"""Settles, for the one-byte types declared as tags, whether they are empty structs or hold a
byte of data - from the default constructors of the macOS build (macos_defaults.py).

    python research/coverage/tag_evidence.py          update the layout files, print the tally

The registry gives a tag one byte, the same as a component with one byte of data. The macOS
executable (same game version) names `ecs::ComponentOps<T>::DefaultConstructComponents` for
every type; its code writes into the new component exactly when the type has members.
Checked against what Script Extender already declares and against the live game: the one
type seen with a non-zero byte on the server (`eoc::PickingStateComponent`, declared as a tag
by Script Extender) is one whose constructor writes.

Each tag layout gets "one_byte_evidence":
  "empty struct"   the constructor writes nothing: a tag, settled
  "data"           the constructor stores a byte: the layout is turned into a component with
                   one field (name unknown), so the value can be read
  "no evidence"    one-frame types and others the engine never default-constructs (the
                   function is a three-instruction stub): still declared as tags on the
                   strength of the registry alone
"""
import json
import sys
from collections import Counter
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import generate  # noqa: E402

DEFAULTS = HERE.parent / "reference" / "macos-symbols" / "defaults.json"


def main() -> int:
    defaults = json.loads(DEFAULTS.read_text(encoding="utf-8"))
    tally = Counter()
    for layout in generate.load(only_confirmed=False):
        path = layout.pop("_path")
        if layout.get("size") != 1 or not (layout.get("tag") or layout.get("one_byte_evidence")):
            continue
        found = defaults.get(layout["component"])
        if found is None:
            evidence = "no evidence"
        elif found["instructions"] <= 3:
            evidence = "no evidence"
        elif found["stores"]:
            evidence = "data"
        else:
            evidence = "empty struct"
        layout["one_byte_evidence"] = evidence
        if evidence == "data":
            store = found["stores"][0]
            default = "zero" if "wzr" in store else "a value computed in the constructor"
            layout["tag"] = False
            layout["fields"] = [{
                "name": "field_0", "offset": 0, "type": "uint8_t", "source": "unknown",
                "evidence": ("One byte of data: ecs::ComponentOps<T>::DefaultConstructComponents of the macOS "
                             f"build (address {found['address']}) stores it ({store}; default {default}). "
                             "What the byte means is not established."),
            }]
            layout["status"] = "confirmed"
        tally[evidence] += 1
        path.write_text(json.dumps(layout, indent=2, ensure_ascii=False), encoding="utf-8", newline="\n")
    print(dict(tally))
    return 0


if __name__ == "__main__":
    sys.exit(main())
