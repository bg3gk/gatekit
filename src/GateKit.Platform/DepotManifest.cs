using System.IO.Compression;
using System.Text;

namespace GateKit.Platform;

/// <summary>Steam's flags of a file in a depot manifest (SteamKit's EDepotFileFlag).</summary>
[Flags]
public enum DepotFileFlags : uint
{
    None = 0,
    UserConfig = 1,
    VersionedUserConfig = 2,
    Encrypted = 4,
    ReadOnly = 8,
    Hidden = 16,
    Executable = 32,
    Directory = 64,
    CustomExecutable = 128,
    InstallScript = 256,
    Symlink = 512,
}

/// <summary>One entry of a depot manifest: a file, a folder or a link, with what Steam delivered.</summary>
/// <param name="Name">The path inside the app folder, with '/' between segments.</param>
/// <param name="Size">Bytes of the file.</param>
/// <param name="Flags">What Steam says the entry is.</param>
/// <param name="Sha1">SHA-1 of the file's content (20 bytes); zeros for a folder.</param>
/// <param name="LinkTarget">Where a link points, for a link.</param>
public sealed record DepotFile(string Name, ulong Size, DepotFileFlags Flags, byte[] Sha1, string? LinkTarget)
{
    public bool IsDirectory => (Flags & DepotFileFlags.Directory) != 0;
    public bool IsLink => (Flags & DepotFileFlags.Symlink) != 0;
    /// <summary>Steam marks the file as the user's to change: it is installed once and not put back.</summary>
    public bool IsUserConfig => (Flags & (DepotFileFlags.UserConfig | DepotFileFlags.VersionedUserConfig)) != 0;
}

/// <summary>
/// A Steam depot manifest (<c>&lt;Steam&gt;/depotcache/&lt;depot&gt;_&lt;manifest&gt;.manifest</c>): the list
/// of every file a depot delivers, with its size and SHA-1.
///
/// The file is a sequence of sections, each a little-endian uint32 magic and, except for the end
/// marker, a uint32 length and that many bytes of protobuf: 0x71F617D0 the payload
/// (ContentManifestPayload: repeated FileMapping), 0x1F4812BE the metadata
/// (ContentManifestMetadata), 0x1B81B817 the signature, 0x32C415AB the end. A manifest fetched
/// from Steam's servers is the same bytes inside a zip archive. Field numbers are SteamKit's
/// <c>content_manifest.proto</c>. The binary format before protobuf (magic 0x16349781) is not read.
/// </summary>
public sealed record DepotManifest(uint DepotId, ulong ManifestId, DateTime? Created, bool FilenamesEncrypted, IReadOnlyList<DepotFile> Files)
{
    public const uint PayloadMagic = 0x71F617D0;
    public const uint MetadataMagic = 0x1F4812BE;
    public const uint SignatureMagic = 0x1B81B817;
    public const uint EndMagic = 0x32C415AB;
    public const uint BinaryV4Magic = 0x16349781;

    /// <summary>The cached manifest of a depot in the first folder that has it, or null.</summary>
    public static string? Find(IEnumerable<string> caches, string depotId, string manifestId) =>
        caches.Select(c => Path.Combine(c, $"{depotId}_{manifestId}.manifest")).FirstOrDefault(File.Exists);

    public static DepotManifest Read(string path) => Parse(File.ReadAllBytes(path), path);

    /// <summary>Reads a manifest's bytes; anything that is not one throws <see cref="InvalidDataException"/> naming the place.</summary>
    public static DepotManifest Parse(byte[] bytes, string source)
    {
        if (bytes.Length >= 4 && bytes[0] == 'P' && bytes[1] == 'K' && bytes[2] == 3 && bytes[3] == 4) bytes = Unzip(bytes, source);
        byte[]? payload = null, metadata = null;
        var at = 0;
        while (at < bytes.Length)
        {
            if (bytes.Length - at < 4) throw new InvalidDataException($"{source}: {bytes.Length - at} stray bytes at offset {at}");
            var magic = BitConverter.ToUInt32(bytes, at);
            at += 4;
            if (magic == EndMagic) break;
            if (magic == BinaryV4Magic) throw new InvalidDataException($"{source}: the binary manifest format before protobuf (magic 0x16349781) is not read");
            if (magic is not (PayloadMagic or MetadataMagic or SignatureMagic))
                throw new InvalidDataException($"{source}: unknown section magic 0x{magic:X8} at offset {at - 4}");
            if (bytes.Length - at < 4) throw new InvalidDataException($"{source}: section 0x{magic:X8} has no length");
            var length = BitConverter.ToUInt32(bytes, at);
            at += 4;
            if (length > bytes.Length - at) throw new InvalidDataException($"{source}: section 0x{magic:X8} at offset {at - 8} says {length} bytes, {bytes.Length - at} are left");
            var body = bytes.AsSpan(at, (int)length).ToArray();
            at += (int)length;
            if (magic == PayloadMagic) payload = body;
            else if (magic == MetadataMagic) metadata = body;
        }
        if (payload is null) throw new InvalidDataException($"{source}: no payload section (0x71F617D0)");

        uint depot = 0;
        ulong gid = 0;
        DateTime? created = null;
        var encrypted = false;
        if (metadata is not null)
        {
            var r = new ProtoReader(metadata, source);
            while (r.Next(out var field, out var wire))
            {
                switch (field)
                {
                    case 1 when wire == ProtoReader.Varint: depot = (uint)r.ReadVarint(); break;
                    case 2 when wire == ProtoReader.Varint: gid = r.ReadVarint(); break;
                    case 3 when wire == ProtoReader.Varint: created = DateTime.UnixEpoch.AddSeconds(r.ReadVarint()); break;
                    case 4 when wire == ProtoReader.Varint: encrypted = r.ReadVarint() != 0; break;
                    default: r.Skip(wire); break;
                }
            }
        }

        var files = new List<DepotFile>();
        var p = new ProtoReader(payload, source);
        while (p.Next(out var field, out var wire))
        {
            if (field == 1 && wire == ProtoReader.Length) files.Add(Mapping(p.ReadBytes(), source));
            else p.Skip(wire);
        }
        return new DepotManifest(depot, gid, created, encrypted, files);
    }

