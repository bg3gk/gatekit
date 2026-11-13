//! lsfdump — reads Larian `.lsf` resource files directly.
//!
//!     lsfdump <source folder> <target folder> [threads] [delete]
//!
//! Every `.lsf` under the source folder is written to the same relative path under the target
//! folder with the extension `.lsn`: a line-based event stream that `gameindex.py` reads in place
//! of the `.lsx` XML that LSLib's Divine produces.
//!
//!     R <tab> region          a region starts (its root node follows)
//!     N <tab> id              a node starts
//!     A <tab> id <tab> type <tab> value     an attribute of the open node
//!     E                       the open node ends
//!
//! In values, backslash, tab, carriage return and line feed are written as \\ \t \r \n.
//! A TranslatedString is written as its handle; its version follows after a tab.
//!
//! The format read here is LSF version 2 to 7 as the game writes it in patch 8: header, then five
//! sections (names, nodes, attributes, values, keys), each stored raw, LZ4 or Zstandard.

use rayon::prelude::*;
use std::fmt::Write as _;
use std::fs;
use std::io::Read;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicUsize, Ordering};

type Res<T> = Result<T, String>;

struct Cursor<'a> {
    d: &'a [u8],
    p: usize,
}

impl<'a> Cursor<'a> {
    fn new(d: &'a [u8]) -> Self {
        Cursor { d, p: 0 }
    }
    fn take(&mut self, n: usize) -> Res<&'a [u8]> {
        if self.p + n > self.d.len() {
            return Err(format!("unexpected end of data at {} (+{})", self.p, n));
        }
        let s = &self.d[self.p..self.p + n];
        self.p += n;
        Ok(s)
    }
    fn u8(&mut self) -> Res<u8> {
        Ok(self.take(1)?[0])
    }
    fn u16(&mut self) -> Res<u16> {
        Ok(u16::from_le_bytes(self.take(2)?.try_into().unwrap()))
    }
    fn u32(&mut self) -> Res<u32> {
        Ok(u32::from_le_bytes(self.take(4)?.try_into().unwrap()))
    }
    fn i32(&mut self) -> Res<i32> {
        Ok(i32::from_le_bytes(self.take(4)?.try_into().unwrap()))
    }
    fn u64(&mut self) -> Res<u64> {
        Ok(u64::from_le_bytes(self.take(8)?.try_into().unwrap()))
    }
    fn i64(&mut self) -> Res<i64> {
        Ok(i64::from_le_bytes(self.take(8)?.try_into().unwrap()))
    }
    fn f32(&mut self) -> Res<f32> {
        Ok(f32::from_le_bytes(self.take(4)?.try_into().unwrap()))
    }
    fn f64(&mut self) -> Res<f64> {
        Ok(f64::from_le_bytes(self.take(8)?.try_into().unwrap()))
    }
}

/// Decompresses one section. `chunked` sections use the LZ4 frame format, others an LZ4 block.
fn section(c: &mut Cursor, on_disk: u32, uncompressed: u32, flags: u8, chunked: bool) -> Res<Vec<u8>> {
    if on_disk == 0 && uncompressed != 0 {
        return Ok(c.take(uncompressed as usize)?.to_vec());
    }
    if on_disk == 0 && uncompressed == 0 {
        return Ok(Vec::new());
    }
    let raw = c.take(on_disk as usize)?;
    match flags & 0x0F {
        0 => Ok(raw.to_vec()),
        2 => {
            if chunked {
                let mut out = Vec::with_capacity(uncompressed as usize);
                lz4_flex::frame::FrameDecoder::new(raw)
                    .read_to_end(&mut out)
                    .map_err(|e| format!("lz4 frame: {e}"))?;
                Ok(out)
            } else {
                lz4_flex::block::decompress(raw, uncompressed as usize).map_err(|e| format!("lz4 block: {e}"))
            }
        }
        3 => {
            let mut out = Vec::with_capacity(uncompressed as usize);
            ruzstd::StreamingDecoder::new(raw)
                .map_err(|e| format!("zstd: {e}"))?
                .read_to_end(&mut out)
                .map_err(|e| format!("zstd: {e}"))?;
            Ok(out)
        }
        1 => Err("zlib-compressed sections are not supported".into()),
        m => Err(format!("unknown compression method {m}")),
    }
}

struct Node {
    name: u32,
    parent: i32,
    first_attr: i32,
}

struct Attr {
    name: u32,
    kind: u32,
    length: u32,
    next: i32,
    offset: u32,
}

