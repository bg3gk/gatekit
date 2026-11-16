# The data format

One format for every file GateKit writes or reads of its own, and for every answer its tools give:
**strict JSON, held to a schema.** This page is the contract; `src/GateKit.Json` is the one
implementation (`JsonText`, `SchemaSet`), and [`gatekit json`](cli.md#json-gatekits-own-data-files) is how a person or a
CI job checks it.

Why strict JSON and not a friendlier format: JSON5, JSONC, YAML, TOML, KDL and Lua tables were
compared on the files GateKit has. Every typical slip in hand-written strict JSON (a missing quote
or comma, a trailing comma, a cut-off file, a key written twice) is refused at its line; YAML, TOML
and KDL read several of them silently as something else. Comments, the one thing the others add,
are better as fields a tool can read. The bridge, MCP (JSON-RPC) and every library speak JSON.

## The text

- **Strict JSON** (RFC 8259): no comments, no trailing commas, no single quotes, no byte order
  mark. A key appears once in an object; a second one is an error, never "the last one wins".
- **Comments are fields.** What a person would write in a comment is an ordinary field the schema
  declares: `about` (what this is), `rule`, `note`, `why`. Tools read them like any other field.
- **Numbers are kept exactly as written.** A 64-bit integer, or a longer one, never passes through
  a double on the way in or out.
- **Errors name the place**: `file:line:column: what was expected`, lines and columns from 1,
  columns in characters. A value that does not fit its schema is placed the same way (its key, or
  the object a missing key belongs in).
- **Nothing is silent.** A missing file is "not there" only where the caller asked for that
  (`ReadIfExists`); a file that does not parse or does not fit throws - never an empty object in
  its place.

## One canonical layout

- A document: 2-space indent; a container that fits in **100 columns** on its line (with its key
  and comma) is written on one line, anything else one member per line; the document itself is
  always expanded unless empty; `{"a": 1}` with a space after `:` and `,` on one line.
- A ledger (`.jsonl`): one row per line, compact (`{"a":1,"b":2}`), every line ending in LF.
- Keys in the order the schema lists them, keys it does not list after them as they came.
- Strings escape only what must be (`"`, `\`, control characters, U+2028/2029, lone surrogates);
  everything else is written as itself, in UTF-8 **without a BOM**, LF line ends, a final newline.
- Formatting twice gives the same bytes. `gatekit json fmt` writes it; `fmt --check` fails on any
  file that is not in it.

## Writing

- A whole file is written to a temporary file beside it and renamed over it: a reader sees the old
  file or the new one, never half.
- A ledger is appended to under a cross-process lock (one writer at a time; a caller that holds the
  lock of a wider scope passes it in). Rows are held to the schema first - all or none - and written
  in one write. A file whose last byte is not a newline is refused: a torn last row stays visible
  until somebody looks at it. Reading reports a torn or empty line at its `file:line`.

## Schemas

- Every kind of file and every tool answer has a schema: `schemas/<kind>.schema.json`, its `$id`
  `<kind>/<version>` (`record/sightings/1`). They are embedded in the build; a checkout's own
  `schemas/` is used when `gatekit json` runs inside it.
- **The subset.** JSON Schema 2020-12 cut down to what GateKit implements, with its own messages:
  `type`, `properties`, `required`, `additionalProperties`, `enum`, `const`, `items`, `pattern`,
  `minLength`, `minimum`, `maximum`, `$ref` within the set (`kind`, `kind#/$defs/x`, `#/$defs/x`),
  `oneOf` on a discriminator (every branch an object giving one property a different `const`),
  `format` (below), and the annotations `description`, `title`, `$id`, `$schema`, `$defs`. A schema
  with any other keyword is refused when the set loads, and a test holds every schema to it.
- **Strict by statement**: a schema with `properties` states `additionalProperties` (false, true,
  or the schema of the other values); `required` names only declared properties.
- **The registry** `schemas/index.json` binds path globs and tool names to `kind/version`. A glob
  matches the trailing segments of a path (`*` within a segment, `**/` across); the first match
  wins. `gatekit json check` walks the tree through it.
- **Versions.** A document carries `"schema": "<kind>/<n>"` (its schema declares that field as a
  `const`), so a file says what it is wherever it lies. Ledger rows carry nothing: the registry binds
  the file to a kind and version. A file written for another version is refused by name.

## Rules fixed in the schema, not left to care

- **Absent means not known.** `null` is not used in our own files: where a schema gives a type, a
  null is refused ("leave the field out"). An empty string is not "unknown" either: a field that
  must say something has `minLength: 1`.
- **Empty object and empty array.** Lua writes an empty table as `[]`. Where a schema says
  `object`, the reader takes an empty array as an empty object, and where it says `array` an empty
  object as an empty array. That one conversion, nothing else.
- **64-bit integers from Lua** travel as decimal strings declared `"format": "uint64"` (or
  `int64`): digits only, no leading zero, within range. A number that needs no more than a double
  holds (microseconds since 1970, a tick) stays a number.
- **Time** is spelled one way: UTC to the second, `yyyy-MM-ddTHH:mm:ssZ`, declared
  `"format": "time"`. Local times and dates without a zone are refused.

## What is not ours

The game's and Script Extender's own files (their settings, `.lsx`, mod metadata) and data from the
internet are read by their own rules, outside this contract. MCP itself is JSON-RPC: the SDK frames
it (`src/GateKit.Mcp/BatchStdioTransport.cs`); what a tool answers inside it is ours and gets a
schema (`tools` in the registry).

## Moving the call sites

`unit/GateKit.Json.UnitTests/json-sites.allow.json` lists every file of `src/` that still parses or
writes JSON on its own, with its count and why. The guard test fails on a new site, a file that
gains one, and an entry whose sites are gone: the list only shrinks.
