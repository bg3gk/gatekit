"""Writes a layout file for every component type the game registers with one byte and Script
Extender does not map: research/coverage/layouts/tags/<name>.json, declared as a tag.

    python research/coverage/tags.py            from coverage.json and work/onebyte.json

A tag is an empty C++ struct; the engine still gives it one byte. The registry cannot tell it
from a component with one byte of data, so the byte is looked at in the running game first
(lua/onebyte.lua -> work/onebyte.json). Measured on types Script Extender already knows: on
the server the byte of a tag is always 0; on the client it is whatever was in memory. Hence:

  - a type with a non-zero byte on the server carries data: it gets no tag here and is listed
    for the usual hypothesis-and-check loop;
  - every other type is declared as a tag. That is safe even if the byte later turns out to
    mean something: the size is right and presence works; the layout is then upgraded.

What was seen is kept in the layout ("one_byte": "alive, zero or garbage" / "not alive"), and
check.py records a tag as verified only when a live holder gives a non-nil entity.<Name>.
"""
import json
import re
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import generate  # noqa: E402

INDEX = HERE / "coverage.json"
ONE_BYTE = HERE / "work" / "onebyte.json"
OUT = HERE / "layouts" / "tags"
SOURCES = HERE.parent / "bg3se" / "BG3Extender"

PREFIX = {"ecl": "Client", "esv": "Server", "eoc": "", "ls": ""}


def camel(word: str) -> str:
    return "".join(part[:1].upper() + part[1:] for part in word.split("_"))


def ext_name(component: str) -> str:
    """ecl::unsheath::VisualStateComponent -> ClientUnsheathVisualState, in Script Extender's style."""
    *namespaces, struct = component.split("::")
    struct = re.sub(r"(OneFrame)?Component$", "", struct)
    words = [PREFIX.get(namespaces[0], camel(namespaces[0]))] + [camel(n) for n in namespaces[1:]]
    name = "".join(words)
    # eoc::item::ItemComponent style: do not say the last namespace word twice
    if namespaces[1:] and struct.startswith(camel(namespaces[-1])):
        name = "".join(words[:-1])
    return name + struct


def existing_names() -> set[str]:
    text = (SOURCES / "GameDefinitions" / "Enumerations" / "ECS.inl").read_text(encoding="utf-8")
    start = text.index("BEGIN_ENUM(ExtComponentType, uint32_t)")
    block = text[start:text.index("END_ENUM()", start)]
    if generate.ENUM_BEGIN in block:  # our own generated names are rebuilt from the layouts
        block = block[:block.index(generate.ENUM_BEGIN)] + block[block.index(generate.ENUM_END):]
    return set(re.findall(r"^\s*E\((\w+)\)", block, re.M))


def main() -> int:
    index = json.loads(INDEX.read_text(encoding="utf-8"))
    seen = json.loads(ONE_BYTE.read_text(encoding="utf-8"))
    types: dict[str, dict] = {}
    for item in index["components"]:
        if item.get("inlineSize") is None:
            continue
        entry = types.setdefault(item["name"], {"mapped": False, "size": 0, "oneFrame": False})
        entry["mapped"] |= item["mapped"]
        entry["size"] = max(entry["size"], item.get("totalSize") or 0)
        entry["oneFrame"] |= item["oneFrame"]

    taken = existing_names()
    for layout in generate.load(only_confirmed=False):
        if OUT not in layout["_path"].parents:
            taken.add(layout["ext_name"])

    OUT.mkdir(parents=True, exist_ok=True)
    for stale in OUT.glob("*.json"):
        stale.unlink()
    written, with_data, renamed = 0, [], []
    for name in sorted(types):
        entry = types[name]
        if entry["mapped"] or entry["size"] > 1:
            continue
        server = (seen["server"].get(name) or {}).get("Values") or {}
        client = (seen["client"].get(name) or {}).get("Values") or {}
        if any(value != "0" for value in server):
            with_data.append((name, server))
            continue
        chosen = ext_name(name)
        if chosen in taken:
            renamed.append((name, chosen))
            chosen += "Tag"
        if chosen in taken:
            raise SystemExit(f"no free name for {name}")
        taken.add(chosen)
        layout = {
            "component": name, "ext_name": chosen, "tag": True, "oneFrame": entry["oneFrame"],
            "size": 1, "fields": [], "status": "confirmed",
            "one_byte": "alive, zero or garbage" if (server or client) else "not alive",
            "one_byte_values": {"server": server, "client": client},
        }
        target = OUT / (name.replace("::", ".") + ".json")
        target.write_text(json.dumps(layout, indent=2), encoding="utf-8", newline="\n")
        written += 1
    print(f"{written} tag layouts written to {OUT}")
    print(f"{len(with_data)} one-byte types carry data (non-zero on the server), not written:")
    for name, values in with_data:
        print(f"    {name}  {values}")
    print(f"{len(renamed)} names were taken and got the suffix Tag: {renamed}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
