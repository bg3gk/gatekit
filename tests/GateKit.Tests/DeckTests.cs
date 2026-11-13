using GateKit.Deck;
using GateKit.Platform;
using GateKit.Stand;

namespace GateKit.Tests;

/// <summary>The deck without a window: where tiles go, what they do while a game is away, and
/// what a deck and the command line say to each other through the state folder.</summary>
public sealed class DeckTests
{
    static GameCopy Copy(string instance, int pid, nint window = 7) => new(instance, pid, window);

    static readonly Dictionary<string, string> NoLabels = [];

    [Fact]
    public void The_grid_grows_as_the_design_says()
    {
        Assert.Equal(1, Grid.Columns(1));
        Assert.Equal(2, Grid.Columns(2));
        Assert.Equal(2, Grid.Columns(3));
        Assert.Equal(2, Grid.Columns(4));
        Assert.Equal(3, Grid.Columns(5));
        Assert.Equal(3, Grid.Columns(9));
        Assert.Equal(4, Grid.Columns(10));
        Assert.Equal(1, Grid.Rows(2, 2));
        Assert.Equal(2, Grid.Rows(3, 2));   // three or four: two rows of two, one quadrant empty
        Assert.Equal(2, Grid.Rows(4, 2));
    }

    [Fact]
    public void Tiles_fill_the_deck_without_overlapping_and_leave_room_for_a_caption()
    {
        var boxes = Grid.Lay(4, 1000, 800);
        Assert.Equal(4, boxes.Count);
        Assert.All(boxes, b => Assert.True(b.Width > 400 && b.Height > 300));
        Assert.Equal(boxes[0].Y, boxes[1].Y);                          // one row
        Assert.True(boxes[1].X >= boxes[0].X + boxes[0].Width);        // side by side, no overlap
        Assert.True(boxes[2].Y >= boxes[0].Y + boxes[0].Height);       // and the next row below
        Assert.Equal(boxes[0].Y + Grid.Caption, boxes[0].Inside.Y);
        Assert.Equal(boxes[0].Height - Grid.Caption, boxes[0].Inside.Height);
        Assert.True(boxes[3].X + boxes[3].Width <= 1000);
        Assert.True(boxes[3].Y + boxes[3].Height <= 800);

        var one = Assert.Single(Grid.Lay(1, 1000, 800));
        Assert.True(one.Width > 980 && one.Height > 780);              // one game fills the deck
        Assert.Empty(Grid.Lay(0, 1000, 800));
    }

    [Fact]
    public void A_game_takes_a_tile_and_keeps_it_while_it_restarts()
    {
        var now = DateTime.UtcNow;
        var tiles = new TileSet();

        var changes = tiles.Observe([Copy("deck1", 100)], NoLabels, now);
        var tile = Assert.Single(tiles.Tiles);
        Assert.Equal("deck1", tile.Instance);
        Assert.Equal(TileState.Waiting, tile.State);     // the game is there; nothing shows it yet
        Assert.Same(tile, Assert.Single(changes.ToShow));

        tiles.Shown(tile, now);
        Assert.Equal(TileState.Shown, tile.State);
        Assert.Empty(tiles.Observe([Copy("deck1", 100)], NoLabels, now).ToShow);   // nothing to do twice

        // The game exits: the tile stays where it is and says it is waiting.
        changes = tiles.Observe([], NoLabels, now.AddSeconds(1));
        Assert.Same(tile, Assert.Single(changes.ToDrop));
        Assert.Equal(TileState.Waiting, Assert.Single(tiles.Tiles).State);
        Assert.Equal(0, tiles.Tiles[0].Pid);

        // It comes back as another process: the same tile takes the new game.
        changes = tiles.Observe([Copy("deck1", 200, 9)], NoLabels, now.AddSeconds(2));
        Assert.Same(tiles.Tiles[0], Assert.Single(changes.ToShow));
        Assert.Equal(200, tiles.Tiles[0].Pid);
        Assert.Single(tiles.Tiles);
    }

    [Fact]
    public void An_empty_tile_is_dropped_after_the_grace_period_unless_it_is_held_open()
    {
        var now = DateTime.UtcNow;
        var tiles = new TileSet { Grace = TimeSpan.FromMinutes(1) };
        tiles.Observe([Copy("deck1", 100), Copy("deck2", 200)], NoLabels, now);
        tiles.Maximize("deck1");

        tiles.Observe([], NoLabels, now.AddSeconds(1));
        Assert.Equal(2, tiles.Tiles.Count);
        tiles.Observe([], NoLabels, now.AddMinutes(5));
        var kept = Assert.Single(tiles.Tiles);
        Assert.Equal("deck1", kept.Instance);            // held open: its place is kept however long
        Assert.Equal("deck1", tiles.Maximized);
    }

