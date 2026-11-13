"""What players and modders know about a thing of the game, from bg3.wiki - as context for naming
members and for understanding what a system does.

    python research/coverage/wiki.py find <words>          titles that match (full-text search)
    python research/coverage/wiki.py page <Title>          the page as plain text (cut to --chars, default 6000)
    python research/coverage/wiki.py ids <Title>           the game's identifiers on the page (UID, UUID)
    python research/coverage/wiki.py uid <StatsName>       the page of a spell, status or passive by its
                                                          name in the game's stats ("Shout_Rage", "HASTE")

bg3.wiki is a community wiki (MediaWiki; text under CC BY-NC-SA 4.0 or CC BY-SA 4.0). Its pages on
spells, conditions and passives carry the game's own identifier ("UID"), which ties a page to
the data index; the rules pages (Dice rolls, Conditions, Actions, Concentration, Surfaces ...)
describe the order in which the game resolves things - the logic the systems implement.

What it is NOT: a source of layouts or of the game's own member names. A name taken from a wiki
description is "inferred", never "game".

Pages are kept in a local cache (work/wiki-cache, not in the repository: the text is the wiki's,
not ours). Requests are plain reads of the public API with a generic client name.
"""
import hashlib
import html
import json
import re
import sys
import urllib.parse
import urllib.request
from pathlib import Path

API = "https://bg3.wiki/w/api.php"
CACHE = Path(__file__).resolve().parent / "work" / "wiki-cache"


def ask(**params) -> dict:
    """One read of the wiki's API, cached by its parameters."""
    params["format"] = "json"
    query = urllib.parse.urlencode(sorted(params.items()))
    cached = CACHE / (hashlib.sha1(query.encode()).hexdigest() + ".json")
    if cached.exists():
        return json.loads(cached.read_text(encoding="utf-8"))
    request = urllib.request.Request(f"{API}?{query}", headers={"User-Agent": "wiki-reader/1.0"})
    with urllib.request.urlopen(request, timeout=30) as response:
        answer = json.loads(response.read().decode("utf-8"))
    CACHE.mkdir(parents=True, exist_ok=True)
    cached.write_text(json.dumps(answer, ensure_ascii=False), encoding="utf-8")
    return answer


def find(words: str, limit: int = 12) -> list[dict]:
    """[{title, snippet}] for a full-text search."""
    rows = ask(action="query", list="search", srsearch=words, srlimit=limit)["query"]["search"]
    return [{"title": row["title"], "snippet": plain(row.get("snippet", ""))} for row in rows]


def plain(text: str) -> str:
    """HTML of a rendered page as readable text."""
    text = re.sub(r"<(script|style)[^>]*>.*?</\1>", " ", text, flags=re.S)
    text = re.sub(r"<(br|/p|/li|/tr|/h\d|/div)[^>]*>", "\n", text)
    text = re.sub(r"</t[dh]>", " | ", text)
    text = html.unescape(re.sub(r"<[^>]+>", "", text))
    lines = [re.sub(r"[ \t\xa0]+", " ", line).strip() for line in text.splitlines()]
    return "\n".join(line for line in lines if line and line != "|")


def page(title: str) -> str:
    """The rendered page as text (the templates are expanded, so stat blocks are readable)."""
    answer = ask(action="parse", page=title, prop="text", redirects=1, disabletoc=1)
    if "error" in answer:
        return ""
    return plain(answer["parse"]["text"]["*"])


def ids(title: str) -> dict:
    """The game's identifiers a page carries: {"UID": [...], "UUID": [...]} - the stats or template
    names and the template GUIDs, which is what ties a wiki page to the data index and to
    GK.Stage (spawn, use, visit)."""
    text = page(title)
    found = {"UID": [], "UUID": []}
    for kind, value in re.findall(r"^(UID|UUID)\s*\n\s*(\S+)", text, flags=re.M):
        if value not in found[kind]:
            found[kind].append(value)
    for value in re.findall(r"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", text):
        if value not in found["UUID"]:
            found["UUID"].append(value)
    return found


def uid(name: str) -> str:
    """The page that carries a stats identifier (the wiki shows it as "UID")."""
    for row in find(f'"{name}"', limit=5):
        text = page(row["title"])
        if name in text:
            return f"== {row['title']}\n{text}"
    return ""


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    args = sys.argv[1:]
    chars = 6000
    if "--chars" in args:
        chars = int(args[args.index("--chars") + 1])
        del args[args.index("--chars"):args.index("--chars") + 2]
    if len(args) < 2:
        print(__doc__)
    elif args[0] == "find":
        for row in find(" ".join(args[1:])):
            print(f"{row['title']}: {row['snippet'][:160]}")
    elif args[0] == "page":
        print(page(" ".join(args[1:]))[:chars] or "no such page")
    elif args[0] == "ids":
        print(json.dumps(ids(" ".join(args[1:])), indent=1))
    elif args[0] == "uid":
        print(uid(args[1])[:chars] or "no page carries this identifier")
