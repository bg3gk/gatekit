"""Builds the coverage tree and puts on hold the layouts the compiler rejects, until it builds.

    python research/coverage/quarantine.py [max rounds, default 6]

Each round: generate.py -> work/build-v34.cmd -> every compiler error inside Coverage.h is
traced to the component whose declaration it is in, and that layout gets "hold": the error
text. A held layout is left out of the header (generate.py) and keeps its status; the hold is
the to-do list for a human or a worker ("uses a type declared in a header included later",
"size differs"). Errors outside Coverage.h that remain when no layout can be blamed stop the
loop: they need a person.

Exit code 0 when the tree builds.
"""
import json
import re
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import generate  # noqa: E402

LOG = HERE / "work" / "build-quarantine.log"
ERROR = re.compile(r"Coverage\.h\((\d+),\d+\): error (C\d+): (.*?)(?: \[|$)")


def blame(lines: list[str], number: int) -> str | None:
    """The engine name of the component whose declaration holds header line `number`."""
    text = lines[number - 1]
    found = re.search(r"static_assert\(sizeof\(bg3se::([\w:]+)\)", text)
    if found:
        return found.group(1)  # C++ name; matched against layouts by its tail below
    for at in range(number - 1, -1, -1):  # the DEFINE_ line of the struct the error is in or before
        macro = re.search(r'DEFINE_\w*COMPONENT\(\w+, "([\w:]+)"\)', lines[at])
        if macro:
            # the error may be in a nested struct written just above the NEXT component
            for ahead in range(number - 1, min(number + 400, len(lines))):
                if lines[ahead].startswith("};"):
                    break
                nxt = re.search(r'DEFINE_\w*COMPONENT\(\w+, "([\w:]+)"\)', lines[ahead])
                if nxt:
                    return nxt.group(1)
            return macro.group(1)
    return None


def hold(component: str, reason: str) -> bool:
    for layout in generate.load(only_confirmed=False):
        name = layout["component"]
        if name == component or name.endswith("::" + component.split("::")[-1]) and \
                generate.namespace_of(name)[1] == component.split("::")[-1] and "::" in component and \
                name.split("::")[0] == component.split("::")[0]:
            if layout.get("status") not in generate.IN_BUILD or layout.get("hold"):
                continue
            path = layout.pop("_path")
            layout["hold"] = "the compiler rejects it: " + reason
            path.write_text(json.dumps(layout, indent=2, ensure_ascii=False), encoding="utf-8", newline="\n")
            return True
    return False


def main() -> int:
    rounds = int(sys.argv[1]) if len(sys.argv) > 1 else 6
    for number in range(1, rounds + 1):
        subprocess.run([sys.executable, str(HERE / "generate.py")], check=True, capture_output=True)
        with open(LOG, "wb") as out:
            code = subprocess.run(["cmd", "/c", str(HERE / "work" / "build-v34.cmd")], stdout=out,
                                  stderr=subprocess.STDOUT).returncode
        if code == 0:
            print(f"round {number}: builds")
            return 0
        text = LOG.read_bytes().decode("cp866", errors="replace")
        lines = generate.HEADER.read_text(encoding="utf-8").split("\n")
        blamed = {}
        for line, error, message in ERROR.findall(text):
            component = blame(lines, int(line))
            if component and component not in blamed:
                blamed[component] = f"{error} at the generated line {lines[int(line) - 1].strip()[:120]!r}"
        if not blamed:
            # errors in template code (a struct used as a hash key has no operator==, a type has
            # no Lua serializer): the compiler names the type; blame the layout that declares it
            mentioned = set(re.findall(r"bg3se::(?:\w+::)*(\w+)", text))
            for layout in generate.load(only_confirmed=True):
                names = {nested["name"] for nested in layout.get("structs") or []}
                names.add(generate.namespace_of(layout["component"])[1])
                hit = names & mentioned
                if hit:
                    blamed[layout["component"]] = f"an error in template code names {sorted(hit)[0]}"
        held = [name for name, reason in blamed.items() if hold(name, reason)]
        print(f"round {number}: {len(ERROR.findall(text))} errors in Coverage.h, {len(held)} layouts put on hold", flush=True)
        for name in held:
            print("   ", name, flush=True)
        if not held:
            other = sorted(set(re.findall(r"error \w+: [^\[]*", text)))[:8]
            print("nothing left to blame; remaining errors:", *other, sep="\n    ")
            return 1
    return 1


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.exit(main())
