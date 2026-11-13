"""Runs Lua in the perf instance's game and prints the result as JSON (a thin wrapper used by
the experiments here; the stand's own `exec` does the same for its default naming).

    python research/perf/lua.py server "return Osi.GetHostCharacter()"
    python research/perf/lua.py client @file.lua
"""
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from startup_profile import load_stand  # noqa: E402


def main() -> int:
    side, code = sys.argv[1], sys.argv[2]
    if code.startswith("@"):
        code = Path(code[1:]).read_text(encoding="utf-8")
    stand = load_stand(False)
    print(json.dumps(stand.Game().lua(side, code, timeout=float(sys.argv[3]) if len(sys.argv) > 3 else 30),
                     ensure_ascii=False, indent=1))
    return 0


if __name__ == "__main__":
    sys.exit(main())
