using System.Globalization;
using System.Text.Json.Nodes;
using GateKit.Bridge;
using GateKit.Platform;

namespace GateKit.Stand;

/// <summary>What a deck does with the window of a copy whose frames it is mirroring. A mirrored
/// copy's window is not looked at - the picture comes off the graphics card - so it need not be on
/// any screen; the choice is only how far out of the way it is put.</summary>
public enum DeckConceal
{
    /// <summary>Nothing: it stays where it is.</summary>
    None,

    /// <summary>Hidden outright. Whether a game keeps drawing with its window hidden is a question
    /// about the engine, answered in `docs/design/deck.md`.</summary>
    Hidden,

    /// <summary>Moved past the edge of every screen and made a tool window, so it is on no screen
    /// and not in the taskbar while still being, as far as the game is told, a shown window.</summary>
    Offscreen,
}

/// <summary>A game running on this machine, as a deck and `window list` see it.</summary>
/// <param name="Instance">The instance it belongs to, or <see cref="DeckRecord.InstalledGame"/>.</param>
/// <param name="Pid">Its process.</param>
/// <param name="Window">Its own window, or 0 while it has none yet.</param>
/// <param name="Mine">Whether this stand is the one that made that instance, which is the only
/// honest way to tell a copy of ours from one somebody else started (see <see cref="Instance.Mine"/>).
/// The installed game is never ours.</param>
public sealed record GameCopy(string Instance, int Pid, nint Window, bool Mine = false);

/// <summary>A tile of a running deck, as the deck writes it down for everyone else to read.</summary>
/// <param name="Why">What the tile is waiting for, or why the game is in its own window instead.
/// A tile that is not showing its game has a reason, and whoever is reading this at four in the
/// morning - a person or an agent - should not have to go and find the deck's own output for it.</param>
public sealed record DeckTile(string Instance, string? Label, int Pid, string State, int Column, int Row, string? Why = null)
{
    public JsonObject ToJson() => new()
    {
        ["instance"] = Instance,
        ["label"] = Label,
        ["pid"] = Pid,
        ["state"] = State,
        ["column"] = Column,
        ["row"] = Row,
        ["why"] = Why,
    };

    public static DeckTile FromJson(JsonObject o) => new(
        o["instance"]?.ToString() ?? "", o["label"]?.ToString(),
        (int?)o["pid"] ?? 0, o["state"]?.ToString() ?? "", (int?)o["column"] ?? 0, (int?)o["row"] ?? 0,
        o["why"]?.ToString());
}

/// <summary>A deck that is running: which process it is and which window holds the tiles.</summary>
public sealed record DeckPresence(int Pid, nint Host, DateTime Updated);

/// <summary>Something asked of a running deck (`gatekit window ...`).</summary>
public sealed record DeckRequest(string Name, string? Instance = null, string? Text = null);

/// <summary>
/// Where a deck and the command line find each other: a folder in the shared state folder, in the
/// spirit of the bridge channel. The deck writes down that it is running and what its tiles are;
/// anyone else drops a request file in and the deck picks it up on its next tick. Nothing here
/// needs a window, so `doctor`, `window list` and the MCP tools work with or without a deck.
/// </summary>
public sealed class DeckRecord(string stateRoot)
{
    /// <summary>The name a tile carries for the game that was installed, not an instance of ours.</summary>
    public const string InstalledGame = "(installed)";

    /// <summary>A deck writes every half second; one that has said nothing for this long is gone.</summary>
    public static readonly TimeSpan Stale = TimeSpan.FromSeconds(10);

    public string Folder { get; } = Path.Combine(stateRoot, "deck");
    public string DeckFile => Path.Combine(Folder, "deck.json");
    public string TilesFile => Path.Combine(Folder, "tiles.json");
    public string LabelsFile => Path.Combine(Folder, "labels.json");
    public string RequestFolder => Path.Combine(Folder, "requests");

    // --- the deck itself ---

