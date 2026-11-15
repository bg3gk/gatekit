using System.Text.Json.Nodes;
using GateKit.Symbols.Decomp;

namespace GateKit.Symbols.UnitTests;

/// <summary>`decomp serve`: what a request carries of the registry - only what is applied - and the
/// channel of files, answered here by a thread standing in for the decompiler.</summary>
public sealed class DecompServerTests : IDisposable
{
    readonly string root = GateKit.Testing.Scratch.NewPath("serve");

    public void Dispose()
    {
        try { Directory.Delete(root, true); }
        catch (IOException) { }
    }

    static JsonObject Json(string text) => (JsonObject)JsonNode.Parse(text)!;

    static readonly JsonObject Stream = Json("""
        {"id": "toy::Stream", "kind": "struct", "size": 120,
         "fields": [{"offset": 8, "name": "Count", "type": "uint32_t", "rung": "inferred"},
                    {"offset": 104, "name": "Seed", "type": "uint64_t", "rung": "game"},
                    {"offset": 112, "name": "NumRolls", "type": "Array<int>", "rung": "confirmed"}]}
        """);

    [Theory]
    [InlineData("reg:rcx", 0)]
    [InlineData("reg:r9", 3)]
    [InlineData("reg:xmm2", 2)]
    [InlineData("stack:+0x28", 4)]
    [InlineData("stack:-0x24", null)]
    public void A_parameter_is_found_by_its_storage(string storage, int? index) => Assert.Equal(index, DecompServer.ParameterIndex(storage));

    [Fact]
    public void A_request_carries_only_the_applied_ties_and_the_applied_fields()
    {
        var entry = Json("""
            {"id": "toy::Read", "rung": "inferred",
             "parameters": [{"storage": "reg:rcx", "name": "stream"}, {"storage": "reg:rdx", "name": "other"}, {"storage": "reg:r8", "name": "third", "type_id": "toy::Stream", "rung": "confirmed"}],
             "ties": [{"type": "toy::Stream", "via": "stream", "rung": "confirmed"}, {"type": "toy::Stream", "via": "other"}]}
            """);
        var lines = DecompServer.Request(0x5000, entry, id => id == "toy::Stream" ? Stream : null);
        Assert.Equal(
        [
            "type\ttoy::Stream\t120\t68\t8\tuint\tSeed\tuint64_t",
            "type\ttoy::Stream\t120\t70\t8\tbytes\tNumRolls\tArray<int>",
            "param\t5000\t0\ttoy::Stream",
            "type\ttoy::Stream\t120\t68\t8\tuint\tSeed\tuint64_t",
            "type\ttoy::Stream\t120\t70\t8\tbytes\tNumRolls\tArray<int>",
            "param\t5000\t2\ttoy::Stream",
            "function\t5000",
        ], lines);
        // a function with no entry, or nothing applied: only the function
        Assert.Equal(["function\t5000"], DecompServer.Request(0x5000, null, _ => null));
        Assert.Equal(["function\t5000"], DecompServer.Request(0x5000, Json("""{"id": "toy::Read", "rung": "inferred", "parameters": [{"storage": "reg:rcx", "name": "s"}], "ties": [{"type": "toy::Stream", "via": "s"}]}"""), _ => Stream));
    }

    [Fact]
    public void A_structure_with_no_applied_field_is_sent_as_its_size()
    {
        Assert.Equal(["type\ttoy::Stream\t120\t0\t0\tbytes\t_\tsize only"], DecompServer.TypeLines("toy::Stream", Stream, _ => false));
    }

    [Fact]
    public async Task A_request_goes_over_the_channel_and_its_answer_comes_back()
    {
        var settings = new DecompSettings(root, Path.Combine(root, "tools"), Path.Combine(root, "game"), Path.Combine(root, "toolkit"), Path.Combine(root, "symbols.sqlite"), null);
        var channel = DecompServer.Channel(settings, "Toy.exe");
        Assert.Null(DecompServer.Ask(settings, "Toy.exe", ["function\t1000"], TimeSpan.FromSeconds(1)));   // nothing open
        Directory.CreateDirectory(channel);
        File.WriteAllText(Path.Combine(channel, "ready"), "5\n");
        var seen = new List<string>();
        var answering = Task.Run(() =>
        {
            for (var tries = 0; tries < 500; tries++)
            {
                foreach (var request in Directory.GetFiles(channel, "*.request"))
                {
                    seen.AddRange(File.ReadAllLines(request));
                    var id = Path.GetFileNameWithoutExtension(request);
                    File.Delete(request);
                    File.WriteAllText(Path.Combine(channel, id + ".writing"), "ok\t3\t4\nvoid F(void)\n{\n}\n");
                    File.Move(Path.Combine(channel, id + ".writing"), Path.Combine(channel, id + ".answer"));
                    return;
                }
                Thread.Sleep(10);
            }
        });
        var served = DecompServer.Ask(settings, "Toy.exe", ["param\t1000\t0\ttoy::Stream", "function\t1000"], TimeSpan.FromSeconds(10));
        await answering;
        Assert.NotNull(served);
        Assert.Equal(("void F(void)\n{\n}\n", 3L, 4L), (served.Text, served.ApplyMs, served.DecompileMs));
        Assert.Equal(["param\t1000\t0\ttoy::Stream", "function\t1000"], seen);
        Assert.Empty(Directory.GetFiles(channel, "*.answer"));
    }
}
