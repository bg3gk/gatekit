using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using GateKit.Bridge;
using GateKit.Platform;
using GateKit.Stand;

namespace GateKit.Deck.Windows;

/// <summary>
/// The deck itself: one window that holds every running game. It finds the games by itself (the
/// stand starts them whether a deck runs or not), adopts each one's window into a tile, and gives
/// every window back when it closes.
///
/// It is a plain Win32 window - a class, a message loop and a few lines of GDI for the captions -
/// and no user interface framework; the project's README says why.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class DeckWindow
{
    const string ClassName = "GateKitDeck";
    const nint TickTimer = 1;

    /// <summary>Ticks between looking again at whether a copy still has the cap it was given.
    /// Twenty-five of the deck's 400 ms ticks: ten seconds, which is quick enough that nobody
    /// watches a copy run away with the machine and rare enough to cost nothing.</summary>
    const int ReapplyEvery = 25;

    static DeckWindow? _running;

    readonly StandSettings _settings;
    readonly IGamePlatform _platform;
    readonly DeckRecord _record;
    readonly TextWriter _out;
    readonly TileSet _tiles = new();
    readonly Dictionary<string, ITileBacking> _backings;
    readonly string _default;
    readonly FrameBudget _budget;
    /// <summary>The cap each copy was last asked for, so that an unchanged budget costs nothing.</summary>
    readonly Dictionary<string, int> _caps = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The background throttle each copy had before this deck touched it, to give back
    /// when it leaves (a copy left uncapped in the background would draw flat out for nobody).</summary>
    readonly Dictionary<string, int> _hadThrottle = new(StringComparer.OrdinalIgnoreCase);
    readonly SoundFocus _sound;
    /// <summary>Which copies this deck may take hold of; <see cref="Adoption"/> is the rule, and
    /// <see cref="_invited"/> the copies named by hand that it also allows.</summary>
    readonly bool _adoptAll;
    readonly HashSet<string> _invited = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Copies this deck has already said it is leaving alone, so it says it once.</summary>
    readonly HashSet<string> _announced = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Copies this deck has already said it cannot budget or quieten, for the same
    /// reason: a thing said once is a warning, said every tick it is noise.</summary>
    readonly HashSet<string> _complained = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Whether each copy was last told to be heard, so that nothing is said twice.</summary>
    readonly Dictionary<string, bool> _heard = new(StringComparer.OrdinalIgnoreCase);

    nint _window;
    nint _background, _captionBrush, _focusBrush, _emptyBrush, _trackBrush, _levelBrush, _mutedBrush;
    Tile? _focus;
    bool _clicked;
    int _ticks;
    string _title = "";

    public DeckWindow(StandSettings settings, IGamePlatform platform, TextWriter output)
    {
        _settings = settings;
        _platform = platform;
        _record = new DeckRecord(settings.StateRoot);
        _out = output;
        // Two backings, chosen per tile and switchable at run time (`window mirror|adopt`):
        // `adopt` takes the game's own window into the tile, `mirror` draws the frames the game
        // copies into a shared texture and needs no window to be seen at all. docs/design/deck.md
        // has what each costs and why adopt is still the default.
        _backings = new Dictionary<string, ITileBacking>(StringComparer.OrdinalIgnoreCase)
        {
            ["adopt"] = new AdoptBacking(platform),
            ["mirror"] = new MirrorBacking(settings, platform, output, settings.DeckConceal),
        };
        _default = _backings.ContainsKey(settings.DeckBacking) ? settings.DeckBacking : "adopt";
        _budget = FrameBudget.Parse(settings.DeckFps);
        _sound = SoundFocus.Parse(settings.DeckSound);
        _adoptAll = string.Equals(settings.DeckAdopt, "all", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The backing that fills a tile: the one it was told to use, else the deck's default.</summary>
    ITileBacking Backing(Tile tile) => _backings[tile.Backing ??= _default];

    /// <summary>Opens the deck and serves it until its window closes.</summary>
    public void Run()
    {
        if (_record.Running() is { } other)
            throw new StandException($"a deck is already running (pid {other.Pid}); one deck holds every game");

        Native.SetThreadDpiAwarenessContext(Native.DpiAwarenessPerMonitorV2);
        _background = Native.CreateSolidBrush(Native.Colour(22, 22, 26));
        _captionBrush = Native.CreateSolidBrush(Native.Colour(44, 44, 54));
        _focusBrush = Native.CreateSolidBrush(Native.Colour(70, 92, 120));
        _emptyBrush = Native.CreateSolidBrush(Native.Colour(32, 32, 38));
        _trackBrush = Native.CreateSolidBrush(Native.Colour(26, 26, 32));
        _levelBrush = Native.CreateSolidBrush(Native.Colour(150, 170, 200));
        _mutedBrush = Native.CreateSolidBrush(Native.Colour(120, 60, 60));
        Register();

        _running = this;
        // The deck is the one window that must not do what this whole thing exists to stop:
        // WS_EX_NOACTIVATE so that appearing never makes it the active window, shown with
        // SW_SHOWNOACTIVATE, and nothing here ever calls SetForegroundWindow on itself. The style
        // goes away the first time the user clicks a tile - that click is the one moment
        // activation is wanted, and it comes from the user, not from a window appearing.
        _window = Native.CreateWindowEx(Native.WsExNoActivate | Native.WsExAppWindow, ClassName, "GateKit deck",
            Native.WsOverlappedWindow | Native.WsClipChildren,
            Native.CwUseDefault, Native.CwUseDefault, 1360, 840, 0, 0, Native.GetModuleHandle(null), 0);
        if (_window == 0) throw new StandException($"the deck window could not be created: Win32 error {Marshal.GetLastPInvokeError()}");

        var wasInFront = Native.GetForegroundWindow();
        Native.ShowWindow(_window, Native.SwShowNoActivate);
        // Checked here, every time anyone opens a deck, because this is the one thing the deck
        // exists to stop and it would come back silently (see "Checking it cannot come back" in
        // the project's README).
        //
        // And not only checked. Windows hands the foreground to a process that has just been
        // started by the process holding it, whatever a window's styles say, and it does so a
        // moment later rather than inside ShowWindow - so one reading here saw nothing while the
        // person watching saw the deck take their keyboard. The foreground is put back, for as
        // long as it takes the system to settle.
        _wasInFront = wasInFront != _window ? wasInFront : 0;
        GiveTheForegroundBack();
        Native.SetTimer(_window, TickTimer, 400, 0);
        _record.Announce(Environment.ProcessId, _window);
        _out.WriteLine($"deck open; every game started from now on takes a tile. State: {_record.Folder}");
        _out.WriteLine($"frame budget: {_budget} (GATEKIT_DECK_FPS; `gatekit window fps` overrides one copy)");
        _out.WriteLine($"sound: {_sound} (GATEKIT_DECK_SOUND; a tile's own mute pins it)");
        _out.WriteLine(_adoptAll
            ? "taking hold of every copy on this machine, including ones this stand did not start (--all)"
            : "taking hold of the copies this stand started; anything else is listed and left alone (--all takes them too)");

        // Ctrl+C in the terminal that started it closes the deck the same way its own button does,
        // so the games get their windows back.
        Console.CancelKeyPress += Interrupt;
        try
        {
            Native.Message message;
            while (Native.GetMessage(&message, 0, 0, 0) > 0)
            {
                Native.TranslateMessage(&message);
                Native.DispatchMessage(&message);
            }
        }
        finally
        {
            Console.CancelKeyPress -= Interrupt;
            ReleaseEverything();
            foreach (var backing in _backings.Values.OfType<IDisposable>()) backing.Dispose();
            _record.Forget();
            _running = null;
            foreach (var brush in new[] { _background, _captionBrush, _focusBrush, _emptyBrush, _trackBrush, _levelBrush, _mutedBrush })
                Native.DeleteObject(brush);
            _out.WriteLine("deck closed; every window is back on the desktop");
        }
    }

    void Interrupt(object? sender, ConsoleCancelEventArgs e)
    {
        e.Cancel = true;
        Native.PostMessage(_window, Native.WmDeckQuit, 0, 0);
    }

    void Register()
    {
        fixed (char* name = ClassName)
        {
            var windowClass = new Native.WindowClass
            {
                Size = (uint)sizeof(Native.WindowClass),
                Style = Native.CsHRedraw | Native.CsVRedraw,
                WindowProcedure = (nint)(delegate* unmanaged<nint, uint, nint, nint, nint>)&Procedure,
                Instance = Native.GetModuleHandle(null),
                Cursor = Native.LoadCursor(0, (nint)Native.IdcArrow),
                Icon = Logo(256),
                SmallIcon = Logo(16),
                Background = 0,            // WM_PAINT fills it; no flash of white behind a game
                ClassName = (nint)name,
            };
            if (Native.RegisterClassEx(&windowClass) == 0 && Marshal.GetLastPInvokeError() != 1410) // already registered
                throw new StandException($"the deck window class could not be registered: Win32 error {Marshal.GetLastPInvokeError()}");
        }
    }

    /// <summary>GateKit's own logo, at the size Windows asks for, read from the repository's brand
    /// assets rather than kept as a copy of its own. Without the file the window simply has the
    /// system's default icon.</summary>
    static nint Logo(int size)
    {
        if (StandSettings.RepositoryRoot() is not { } repository) return 0;
        // The medallion, transparent outside its gold ring: what belongs on a window and in a
        // taskbar. assets/brand/logo.ico is the square mark on the brand field, for other places.
        var file = Path.Combine(repository, "assets", "brand", "logo-round.ico");
        return File.Exists(file)
            ? Native.LoadImage(0, file, Native.ImageIcon, size, size, Native.LrLoadFromFile | Native.LrShared)
            : 0;
    }

    [UnmanagedCallersOnly]
    static nint Procedure(nint window, uint message, nint wParam, nint lParam)
    {
        var deck = _running;
        if (deck is not null && deck.Handle(window, message, wParam, lParam, out var answer)) return answer;
        return Native.DefWindowProc(window, message, wParam, lParam);
    }

    bool Handle(nint window, uint message, nint wParam, nint lParam, out nint answer)
    {
        answer = 0;
        switch (message)
        {
            case Native.WmTimer when wParam == TickTimer:
                Tick();
                return true;
            case Native.WmSize:
                Place();
                return true;
            case Native.WmPaint:
                Paint();
                return true;
            case Native.WmEraseBackground:
                answer = 1;   // WM_PAINT does it, with the games clipped out
                return true;
            case Native.WmLButtonDown:
                Focus(Low(lParam), High(lParam));
                return true;
            case Native.WmParentNotify when (uint)(wParam & 0xFFFF) == Native.WmLButtonDown:
                Focus(Low(lParam), High(lParam));
                return false;   // the game gets the click as well: it is its own window
            case Native.WmKeyDown or Native.WmKeyUp or Native.WmChar or Native.WmSysKeyDown or Native.WmSysKeyUp:
                // The deck holds the keyboard until a tile is clicked, and then passes what it is
                // given to that game - the same posted messages `gatekit key` uses.
                if (_focus is { } tile) Backing(tile).Key(tile, message, wParam, lParam);
                return true;
            case Native.WmDeckQuit or Native.WmClose or Native.WmEndSession:
                ReleaseEverything();
                if (message != Native.WmEndSession) Native.DestroyWindow(window);
                return true;
            case Native.WmDestroy:
                Native.KillTimer(window, TickTimer);
                Native.PostQuitMessage(0);
                return true;
        }
        return false;
    }

    static int Low(nint value) => (short)(value & 0xFFFF);
    static int High(nint value) => (short)((value >> 16) & 0xFFFF);

    // --- what the deck does every tick ---

    void Tick()
    {
        foreach (var request in _record.Take()) Apply(request);

        var now = DateTime.UtcNow;
        var changes = _tiles.Observe(Ours(DeckRecord.Copies(_settings, _platform)), _record.Labels(), now);
        foreach (var tile in changes.ToDrop) Give(tile);
        foreach (var tile in changes.ToShow)
        {
            switch (Backing(tile).Show(tile, _window, out var why))
            {
                case TileShow.Shown:
                    tile.Why = null;
                    _tiles.Shown(tile, now);
                    _out.WriteLine($"{tile.Instance}: pid {tile.Pid} is in the deck ({Backing(tile).Name})");
                    break;
                case TileShow.NotYet:
                    // Still coming. The reason is worth keeping even though nothing is printed:
                    // this is what `window list` shows for a tile that is taking its time.
                    tile.Why = why;
                    break;
                case TileShow.Refused:
                    tile.Why = why;
                    _tiles.Stubborn(tile, now);
                    _out.WriteLine($"{tile.Instance}: it keeps its own window ({why})");
                    break;
            }
        }

        GiveTheForegroundBack();
        Place();
        Spend();
        Listen();
        _record.WriteTiles(_tiles.Records());
        if (_ticks++ % 4 == 0) _record.Announce(Environment.ProcessId, _window);

        var shown = _tiles.Tiles.Count(t => t.State == TileState.Shown);
        var title = _tiles.Maximized is { } held
            ? $"GateKit deck - {held} held open ({_tiles.Tiles.Count} in all)"
            : string.Create(CultureInfo.InvariantCulture, $"GateKit deck - {shown} of {_tiles.Tiles.Count}");
        if (title != _title) Native.SetWindowText(_window, _title = title);
    }

    /// <summary>The copies this deck may take hold of. A deck adopts a window, caps a frame rate
    /// and mutes the sound, and doing all three to a game somebody else started - another person,
    /// another session, a copy of the installed game being played - is not something to do by
    /// accident. So by default it takes only what this stand started
    /// (<see cref="GameCopy.Mine"/>), says once what it is leaving alone, and does nothing to it.
    ///
    /// Two ways past that, both of them someone asking: `gatekit deck --all`, and naming a copy
    /// with `window maximize|mirror|adopt`, which is as plain an ask as there is.</summary>
    IReadOnlyList<GameCopy> Ours(IReadOnlyList<GameCopy> copies)
    {
        var rule = new Adoption(_adoptAll) { Invited = _invited };
        foreach (var copy in rule.NotOurs(copies))
        {
            if (!_announced.Add(copy.Instance)) continue;
            _out.WriteLine($"{copy.Instance}: pid {copy.Pid} is running but this stand did not start it, "
                + "so the deck is leaving it alone - its window, its frame rate and its sound. "
                + $"`gatekit window maximize {copy.Instance}` takes it anyway, and `gatekit deck --all` takes everything.");
        }
        return rule.Ours(copies);
    }

    void Apply(DeckRequest request)
    {
        switch (request.Name)
        {
            case "maximize" when request.Instance is { Length: > 0 } one:
                Invite(one);
                _tiles.Maximize(one);
                break;
            case "grid":
                _tiles.ShowGrid();
                break;
            case "release" when request.Instance is { Length: > 0 } gone:
                _invited.Remove(gone);
                Unspend(gone);
                Unhear(gone);
                if (_tiles.Release(gone) is { } left) Give(left);
                break;
            case "backing" when request.Instance is { Length: > 0 } which && request.Text is { Length: > 0 } wanted:
                Invite(which);
                Rebacking(which, wanted);
                break;
            case "quit":
                Native.PostMessage(_window, Native.WmClose, 0, 0);
                break;
        }
    }

    /// <summary>Someone has named a copy this stand did not start, which is them asking for it.
    /// Said out loud, because the deck is about to do three things to that game.</summary>
    void Invite(string instance)
    {
        if (_adoptAll || !_invited.Add(instance)) return;
        if (DeckRecord.Copies(_settings, _platform).Any(c =>
                string.Equals(c.Instance, instance, StringComparison.OrdinalIgnoreCase) && !c.Mine))
        {
            _out.WriteLine($"{instance}: this stand did not start it, and you asked for it by name, so the deck "
                + $"is taking its window, its frame rate and its sound. `gatekit window release {instance}` gives all three back.");
        }
    }

    void Give(Tile tile)
    {
        if (_focus == tile) _focus = null;
        Backing(tile).Drop(tile);
    }

    /// <summary>A copy is leaving this deck: it gets back the frame rate it had before the deck
    /// capped it, the engine's own background throttle included.</summary>
    Task? Unspend(string instance)
    {
        _caps.Remove(instance);
        if (!_hadThrottle.Remove(instance, out var background)) return null;
        return Task.Run(() => FrameCap.TryLift(_settings, _platform, instance, background));
    }

    /// <summary>`window mirror|adopt NAME`: the tile's content is given back by the backing that
    /// holds it and the tile waits for the other one to fill it, on the next tick.</summary>
    void Rebacking(string instance, string wanted)
    {
        if (!_backings.ContainsKey(wanted))
        {
            _out.WriteLine($"{instance}: there is no \"{wanted}\" backing ({string.Join(", ", _backings.Keys)})");
            return;
        }
        var tile = _tiles.Find(instance);
        if (tile is null)
        {
            _out.WriteLine($"{instance}: no tile here to change");
            return;
        }
        if (string.Equals(tile.Backing ?? _default, wanted, StringComparison.OrdinalIgnoreCase)) return;
        Give(tile);
        tile.Backing = wanted;
        tile.Note = null;
        tile.Stubborn = false;
        tile.State = TileState.Waiting;
        tile.Since = DateTime.UtcNow;
        _out.WriteLine($"{instance}: {wanted} from now on");
    }

    // --- the frame budget: what each copy is allowed to draw ---

    /// <summary>Gives every copy the cap the budget says it is worth, and tells only the copies
    /// whose cap has changed. The tile in view - held open, or the one with the keyboard - takes
    /// what it needs and the rest share the floor, which is what makes several copies affordable
    /// on an ordinary machine; <see cref="FrameBudget"/> is the whole of the rule.
    ///
    /// The game is asked over the bridge, which answers on its own tick, so it is asked from
    /// another thread: the deck's tick redraws windows and must never wait on a game.</summary>
    /// <summary>What held the foreground when this deck opened, until it has plainly kept it.</summary>
    nint _wasInFront;

    /// <summary>
    /// Opening a window must not take the keyboard from what somebody was typing in. The styles
    /// say so - WS_EX_NOACTIVATE, SW_SHOWNOACTIVATE, no SetForegroundWindow anywhere - and that is
    /// not always enough: the system gives the foreground to a process it has just been asked to
    /// start, and it does it a moment after the window appears. So for the first few ticks the
    /// deck checks whether the foreground has become one of its own windows and, if it has, hands
    /// it back to whoever had it.
    ///
    /// It never takes the foreground from a third window: if the person has moved on somewhere
    /// else by then, that is theirs and the deck leaves it.
    /// </summary>
    void GiveTheForegroundBack()
    {
        if (_wasInFront == 0) return;
        if (_ticks > 8) { _wasInFront = 0; return; }        // settled: stop looking
        var front = Native.GetForegroundWindow();
        if (front == 0) return;
        Native.GetWindowThreadProcessId(front, out var owner);
        if (owner != Environment.ProcessId) return;         // not ours: leave it alone
        if (Native.IsWindow(_wasInFront) == 0) { _wasInFront = 0; return; }
        _out.WriteLine("the deck was given the foreground as it opened; handing it back");
        var mine = Native.GetCurrentThreadId();
        var theirs = Native.GetWindowThreadProcessId(front, out _);
        var attached = theirs != 0 && theirs != mine && Native.AttachThreadInput(mine, theirs, 1) != 0;
        try { Native.SetForegroundWindow(_wasInFront); }
        finally { if (attached) Native.AttachThreadInput(mine, theirs, 0); }
    }

    void Spend()
    {
        if (!_budget.Enabled) return;
        var wanted = _budget.Caps(_tiles.Tiles, _tiles.Maximized, _focus?.Instance);

        foreach (var tile in _tiles.Tiles)
        {
            if (!wanted.TryGetValue(tile.Instance, out var fps))
            {
                // No game in the tile: forget what it was told, so a restart is told again. The
                // throttle it had is kept: the copy that comes back is the same copy.
                _caps.Remove(tile.Instance);
                tile.Capped = null;
                continue;
            }
            // The engine re-applies its own graphic config into the same field whenever a
            // graphics setting changes, which quietly throws the deck's cap away - and a copy
            // that has gone back to drawing flat out while nobody watches it is the whole cost
            // the budget exists to avoid. So the cap is looked at again now and then, and put
            // back when it has drifted; nothing is sent while it still holds.
            var again = _ticks % ReapplyEvery == 0;
            if (!again && _caps.TryGetValue(tile.Instance, out var already) && already == fps) continue;

            _caps[tile.Instance] = fps;
            tile.Capped = FrameBudget.Note(fps);
            var instance = tile.Instance;
            var first = !_hadThrottle.ContainsKey(instance);
            var settled = Settled(tile);
            Task.Run(() =>
            {
                // The first time a copy is capped, note what the engine was doing to it, so that
                // the same can be put back when it leaves the deck.
                var now = FrameCap.TryRead(_settings, _platform, instance);
                if (first && now is { } had) _hadThrottle[instance] = had.InBackground;
                // Already what was asked for: say nothing and send nothing. This is the usual
                // answer on a re-check, and it must not cost a bridge call's worth of noise.
                if (again && now is { Asked: var asked } && asked == fps) return;
                var outcome = FrameCap.TrySet(_settings, _platform, instance, fps);
                if (outcome == CapOutcome.Applied || !settled) return;
                Complain(instance, outcome == CapOutcome.Refused
                    ? "will not take a frame cap, so the deck's budget does nothing for it. It needs "
                        + "GateKit's Script Extender build; if it has one, the game has been patched and the "
                        + "frame cap no longer recognises the engine's layout (docs/design/deck.md)."
                    : "is not answering the bridge, so the deck's budget does nothing for it. Without the "
                        + "budget every copy but the one in front draws at thirty frames a second.");
            });
        }
    }

    // --- sound: only the copy you are looking at ---

    /// <summary>Silences every copy but the one in view, and says nothing to a copy that is
    /// already as it should be. A tile whose sound a person has touched is pinned and skipped;
    /// <see cref="SoundFocus"/> is the whole of the rule.</summary>
    void Listen()
    {
        if (!_sound.Enabled) return;
        var wanted = _sound.Heard(_tiles.Tiles, _tiles.Maximized, _focus?.Instance);

        foreach (var tile in _tiles.Tiles)
        {
            if (!wanted.TryGetValue(tile.Instance, out var heard)) continue;
            if (_heard.TryGetValue(tile.Instance, out var already) && already == heard) continue;

            _heard[tile.Instance] = heard;
            tile.Muted = !heard;
            Hear(tile.Instance, heard, Settled(tile));
        }
    }

    /// <summary>Mutes or unmutes one copy's master volume, off the deck's own thread: the game
    /// answers on its own tick and the deck must never wait on it.</summary>
    void Hear(string instance, bool heard, bool settled) => Task.Run(() =>
    {
        try
        {
            var sound = new SoundControl(SessionFor(instance));
            (heard ? sound.UnmuteAsync(null) : sound.MuteAsync(null)).GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is StandException or IOException or TimeoutException or BridgeTimeoutException or BridgeLuaException)
        {
            // A copy that is loading or closing keeps the sound it has and is asked again; one
            // that has settled and still cannot be reached is worth saying out loud, because a
            // deck silently failing to quieten three games is how you end up with three games
            // playing at once and no idea why.
            if (settled) Complain(instance, "will not be quietened, so it keeps playing whether you are looking at it or not");
        }
    });

    /// <summary>How loud one copy is, as the tile's own slider sets it. Touching it pins the tile:
    /// the deck's policy leaves that copy alone from then on.</summary>
    void SetVolume(Tile tile, int volume)
    {
        tile.SoundPinned = true;
        tile.Volume = Math.Clamp(volume, 0, 100);
        tile.Muted = tile.Volume == 0;
        _heard.Remove(tile.Instance);
        var instance = tile.Instance;
        var value = (double)tile.Volume;
        Task.Run(() =>
        {
            try
            {
                new SoundControl(SessionFor(instance)).SetAsync(null, value).GetAwaiter().GetResult();
            }
            catch (Exception e) when (e is StandException or IOException or TimeoutException) { }
        });
    }

    /// <summary>A copy is leaving this deck: whatever the deck did to its sound is undone.</summary>
    Task? Unhear(string instance)
    {
        if (!_heard.Remove(instance, out var heard) || heard) return null;
        return Task.Run(() =>
        {
            try
            {
                new SoundControl(SessionFor(instance)).UnmuteAsync(null).GetAwaiter().GetResult();
            }
            catch (Exception e) when (e is StandException or IOException or TimeoutException) { }
        });
    }

    /// <summary>How long a game is given to settle before the deck will complain that it cannot be
    /// budgeted or quietened. A copy that has only just appeared in its tile is still loading, and
    /// a bridge that is not up yet is not a fault.</summary>
    static readonly TimeSpan SettlingTime = TimeSpan.FromSeconds(20);

    static bool Settled(Tile tile) => DateTime.UtcNow - tile.Since > SettlingTime;

    /// <summary>Says once that a copy cannot be managed, and why. Called from the threads that do
    /// the asking, so it takes the set under a lock.</summary>
    void Complain(string instance, string what)
    {
        lock (_complained)
        {
            if (!_complained.Add(instance)) return;
        }
        _out.WriteLine($"{instance}: this copy {what}");
    }

    GameSession SessionFor(string instance) => new Stand.Stand(
        _settings with { Instance = instance == DeckRecord.InstalledGame ? null : instance },
        _platform, TextWriter.Null).Session();

    // --- the sound control in a tile's caption: a level to drag and a mute to click ---

    const int MuteWidth = 20, SliderWidth = 74, SoundGap = 6;

    /// <summary>Where a tile's level bar and its mute sit in that tile's caption. One place, so
    /// that what is drawn and what is clicked can never drift apart.</summary>
    static (Native.Rect Level, Native.Rect Mute) SoundControl(TileBox box)
    {
        var right = box.X + box.Width - SoundGap;
        var mute = new Native.Rect { Left = right - MuteWidth, Top = box.Y + 3, Right = right, Bottom = box.Y + Grid.Caption - 3 };
        var level = new Native.Rect
        {
            Left = mute.Left - SoundGap - SliderWidth, Top = box.Y + 7,
            Right = mute.Left - SoundGap, Bottom = box.Y + Grid.Caption - 7,
        };
        return (level, mute);
    }

    /// <summary>A click in a caption's sound control: the level sets the volume where it was
    /// clicked, the mute turns it off and on. True when the click was one of those, so that it
    /// does not also hand the tile the keyboard.</summary>
    bool ClickedSound(Tile tile, TileBox box, int x, int y)
    {
        if (tile.State != TileState.Shown) return false;
        var (level, mute) = SoundControl(box);
        if (x >= mute.Left && x < mute.Right && y >= mute.Top && y < mute.Bottom)
        {
            SetVolume(tile, tile.Muted ? (tile.Volume > 0 ? tile.Volume : 100) : 0);
            return true;
        }
        if (x >= level.Left && x < level.Right && y >= level.Top - 4 && y < level.Bottom + 4)
        {
            SetVolume(tile, (int)Math.Round(100.0 * (x - level.Left) / Math.Max(1, level.Right - level.Left)));
            return true;
        }
        return false;
    }

    void ReleaseEverything()
    {
        // On the way out the giving back has to finish before the process does, so these are
        // waited for - all at once, and not for long: a copy that does not answer is one that is
        // closing too, and a deck must not hang on it.
        var lifting = new List<Task>();
        foreach (var tile in _tiles.Clear())
        {
            if (Unspend(tile.Instance) is { } task) lifting.Add(task);
            if (Unhear(tile.Instance) is { } unmute) lifting.Add(unmute);
            Give(tile);
        }
        if (lifting.Count > 0) Task.WaitAll([.. lifting], FrameCap.Timeout + TimeSpan.FromSeconds(1));
        _record.WriteTiles([]);
    }

    /// <summary>Puts every tile where the layout says, without activating anything.</summary>
    void Place()
    {
        if (_window == 0) return;
        Native.GetClientRect(_window, out var client);
        // Back to front, so the one in view ends up on top of the ones it covers.
        foreach (var place in _tiles.Layout(client.Right, client.Bottom).OrderBy(p => p.Front))
            Backing(place.Tile).Place(place.Tile, place.Box, place.Front);
        Native.InvalidateRect(_window, 0, 0);
    }

    /// <summary>The user clicked the deck. Only now may it become the active window: the style that
    /// kept it from ever being activated is dropped, and the tile under the click gets the keyboard.</summary>
    void Focus(int x, int y)
    {
        if (!_clicked)
        {
            _clicked = true;
            var style = Native.GetWindowLongPtr(_window, Native.GwlExStyle);
            Native.SetWindowLongPtr(_window, Native.GwlExStyle, (nint)(((ulong)style) & ~(ulong)Native.WsExNoActivate));
            Native.SetForegroundWindow(_window);
        }
        Native.GetClientRect(_window, out var client);
        foreach (var (tile, box, front) in _tiles.Layout(client.Right, client.Bottom))
        {
            if (!front) continue;
            if (x < box.X || x >= box.X + box.Width || y < box.Y || y >= box.Y + box.Height) continue;
            if (ClickedSound(tile, box, x, y))
            {
                Native.InvalidateRect(_window, 0, 0);
                return;
            }
            _focus = tile;
            Backing(tile).Focus(tile);
            Spend();
            Listen();
            Native.InvalidateRect(_window, 0, 0);
            return;
        }
    }

    // --- drawing: captions, and what a tile shows when there is no game in it ---

    void Paint()
    {
        Native.PaintStruct paint;
        var dc = Native.BeginPaint(_window, &paint);
        try
        {
            Native.GetClientRect(_window, out var client);
            var whole = new Native.Rect { Right = client.Right, Bottom = client.Bottom };
            Native.FillRect(dc, &whole, _background);
            Native.SelectObject(dc, Native.GetStockObject(Native.DefaultGuiFont));
            Native.SetBkMode(dc, Native.TransparentBackground);
            foreach (var (tile, box, front) in _tiles.Layout(client.Right, client.Bottom))
            {
                if (!front) continue;   // held open: this one sits behind, covered by the one in view
                var caption = new Native.Rect { Left = box.X, Top = box.Y, Right = box.X + box.Width, Bottom = box.Y + Grid.Caption };
                Native.FillRect(dc, &caption, tile == _focus ? _focusBrush : _captionBrush);
                var text = caption;
                text.Left += 6;
                text.Right -= 6;
                if (tile.State == TileState.Shown)
                {
                    var (level, mute) = SoundControl(box);
                    text.Right = level.Left - SoundGap;
                    Native.FillRect(dc, &level, _trackBrush);
                    if (!tile.Muted && tile.Volume > 0)
                    {
                        var filled = level;
                        filled.Right = level.Left + (level.Right - level.Left) * tile.Volume / 100;
                        Native.FillRect(dc, &filled, _levelBrush);
                    }
                    Native.FillRect(dc, &mute, tile.Muted ? _mutedBrush : _trackBrush);
                    Native.SetTextColor(dc, Native.Colour(224, 224, 236));
                    var glyph = mute;
                    Native.DrawText(dc, tile.Muted ? "x" : "<", -1, &glyph,
                        Native.DtSingleLine | Native.DtVCenter | Native.DtCenter);
                }
                Native.SetTextColor(dc, Native.Colour(224, 224, 236));
                Native.DrawText(dc, tile.Caption, -1, &text, Native.DtSingleLine | Native.DtVCenter | Native.DtLeft | Native.DtEndEllipsis);
                Backing(tile).Draw(dc, tile, box);
                if (tile.State == TileState.Shown) continue;
                var inside = box.Inside;
                var empty = new Native.Rect { Left = inside.X, Top = inside.Y, Right = inside.X + inside.Width, Bottom = inside.Y + inside.Height };
                Native.FillRect(dc, &empty, _emptyBrush);
                Native.SetTextColor(dc, Native.Colour(150, 150, 164));
                Native.DrawText(dc, Says(tile), -1, &empty, Native.DtSingleLine | Native.DtVCenter | Native.DtCenter | Native.DtEndEllipsis);
            }
        }
        finally { Native.EndPaint(_window, &paint); }
    }

    static string Says(Tile tile) => tile.State switch
    {
        TileState.Loose => "(in a window of its own)",
        _ when tile.Pid > 0 => "(starting)",
        _ => "(waiting for the game)",
    };
}
