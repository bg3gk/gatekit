"""What a dialogue of the game offers, read from its tree in the data index (kit/gatekit.py,
tables dialogs / dialog_nodes / dialog_links / dialog_flags), so that a tour can walk its
branches on purpose instead of always taking the last answer.

    python research/coverage/dialog_plan.py DEN_Weaponsmith            one dialogue (part of its name)
    python research/coverage/dialog_plan.py --rank DEN_ 20             the richest dialogues whose name has the text

For a dialogue: how many questions and answers, the widest choice, whether it has dice rolls
(ActiveRoll / PassiveRoll), a trade node, nested dialogues, cinematic nodes; and the flags its
nodes CHECK - global and object flags that gate branches (setting them before the dialogue
starts opens what a fresh game never shows).

plan(name) returns the passes a tour should play:
    [ {"answers": "first"}, {"answers": "second"}, {"answers": "last", "flags": [...]}, ... ]
"""
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
# The offline tools are kit/gatekit.py; the stand has a module of the same name, so this one is
# loaded by its path under a name of its own.
import importlib.util  # noqa: E402
sys.path.insert(0, str(HERE.parent))
import oldrepo  # noqa: E402

_KIT = oldrepo.root() / "kit"

_spec = importlib.util.spec_from_file_location("gatekit_offline", _KIT / "gatekit.py")
kit = importlib.util.module_from_spec(_spec)
sys.path.insert(0, str(_KIT))
_spec.loader.exec_module(kit)
sys.path.remove(str(_KIT))


def q(sql: str, params=()) -> list[tuple]:
    """Rows of the data index as plain tuples."""
    return [tuple(dict(row).values()) for row in kit.rows("index", sql, params)]


def describe(name: str) -> dict | None:
    found = q("SELECT id, name, uuid, nodes FROM dialogs WHERE name LIKE ? AND shadowed = 0 "
                              "ORDER BY length(name) LIMIT 1", (f"%{name}%",))
    if not found:
        return None
    dialog_id, full, uuid, nodes = found[0]
    kinds = dict(q("SELECT constructor, COUNT(*) FROM dialog_nodes WHERE dialog_id = ? GROUP BY 1", (dialog_id,)))
    widest = q("SELECT MAX(n) FROM (SELECT COUNT(*) AS n FROM dialog_links WHERE dialog_id = ? GROUP BY node)",
                      (dialog_id,))[0][0] or 0
    checks = q("SELECT DISTINCT flag_type, flag, value FROM dialog_flags WHERE dialog_id = ? AND kind = 'check' "
                               "AND flag_type IN ('Global', 'Object')", (dialog_id,))
    sets = {row[0] for row in q("SELECT DISTINCT flag FROM dialog_flags WHERE dialog_id = ? AND kind = 'set'",
                                       (dialog_id,))}
    return {
        "name": full, "uuid": uuid, "nodes": nodes, "questions": kinds.get("TagQuestion", 0),
        "answers": kinds.get("TagAnswer", 0), "widest": widest,
        "rolls": kinds.get("ActiveRoll", 0) + kinds.get("PassiveRoll", 0), "trade": kinds.get("Trade", 0),
        "nested": kinds.get("Nested Dialog", 0), "cinematic": kinds.get("TagCinematic", 0),
        # flags the dialogue checks and does not set itself: they come from elsewhere in the story
        "gates": [{"type": kind, "flag": flag, "value": value} for kind, flag, value in checks if flag not in sets],
    }


def richness(info: dict) -> int:
    """A rough measure of how much of the game a dialogue touches."""
    return info["rolls"] * 8 + info["trade"] * 10 + info["nested"] * 4 + info["cinematic"] * 2 + info["questions"] \
        + min(len(info["gates"]), 10)


def plan(name: str, passes: int = 3) -> list[dict]:
    info = describe(name)
    if info is None:
        return [{"answers": "last"}]
    out = [{"answers": "first"}]
    if info["widest"] >= 2 and passes >= 2:
        out.append({"answers": "second"})
    wanted = [gate for gate in info["gates"] if gate["value"] == 1][:12]
    if wanted and passes >= 3:
        out.append({"answers": "first", "flags": wanted})
    if info["widest"] >= 3 and passes >= 4:
        out.append({"answers": "third"})
    return out


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    if len(sys.argv) >= 3 and sys.argv[1] == "--rank":
        limit = int(sys.argv[3]) if len(sys.argv) > 3 else 20
        names = [row[0] for row in q("SELECT name FROM dialogs WHERE name LIKE ? AND shadowed = 0",
                                            (f"%{sys.argv[2]}%",))]
        ranked = sorted((describe(name) for name in names), key=lambda info: -richness(info))[:limit]
        for info in ranked:
            print(f"{richness(info):4}  {info['name']}: {info['questions']} questions, {info['rolls']} rolls, "
                  f"{info['trade']} trade, {len(info['gates'])} gates")
    elif len(sys.argv) >= 2:
        info = describe(sys.argv[1])
        print(info)
        print(plan(sys.argv[1], 4))
    else:
        print(__doc__)