const TYPE_NAMES: [&str; 34] = [
    "None", "uint8", "int16", "uint16", "int32", "uint32", "float", "double", "ivec2", "ivec3", "ivec4", "fvec2",
    "fvec3", "fvec4", "mat2x2", "mat3x3", "mat3x4", "mat4x3", "mat4x4", "bool", "string", "path", "FixedString",
    "LSString", "uint64", "ScratchBuffer", "old_int64", "int8", "TranslatedString", "WString", "LSWString", "guid",
    "int64", "TranslatedFSString",
];

fn escape(out: &mut String, s: &str) {
    for ch in s.chars() {
        match ch {
            '\\' => out.push_str("\\\\"),
            '\t' => out.push_str("\\t"),
            '\n' => out.push_str("\\n"),
            '\r' => out.push_str("\\r"),
            c => out.push(c),
        }
    }
}

fn text(bytes: &[u8]) -> String {
    let end = bytes.iter().position(|&b| b == 0).unwrap_or(bytes.len());
    String::from_utf8_lossy(&bytes[..end]).into_owned()
}

/// A guid as LSLib prints it for this game: .NET layout for the first eight bytes, the last
/// eight swapped in pairs.
fn guid(b: &[u8]) -> String {
    let a = u32::from_le_bytes(b[0..4].try_into().unwrap());
    let c = u16::from_le_bytes(b[4..6].try_into().unwrap());
    let d = u16::from_le_bytes(b[6..8].try_into().unwrap());
    format!(
        "{:08x}-{:04x}-{:04x}-{:02x}{:02x}-{:02x}{:02x}{:02x}{:02x}{:02x}{:02x}",
        a, c, d, b[9], b[8], b[11], b[10], b[13], b[12], b[15], b[14]
    )
}

fn base64(data: &[u8], out: &mut String) {
    const T: &[u8; 64] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    for chunk in data.chunks(3) {
        let b = [chunk[0], *chunk.get(1).unwrap_or(&0), *chunk.get(2).unwrap_or(&0)];
        out.push(T[(b[0] >> 2) as usize] as char);
        out.push(T[(((b[0] & 3) << 4) | (b[1] >> 4)) as usize] as char);
        out.push(if chunk.len() > 1 { T[(((b[1] & 15) << 2) | (b[2] >> 6)) as usize] as char } else { '=' });
        out.push(if chunk.len() > 2 { T[(b[2] & 63) as usize] as char } else { '=' });
    }
}

fn floats(c: &mut Cursor, n: usize, out: &mut String) -> Res<()> {
    for i in 0..n {
        if i > 0 {
            out.push(' ');
        }
        let _ = write!(out, "{}", c.f32()?);
    }
    Ok(())
}

fn ints(c: &mut Cursor, n: usize, out: &mut String) -> Res<()> {
    for i in 0..n {
        if i > 0 {
            out.push(' ');
        }
        let _ = write!(out, "{}", c.i32()?);
    }
    Ok(())
}

/// Writes the value of an attribute; returns Ok(false) for a type this reader does not know.
fn value(kind: u32, data: &[u8], out: &mut String) -> Res<bool> {
    let mut c = Cursor::new(data);
    match kind {
        1 => write!(out, "{}", c.u8()?).unwrap(),
        2 => write!(out, "{}", c.u16()? as i16).unwrap(),
        3 => write!(out, "{}", c.u16()?).unwrap(),
        4 => write!(out, "{}", c.i32()?).unwrap(),
        5 => write!(out, "{}", c.u32()?).unwrap(),
        6 => write!(out, "{}", c.f32()?).unwrap(),
        7 => write!(out, "{}", c.f64()?).unwrap(),
        8 => ints(&mut c, 2, out)?,
        9 => ints(&mut c, 3, out)?,
        10 => ints(&mut c, 4, out)?,
        11 => floats(&mut c, 2, out)?,
        12 => floats(&mut c, 3, out)?,
        13 => floats(&mut c, 4, out)?,
        14 => floats(&mut c, 4, out)?,
        15 => floats(&mut c, 9, out)?,
        16 | 17 => floats(&mut c, 12, out)?,
        18 => floats(&mut c, 16, out)?,
        19 => out.push_str(if c.u8()? != 0 { "True" } else { "False" }),
        20 | 21 | 22 | 23 | 29 | 30 => escape(out, &text(data)),
        24 => write!(out, "{}", c.u64()?).unwrap(),
        25 => base64(data, out),
        26 | 32 => write!(out, "{}", c.i64()?).unwrap(),
        27 => write!(out, "{}", c.u8()? as i8).unwrap(),
        28 | 33 => {
            // version (u16), handle length (i32), handle; type 33 carries arguments after that
            let version = c.u16()?;
            let len = c.i32()?.max(0) as usize;
            let handle = text(c.take(len.min(c.d.len() - c.p))?);
            escape(out, &handle);
            let _ = write!(out, "\t{}", version);
        }
        31 => {
            if data.len() < 16 {
                return Err("short guid".into());
            }
            out.push_str(&guid(data));
        }
        _ => return Ok(false),
    }
    Ok(true)
}

