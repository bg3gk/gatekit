# Searching the map, by people and by machines

**Status: design, not built.**

The map answers questions about the game — which dialogue says this line, what this stat does,
what a template is made of, where a timeline hides a weapon. Today you can ask it only from a
command line, with the database on your own disk. Two kinds of asker are left out: a modder who
just wants to look something up, and an agent with no local copy.

Norbyte's search site is the obvious shape to copy. We can do better, cheaply, by noticing that
**people and machines want the same pages** — and that a page and an API response can be the
same thing.

## The idea: the site is the API

Every object gets a URL. Every URL has a `.json` twin with the same content as data.

```
/dialog/DEN_AttackOnDen_Sapper_003           a page a person reads
/dialog/DEN_AttackOnDen_Sapper_003.json      the same thing an agent fetches
/stat/WPN_Shortsword   /template/<uuid>   /timeline/<uuid>   /loca/<handle>   /symbol/<name>
```

No server, no API keys, no rate limits, no uptime to maintain, nothing to pay for: static files,
cached by the browser and by every proxy in between. An agent needs no client library — it
fetches a URL. The scheme is documented and stable, so an agent builds the address from an id it
already has instead of scraping a search page.

Search itself runs in the reader's browser over a compact index, so even that needs no server.

**Why this beats giving agents a search endpoint**: an agent that has run `gatekit data install`
already has the whole database locally, and asking it through MCP is faster, works offline and
has no limits at all. The site is for the agent that has installed nothing, and for people. One
mechanism serves both, instead of two half-built ones.

## The search is full, because the text is small

Measured on the real index:

| | |
|---|---|
| all localized text, every language | **30 MB** (English 14.3, Russian 15.8) |
| names of every object in the game | 32 MB across 1.13 M objects |
| the kinds worth their own page — entities, dialogues, timelines, templates, stats | **124,781** |
| the raw attribute rows behind all of it | 31 M, and the reason the whole index is 3.47 GB |

So the thing people actually search — text and names — is tens of megabytes, not gigabytes.
The site carries all of it: every line of every language, every name, with real full-text
search, not a list of titles. What it leaves behind is the long tail of raw attributes of the
million objects nobody looks up by hand; every page links to the downloadable index for those,
with the command that fetches it.

Budget: a published GitHub Pages site may be no larger than 1 GB, and Git LFS cannot serve Pages
at all. Text, names and the 125 k object pages should land a long way under that.

## Built here, deployed as a site, never committed

The index can only be built on a machine that owns the game, so nothing in continuous
integration can build the site. It is rendered locally — `gatekit site build` — and published as
a single orphan commit force-pushed to the site's branch, so the history never accumulates
copies and taking the site down is deleting a branch.

## Cross-links are the point

What made the coverage and decompilation work possible was not any single lookup, it was
following a thread: this component appears on that entity, which belongs to this dialogue, whose
line is spoken by that actor, whose timeline hides a weapon at that frame. A database makes that
tedious; a web of pages makes it easy.

So every page links outward to everything it touches, and says what links back to it. A person
follows the links by clicking; an agent follows them by reading `links` in the JSON.

## How it is built

`gatekit site build` renders the whole site from a built index into a folder: a page and a
`.json` twin for each of the 125 k objects, and one small SQLite holding all the text and names
with a full-text index, which the browser queries over HTTP range requests — real search,
in the reader's browser, with no server.

Each kind of object is described once and rendered twice, as HTML and as JSON, so the two
cannot drift. Reproducible, so anyone can regenerate it and compare, and a new game patch is a
rebuild rather than an event.

**The interface is one search box and then links.** No sidebars, no panels, no framework. A
result is a line you click. An object's page is its fields, and every value that names something
else — a UUID, a localization handle, a stat, a template — is rendered the way an identifier is
highlighted in code, and is a link. Reading a page should feel like reading a highlighted
record, and following a thread should be clicking through it.

## Open questions

- **How much of the game's text to show.** Dialogue lines are the game's writing: showing them
  is what makes the site useful, and is also the part most clearly Larian's. The promise in
  `what-we-publish.md` holds — it goes the day it is asked for — and the site lives in the same
  repository, so it goes with it.
- **Patches.** Either rebuild per patch and keep the old one at a versioned path, or say plainly
  which build the site describes. The second is cheaper and probably enough.
- **How much of the symbols and the component map belong on it.** The three maps meet in a
  useful place — a component of the engine is seen on entities, which belong to dialogues and
  levels — so a page per engine class, linking into the game's own content, is probably worth
  more than either half alone. Start with the game's content, then add the other two and link
  them, rather than designing all three at once.