    [Fact]
    public void A_held_tile_fills_the_deck_and_the_others_keep_a_real_size_out_of_view()
    {
        var now = DateTime.UtcNow;
        var tiles = new TileSet();
        tiles.Observe([Copy("deck1", 100), Copy("deck2", 200)], NoLabels, now);

        var grid = tiles.Layout(1000, 800);
        Assert.Equal(2, grid.Count);
        Assert.True(grid[0].Box.Width < 600);
        Assert.All(grid, p => Assert.True(p.Front));

        tiles.Maximize("deck2");
        var held = tiles.Layout(1000, 800);
        var one = held.Single(t => t.Tile.Instance == "deck2");
        var covered = held.Single(t => t.Tile.Instance == "deck1");
        Assert.True(one.Box.Width > 980 && one.Box.Height > 780);
        Assert.True(one.Front);
        Assert.False(covered.Front);                      // behind the held one, not hidden away
        Assert.Equal(one.Box, covered.Box);               // the same size, still rendering

        tiles.ShowGrid();
        Assert.Null(tiles.Maximized);
        Assert.True(tiles.Layout(1000, 800)[0].Box.Width < 600);
    }

    [Fact]
    public void Maximize_works_before_the_game_is_even_there_and_release_takes_a_tile_out()
    {
        var now = DateTime.UtcNow;
        var tiles = new TileSet();
        tiles.Maximize("deck1");
        Assert.Equal("deck1", tiles.Maximized);
        Assert.Equal(TileState.Waiting, Assert.Single(tiles.Tiles).State);

        tiles.Observe([Copy("deck1", 100)], new Dictionary<string, string> { ["deck1"] = "Nautiloid run" }, now);
        var tile = Assert.Single(tiles.Tiles);
        Assert.Equal(100, tile.Pid);
        Assert.Contains("Nautiloid run", tile.Caption);
        Assert.Contains("pid 100", tile.Caption);

        tiles.Shown(tile, now);
        Assert.Same(tile, tiles.Release("deck1"));
        Assert.Empty(tiles.Tiles);
        Assert.Null(tiles.Maximized);

        // A released copy is left alone - taking it back on the next tick would make release
        // useless - until a tile is asked for it again.
        Assert.Empty(tiles.Observe([Copy("deck1", 100)], NoLabels, now).ToShow);
        Assert.Empty(tiles.Tiles);
        tiles.Maximize("deck1");
        Assert.Single(tiles.Observe([Copy("deck1", 100)], NoLabels, now).ToShow);
    }

    [Fact]
    public void A_game_the_backing_cannot_show_is_left_alone()
    {
        var now = DateTime.UtcNow;
        var tiles = new TileSet();
        var tile = Assert.Single(tiles.Observe([Copy("deck1", 100)], NoLabels, now).ToShow);
        tiles.Stubborn(tile, now);
        Assert.Equal(TileState.Loose, tile.State);
        Assert.Empty(tiles.Observe([Copy("deck1", 100)], NoLabels, now).ToShow);  // not tried again
    }

    [Fact]
    public void The_record_is_how_a_deck_and_the_command_line_find_each_other()
    {
        using var temp = new TempFolder();
        var record = new DeckRecord(temp.Path);
        Assert.Null(record.Running());
        Assert.Empty(record.Tiles());

        record.Announce(Environment.ProcessId, 1234);
        var presence = record.Running();
        Assert.NotNull(presence);
        Assert.Equal(Environment.ProcessId, presence.Pid);
        Assert.Equal(1234, presence.Host);

        record.Label("deck1", "Nautiloid run");
        record.Label("deck2", "scratch");
        record.Label("deck2", null);
        Assert.Equal(new Dictionary<string, string> { ["deck1"] = "Nautiloid run" }, record.Labels());

        record.Send(new DeckRequest("maximize", "deck1"));
        record.Send(new DeckRequest("grid"));
        var requests = record.Take();
        Assert.Equal(["maximize", "grid"], requests.Select(r => r.Name));
        Assert.Equal("deck1", requests[0].Instance);
        Assert.Empty(record.Take());   // taken once

        record.WriteTiles([new DeckTile("deck1", "Nautiloid run", 100, "shown", 0, 0)]);
        var tile = Assert.Single(record.Tiles());
        Assert.Equal(("deck1", "Nautiloid run", 100, "shown"), (tile.Instance, tile.Label, tile.Pid, tile.State));

        record.Forget();
        Assert.Null(record.Running());
        Assert.Empty(record.Tiles());
        Assert.NotEmpty(record.Labels());   // names a person gave outlive the deck
    }

