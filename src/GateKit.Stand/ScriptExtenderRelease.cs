using System.IO.Compression;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace GateKit.Stand;

/// <summary>
/// Fetches GateKit's build of Script Extender from the fork's releases, so a user needs no C++
/// toolchain. Which release is fetched is pinned in se-release.json beside this file's repository
/// root, not decided at run time: a build and the mods that rely on it belong together.
/// </summary>
public sealed class ScriptExtenderRelease(HttpClient? http = null)
{
    public const string DefaultRepository = "bg3gk/bg3se";

    readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

    /// <summary>What a pin names: a release of a repository, and the hash the download must have.</summary>
    public sealed record Pin(string Repository, string Tag, string Asset, string? Sha256)
    {
        public static Pin Read(string file)
        {
            JsonNode node;
            try
            {
                node = JsonNode.Parse(File.ReadAllText(file))
                    ?? throw new StandException($"{file} is empty");
            }
            catch (Exception e) when (e is IOException or System.Text.Json.JsonException)
            {
                throw new StandException($"{file} cannot be read: {e.Message}");
            }
            string Need(string key) => node[key]?.ToString()
                ?? throw new StandException($"{file} has no \"{key}\"");
            return new Pin(node["repository"]?.ToString() ?? DefaultRepository, Need("tag"), Need("asset"),
                node["sha256"]?.ToString());
        }
    }

    /// <summary>Downloads the pinned build into <paramref name="into"/> and returns that folder.</summary>
    public async Task<string> FetchAsync(Pin pin, string into, TextWriter log, CancellationToken cancel = default)
    {
        var url = $"https://github.com/{pin.Repository}/releases/download/{pin.Tag}/{pin.Asset}";
        log.WriteLine($"fetching {pin.Repository} {pin.Tag} ({pin.Asset})");

        byte[] bytes;
        try
        {
            bytes = await _http.GetByteArrayAsync(url, cancel);
        }
        catch (HttpRequestException e)
        {
            throw new StandException(
                $"could not fetch {url}: {e.Message}\n" +
                "If the release does not exist yet, build the fork yourself and use `gatekit se install <folder>`.");
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (pin.Sha256 is { } expected && !hash.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new StandException($"{pin.Asset} is not what was pinned: expected sha256 {expected}, got {hash}");

        Directory.CreateDirectory(into);
        if (pin.Asset.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/')) continue;
                // flat: only the files a build folder needs, never a path out of it
                var name = Path.GetFileName(entry.FullName);
                if (name.Length == 0) continue;
                entry.ExtractToFile(Path.Combine(into, name), overwrite: true);
            }
        }
        else
        {
            File.WriteAllBytes(Path.Combine(into, Path.GetFileName(pin.Asset)), bytes);
        }

        var dll = Path.Combine(into, "BG3ScriptExtender.dll");
        if (!File.Exists(dll))
            throw new StandException($"{pin.Asset} holds no BG3ScriptExtender.dll");
        log.WriteLine($"  {FormatSize(bytes.Length)}, sha256 {hash[..12]}…");
        return into;
    }

    /// <summary>The newest release of the fork, for `se pin --latest` and for saying a pin is behind.</summary>
    public async Task<(string Tag, string? Asset)> LatestAsync(string repository, CancellationToken cancel = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.github.com/repos/{repository}/releases/latest");
        request.Headers.UserAgent.ParseAdd("GateKit");
        using var response = await _http.SendAsync(request, cancel);
        response.EnsureSuccessStatusCode();
        var release = await response.Content.ReadFromJsonAsync<JsonNode>(cancel)
            ?? throw new StandException($"{repository} returned no release");
        var tag = release["tag_name"]?.ToString() ?? throw new StandException("the release has no tag");
        var asset = (release["assets"] as JsonArray)?
            .Select(a => a?["name"]?.ToString())
            .FirstOrDefault(n => n is not null && n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        return (tag, asset);
    }

    static string FormatSize(long bytes) => bytes < 1024 * 1024
        ? $"{bytes / 1024.0:0.#} KB"
        : $"{bytes / (1024.0 * 1024):0.#} MB";
}
