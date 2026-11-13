using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using GateKit.Stand;

namespace GateKit.Tests;

public sealed class ScriptExtenderReleaseTests : IDisposable
{
    readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    static byte[] Zip(params (string Name, string Content)[] entries)
    {
        var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(content);
            }
        return memory.ToArray();
    }

    /// <summary>Serves one body for any request, so no test touches the network.</summary>
    sealed class Serve(byte[] body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public string? Asked { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Asked = request.RequestUri?.ToString();
            return Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(body) });
        }
    }

    ScriptExtenderRelease.Pin WritePin(string json)
    {
        var file = _temp["se-release.json"];
        File.WriteAllText(file, json);
        return ScriptExtenderRelease.Pin.Read(file);
    }

    [Fact]
    public void A_pin_defaults_to_the_fork_but_needs_a_tag_and_an_asset()
    {
        var pin = WritePin("""{ "tag": "v34-1", "asset": "se.zip", "sha256": "ab12" }""");
        Assert.Equal(ScriptExtenderRelease.DefaultRepository, pin.Repository);
        Assert.Equal("v34-1", pin.Tag);
        Assert.Equal("ab12", pin.Sha256);

        var missing = Assert.Throws<StandException>(() => WritePin("""{ "asset": "se.zip" }"""));
        Assert.Contains("tag", missing.Message);
    }

    [Fact]
    public async Task Fetching_unpacks_the_build_flat_into_the_folder()
    {
        var body = Zip(("BG3ScriptExtender.dll", "a build"), ("nested/notes.txt", "beside it"));
        var serve = new Serve(body);
        var release = new ScriptExtenderRelease(new HttpClient(serve));
        var pin = new ScriptExtenderRelease.Pin("bg3gk/bg3se", "v34-1", "se.zip", null);

        var into = await release.FetchAsync(pin, _temp["build"], new StringWriter());

        Assert.Equal("a build", File.ReadAllText(Path.Combine(into, "BG3ScriptExtender.dll")));
        Assert.True(File.Exists(Path.Combine(into, "notes.txt")), "a nested entry lands in the folder, not below it");
        Assert.Contains("bg3gk/bg3se/releases/download/v34-1/se.zip", serve.Asked);
    }

    [Fact]
    public async Task A_download_that_is_not_what_was_pinned_is_refused()
    {
        var release = new ScriptExtenderRelease(new HttpClient(new Serve(Zip(("BG3ScriptExtender.dll", "something else")))));
        var pin = new ScriptExtenderRelease.Pin("bg3gk/bg3se", "v34-1", "se.zip",
            Convert.ToHexStringLower(SHA256.HashData("the expected build"u8.ToArray())));

        var error = await Assert.ThrowsAsync<StandException>(() =>
            release.FetchAsync(pin, _temp["build"], new StringWriter()));
        Assert.Contains("not what was pinned", error.Message);
        Assert.False(Directory.Exists(_temp["build"]), "nothing is unpacked from a download that failed its hash");
    }

    [Fact]
    public async Task A_missing_release_says_how_to_go_on_without_it()
    {
        var release = new ScriptExtenderRelease(new HttpClient(new Serve([], HttpStatusCode.NotFound)));
        var pin = new ScriptExtenderRelease.Pin("bg3gk/bg3se", "v34-1", "se.zip", null);

        var error = await Assert.ThrowsAsync<StandException>(() =>
            release.FetchAsync(pin, _temp["build"], new StringWriter()));
        Assert.Contains("se install <folder>", error.Message);
    }

    [Fact]
    public async Task An_archive_without_the_extender_is_refused()
    {
        var release = new ScriptExtenderRelease(new HttpClient(new Serve(Zip(("readme.txt", "no build here")))));
        var pin = new ScriptExtenderRelease.Pin("bg3gk/bg3se", "v34-1", "se.zip", null);

        var error = await Assert.ThrowsAsync<StandException>(() =>
            release.FetchAsync(pin, _temp["build"], new StringWriter()));
        Assert.Contains("BG3ScriptExtender.dll", error.Message);
    }
}
