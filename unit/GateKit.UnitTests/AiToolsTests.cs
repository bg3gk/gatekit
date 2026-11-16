using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GateKit.Copies;
using GateKit.Mcp;

namespace GateKit.UnitTests;

/// <summary>
/// The tools of the mod GateKit AI (mods/AI/tools/ai.json) are one contract for every client,
/// whatever model it runs: plain JSON Schema that every vendor's function calling takes. Held
/// here: flat objects of strings, integers and booleans; only the keywords type, properties,
/// required, description and enum; every argument described; names of lower-case letters, digits
/// and underscores; nothing that names a vendor; and the same definitions as the `tools` array a
/// client without MCP is given.
/// </summary>
public sealed partial class AiToolsTests
{
    static string File_ => Path.Combine(CopySettings.RepositoryRoot()!, "mods", "AI", "tools", "ai.json");

    static readonly HashSet<string> SchemaKeywords = new(StringComparer.Ordinal) { "type", "properties", "required" };
    static readonly HashSet<string> ArgumentKeywords = new(StringComparer.Ordinal) { "type", "description", "enum" };
    static readonly HashSet<string> ArgumentTypes = new(StringComparer.Ordinal) { "string", "integer", "boolean" };
    static readonly string[] Vendors = ["claude", "anthropic", "openai", "gpt", "gemini", "google", "llama", "ollama", "mistral", "copilot"];

    static JsonArray Tools() => JsonNode.Parse(File.ReadAllText(File_))!.AsArray();

    [Fact]
    public void The_tools_are_four_and_their_names_fit_every_client()
    {
        var names = Tools().Select(t => (string)t!["name"]!).ToList();
        Assert.Equal(["ai_step", "ai_look", "ai_scene", "ai_session"], names);
        Assert.All(names, name => Assert.Matches(Name(), name));
    }

    [Fact]
    public void Each_schema_is_flat_and_uses_only_the_keywords_every_vendor_takes()
    {
        foreach (var tool in Tools())
        {
            var name = (string)tool!["name"]!;
            var description = (string)tool["description"]!;
            Assert.InRange(description.Length, 20, 1024);
            var schema = tool["inputSchema"]!.AsObject();
            Assert.All(schema.Select(p => p.Key), key => Assert.Contains(key, SchemaKeywords));
            Assert.Equal("object", (string)schema["type"]!);
            var properties = schema["properties"]!.AsObject();
            foreach (var (argument, node) in properties)
            {
                var definition = node!.AsObject();
                Assert.True(definition.All(p => ArgumentKeywords.Contains(p.Key)), $"{name}.{argument} uses a keyword beyond type, description, enum");
                Assert.Contains((string)definition["type"]!, ArgumentTypes);
                Assert.True(((string?)definition["description"])?.Length >= 10, $"{name}.{argument} has no description");
                if (definition["enum"] is JsonArray values) Assert.All(values, v => Assert.Equal(System.Text.Json.JsonValueKind.String, v!.GetValueKind()));
            }
            if (schema["required"] is JsonArray required)
                Assert.All(required, r => Assert.True(properties.ContainsKey((string)r!), $"{name}: required names no argument"));
        }
    }

    [Fact]
    public void Nothing_names_a_vendor()
    {
        var text = File.ReadAllText(File_).ToLowerInvariant();
        var brief = File.ReadAllText(Path.Combine(CopySettings.RepositoryRoot()!, "mods", "AI", "brief.md")).ToLowerInvariant();
        foreach (var vendor in Vendors)
        {
            Assert.False(Regex.IsMatch(text, $@"\b{vendor}\b"), $"the tools name {vendor}");
            Assert.False(Regex.IsMatch(brief, $@"\b{vendor}\b"), $"the brief names {vendor}");
        }
    }

    [Fact]
    public void A_client_without_MCP_is_given_the_same_definitions()
    {
        var host = new ToolHost(_ => throw new CopyException("no copy is opened"), Path.GetDirectoryName(File_));
        var server = new GateKitServer(host);
        var tools = server.Plugins.Current().Where(t => t.Name.StartsWith("ai_", StringComparison.Ordinal)).ToList();
        var functions = McpCommand.FunctionTools(tools);
        Assert.Equal(4, functions.Count);
        foreach (var (function, source) in functions.Zip(Tools()))
        {
            Assert.Equal("function", (string)function!["type"]!);
            Assert.Equal((string)source!["name"]!, (string)function["function"]!["name"]!);
            Assert.Equal((string)source["description"]!, (string)function["function"]!["description"]!);
            Assert.True(JsonNode.DeepEquals(source["inputSchema"], function["function"]!["parameters"]), "the parameters are the tool's schema");
        }
    }

    [GeneratedRegex("^[a-z0-9_]{1,48}$")]
    private static partial Regex Name();
}