    /// <summary>The deck running on this machine, or null: a record that is fresh and whose process
    /// is still there.</summary>
    public DeckPresence? Running()
    {
        var record = JsonFiles.ReadObject(DeckFile);
        if (record.Count == 0 || (int?)record["pid"] is not { } pid) return null;
        var updated = DateTime.TryParse(record["updated"]?.ToString(), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var stamp) ? stamp : DateTime.MinValue;
        if (DateTime.UtcNow - updated > Stale) return null;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            if (process.HasExited) return null;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return null;
        }
        return new DeckPresence(pid, (nint)((long?)record["host"] ?? 0), updated);
    }

    /// <summary>The deck says it is alive (and keeps saying it, every tick).</summary>
    public void Announce(int pid, nint host) => JsonFiles.WriteObject(DeckFile, new JsonObject
    {
        ["pid"] = pid,
        ["host"] = (long)host,
        ["updated"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
    });

    /// <summary>The deck is closing: its record and tiles go, its labels stay.</summary>
    public void Forget()
    {
        FileRetry.TryDelete(DeckFile);
        FileRetry.TryDelete(TilesFile);
    }

    // --- tiles ---

    public void WriteTiles(IEnumerable<DeckTile> tiles) =>
        JsonFiles.WriteText(TilesFile, JsonFiles.Format(new JsonArray([.. tiles.Select(t => (JsonNode)t.ToJson())])));

    public IReadOnlyList<DeckTile> Tiles()
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(TilesFile, JsonFiles.Utf8)) is JsonArray array
                ? [.. array.OfType<JsonObject>().Select(DeckTile.FromJson)]
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return [];
        }
    }

    // --- labels: a name a person gave a copy; they outlive the deck ---

    public IReadOnlyDictionary<string, string> Labels()
    {
        var record = JsonFiles.ReadObject(LabelsFile);
        return record.Where(p => p.Value is JsonValue)
            .ToDictionary(p => p.Key, p => p.Value!.ToString(), StringComparer.Ordinal);
    }

    public void Label(string instance, string? label)
    {
        var labels = new JsonObject();
        foreach (var (key, value) in Labels())
        {
            if (!string.Equals(key, instance, StringComparison.Ordinal)) labels[key] = value;
        }
        if (label is { Length: > 0 }) labels[instance] = label;
        JsonFiles.WriteObject(LabelsFile, labels);
    }

    // --- requests ---

    public void Send(DeckRequest request)
    {
        // Ticks, not milliseconds, and zero-padded so that sorting the names as text sorts them
        // by time. The deck applies requests in the order the names sort in, and two sent in the
        // same millisecond used to be separated only by a random GUID - so `maximize` then `grid`
        // could arrive the other way round. It showed as a test that passed on one operating
        // system and failed on another, which is what a race usually looks like from outside.
        var file = Path.Combine(RequestFolder,
            $"{DateTime.UtcNow.Ticks:D19}-{Guid.NewGuid():N}.json");
        JsonFiles.WriteObject(file, new JsonObject
        {
            ["name"] = request.Name,
            ["instance"] = request.Instance,
            ["text"] = request.Text,
        });
    }

    /// <summary>Every request waiting, oldest first; each is taken away as it is read.</summary>
    public IReadOnlyList<DeckRequest> Take()
    {
        if (!Directory.Exists(RequestFolder)) return [];
        var requests = new List<DeckRequest>();
        foreach (var file in Directory.EnumerateFiles(RequestFolder, "*.json").Order(StringComparer.Ordinal))
        {
            var record = JsonFiles.ReadObject(file);
            FileRetry.TryDelete(file);
            if (record["name"]?.ToString() is { Length: > 0 } name)
                requests.Add(new DeckRequest(name, record["instance"]?.ToString(), record["text"]?.ToString()));
        }
        return requests;
    }

    // --- the copies a deck shows ---

    /// <summary>Every game running on this machine with the instance it belongs to, instances in
    /// name order and the installed game last - the order tiles are laid out in.</summary>
    public static IReadOnlyList<GameCopy> Copies(StandSettings settings, IGamePlatform platform)
    {
        var instances = Instance.Names(settings.InstancesRoot)
            .Select(name => (Name: name, Root: new Instance(name, settings, platform)))
            .ToList();
        var found = new List<GameCopy>();
        foreach (var process in platform.GameProcesses())
        {
            var match = instances.FirstOrDefault(i => i.Root.Inside(process.ExecutablePath));
            var instance = match.Name ?? InstalledGame;
            found.Add(new GameCopy(instance, process.Pid,
                platform.CanHostWindows ? platform.FindGameWindow(process.Pid) : 0,
                match.Root?.Mine ?? false));
        }
        return [.. found.OrderBy(c => c.Instance == InstalledGame ? 1 : 0)
            .ThenBy(c => c.Instance, StringComparer.Ordinal).ThenBy(c => c.Pid)];
    }
}