fn convert(data: &[u8]) -> Res<String> {
    let mut c = Cursor::new(data);
    if c.take(4)? != b"LSOF" {
        return Err("not an LSF file".into());
    }
    let version = c.u32()?;
    if !(2..=7).contains(&version) {
        return Err(format!("LSF version {version} is not supported"));
    }
    if version >= 5 {
        c.i64()?;
    } else {
        c.i32()?;
    }
    let strings_u = c.u32()?;
    let strings_d = c.u32()?;
    let (keys_u, keys_d) = if version >= 6 { (c.u32()?, c.u32()?) } else { (0, 0) };
    let nodes_u = c.u32()?;
    let nodes_d = c.u32()?;
    let attrs_u = c.u32()?;
    let attrs_d = c.u32()?;
    let values_u = c.u32()?;
    let values_d = c.u32()?;
    let flags = c.u8()?;
    c.u8()?;
    c.u16()?;
    let meta_format = c.u32()?;

    // The header lists the keys section second, but in the file it comes last; it is not needed here.
    let _ = (keys_u, keys_d);
    let strings = section(&mut c, strings_d, strings_u, flags, false)?;
    let nodes_raw = section(&mut c, nodes_d, nodes_u, flags, true)?;
    let attrs_raw = section(&mut c, attrs_d, attrs_u, flags, true)?;
    let values = section(&mut c, values_d, values_u, flags, true)?;

    // names: a hash table of chains of strings; a name is (bucket << 16) | position in the chain
    let mut names: Vec<Vec<String>> = Vec::new();
    {
        let mut s = Cursor::new(&strings);
        let buckets = s.u32()?;
        for _ in 0..buckets {
            let count = s.u16()?;
            let mut chain = Vec::with_capacity(count as usize);
            for _ in 0..count {
                let len = s.u16()? as usize;
                chain.push(String::from_utf8_lossy(s.take(len)?).into_owned());
            }
            names.push(chain);
        }
    }
    let name = |index: u32| -> &str {
        names
            .get((index >> 16) as usize)
            .and_then(|chain| chain.get((index & 0xFFFF) as usize))
            .map(|s| s.as_str())
            .unwrap_or("?")
    };

    let adjacency = meta_format == 1;
    let mut nodes = Vec::new();
    {
        let mut n = Cursor::new(&nodes_raw);
        let size = if adjacency { 16 } else { 12 };
        for _ in 0..nodes_raw.len() / size {
            if adjacency {
                let name = n.u32()?;
                let parent = n.i32()?;
                n.i32()?;
                let first_attr = n.i32()?;
                nodes.push(Node { name, parent, first_attr });
            } else {
                let name = n.u32()?;
                let first_attr = n.i32()?;
                let parent = n.i32()?;
                nodes.push(Node { name, parent, first_attr });
            }
        }
    }
    let mut attrs = Vec::new();
    {
        let mut a = Cursor::new(&attrs_raw);
        let size = if adjacency { 16 } else { 12 };
        let mut offset = 0u32;
        let mut previous: Vec<i32> = Vec::new(); // without adjacency: last attribute seen per node
        for i in 0..attrs_raw.len() / size {
            let name = a.u32()?;
            let type_length = a.u32()?;
            let (kind, length) = (type_length & 0x3F, type_length >> 6);
            if adjacency {
                let next = a.i32()?;
                let off = a.u32()?;
                attrs.push(Attr { name, kind, length, next, offset: off });
            } else {
                let node = a.i32()?;
                attrs.push(Attr { name, kind, length, next: -1, offset });
                offset += length;
                if node >= 0 {
                    let node = node as usize;
                    if previous.len() <= node {
                        previous.resize(node + 1, -1);
                    }
                    if previous[node] >= 0 {
                        attrs[previous[node] as usize].next = i as i32;
                    }
                    previous[node] = i as i32;
                }
            }
        }
        if !adjacency {
            // first attribute of each node is not stored either: it is the first one that names the node
            let mut first: Vec<i32> = vec![-1; nodes.len()];
            let mut a = Cursor::new(&attrs_raw);
            for i in 0..attrs_raw.len() / size {
                a.u32()?;
                a.u32()?;
                let node = a.i32()?;
                if node >= 0 && (node as usize) < first.len() && first[node as usize] < 0 {
                    first[node as usize] = i as i32;
                }
            }
            for (node, f) in nodes.iter_mut().zip(first) {
                node.first_attr = f;
            }
        }
    }

    // children in file order
    let mut children: Vec<Vec<u32>> = vec![Vec::new(); nodes.len()];
    let mut roots = Vec::new();
    for (i, node) in nodes.iter().enumerate() {
        if node.parent < 0 {
            roots.push(i as u32);
        } else if (node.parent as usize) < nodes.len() {
            children[node.parent as usize].push(i as u32);
        }
    }

    let mut out = String::with_capacity(values.len() * 3 + nodes.len() * 24);
    // depth-first without recursion: (node, next child position)
    for root in roots {
        out.push_str("R\t");
        escape(&mut out, name(nodes[root as usize].name));
        out.push('\n');
        let mut stack: Vec<(u32, usize)> = vec![(root, 0)];
        emit_node(&mut out, &nodes[root as usize], &attrs, &values, &name)?;
        while let Some((index, position)) = stack.last().copied() {
            let kids = &children[index as usize];
            if position < kids.len() {
                stack.last_mut().unwrap().1 += 1;
                let child = kids[position];
                emit_node(&mut out, &nodes[child as usize], &attrs, &values, &name)?;
                stack.push((child, 0));
            } else {
                out.push_str("E\n");
                stack.pop();
            }
        }
    }
    Ok(out)
}

