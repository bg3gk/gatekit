using System.Security.Cryptography;

namespace GateKit.Release;

/// <summary>
/// Writes one byte stream into files of at most <paramref name="partBytes"/>, hashing each as it
/// goes. A single part keeps the plain name; several become <c>name.001</c>, <c>name.002</c>, ...
/// so a database that outgrows one release asset needs no new format, only more parts.
/// </summary>
internal sealed class SplitWriter(string folder, string baseName, long partBytes) : Stream
{
    readonly List<(string Temp, long Size, string Sha256)> _done = [];
    FileStream? _file;
    IncrementalHash? _hash;
    long _inPart;

    public long Total { get; private set; }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        while (!buffer.IsEmpty)
        {
            if (_file is null) Open();
            var room = (int)Math.Min(buffer.Length, partBytes - _inPart);
            var chunk = buffer[..room];
            _file!.Write(chunk);
            _hash!.AppendData(chunk);
            _inPart += room;
            Total += room;
            buffer = buffer[room..];
            if (_inPart >= partBytes) ClosePart();
        }
    }

    void Open()
    {
        var temp = Path.Combine(folder, $"{baseName}.{_done.Count:000}.writing");
        _file = File.Create(temp);
        _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        _inPart = 0;
    }

    void ClosePart()
    {
        if (_file is null) return;
        var temp = _file.Name;
        _file.Dispose();
        _file = null;
        _done.Add((temp, _inPart, Convert.ToHexStringLower(_hash!.GetHashAndReset())));
        _hash.Dispose();
        _hash = null;
    }

    /// <summary>Closes the last part and gives every part its final name.</summary>
    public IReadOnlyList<AssetPart> Finish()
    {
        ClosePart();
        if (_done.Count == 0) throw new ReleaseException("nothing was written");
        var parts = new List<AssetPart>();
        for (var i = 0; i < _done.Count; i++)
        {
            var (temp, size, sha) = _done[i];
            var name = _done.Count == 1 ? baseName : $"{baseName}.{i + 1:000}";
            var final = Path.Combine(folder, name);
            File.Move(temp, final, overwrite: true);
            parts.Add(new AssetPart(name, size, sha));
        }
        _done.Clear();
        return parts;
    }

    /// <summary>Removes whatever was written when an export fails part way.</summary>
    public void Abandon()
    {
        ClosePart();
        foreach (var (temp, _, _) in _done)
        {
            try { File.Delete(temp); } catch (IOException) { /* a leftover is better than a crash */ }
        }
        _done.Clear();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _file?.Dispose(); _hash?.Dispose(); }
        base.Dispose(disposing);
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => Total;
    public override long Position { get => Total; set => throw new NotSupportedException(); }
    public override void Flush() => _file?.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>Reads a list of files one after another as one stream: the parts joined back together.</summary>
internal sealed class ConcatStream(IReadOnlyList<string> files) : Stream
{
    int _index;
    FileStream? _current;

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        while (true)
        {
            if (_current is null)
            {
                if (_index >= files.Count) return 0;
                _current = File.OpenRead(files[_index++]);
            }
            var read = _current.Read(buffer);
            if (read > 0) return read;
            _current.Dispose();
            _current = null;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _current?.Dispose();
        base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => files.Sum(f => new FileInfo(f).Length);
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
