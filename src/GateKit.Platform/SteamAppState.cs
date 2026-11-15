using System.Text;

namespace GateKit.Platform;

/// <summary>One depot Steam has installed for an app: its id and the manifest the installed files came from.</summary>
/// <param name="DepotId">The depot's id.</param>
/// <param name="ManifestId">The manifest (a 64-bit id, as Steam writes it).</param>
/// <param name="Size">Bytes Steam counts for it, when the app manifest says.</param>
/// <param name="DlcAppId">The DLC the depot belongs to, when it is one.</param>
public sealed record SteamDepot(string DepotId, string ManifestId, long? Size, string? DlcAppId);

/// <summary>A depot that installs into another app's folder (Steam's "SharedDepots": redistributables).</summary>
public sealed record SteamSharedDepot(string DepotId, string AppId);

/// <summary>
/// What Steam says about an installed app in <c>steamapps/appmanifest_&lt;app&gt;.acf</c>: the build,
/// the installed depots and their manifests. Nothing here is written by GateKit.
/// </summary>
public sealed record SteamAppState
{
    public required string AppId { get; init; }
    public string? Name { get; init; }
    public required string InstallDir { get; init; }
    public string? BuildId { get; init; }
    /// <summary>The installed depots in the order the app manifest lists them.</summary>
    public required IReadOnlyList<SteamDepot> Depots { get; init; }
    public required IReadOnlyList<SteamSharedDepot> SharedDepots { get; init; }
    /// <summary>The language Steam installed (UserConfig/language), when it says.</summary>
    public string? Language { get; init; }
    /// <summary>The app manifest itself.</summary>
    public required string ManifestPath { get; init; }
    /// <summary>The Steam library the app is in (the folder holding steamapps).</summary>
    public required string Library { get; init; }
    /// <summary>The app's folder: <c>&lt;library&gt;/steamapps/common/&lt;installdir&gt;</c>.</summary>
    public string Folder => Path.Combine(Library, "steamapps", "common", InstallDir);

    /// <summary>Reads an app manifest. A file that is not Valve's KeyValues text, or has no AppState, throws <see cref="InvalidDataException"/>.</summary>
    public static SteamAppState Read(string acfPath)
    {
        var root = KeyValues.Parse(File.ReadAllText(acfPath, Encoding.UTF8), acfPath);
        var state = root.Child("AppState") ?? throw new InvalidDataException($"{acfPath}: no AppState block");
        var appId = state.Text("appid") ?? throw new InvalidDataException($"{acfPath}: AppState has no appid");
        var steamapps = Path.GetDirectoryName(Path.GetFullPath(acfPath))!;
        var depots = new List<SteamDepot>();
        foreach (var depot in state.Child("InstalledDepots")?.Children ?? [])
        {
            if (depot.Text("manifest") is not { Length: > 0 } manifest) continue;
            depots.Add(new SteamDepot(depot.Key, manifest,
                long.TryParse(depot.Text("size"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var size) ? size : null,
                depot.Text("dlcappid")));
        }
        var shared = (state.Child("SharedDepots")?.Children ?? [])
            .Where(d => d.Value is not null).Select(d => new SteamSharedDepot(d.Key, d.Value!)).ToList();
        return new SteamAppState
        {
            AppId = appId,
            Name = state.Text("name"),
            InstallDir = state.Text("installdir") ?? throw new InvalidDataException($"{acfPath}: AppState has no installdir"),
            BuildId = state.Text("buildid"),
            Depots = depots,
            SharedDepots = shared,
            Language = state.Child("UserConfig")?.Text("language"),
            ManifestPath = Path.GetFullPath(acfPath),
            Library = Path.GetDirectoryName(steamapps)!,
        };
    }

    /// <summary>
    /// The app manifest of the Steam app installed in <paramref name="appFolder"/>, or null when the
    /// folder is not <c>&lt;library&gt;/steamapps/common/&lt;installdir&gt;</c> of any app manifest
    /// beside it (a GOG or other install, a copy, a folder moved by hand).
    /// </summary>
    public static SteamAppState? ForFolder(string appFolder, StringComparison comparison)
    {
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(appFolder));
        var common = Path.GetDirectoryName(folder);
        var steamapps = common is null ? null : Path.GetDirectoryName(common);
        if (steamapps is null || !string.Equals(Path.GetFileName(common), "common", comparison)
            || !string.Equals(Path.GetFileName(steamapps), "steamapps", comparison))
            return null;
        var name = Path.GetFileName(folder);
        foreach (var acf in Directory.EnumerateFiles(steamapps, "appmanifest_*.acf").Order(StringComparer.Ordinal))
        {
            SteamAppState state;
            try { state = Read(acf); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { continue; }
            if (string.Equals(state.InstallDir, name, comparison)) return state;
        }
        return null;
    }

    /// <summary>The folders Steam keeps depot manifests in for this app, the ones that exist:
    /// <c>&lt;Steam&gt;/depotcache</c> and the library's own <c>depotcache</c>.</summary>
    public IReadOnlyList<string> DepotCaches(string? steamRoot, StringComparison comparison)
    {
        var list = new List<string>();
        foreach (var candidate in new[] { steamRoot, Library }.OfType<string>()
                     .SelectMany(r => new[] { Path.Combine(r, "depotcache"), Path.Combine(r, "steamapps", "depotcache") }))
        {
            var full = Path.GetFullPath(candidate);
            if (Directory.Exists(full) && !list.Any(l => string.Equals(l, full, comparison))) list.Add(full);
        }
        return list;
    }
}

/// <summary>Valve's KeyValues text (<c>.acf</c>, <c>.vdf</c>): quoted or bare tokens, nested blocks, <c>//</c> comments.</summary>
public sealed class KeyValues
{
    public string Key { get; }
    /// <summary>The value of a key that has one; null for a block.</summary>
    public string? Value { get; }
    public IReadOnlyList<KeyValues> Children { get; }

