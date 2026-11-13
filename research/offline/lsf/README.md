# lsfdump — reading `.lsf` directly

Most of the game's data is in `.lsf`, Larian's binary resource format. The index used to have
LSLib's Divine convert every `.lsf` to `.lsx` (XML) and then parse the XML: 219 seconds for all
packages and 9 GB of temporary text. `lsfdump` is a small Rust program that
reads `.lsf` itself and writes a compact event stream the index reads instead.

It is optional. Without it `gameindex.py build` works as before through Divine; with it the
conversion step takes seconds instead of minutes.

| Main package (`Gustav.pak`, 71,449 `.lsf`, 2.4 GB) | Divine to `.lsx` | `lsfdump` |
|---|---|---|
| Conversion of this package alone | not measured separately | 14 s |
| Conversion step of a full index build, all packages (the second measured on a busy machine) | 219 s | 48 s |
| Converted files on disk | 9 GB | 3 GB |
| Reading one large file in the index (root templates of `Shared`) | 1.35 s | 0.47 s |

## Building

Needs the Rust toolchain (rustup.rs; the MSVC build tools it asks for come with Visual Studio).

```
cd index\lsf
cargo build --release
```

The index looks for the program at `index\lsf\target\release\lsfdump.exe`, then
`index\lsf\lsfdump.exe`, or where `--lsfdump PATH` / `BG3_LSFDUMP` point. `build --no-lsfdump`
forces the Divine path. Divine is still needed either way: it unpacks the packages.

Four small crates are downloaded by cargo: `lz4_flex`, `ruzstd` (Zstandard in pure Rust),
`rayon` and `walkdir`.

## Using it alone

```
lsfdump <source folder or file> <target folder> [threads]
```

Every `.lsf` under the source is written to the same relative path under the target with the
extension `.lsn`:

```
R <tab> region                         a region starts; its root node follows
N <tab> id                             a node starts
A <tab> id <tab> type <tab> value      an attribute of the open node
E                                      the open node ends
```

Backslash, tab, carriage return and line feed inside a value are written `\\`, `\t`, `\r`, `\n`.
A `TranslatedString` is written as its handle, with its version after one more tab. Files that
cannot be read are reported on standard error as `FAILED <tab> path <tab> reason` and the exit
code is 1.

## The format, as read here

LSF versions 2 to 7. After the magic `LSOF`, the version and the engine version come ten sizes
(five sections, uncompressed and on disk), a compression byte and the metadata format.

- **Sections**, in file order: names, nodes, attributes, values, keys. The header lists the keys
  second, but they are stored last. A section with a zero on-disk size is stored raw; otherwise
  it is LZ4 or Zstandard by the low four bits of the compression byte. All but the names use
  the LZ4 *frame* format, the names a plain LZ4 block.
- **Names** are a hash table of chains; a name is referred to as `(bucket << 16) | position`.
- **Nodes and attributes** come in two layouts. With metadata format 1 a node is 16 bytes (name,
  parent, next sibling, first attribute) and an attribute 16 bytes (name, type and length, next
  attribute, offset into the values). Otherwise a node is 12 bytes (name, first attribute,
  parent) and an attribute 12 bytes (name, type and length, node); offsets are then the running
  sum of lengths and an attribute chain is every attribute naming the node, in order.
- **Type and length** share 32 bits: type in the low six, length above.
- **Values** by type: integers and floats little-endian; vectors as that many floats or ints;
  strings with a terminating zero; a guid as 16 bytes; a `TranslatedString` as a 16-bit version,
  a 32-bit length and the handle.

## Checked against LSLib

`compare.py` reads a folder of `.lsn` and the `.lsx` Divine wrote for the same files and compares
every node and attribute:

```
python compare.py <folder of .lsn> <folder of .lsx>
```

On all 7,750 `.lsf` of `Shared.pak`: same structure in every file, 12,134,872 attributes, no
difference in value. The comparison allows for what the two tools deliberately print
differently, and these are also the differences you will see in the index:

| | LSLib (`.lsx`) | `lsfdump` |
|---|---|---|
| Very small floats | `-1.6391277E-07` | `-0.00000016391277` |
| Matrices | rows, rounded to two decimals, with line breaks | sixteen (or nine, four) numbers in stored order (columns), full precision |
| Control characters in strings | dropped (XML cannot hold them) | kept |
| Not-a-number | text in the system's language | `NaN` |

The last two are why one dialog could not be converted by Divine at all and needed a special
case in the index; with `lsfdump` it reads like any other file.

Not checked: `Gustav.pak` file by file against Divine (it reads all 71,449 files without an
error, and the index built from it gives the same counts of scenes and tracks as the one
built through Divine); `TranslatedFSString` values with
arguments, which the game's data does not seem to use; LSF versions below 5 beyond the handful of
files in `Shared.pak`.
