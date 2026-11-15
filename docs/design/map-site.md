# The map site

The map answers questions about the game - which dialogue says this line, what this stat does,
what a template is made of, what is at an offset of a component, what calls an address - for two
kinds of asker that a command line leaves out: a modder who wants to look something up, and an
agent with no local copy of the databases. An agent that has installed them asks them locally,
through `gatekit query` or the MCP tools, faster and with no limits; the site is for everyone else.

It is built, in its own repository, [bg3gk/map](https://github.com/bg3gk/map): an engine that
renders the released databases into pages and serves them from a server of its own, so the server
needs no copy of the game. Its design, its commands and how it is deployed are that repository's
documents; what follows is why it is shaped the way it is.

- **The site is the API.** Every object has an address, and every page is drawn from a `.json` an
  agent fetches directly. The address scheme is stable and described in the site's `llms.txt`, so
  an agent builds an address from an id it already has instead of scraping a search page.
- **Cross-links are the point.** What made the coverage and decompilation work possible was
  following a thread - a component on an entity, the entity in a dialogue, the dialogue's line
  spoken by an actor whose timeline hides a weapon. Every value that names something else is a
  link, and every page says what links back to it.
- **One search box, then links.** No sidebars and no framework: an object's page is its fields,
  rendered the way identifiers are highlighted in code.
- **Easy to withdraw.** It carries the game's own text, so it is kept out of search engines
  (`noindex`, and a `robots.txt` that lets crawlers read `llms.txt` only) and is taken down the
  same day it is asked for ([what-we-publish.md](what-we-publish.md)).
- **Rebuilt, never committed.** It is rendered from the release the map repository pins; a new
  game patch is a new release and a rebuild.
