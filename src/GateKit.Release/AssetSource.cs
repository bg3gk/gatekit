namespace GateKit.Release;

/// <summary>
/// Where the files of a release are read from. A release on GitHub is the normal one; a folder
/// (or a <c>file://</c> URL) is what an export is tested against before anything is uploaded.
/// </summary>
public interface IAssetSource
{
    /// <summary>What to print, and what to name in an error: the full location of one asset.</summary>
    string Describe(string asset);

    /// <summary>The asset as a local file, when it already is one; null when it must be fetched.</summary>
    string? LocalPath(string asset);

    /// <summary>Opens the asset for reading. Throws <see cref="ReleaseException"/> when it is not there.</summary>
    Task<Stream> OpenAsync(string asset, CancellationToken cancel = default);
}

/// <summary>Assets of a GitHub release: <c>https://github.com/owner/repo/releases/download/tag/asset</c>.</summary>
public sealed class HttpAssetSource(HttpClient http, string baseUrl, string? whenMissing = null) : IAssetSource
{
    readonly string _base = baseUrl.TrimEnd('/');

    public string Describe(string asset) => $"{_base}/{asset}";

    public string? LocalPath(string asset) => null;

    public async Task<Stream> OpenAsync(string asset, CancellationToken cancel = default)
    {
        var url = Describe(asset);
        HttpResponseMessage response;
        try
        {
            response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel);
        }
        catch (HttpRequestException e)
        {
            throw new ReleaseException($"could not fetch {url}: {e.Message}" + (whenMissing is null ? "" : "\n" + whenMissing));
        }
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new ReleaseException(
                $"{url} answered {status} ({response.ReasonPhrase})." + (whenMissing is null ? "" : "\n" + whenMissing));
        }
        return await response.Content.ReadAsStreamAsync(cancel);
    }
}

/// <summary>Assets lying in a folder: an export that has not been uploaded, or a mirror on a disk.</summary>
public sealed class FolderAssetSource(string folder) : IAssetSource
{
    public string Folder { get; } = folder;

    public string Describe(string asset) => Path.Combine(Folder, asset);

    public string? LocalPath(string asset) => File.Exists(Describe(asset)) ? Describe(asset) : null;

    public Task<Stream> OpenAsync(string asset, CancellationToken cancel = default)
    {
        var path = Describe(asset);
        if (!File.Exists(path)) throw new ReleaseException($"{path} is not there");
        return Task.FromResult<Stream>(File.OpenRead(path));
    }
}

/// <summary>Reads <c>--from</c>: a folder, a <c>file://</c> URL or an <c>http(s)://</c> base URL.</summary>
public static class AssetSources
{
    public static IAssetSource For(string location, HttpClient? http = null)
    {
        if (Uri.TryCreate(location, UriKind.Absolute, out var uri))
        {
            if (uri.IsFile) return new FolderAssetSource(uri.LocalPath);
            if (uri.Scheme is "http" or "https")
                return new HttpAssetSource(http ?? new HttpClient { Timeout = TimeSpan.FromHours(1) }, location);
            throw new ReleaseException($"{location}: only a folder, a file:// URL or an http(s):// URL can be read");
        }
        return new FolderAssetSource(location);
    }
}
