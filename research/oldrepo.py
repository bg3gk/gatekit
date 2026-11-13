"""Where the old Python stand and its databases are.

These tools were written in the old repository, next to the Python stand (`stand/gatekit.py`,
`stand/instances.py`), the offline tools (`kit/gatekit.py`) and the symbol database. The stand
has since been rewritten in C# in this repository; these tools have not been ported, so they
still need a checkout of the old repository to run.

Point `GATEKIT_OLD_REPO` at it:

    set GATEKIT_OLD_REPO=<path of the old repository>

The databases can be named directly instead, with the variables the old tools already read:
`BG3_SYMBOLS_DB`, `BG3_INDEX_DB`.
"""
import os
import pathlib

HERE = pathlib.Path(__file__).resolve().parent


def root() -> pathlib.Path:
    """The old repository's root."""
    value = os.environ.get("GATEKIT_OLD_REPO")
    if not value:
        raise SystemExit(
            "these tools still need the old Python stand: set GATEKIT_OLD_REPO to a checkout of "
            "the old repository (see research/README.md and research/coverage/README.md)")
    path = pathlib.Path(value).expanduser().resolve()
    if not (path / "stand" / "gatekit.py").exists():
        raise SystemExit(f"GATEKIT_OLD_REPO={path} has no stand/gatekit.py")
    return path


def symbols_db() -> pathlib.Path:
    """The symbol database built by the old `symbols/symbols.py`."""
    value = os.environ.get("BG3_SYMBOLS_DB")
    if value:
        return pathlib.Path(value)
    return root() / "symbols" / "symbols.sqlite"


def kit_gatekit() -> pathlib.Path:
    """The old offline query tool, `kit/gatekit.py`."""
    return root() / "kit" / "gatekit.py"
