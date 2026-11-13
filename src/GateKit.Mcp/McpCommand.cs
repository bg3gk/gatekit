using System.CommandLine;
using System.Text;
using GateKit.Stand;

namespace GateKit.Mcp;

/// <summary>`gatekit mcp`: the MCP server over stdio, and `gatekit mcp tools` to see what it offers.</summary>
public static class McpCommand
{
    public static Command CreateCommand()
    {
        var command = new Command("mcp", "serve GateKit to AI agents over the Model Context Protocol (stdio, one JSON message per line)");
        command.SetAction((parse, cancel) => ServeAsync(Instance(parse), cancel));
        command.Add(ToolList());
        command.Add(ToolCall());
        return command;
    }

    /// <summary>
    /// `gatekit mcp call &lt;tool&gt; [json]`: one tool, straight from a shell. The MCP server is how
    /// an agent reaches these, but a person debugging the loop - and a script that records a
    /// recipe - should not have to speak JSON-RPC over a pipe to do the same thing.
    /// </summary>
    static Command ToolCall()
    {
        var name = new Argument<string>("tool") { Description = "the tool's name, e.g. bg3_coverage_todo (`gatekit mcp tools` lists them)" };
        var arguments = new Argument<string?>("arguments")
        {
            Description = "its arguments as one JSON object; omitted means {}",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var command = new Command("call", "run one of this server's tools and print what it answers") { name, arguments };
        command.SetAction(async (parse, cancel) =>
        {
            var server = new GateKitServer(ToolHost.FromEnvironment(Instance(parse)));
            var text = parse.GetValue(arguments);
            var json = string.IsNullOrWhiteSpace(text) ? null : System.Text.Json.Nodes.JsonNode.Parse(text)?.AsObject();
            var result = await server.CallAsync(parse.GetValue(name)!, Args.From(json), cancel);
            foreach (var block in result.Content)
            {
                if (block is ModelContextProtocol.Protocol.TextContentBlock written) Console.Out.WriteLine(written.Text);
            }
            return result.IsError == true ? 1 : 0;
        });
        return command;
    }

    /// <summary>`--instance` is the root's, so it is read by name; without it the environment decides
    /// (GATEKIT_INSTANCE, BG3_GATEKIT_INSTANCE), exactly as for every other command.</summary>
    static string? Instance(ParseResult parse)
    {
        try { return parse.GetValue<string?>("--instance"); }
        catch (ArgumentException) { return null; } // the option is not there (the command stands alone)
    }

    static async Task<int> ServeAsync(string? instance, CancellationToken cancel)
    {
        var standardOut = Console.Out;
        // Nothing but the protocol may reach stdout: a stray Console.Write goes to stderr instead.
        Console.SetOut(Console.Error);
        try
        {
            var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = false, NewLine = "\n" };
            await new GateKitServer(ToolHost.FromEnvironment(instance)).ServeAsync(input, output, cancel);
            return 0;
        }
        catch (StandException e)
        {
            Console.Error.WriteLine("ERROR: " + e.Message);
            return 1;
        }
        finally
        {
            Console.SetOut(standardOut);
        }
    }

    static Command ToolList()
    {
        var command = new Command("tools", "the tools this server offers, the tools folder included (nothing is started)");
        command.SetAction(parse =>
        {
            var host = ToolHost.FromEnvironment(Instance(parse));
            var server = new GateKitServer(host);
            Console.Out.WriteLine("instance: " + (host.Instance ?? "none (the installed game)"));
            Console.Out.WriteLine($"tools folder: {server.Plugins.Folder}"
                + (Directory.Exists(server.Plugins.Folder) ? "" : " (none yet)"));
            foreach (var tool in server.Tools())
                Console.Out.WriteLine($"{tool.Name,-20} {Shorten(tool.Definition.Description)}");
            return 0;
        });
        return command;
    }

    static string Shorten(string? text) =>
        text is null ? "" : text.Length <= 96 ? text : text[..95].TrimEnd() + "...";
}
