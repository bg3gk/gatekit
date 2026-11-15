# The decompiler kept open: how fast one function is decompiled again

The question: can a decompiler kept open on a binary's project decompile one function again, with
new types applied, in about a second? If so, `decomp code` can show typed pseudo-code of what the
registry confirmed a moment ago, and the decompiler's project stays a cache derived from the
registry ([registry/README.md](../../registry/README.md)).

## How it was measured

`gatekit decomp serve bg3_dx11.exe` opens the existing project read-only (Ghidra 12.2 without its
window, `-noanalysis -readOnly`, `tools/ghidra/DecompServe.java`), then answers requests over a
folder of files. `gatekit decomp serve --ask <binary+0xRVA> [--param i=<type>] --count N` sends one
function N times, the first time with the given registry types put on its parameters, and prints
only the milliseconds. One run on Windows, build 25605617, a project of the whole executable.

| what | milliseconds |
|---|---|
| start to ready: the launcher, the JVM, the project and the program opened | about 16,000 |
| the decompiler's own opening inside it | 385 |
| a function of 33 lines, first request | 442 round trip (406 decompiling) |
| the same, again | 60 round trip (11 decompiling) |
| the same with two parameters typed by registry structures, first request | 68 round trip (23 applying, 12 decompiling) |
| a function of 185 lines with one parameter typed | 125 round trip (57 applying, 45 decompiling), then 92 |
| a function of 525 lines | 435, 405, 389 round trip (391, 351, 331 decompiling) |
| through `decomp code`, a type first given | 83 round trip, the structure's applied field read by name |

## The answer

Yes. Once the program is open, one function is decompiled again with new types in well under a
second - tens of milliseconds for a short function, a few hundred for a long one; the file channel
adds about 50 ms. The cost is the opening, about 16 s, paid once per session of the server.

A structure the registry gave a confirmed field and a function tied to it read, through `decomp
code` with the server open, with the field by name; a field of the same structure on a shown rung
was not sent and did not appear. The project's program database was not written by the session
(only Ghidra's two small index files of the project were rewritten on closing).

## Not measured

- A second session on one project while the server holds it: Ghidra's project lock is expected
  to refuse it, so `decomp refresh` of a binary is run with no server open on it. Not tried.
- Memory: the server was given the default heap of 20 GB and was not watched for what it used.
