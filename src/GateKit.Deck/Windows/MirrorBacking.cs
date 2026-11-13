using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using GateKit.Bridge;
using GateKit.Platform;
using GateKit.Stand;

namespace GateKit.Deck.Windows;

/// <summary>
/// The backing that never touches the game's window: the game copies its finished frame into a
/// texture on the graphics card, and the deck draws that texture in the tile.
///
/// It is the other half of GateKit's Script Extender build - the game's end is
/// <c>Extender/Client/IMGUI/FrameShare.inl</c> there - and it is what lets a mirrored copy's window
/// be hidden altogether: what you see is the deck, not somebody else's window moved into it.
///
/// Nothing of the frame crosses into system memory and nothing is encoded. The game's cost is one
/// CopyResource per frame; the deck's is one CopyResource and one Present per frame it shows.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class MirrorBacking : ITileBacking, IDisposable
{
    const string TileClass = "GateKitTile";

    /// <summary>How long a copy that has agreed is given to share its first frame before the tile
    /// gives up on it and says so.</summary>
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a copy whose bridge is not answering is kept waiting for before the tile gives up.
    /// A quiet bridge is not a refusal: it is what a game sounds like while it starts, loads a
    /// level, or comes back from a crash at four in the morning. Treating it as a refusal is how a
    /// tile ended up in its own window for the rest of a night after one badly timed question.
    /// </summary>
    static readonly TimeSpan BridgePatience = TimeSpan.FromMinutes(4);

    /// <summary>How often a copy that was not ready is asked again.</summary>
    static readonly TimeSpan AskAgainEvery = TimeSpan.FromSeconds(3);

    /// <summary>The fastest a tile is drawn. The game may present faster; nobody watching a tile
    /// needs more, and every frame beyond this would be work thrown away.</summary>
    const double TileFramesPerSecond = 60.0;

    readonly StandSettings _settings;
    readonly IGamePlatform _platform;
    readonly TextWriter _out;
    readonly DeckConceal _conceal;

    readonly Lock _lock = new();
    readonly Dictionary<string, Mirrored> _held = new(StringComparer.OrdinalIgnoreCase);

    MirrorDevice? _device;
    string _deviceWhy = "";
    Thread? _thread;
    volatile bool _stopping;
    static bool _classRegistered;

    public MirrorBacking(StandSettings settings, IGamePlatform platform, TextWriter output, DeckConceal conceal)
    {
        _settings = settings;
        _platform = platform;
        _out = output;
        _conceal = conceal;
    }

    public string Name => "mirror";

    /// <summary>Why a copy is not sharing its frames, and whether that is worth waiting out.
    /// Temporary means the game is not ready to be asked; it says nothing about whether it can.</summary>
    readonly record struct NotMirroring(string Why, bool Temporary);

    /// <summary>One game being mirrored: its share, its view, the child window it is drawn in, and
    /// what was done to its own window.</summary>
    sealed class Mirrored
    {
        public int Pid;
        public nint Child;
        public MirrorView? View;
        public FrameShare? Share;
        public Task<NotMirroring?>? Asking;
        public Stopwatch Waited = Stopwatch.StartNew();
        /// <summary>When a copy that was not ready may be asked again, and what it last said.</summary>
        public DateTime AskAgainAt;
        public string? Waiting;
        public Concealment? Concealed;
        /// <summary>Set once something made mirroring impossible for this copy, so the tile says it
        /// and stops trying rather than waiting out its patience.</summary>
        public string? Refused;
        public TileBox Box;
        public bool Front = true;
        /// <summary>Where the tile's window was last actually put, so it is not put there again.</summary>
        public (int X, int Y, int Width, int Height) Placed = (int.MinValue, int.MinValue, 0, 0);
        public bool PlacedFront = true;
        public Tile? Tile;

        // What the last stretch measured, for the line the deck prints and the tile's caption
        public long Shown;
        public ulong Presents;
        public long LatencySamples;
        public double LatencyTotal;
        public Stopwatch Since = Stopwatch.StartNew();
    }

    // --- ITileBacking ---

    public TileShow Show(Tile tile, nint host, out string? why)
    {
        why = null;
        lock (_lock)
        {
            if (_held.TryGetValue(tile.Instance, out var held) && held.Pid == tile.Pid)
            {
                if (held.Share is not null) return TileShow.Shown;
                if (held.Asking is { IsCompleted: true })
                {
                    if (held.Asking.Result is { } no)
                    {
                        if (!no.Temporary)
                        {
                            why = no.Why;
                            Forget(tile.Instance);
                            return TileShow.Refused;
                        }
                        // Not ready to be asked. Wait, say so, and ask again - but not for ever.
                        held.Asking = null;
                        held.Waiting = no.Why;
                        held.AskAgainAt = DateTime.UtcNow + AskAgainEvery;
                        if (held.Waited.Elapsed > BridgePatience)
                        {
                            why = no.Why + $" (asked for {BridgePatience.TotalMinutes:0} minutes)";
                            Forget(tile.Instance);
                            return TileShow.Refused;
                        }
                        return TileShow.NotYet;
                    }
                    held.Asking = null;
                    held.Waiting = null;
                    held.Waited.Restart();
                }
                if (held.Asking is null && held.Waiting is not null)
                {
                    // Still waiting on the game itself; ask again when its turn comes round.
                    why = held.Waiting;
                    if (DateTime.UtcNow >= held.AskAgainAt)
                    {
                        var again = tile.Instance;
                        held.Asking = Task.Run(() => AskToMirror(again));
                    }
                    return TileShow.NotYet;
                }
                if (held.Asking is null && Attach(tile, held, host)) return TileShow.Shown;
                if (held.Refused is { } trouble)
                {
                    why = trouble;
                    Forget(tile.Instance);
                    return TileShow.Refused;
                }
                if (held.Waited.Elapsed > Patience)
                {
                    why = "it shared no frame in " + Patience.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)
                        + " s (is this GateKit's Script Extender build, on the DirectX 11 executable?)";
                    Forget(tile.Instance);
                    return TileShow.Refused;
                }
                return TileShow.NotYet;
            }

            if (tile.Pid <= 0) return TileShow.NotYet;
            Forget(tile.Instance);

            var started = new Mirrored { Pid = tile.Pid, Tile = tile };
            // The bridge turns it on in the game; it answers in a moment, and the tile waits.
            var instance = tile.Instance;
            started.Asking = Task.Run(() => AskToMirror(instance));
            _held[tile.Instance] = started;
            return TileShow.NotYet;
        }
    }

    public void Place(Tile tile, TileBox box, bool front)
    {
        lock (_lock)
        {
            if (!_held.TryGetValue(tile.Instance, out var held) || held.Child == 0) return;
            held.Box = box;
            held.Front = front;
            var inside = Letterbox(box.Inside, held);
            // Only when it has actually moved. The deck places every tile on every tick, and
            // moving a swap chain's window - even to where it already is, even only in the z
            // order - makes the window flash: the surface is dropped and nothing redraws it until
            // the game happens to present again. That is the blink a person sees while watching a
            // mirrored tile that is doing nothing at all.
            if (held.Placed == (inside.X, inside.Y, inside.Width, inside.Height) && held.PlacedFront == front) return;
            held.Placed = (inside.X, inside.Y, inside.Width, inside.Height);
            held.PlacedFront = front;
            Native.SetWindowPos(held.Child, front ? Native.HwndTop : Native.HwndBottom,
                inside.X, inside.Y, inside.Width, inside.Height, Native.SwpNoActivate);
        }
    }

    public void Drop(Tile tile)
    {
        lock (_lock)
        {
            if (!_held.ContainsKey(tile.Instance)) return;
            var instance = tile.Instance;
            Forget(instance);
            // The game stops copying frames it has nobody to give them to. It is told on another
            // thread; if it never hears, the next start clears it anyway.
            Task.Run(() => AskToStop(instance));
        }
    }

    /// <summary>Nothing to give the keyboard to: a mirrored copy's window may be hidden, and a
    /// hidden window cannot hold focus. The deck keeps the keyboard and forwards what it gets,
    /// which is <see cref="Key"/> below.</summary>
    public void Focus(Tile tile) { }

    public void Key(Tile tile, uint message, nint wParam, nint lParam)
    {
        // Posted messages reach a window that is hidden or off the screen exactly as they reach one
        // on it, which is why `gatekit key` never needed a visible window either.
        if (tile.Window != 0) Native.PostMessage(tile.Window, message, wParam, lParam);
    }

    /// <summary>Nothing: the tile's own child window draws the frames.</summary>
    public void Draw(nint deviceContext, Tile tile, TileBox box) { }

    // --- turning it on and off in the game ---

    /// <summary>Asks that copy to start sharing its frames. Null when it agreed; otherwise why not,
    /// which is what the tile then says.</summary>
    NotMirroring? AskToMirror(string instance)
    {
        try
        {
            var session = SessionFor(instance);
            if (session.State(Side.Client) is null)
                return new NotMirroring("waiting for the bridge in this copy to answer", Temporary: true);
            var answer = session.ClientAsync("return Ext.ClientRender.MirrorEnable(true)", TimeSpan.FromSeconds(10))
                .GetAwaiter().GetResult();
            if (answer is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<bool>(out var on) && on) return null;
            // An answer that is not "yes" is this build saying it cannot, which waiting will not change.
            return new NotMirroring("this copy's renderer will not share its frames (GateKit's Script Extender build "
                + "shares them from the DirectX 11 renderer only)", Temporary: false);
        }
        // A question that timed out or could not be delivered is about this moment, not about the
        // copy: the game may be loading a level, saving, or still starting. Ask again.
        catch (Exception e) when (e is StandException or IOException or TimeoutException)
        {
            return new NotMirroring(e.Message, Temporary: true);
        }
    }

    void AskToStop(string instance)
    {
        try
        {
            var session = SessionFor(instance);
            if (session.State(Side.Client) is null) return;
            session.ClientAsync("return Ext.ClientRender.MirrorEnable(false)", TimeSpan.FromSeconds(5))
                .GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is StandException or IOException or TimeoutException) { }
    }

    GameSession SessionFor(string instance) => new GateKit.Stand.Stand(
        _settings with { Instance = instance == DeckRecord.InstalledGame ? null : instance },
        _platform, TextWriter.Null).Session();

    // --- the tile's own window and view ---

    /// <summary>The copy has agreed; take its block, make the tile's window, and put its own window
    /// out of the way. False while it is still not sharing.</summary>
    bool Attach(Tile tile, Mirrored held, nint host)
    {
        held.Share ??= FrameShare.Open(held.Pid);
        if (held.Share is null) return false;
        if (held.Share.Header.Width == 0) return false;      // agreed, but no frame copied yet

        // Only now is the graphics card the game draws on known, and the deck's own device has to
        // be on that one: a texture shared across two adapters is not shared at all.
        if (Device(held.Share.Header.AdapterLuid) is null)
        {
            held.Refused = _deviceWhy;
            return false;
        }

        if (held.Child == 0)
        {
            RegisterTileClass();
            var inside = Letterbox(held.Box.Inside, held);
            held.Child = Native.CreateWindowEx(0, TileClass, null,
                Native.WsChild | Native.WsVisible | Native.WsClipChildren,
                inside.X, inside.Y, Math.Max(1, inside.Width), Math.Max(1, inside.Height),
                host, 0, Native.GetModuleHandle(null), 0);
            if (held.Child == 0) return false;
        }
        if (held.View is null)
        {
            held.View = new MirrorView(_device!) { Window = held.Child };
            held.Presents = held.Share.Header.Presents;   // the first report measures from here
            held.Since.Restart();
        }

        // The game's own window is found for two reasons: keys are posted to it, and it is what is
        // put out of sight. It is never adopted, moved into the deck or re-parented.
        tile.Window = _platform.FindGameWindow(held.Pid);
        if (tile.Window != 0 && held.Concealed is null && _conceal != DeckConceal.None)
            held.Concealed = Concealment.Apply(tile.Window, _conceal);

        Start();
        return true;
    }

    /// <summary>A tile's window does nothing of its own: the swap chain draws it and the deck moves
    /// it. Clicks go to the deck through WM_PARENTNOTIFY, as they do for a game's own window.</summary>
    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    static nint TileProcedure(nint window, uint message, nint wParam, nint lParam) =>
        Native.DefWindowProc(window, message, wParam, lParam);

    static void RegisterTileClass()
    {
        if (_classRegistered) return;
        _classRegistered = true;
        fixed (char* name = TileClass)
        {
            var windowClass = new Native.WindowClass
            {
                Size = (uint)sizeof(Native.WindowClass),
                WindowProcedure = (nint)(delegate* unmanaged<nint, uint, nint, nint, nint>)&TileProcedure,
                Instance = Native.GetModuleHandle(null),
                Cursor = Native.LoadCursor(0, (nint)Native.IdcArrow),
                Background = 0,     // the swap chain fills it; no flash of white behind a game
                ClassName = (nint)name,
            };
            Native.RegisterClassEx(&windowClass);
        }
    }

    /// <summary>Where the frame goes inside a tile: as large as it fits with the game's own shape
    /// kept, so a tile that is not the game's shape has a margin rather than a stretched picture.</summary>
    static TileBox Letterbox(TileBox box, Mirrored held)
    {
        var header = held.Share?.Header;
        if (header is not { Width: > 0, Height: > 0 }) return box;
        var scale = Math.Min((double)box.Width / header.Value.Width, (double)box.Height / header.Value.Height);
        var width = Math.Max(1, (int)(header.Value.Width * scale));
        var height = Math.Max(1, (int)(header.Value.Height * scale));
        return new TileBox(box.X + (box.Width - width) / 2, box.Y + (box.Height - height) / 2, width, height);
    }

    MirrorDevice? Device(ulong luid)
    {
        if (_device is not null) return _device;
        if (_deviceWhy.Length > 0) return null;
        _device = MirrorDevice.Create(luid, out var why);
        if (_device is null) _deviceWhy = why;
        return _device;
    }

    void Forget(string instance)
    {
        if (!_held.Remove(instance, out var held)) return;
        held.View?.Dispose();
        held.Share?.Dispose();
        held.Concealed?.Undo();
        if (held.Child != 0) Native.DestroyWindow(held.Child);
        if (held.Tile is { } tile) tile.Note = null;
    }

    // --- the thread that shows the frames ---

    void Start()
    {
        if (_thread is not null) return;
        _stopping = false;
        _thread = new Thread(Pump) { IsBackground = true, Name = "gatekit-mirror" };
        _thread.Start();
    }

    /// <summary>
    /// One thread for every tile. It never renders of its own accord: it reads a word of shared
    /// memory per tile, and only a tile whose game has shown a new frame since the last one costs
    /// anything. Between rounds it sleeps, so an idle deck is idle.
    /// </summary>
    void Pump()
    {
        var report = Stopwatch.StartNew();
        while (!_stopping)
        {
            var drew = false;
            lock (_lock)
            {
                foreach (var held in _held.Values)
                {
                    if (held.View is null || held.Share is null || !held.Front) continue;
                    // A tile does not need more frames than an eye does. The rate is the frames
                    // drawn since the last report over how long that has been - not the total,
                    // which would climb until the tile throttled itself to a stop.
                    var since = held.Since.Elapsed.TotalSeconds;
                    if (since > 0 && (held.View.Shown - held.Shown) / since > TileFramesPerSecond) continue;
                    if (held.View.Draw(held.Share, out _)) drew = true;
                }
                if (report.Elapsed > TimeSpan.FromSeconds(10))
                {
                    report.Restart();
                    Report();
                }
            }
            // 3 ms: a frame is noticed within that, and a round that finds nothing costs one
            // memory read per tile.
            Thread.Sleep(drew ? 1 : 3);
        }
    }

    /// <summary>What mirroring is costing and reaching, said once every ten seconds while it runs.
    /// The numbers are what <c>docs/design/deck.md</c> asks to be measured, taken where they
    /// actually happen rather than guessed at.</summary>
    void Report()
    {
        foreach (var (instance, held) in _held)
        {
            if (held.View is null || held.Share is null) continue;
            var header = held.Share.Header;
            var seconds = held.Since.Elapsed.TotalSeconds;
            if (seconds < 1) continue;

            var shown = held.View.Shown - held.Shown;
            var presents = header.Presents - held.Presents;
            var samples = held.View.LatencySamples - held.LatencySamples;
            var latency = held.View.LatencyMillisecondsTotal - held.LatencyTotal;
            held.Shown = held.View.Shown;
            held.Presents = header.Presents;
            held.LatencySamples = held.View.LatencySamples;
            held.LatencyTotal = held.View.LatencyMillisecondsTotal;
            held.Since.Restart();

            var tileRate = shown / seconds;
            var gameRate = presents / seconds;
            var copy = header.CopyCount == 0 || header.QpcFrequency == 0
                ? 0.0
                : (double)header.CopyTicks * 1000.0 / header.QpcFrequency / header.CopyCount;
            var note = string.Create(CultureInfo.InvariantCulture, $"mirror {tileRate:0} fps");
            if (held.Tile is { } tile) tile.Note = note;
            var trouble = shown != 0 ? ""
                : !held.Front ? " - behind the tile held open, so it is not drawn"
                : held.View.Why.Length > 0 ? $" - {held.View.Why}"
                : "";
            _out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{instance}: tile {tileRate:0.0} fps, {(samples == 0 ? 0 : latency / samples):0.0} ms present to screen; "
                + $"game {gameRate:0.0} fps, copy {copy:0.000} ms, {header.Skipped} frames skipped{trouble}"));
        }
    }

    public void Dispose()
    {
        _stopping = true;
        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;
        lock (_lock)
        {
            foreach (var instance in _held.Keys.ToList()) Forget(instance);
            _device?.Dispose();
            _device = null;
        }
    }
}
