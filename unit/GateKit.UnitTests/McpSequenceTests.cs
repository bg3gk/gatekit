using System.Text.Json.Nodes;
using GateKit.Mcp;

namespace GateKit.UnitTests;

/// <summary>`gatekit mcp sequence`: calls side by side in one server, a wait awaited with its delay.</summary>
public sealed class McpSequenceTests
{
    static McpSequence.Answer Wait(params string[] whats) => new(
        [new JsonObject
        {
            ["endedBy"] = "attention",
            ["attention"] = new JsonArray(whats.Select(w => (JsonNode)new JsonObject { ["what"] = w, ["side"] = "client", ["since_ms"] = 20 }).ToArray()),
        }.ToJsonString()], false);

    [Fact]
    public async Task A_background_wait_is_awaited_after_its_trigger_and_started_again_until_the_named_item()
    {
        var plan = (JsonObject)JsonNode.Parse("""
            { "steps": [
              { "id": "w", "tool": "bg3_wait", "arguments": { "timeout": 5 }, "background": true },
              { "id": "t", "tool": "bg3_lua", "arguments": { "code": "return 1" } },
              { "await": "w", "since": "t", "until": ["reaction"] } ] }
            """)!;
        var waits = 0;
        var calls = new List<string>();
        var trigger = new TaskCompletionSource();
        var output = new StringWriter();
        var code = await McpSequence.RunAsync(plan, async (tool, _, _) =>
        {
            calls.Add(tool);
            if (tool == "bg3_lua")
            {
                trigger.SetResult();
                return new McpSequence.Answer(["1", "attention: [{\"what\":\"combat_started\",\"waiting\":true,\"side\":\"server\"}]"], false);
            }
            // the first wait ends on the trigger's own job, the second on the prompt
            if (++waits == 1) { await trigger.Task; return Wait("job_done"); }
            await Task.Delay(30);
            return Wait("reaction");
        }, output);
        Assert.Equal(0, code);
        Assert.Equal(["bg3_wait", "bg3_lua", "bg3_wait"], calls);
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!).ToList();
        Assert.Equal("combat_started", (string)lines[0]["attention"]![0]!["what"]!);
        var rounds = (JsonArray)lines[1]["rounds"]!;
        Assert.Equal(2, rounds.Count);
        Assert.Equal("reaction", (string)rounds[1]!["attention"]![0]!["what"]!);
        Assert.Null(lines[1]["missing"]);
    }

    [Fact]
    public async Task A_named_item_that_never_comes_fails_the_sequence()
    {
        var plan = (JsonObject)JsonNode.Parse("""
            { "steps": [ { "id": "w", "tool": "bg3_wait", "background": true }, { "await": "w", "until": ["trade"] } ] }
            """)!;
        var output = new StringWriter();
        var code = await McpSequence.RunAsync(plan, (_, _, _) => Task.FromResult(new McpSequence.Answer(
            [new JsonObject { ["endedBy"] = "timeout", ["attention"] = new JsonArray() }.ToJsonString()], false)), output);
        Assert.Equal(1, code);
        Assert.Contains("\"missing\":[\"trade\"]", output.ToString(), StringComparison.Ordinal);
    }
}