    [Fact]
    public void Window_list_names_every_copy_with_or_without_a_deck()
    {
        using var game = new FakeGame();
        var settings = game.Settings();
        var stand = game.Stand("deck1");
        stand.CreateInstance();
        game.Platform.Processes.Add(new GameProcess(4242, stand.Layout.Exe));
        game.Platform.Processes.Add(new GameProcess(7, Path.Combine(settings.GameFolder, "bin", "bg3_dx11.exe")));

        var lines = DeckApi.List(settings, game.Platform);
        Assert.StartsWith("deck: none", lines[0]);
        Assert.Contains(lines, l => l.StartsWith("deck1", StringComparison.Ordinal) && l.Contains("4242", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith(DeckRecord.InstalledGame, StringComparison.Ordinal));

        // Nothing may be asked of a deck that is not running, and it says so plainly.
        var failure = Assert.Throws<StandException>(() => DeckApi.Maximize(settings, "deck1"));
        Assert.Contains("no deck is running", failure.Message, StringComparison.Ordinal);

        // A label is kept whether a deck runs or not, and `list` shows it.
        DeckApi.Label(settings, "deck1", "Nautiloid run");
        Assert.Contains(DeckApi.List(settings, game.Platform), l => l.Contains("Nautiloid run", StringComparison.Ordinal));
    }
    // --- the frame budget: how a deck shares the machine between its copies ---

    static TileSet Deck(params string[] instances)
    {
        var tiles = new TileSet();
        var now = DateTime.UtcNow;
        tiles.Observe([.. instances.Select((name, i) => Copy(name, 100 + i))], NoLabels, now);
        foreach (var tile in tiles.Tiles) tiles.Shown(tile, now);
        return tiles;
    }

    [Fact]
    public void One_copy_on_its_own_is_never_capped()
    {
        var tiles = Deck("only");
        var caps = new FrameBudget(Floor: 10).Caps(tiles.Tiles, null, null);
        Assert.Equal(0, caps["only"]);
    }

    [Fact]
    public void The_tile_held_open_takes_what_it_needs_and_the_rest_share_the_floor()
    {
        var tiles = Deck("watched", "scenario", "agent");
        tiles.Maximize("watched");
        var caps = new FrameBudget(Floor: 10).Caps(tiles.Tiles, tiles.Maximized, null);
        Assert.Equal(0, caps["watched"]);
        Assert.Equal(10, caps["scenario"]);
        Assert.Equal(10, caps["agent"]);
    }

    [Fact]
    public void In_a_grid_the_tile_with_the_keyboard_is_the_one_being_watched()
    {
        var tiles = Deck("a", "b");
        var caps = new FrameBudget(Floor: 10).Caps(tiles.Tiles, null, "b");
        Assert.Equal(10, caps["a"]);
        Assert.Equal(0, caps["b"]);
    }

    [Fact]
    public void With_a_grid_and_no_keyboard_anywhere_nobody_is_being_watched_in_particular()
    {
        var tiles = Deck("a", "b");
        var caps = new FrameBudget(Floor: 10).Caps(tiles.Tiles, null, null);
        Assert.Equal(10, caps["a"]);
        Assert.Equal(10, caps["b"]);
    }

    [Fact]
    public void A_tile_waiting_for_its_game_is_told_nothing()
    {
        var tiles = new TileSet();
        var now = DateTime.UtcNow;
        tiles.Observe([Copy("here", 1), Copy("starting", 2)], NoLabels, now);
        tiles.Shown(tiles.Find("here")!, now);
        var caps = new FrameBudget(Floor: 10).Caps(tiles.Tiles, null, null);
        Assert.False(caps.ContainsKey("starting"));
        Assert.Single(caps);
    }

    [Fact]
    public void A_budget_switched_off_leaves_every_copy_as_the_engine_would_have_it()
    {
        var tiles = Deck("a", "b", "c");
        Assert.Empty(FrameBudget.Off.Caps(tiles.Tiles, "a", null));
        Assert.False(FrameBudget.Off.Enabled);
    }

    [Fact]
    public void A_budget_is_read_the_way_a_person_writes_one()
    {
        Assert.Equal(new FrameBudget(10), FrameBudget.Parse(null));
        Assert.Equal(new FrameBudget(10), FrameBudget.Parse(""));
        Assert.Equal(new FrameBudget(5), FrameBudget.Parse("5"));
        Assert.Equal(new FrameBudget(10, 60), FrameBudget.Parse("10/60"));
        Assert.Equal(FrameBudget.Off, FrameBudget.Parse("off"));
        Assert.Equal(FrameBudget.Off, FrameBudget.Parse("0"));
        Assert.Throws<StandException>(() => FrameBudget.Parse("quickly"));
        Assert.Throws<StandException>(() => FrameBudget.Parse("-1"));
        Assert.Throws<StandException>(() => FrameBudget.Parse("10/wrong"));
    }

    [Fact]
    public void A_capped_tile_says_so_in_its_caption()
    {
        var tile = new Tile("agent") { Pid = 42, Capped = FrameBudget.Note(10) };
        Assert.Contains("[10 fps]", tile.Caption, StringComparison.Ordinal);
        Assert.Null(FrameBudget.Note(0));
    }
    // --- sound: only the copy you are looking at ---

    [Fact]
    public void Only_the_tile_held_open_is_heard()
    {
        var tiles = Deck("watched", "scenario");
        tiles.Maximize("watched");
        var heard = new SoundFocus().Heard(tiles.Tiles, tiles.Maximized, null);
        Assert.True(heard["watched"]);
        Assert.False(heard["scenario"]);
    }

    [Fact]
    public void In_a_grid_sound_follows_the_tile_with_the_keyboard()
    {
        var tiles = Deck("a", "b");
        var heard = new SoundFocus().Heard(tiles.Tiles, null, "a");
        Assert.True(heard["a"]);
        Assert.False(heard["b"]);
    }

    [Fact]
    public void With_nothing_being_watched_no_copy_is_silenced()
    {
        var tiles = Deck("a", "b");
        Assert.All(new SoundFocus().Heard(tiles.Tiles, null, null).Values, Assert.True);
    }

    [Fact]
    public void A_copy_whose_sound_a_person_has_taken_over_is_left_alone()
    {
        var tiles = Deck("watched", "listening");
        tiles.Maximize("watched");
        tiles.Find("listening")!.SoundPinned = true;
        var heard = new SoundFocus().Heard(tiles.Tiles, tiles.Maximized, null);
        Assert.False(heard.ContainsKey("listening"));
        Assert.True(heard["watched"]);
    }

    [Fact]
    public void Sound_can_be_switched_off_altogether()
    {
        var tiles = Deck("a", "b");
        tiles.Maximize("a");
        Assert.Empty(SoundFocus.Off.Heard(tiles.Tiles, tiles.Maximized, null));
        Assert.Equal(SoundFocus.Off, SoundFocus.Parse("off"));
        Assert.True(SoundFocus.Parse(null).Enabled);
        Assert.Throws<StandException>(() => SoundFocus.Parse("sometimes"));
    }
    // --- what a deck may take hold of ---

    static GameCopy Theirs(string instance, int pid = 500) => new(instance, pid, 7, Mine: false);
    static GameCopy Ours(string instance, int pid = 400) => new(instance, pid, 7, Mine: true);

    [Fact]
    public void A_deck_leaves_alone_a_copy_this_stand_did_not_start()
    {
        IReadOnlyList<GameCopy> running = [Ours("deck1"), Theirs("sound"), Theirs(DeckRecord.InstalledGame)];
        var rule = new Adoption();
        Assert.Equal(["deck1"], rule.Ours(running).Select(c => c.Instance));
        Assert.Equal(["sound", DeckRecord.InstalledGame], rule.NotOurs(running).Select(c => c.Instance));
    }

    [Fact]
    public void Naming_a_copy_is_asking_for_it()
    {
        IReadOnlyList<GameCopy> running = [Ours("deck1"), Theirs("sound")];
        var rule = new Adoption { Invited = ["SOUND"] };    // `window maximize sound`, case-free
        Assert.Equal(["deck1", "sound"], rule.Ours(running).Select(c => c.Instance));
        Assert.Empty(rule.NotOurs(running));
    }

    [Fact]
    public void With_all_a_deck_takes_everything_and_leaves_nothing_alone()
    {
        IReadOnlyList<GameCopy> running = [Ours("deck1"), Theirs("sound")];
        var rule = new Adoption(All: true);
        Assert.Equal(2, rule.Ours(running).Count);
        Assert.Empty(rule.NotOurs(running));
        Assert.True(Adoption.Parse("all").All);
        Assert.False(Adoption.Parse(null).All);
        Assert.Throws<StandException>(() => Adoption.Parse("sometimes"));
    }

    [Fact]
    public void A_deck_with_nothing_of_its_own_takes_nothing()
    {
        IReadOnlyList<GameCopy> running = [Theirs("a"), Theirs("b")];
        Assert.Empty(new Adoption().Ours(running));
    }
}