fn emit_node<'a>(out: &mut String, node: &Node, attrs: &[Attr], values: &[u8], name: &impl Fn(u32) -> &'a str) -> Res<()> {
    out.push_str("N\t");
    escape(out, name(node.name));
    out.push('\n');
    let mut index = node.first_attr;
    let mut guard = 0;
    while index >= 0 && (index as usize) < attrs.len() {
        let a = &attrs[index as usize];
        let start = a.offset as usize;
        let end = start + a.length as usize;
        if end > values.len() {
            return Err(format!("attribute value outside the values section ({start}+{})", a.length));
        }
        out.push_str("A\t");
        escape(out, name(a.name));
        out.push('\t');
        out.push_str(TYPE_NAMES.get(a.kind as usize).copied().unwrap_or("?"));
        out.push('\t');
        if !value(a.kind, &values[start..end], out)? {
            let _ = write!(out, "?type{}", a.kind);
        }
        out.push('\n');
        index = a.next;
        guard += 1;
        if guard > 1_000_000 {
            return Err("attribute chain does not end".into());
        }
    }
    Ok(())
}

fn main() {
    let args: Vec<String> = std::env::args().collect();
    if args.len() < 3 {
        eprintln!("usage: lsfdump <source folder or file> <target folder> [threads] [delete]");
        std::process::exit(2);
    }
    let source = PathBuf::from(&args[1]);
    let target = PathBuf::from(&args[2]);
    if let Some(n) = args.get(3).and_then(|s| s.parse::<usize>().ok()) {
        rayon::ThreadPoolBuilder::new().num_threads(n).build_global().ok();
    }
    // "delete" as the fourth argument removes each .lsf once it has been written out, for
    // converting a freshly unpacked tree in place.
    let delete = args.get(4).map(|s| s == "delete").unwrap_or(false);
    let base: &Path = if source.is_file() { source.parent().unwrap_or(Path::new(".")) } else { &source };
    let files: Vec<PathBuf> = walkdir::WalkDir::new(&source)
        .into_iter()
        .filter_map(|e| e.ok())
        .filter(|e| e.file_type().is_file())
        .map(|e| e.into_path())
        .filter(|p| p.extension().map(|x| x.eq_ignore_ascii_case("lsf")).unwrap_or(false))
        .collect();
    let failed = AtomicUsize::new(0);
    let bytes = AtomicUsize::new(0);
    files.par_iter().for_each(|path| {
        let relative = path.strip_prefix(base).unwrap_or(path);
        let out_path = target.join(relative).with_extension("lsn");
        let result = fs::read(path).map_err(|e| e.to_string()).and_then(|data| convert(&data));
        match result {
            Ok(text) => {
                if let Some(parent) = out_path.parent() {
                    let _ = fs::create_dir_all(parent);
                }
                bytes.fetch_add(text.len(), Ordering::Relaxed);
                if let Err(e) = fs::write(&out_path, text) {
                    failed.fetch_add(1, Ordering::Relaxed);
                    eprintln!("FAILED\t{}\t{}", relative.display(), e);
                } else if delete {
                    let _ = fs::remove_file(path);
                }
            }
            Err(e) => {
                failed.fetch_add(1, Ordering::Relaxed);
                eprintln!("FAILED\t{}\t{}", relative.display(), e);
            }
        }
    });
    println!(
        "{} files, {} failed, {} MB written",
        files.len(),
        failed.load(Ordering::Relaxed),
        bytes.load(Ordering::Relaxed) / 1_000_000
    );
    if failed.load(Ordering::Relaxed) > 0 {
        std::process::exit(1);
    }
}
