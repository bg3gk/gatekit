using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace GateKit.Deck.Windows;

/// <summary>
/// What the game publishes about the frame it is sharing. The game's end of this is
/// <c>Extender/Client/IMGUI/FrameShare.inl</c> in GateKit's Script Extender build, and the two
/// declarations have to stay the same shape; the magic and version below are the guard against
/// their drifting apart.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct FrameShareHeader
{
    public const uint ExpectedMagic = 0x53464B47;    // 'GKFS'
    public const uint ExpectedVersion = 1;

    public uint Magic, Version, Enabled, Width, Height, Format, Hdr10, Generation, Reserved0, Reserved1;
    public ulong Frame, Presents, FrameQpc, QpcFrequency, AdapterLuid, CopyTicks, CopyCount, Skipped;
    public fixed char TextureName[64];
}

/// <summary>
/// The reader's end of the shared block: open it by the game's process id, then read it as often as
/// wanted - it is a page of memory, so a read costs nothing and no bridge call is involved.
/// <see cref="FrameShareHeader.Frame"/> going up is a new frame, <see cref="FrameShareHeader.Generation"/>
/// going up means the texture was made again (a resize) and has to be opened under its new name.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class FrameShare : IDisposable
{
    readonly MemoryMappedFile _file;
    readonly MemoryMappedViewAccessor _view;
    readonly byte* _memory;

    FrameShare(MemoryMappedFile file, MemoryMappedViewAccessor view, byte* memory)
    {
        _file = file;
        _view = view;
        _memory = memory;
    }

    public static string SectionName(int pid) => $"Local\\GateKitFrameShare_{pid}";

    /// <summary>Opens what the game of that process publishes, or null when it publishes nothing
    /// (no GateKit extender build, a renderer that cannot share, or the game is only starting).</summary>
    public static FrameShare? Open(int pid)
    {
        MemoryMappedFile? file = null;
        MemoryMappedViewAccessor? view = null;
        try
        {
            file = MemoryMappedFile.OpenExisting(SectionName(pid), MemoryMappedFileRights.Read);
            view = file.CreateViewAccessor(0, sizeof(FrameShareHeader), MemoryMappedFileAccess.Read);
            byte* memory = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref memory);
            var share = new FrameShare(file, view, memory);
            var header = share.Header;
            if (header.Magic != FrameShareHeader.ExpectedMagic || header.Version != FrameShareHeader.ExpectedVersion)
            {
                share.Dispose();
                return null;
            }
            return share;
        }
        catch (Exception e) when (e is FileNotFoundException or IOException or UnauthorizedAccessException)
        {
            view?.Dispose();
            file?.Dispose();
            return null;
        }
    }

    /// <summary>The block as it is now. A copy of a couple of hundred bytes, so that nothing reads
    /// one field of one frame and another field of the next.</summary>
    public FrameShareHeader Header => *(FrameShareHeader*)_memory;

    /// <summary>The shared texture's name, or empty while the game is sharing nothing.</summary>
    public string TextureName => new(((FrameShareHeader*)_memory)->TextureName);

    public void Dispose()
    {
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _file.Dispose();
    }
}
