using System.Diagnostics;
using System.Runtime.Versioning;

namespace GateKit.Deck.Windows;

/// <summary>Why a frame could not be shown, for the tile to say something honest.</summary>
internal enum MirrorTrouble
{
    None,
    /// <summary>The game shares nothing yet: no frame has been copied.</summary>
    NoFrame,
    /// <summary>The frame's format cannot go through a swap chain of ours.</summary>
    Format,
    /// <summary>Direct3D refused something; <see cref="MirrorView.Why"/> says what.</summary>
    Refused,
}

/// <summary>
/// The host's graphics device: one for the whole deck, on the same adapter the game's texture
/// lives on - a texture shared between two adapters is not shared at all.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class MirrorDevice : IDisposable
{
    MirrorDevice(nint factory, nint device, nint device1, nint context, ulong luid)
    {
        Factory = factory;
        Device = device;
        Device1 = device1;
        Context = context;
        AdapterLuid = luid;
    }

    public nint Factory { get; private set; }
    public nint Device { get; private set; }
    public nint Device1 { get; private set; }
    public nint Context { get; private set; }

    /// <summary>The adapter this device is on; a game on another one cannot be mirrored by it.</summary>
    public ulong AdapterLuid { get; }

    /// <summary>A device on the adapter with that LUID (0: whichever DXGI names first), or null
    /// with the reason, which is what the tile then says.</summary>
    public static MirrorDevice? Create(ulong luid, out string why)
    {
        why = "";
        nint factory = 0, adapter = 0, device = 0, device1 = 0, context = 0;
        try
        {
            var iid = Mirror.IID_IDXGIFactory2;
            nint made;
            var hr = Mirror.CreateDXGIFactory1(&iid, &made);
            if (hr < 0)
            {
                why = $"DXGI has no factory here (0x{hr:x8})";
                return null;
            }
            factory = made;

            for (uint index = 0; Mirror.EnumAdapters(factory, index, out var candidate) >= 0; index++)
            {
                if (Mirror.GetAdapterDescription(candidate, out var description) >= 0)
                {
                    var found = ((ulong)(uint)description.LuidHigh << 32) | description.LuidLow;
                    if (luid == 0 || found == luid)
                    {
                        adapter = candidate;
                        break;
                    }
                }
                Mirror.Release(candidate);
            }
            if (adapter == 0)
            {
                why = "the graphics card the game draws on is not one this machine lists";
                return null;
            }

            var levels = stackalloc int[] { 0xb100, 0xb000 };   // 11_1, 11_0
            int level;
            nint madeDevice, madeContext;
            hr = Mirror.D3D11CreateDevice(adapter, Mirror.DriverTypeUnknown, 0, Mirror.CreateDeviceBgraSupport,
                levels, 2, Mirror.SdkVersion, &madeDevice, &level, &madeContext);
            if (hr < 0)
            {
                why = $"Direct3D 11 would not start on that card (0x{hr:x8})";
                return null;
            }
            device = madeDevice;
            context = madeContext;

            var qi = Mirror.QueryInterface(device, Mirror.IID_ID3D11Device1, out device1);
            if (qi < 0 || device1 == 0)
            {
                why = $"this Direct3D has no ID3D11Device1, so a shared texture cannot be opened by name (0x{qi:x8})";
                return null;
            }

            var adapterLuid = Mirror.GetAdapterDescription(adapter, out var desc) >= 0
                ? ((ulong)(uint)desc.LuidHigh << 32) | desc.LuidLow : 0;
            var mirror = new MirrorDevice(factory, device, device1, context, adapterLuid);
            factory = device = device1 = context = 0;    // the device owns them now
            return mirror;
        }
        finally
        {
            Mirror.Let(ref adapter);
            Mirror.Let(ref context);
            Mirror.Let(ref device1);
            Mirror.Let(ref device);
            Mirror.Let(ref factory);
        }
    }

    public void Dispose()
    {
        var context = Context;
        var device1 = Device1;
        var device = Device;
        var factory = Factory;
        Context = Device1 = Device = Factory = 0;
        Mirror.Let(ref context);
        Mirror.Let(ref device1);
        Mirror.Let(ref device);
        Mirror.Let(ref factory);
    }
}