    static DepotFile Mapping(ReadOnlySpan<byte> bytes, string source)
    {
        string? name = null, link = null;
        ulong size = 0;
        uint flags = 0;
        byte[] sha = [];
        var r = new ProtoReader(bytes.ToArray(), source);
        while (r.Next(out var field, out var wire))
        {
            switch (field)
            {
                case 1 when wire == ProtoReader.Length: name = Encoding.UTF8.GetString(r.ReadBytes()); break;
                case 2 when wire == ProtoReader.Varint: size = r.ReadVarint(); break;
                case 3 when wire == ProtoReader.Varint: flags = (uint)r.ReadVarint(); break;
                case 5 when wire == ProtoReader.Length: sha = r.ReadBytes().ToArray(); break;
                case 7 when wire == ProtoReader.Length: link = Encoding.UTF8.GetString(r.ReadBytes()); break;
                default: r.Skip(wire); break; // 4 sha_filename, 6 chunks
            }
        }
        if (name is null) throw new InvalidDataException($"{source}: a file mapping has no filename");
        return new DepotFile(Normalize(name), size, (DepotFileFlags)flags, sha, link);
    }

    /// <summary>A manifest path with '/' between segments and none at either end.</summary>
    public static string Normalize(string name) => name.Replace('\\', '/').Trim('/');

    static byte[] Unzip(byte[] bytes, string source)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var entry = zip.Entries.FirstOrDefault(e => e.Length > 0) ?? throw new InvalidDataException($"{source}: an empty zip archive");
        using var stream = entry.Open();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}

/// <summary>The protobuf wire format, as far as reading a depot manifest needs it.</summary>
public sealed class ProtoReader(byte[] bytes, string source)
{
    public const int Varint = 0, Fixed64 = 1, Length = 2, Fixed32 = 5;

    int _at;

    /// <summary>The next field's number and wire type; false at the end.</summary>
    public bool Next(out int field, out int wire)
    {
        field = wire = 0;
        if (_at >= bytes.Length) return false;
        var key = ReadVarint();
        field = (int)(key >> 3);
        wire = (int)(key & 7);
        if (field == 0) throw new InvalidDataException($"{source}: protobuf field 0 at offset {_at}");
        return true;
    }

    public ulong ReadVarint()
    {
        ulong value = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (_at >= bytes.Length) throw new InvalidDataException($"{source}: a protobuf varint runs past the end");
            var b = bytes[_at++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return value;
        }
        throw new InvalidDataException($"{source}: a protobuf varint longer than 10 bytes at offset {_at}");
    }

    public ReadOnlySpan<byte> ReadBytes()
    {
        var length = ReadVarint();
        if (length > (ulong)(bytes.Length - _at)) throw new InvalidDataException($"{source}: a protobuf field of {length} bytes at offset {_at} runs past the end");
        var span = bytes.AsSpan(_at, (int)length);
        _at += (int)length;
        return span;
    }

    public void Skip(int wire)
    {
        switch (wire)
        {
            case Varint: ReadVarint(); break;
            case Fixed64: Advance(8); break;
            case Length: ReadBytes(); break;
            case Fixed32: Advance(4); break;
            default: throw new InvalidDataException($"{source}: protobuf wire type {wire} at offset {_at} is not read");
        }
    }

    void Advance(int count)
    {
        if (bytes.Length - _at < count) throw new InvalidDataException($"{source}: a protobuf field runs past the end");
        _at += count;
    }
}