    KeyValues(string key, string? value, IReadOnlyList<KeyValues> children)
    {
        Key = key;
        Value = value;
        Children = children;
    }

    /// <summary>The first child of that name (Valve's keys are case-insensitive).</summary>
    public KeyValues? Child(string key) => Children.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>The value of the first child of that name, or null.</summary>
    public string? Text(string key) => Child(key)?.Value;

    /// <summary>The whole text as one root block (key "").</summary>
    public static KeyValues Parse(string text, string source)
    {
        var at = 0;
        var children = Block(text, ref at, source, topLevel: true);
        return new KeyValues("", null, children);
    }

    static List<KeyValues> Block(string text, ref int at, string source, bool topLevel)
    {
        var list = new List<KeyValues>();
        while (true)
        {
            var token = Next(text, ref at, source, out var quoted);
            if (token is null)
            {
                if (topLevel) return list;
                throw new InvalidDataException($"{source}: a block is not closed");
            }
            if (!quoted && token == "}")
            {
                if (topLevel) throw new InvalidDataException($"{source}: '}}' without a block at offset {at}");
                return list;
            }
            var value = Next(text, ref at, source, out var valueQuoted)
                ?? throw new InvalidDataException($"{source}: key \"{token}\" has no value");
            if (!valueQuoted && value == "{") list.Add(new KeyValues(token, null, Block(text, ref at, source, topLevel: false)));
            else list.Add(new KeyValues(token, value, []));
        }
    }

    static string? Next(string text, ref int at, string source, out bool quoted)
    {
        quoted = false;
        while (at < text.Length)
        {
            var c = text[at];
            if (char.IsWhiteSpace(c)) { at++; continue; }
            if (c == '/' && at + 1 < text.Length && text[at + 1] == '/')
            {
                while (at < text.Length && text[at] != '\n') at++;
                continue;
            }
            break;
        }
        if (at >= text.Length) return null;
        if (text[at] is '{' or '}') return text[at++].ToString();
        var sb = new StringBuilder();
        if (text[at] == '"')
        {
            quoted = true;
            at++;
            while (true)
            {
                if (at >= text.Length) throw new InvalidDataException($"{source}: a quoted string is not closed");
                var c = text[at++];
                if (c == '"') return sb.ToString();
                if (c == '\\' && at < text.Length)
                {
                    var e = text[at++];
                    sb.Append(e switch { 'n' => '\n', 't' => '\t', _ => e });
                    continue;
                }
                sb.Append(c);
            }
        }
        while (at < text.Length && !char.IsWhiteSpace(text[at]) && text[at] is not ('{' or '}' or '"')) sb.Append(text[at++]);
        return sb.ToString();
    }
}