/// <summary>
/// One tile's picture: the game's shared texture opened on our device, and a swap chain on the
/// tile's own child window that it is copied into.
///
/// There is no shader and no geometry here, which is the whole point of the drawing path chosen
/// (<c>src/GateKit.Deck/README.md</c> says why): the swap chain is made at the game's own
/// resolution with DXGI_SCALING_STRETCH, so showing a frame is one CopyResource and one Present,
/// and DXGI does the scaling to the tile on the way to the screen.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class MirrorView : IDisposable
{
    readonly MirrorDevice _device;

    nint _window;
    nint _swapChain;
    nint _texture, _mutex;
    uint _generation;
    uint _width, _height;
    int _format, _swapChainFormat;
    ulong _lastFrame;
    bool _colourSpaceSet;

    public MirrorView(MirrorDevice device) => _device = device;

    /// <summary>The child window this draws into; the deck owns it and moves it.</summary>
    public nint Window { get => _window; set => _window = value; }

    /// <summary>What went wrong last, for the tile's caption.</summary>
    public string Why { get; private set; } = "";

    /// <summary>Frames shown, and what they cost.</summary>
    public long Shown { get; private set; }
    public double LatencyMillisecondsTotal { get; private set; }
    public long LatencySamples { get; private set; }

    /// <summary>Shows the newest frame if there is one. False when there was nothing new to show -
    /// which is the usual answer and costs a single memory read.</summary>
    public bool Draw(FrameShare share, out MirrorTrouble trouble)
    {
        trouble = MirrorTrouble.None;
        var header = share.Header;
        if (header.Generation != _generation)
        {
            Let();
            _generation = header.Generation;
            if (header.Width == 0 || share.TextureName.Length == 0)
            {
                trouble = MirrorTrouble.NoFrame;
                return false;
            }
            if (!Open(share, header, out trouble)) return false;
        }
        if (_texture == 0)
        {
            trouble = MirrorTrouble.NoFrame;
            return false;
        }
        if (_window == 0)
        {
            Why = "the tile has no window to draw in";
            return false;
        }
        if (header.Frame == _lastFrame)
        {
            if (Why.Length == 0) Why = "the game has shown no new frame since the last one drawn";
            return false;
        }
        if (!Chain(header, out trouble)) return false;

        // A millisecond at most: a frame we cannot have this moment is not worth waiting for, and
        // the game must never be held up by us holding its texture.
        var acquired = Mirror.AcquireSync(_mutex, 0, 1);
        if (acquired != 0)
        {
            Why = $"the game is holding its frame texture (0x{acquired:x8})";
            return false;
        }
        try
        {
            var buffer = Mirror.GetBuffer(_swapChain, 0, Mirror.IID_ID3D11Texture2D, out var back);
            if (buffer < 0)
            {
                Why = $"the tile's swap chain has no back buffer (0x{buffer:x8})";
                return false;
            }
            Mirror.CopyResource(_device.Context, back, _texture);
            Mirror.Release(back);
        }
        finally { Mirror.ReleaseSync(_mutex, 0); }

        Mirror.Present(_swapChain, 0, 0);
        _lastFrame = header.Frame;
        Shown++;
        Why = "";
        if (header.QpcFrequency != 0 && header.FrameQpc != 0)
        {
            var ticks = (long)((ulong)Stopwatch.GetTimestamp() - header.FrameQpc);
            if (ticks > 0 && ticks < (long)header.QpcFrequency)   // a second: anything more is a stale reading
            {
                LatencyMillisecondsTotal += ticks * 1000.0 / header.QpcFrequency;
                LatencySamples++;
            }
        }
        return true;
    }

    bool Open(FrameShare share, FrameShareHeader header, out MirrorTrouble trouble)
    {
        trouble = MirrorTrouble.None;
        if (Mirror.SwapChainFormat((int)header.Format) == Mirror.FormatUnknown)
        {
            Why = $"the game's frame is in format {header.Format}, which a tile cannot show";
            trouble = MirrorTrouble.Format;
            return false;
        }
        var hr = Mirror.OpenSharedResourceByName(_device.Device1, share.TextureName,
            Mirror.SharedResourceRead | Mirror.SharedResourceWrite, Mirror.IID_ID3D11Texture2D, out _texture);
        if (hr < 0 || _texture == 0)
        {
            _texture = 0;
            Why = $"the game's frame texture could not be opened (0x{hr:x8})";
            trouble = MirrorTrouble.Refused;
            return false;
        }
        if (Mirror.QueryInterface(_texture, Mirror.IID_IDXGIKeyedMutex, out _mutex) < 0 || _mutex == 0)
        {
            Let();
            Why = "the game's frame texture has no keyed mutex";
            trouble = MirrorTrouble.Refused;
            return false;
        }
        _width = header.Width;
        _height = header.Height;
        _format = (int)header.Format;
        Why = "";
        return true;
    }

    /// <summary>The swap chain for the size the game is drawing at, made or resized as needed.</summary>
    bool Chain(FrameShareHeader header, out MirrorTrouble trouble)
    {
        trouble = MirrorTrouble.None;
        var wanted = Mirror.SwapChainFormat(_format);
        if (_swapChain != 0 && _swapChainFormat == wanted && _width == header.Width && _height == header.Height) return true;

        if (_swapChain != 0 && _swapChainFormat == wanted)
        {
            if (Mirror.ResizeBuffers(_swapChain, 0, header.Width, header.Height, 0, 0) >= 0)
            {
                _width = header.Width;
                _height = header.Height;
                return true;
            }
            Mirror.Let(ref _swapChain);
        }

        Mirror.Let(ref _swapChain);
        _colourSpaceSet = false;
        // The buffers are the game's own size and the window is the tile's; DXGI scales between
        // them on the way to the screen, which is why nothing here has to draw a quad. The flip
        // model is asked for first and the older one is the fallback: which of them a driver will
        // give for a child window of this size and format is not worth predicting.
        var hr = 0;
        foreach (var (effect, buffers) in new[]
                 {
                     (Mirror.SwapEffectFlipDiscard, 2u),
                     (Mirror.SwapEffectFlipSequential, 2u),
                     (Mirror.SwapEffectDiscard, 1u),
                 })
        {
            var description = new Mirror.SwapChainDescription
            {
                Width = header.Width,
                Height = header.Height,
                Format = wanted,
                Sample = new Mirror.SampleDescription { Count = 1, Quality = 0 },
                BufferUsage = Mirror.UsageRenderTargetOutput,
                BufferCount = buffers,
                Scaling = Mirror.ScalingStretch,
                SwapEffect = effect,
                AlphaMode = Mirror.AlphaModeIgnore,
            };
            hr = Mirror.CreateSwapChainForHwnd(_device.Factory, _device.Device, _window, description, out _swapChain);
            if (hr >= 0 && _swapChain != 0) break;
            _swapChain = 0;
        }
        if (_swapChain == 0)
        {
            Why = $"a swap chain for the tile could not be made (0x{hr:x8})";
            trouble = MirrorTrouble.Refused;
            return false;
        }
        Mirror.MakeWindowAssociation(_device.Factory, _window,
            Mirror.WindowAssociationNoWindowChanges | Mirror.WindowAssociationNoAltEnter);
        _swapChainFormat = wanted;
        _width = header.Width;
        _height = header.Height;
        ColourSpace(header);
        return true;
    }

    /// <summary>An HDR10 frame is PQ-encoded Rec. 2020, and is passed through as such when the
    /// screen the deck is on can take it. Converting it here would mean a shader pass per tile; the
    /// desktop compositor already does exactly this conversion for every HDR window on the screen,
    /// and the game's own window beside the deck is being shown that way too.</summary>
    void ColourSpace(FrameShareHeader header)
    {
        if (_colourSpaceSet || header.Hdr10 == 0 || _swapChainFormat != Mirror.FormatR10G10B10A2Unorm) return;
        if (Mirror.QueryInterface(_swapChain, Mirror.IID_IDXGISwapChain3, out var chain3) < 0 || chain3 == 0) return;
        try
        {
            if (Mirror.CheckColourSpaceSupport(chain3, Mirror.ColourSpaceRgbFullG2084NoneP2020, out var support) >= 0
                && (support & 1) != 0)
            {
                Mirror.SetColourSpace(chain3, Mirror.ColourSpaceRgbFullG2084NoneP2020);
                _colourSpaceSet = true;
            }
            else
            {
                Why = "the game is in HDR and this screen is not; the tile's colours are approximate";
            }
        }
        finally { Mirror.Release(chain3); }
    }

    void Let()
    {
        Mirror.Let(ref _mutex);
        Mirror.Let(ref _texture);
        _lastFrame = 0;
    }

    public void Dispose()
    {
        Let();
        Mirror.Let(ref _swapChain);
    }
}
