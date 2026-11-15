using System.CommandLine;
using System.Text;
using GateKit.Copies;

namespace GateKit.Mcp;

/// <summary>`gatekit mcp`: the MCP server over stdio, and `gatekit mcp tools` to see what it offers.</summary>
public static class McpCommand
{
    public static Command CreateCommand()
    {
        var command = new Command("mcp", "serve GateKit to AI agents over the Model Context Protocol (stdio, one JSON message per line)");
        command.SetAction((parse, cancel) => ServeAsync(parse, cancel));
        command.Add(ToolList());
        command.Add(ToolCall());
        command.Add(Sequence());
        command.Add(Config());
        return command;
    }

    /// <summary>`gatekit mcp sequence FILE [--set NAME=VALUE]`: tool calls run by one server, some side
    /// by side (McpSequence): a bg3_wait in the background, a trigger, the wait awaited with its delay.</summary>
    static Command Sequence()
    {
        var file = new Argument<string>("file") { Description = "the plan: { \"steps\": [...] } (see tests/_selftest/attention/*.json)" };
        var sets = new Option<string[]>("--set") { Description = "NAME=VALUE: replaces ${NAME} in the plan (e.g. --set SPEED=10)", AllowMultipleArgumentsPerToken = true };
        var command = new Command("sequence", "run a plan of tool calls in one server, background calls and awaits included; one JSON line per step") { file, sets };
        command.SetAction(async (parse, cancel) =>
        {
            var server = new GateKitServer(Host(parse));
            System.Text.Json.Nodes.JsonObject plan;
            try { plan = McpSequence.Load(parse.GetValue(file)!, parse.GetValue(sets) ?? []); }
            catch (Exception e) when (e is ArgumentException or IOException or System.Text.Json.JsonException)
            {
                Console.Error.WriteLine("ERROR: " + e.Message);
                return 2;
            }
            return await McpSequence.RunAsync(plan, async (tool, arguments, c) =>
            {
                var result = await server.CallAsync(tool, Args.From(arguments), c);
                var texts = result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(b => b.Text).ToList();
                return new McpSequence.Answer(texts, result.IsError == true);
            }, Console.Out, cancel);
        });
        return command;
    }


    /// <summary>
    /// `gatekit --instance &lt;copy&gt; mcp config`: how a client registers this server for one game
    /// copy. Several agents each drive a copy of their own, and a registration that names no copy
    /// drives the installed game - the player's own, with their saves - while one that starts
    /// through `dotnet run` builds first, under whatever games are running. So the registration
    /// written here names the copy and starts the program that is already built: this one.
    /// </summary>
    static Command Config()
    {
        var folder = new Option<string?>("--out") { Description = "write .mcp.json into this folder (a client started there finds the server); without it the registration is printed" };
        var command = new Command("config", "the registration of this server for one game copy, started from this build: name the copy with --instance") { folder };
        command.SetAction(parse =>
        {
            try
            {
                var registration = Registration(Instance(parse) ?? Environment.GetEnvironmentVariable("GATEKIT_INSTANCE"),
                    Environment.ProcessPath, System.Reflection.Assembly.GetEntryAssembly()?.Location);
                var text = registration.ToJsonString(new System.Text.Json.JsonSerializerOptions
                    { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }).ReplaceLineEndings("\n") + "\n";
                if (parse.GetValue(folder) is { } target)
                {
                    Directory.CreateDirectory(target);
                    var path = Path.Combine(Path.GetFullPath(target), ".mcp.json");
                    File.WriteAllText(path, text, new UTF8Encoding(false));
                    Console.Out.WriteLine($"wrote {path}");
                }
                else Console.Out.Write(text);
                return 0;
            }
            catch (CopyException e)
            {
                Console.Error.WriteLine("ERROR: " + e.Message);
                return 1;
            }
        });
        return command;
    }

    /// <summary>The <c>mcpServers</c> entry that starts this build for one copy.</summary>
    public static System.Text.Json.Nodes.JsonObject Registration(string? instance, string? program, string? entryAssembly)
    {
        if (string.IsNullOrWhiteSpace(instance))
            throw new CopyException("name the game copy this server is for: gatekit --instance <copy> mcp config. "
                + "A registration without one would drive the installed game.");
        if (string.IsNullOrEmpty(program))
            throw new CopyException("this program's own path cannot be read here, so no registration can point at it");
        var arguments = new System.Text.Json.Nodes.JsonArray();
        // Started as `dotnet gatekit.dll`, the program is dotnet and the build is its first argument.
        if (Path.GetFileNameWithoutExtension(program).Equals("dotnet", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(entryAssembly))
            arguments.Add(entryAssembly);
        arguments.Add("--instance");
        arguments.Add(instance);
        arguments.Add("mcp");
        return new System.Text.Json.Nodes.JsonObject
        {
            ["mcpServers"] = new System.Text.Json.Nodes.JsonObject
            {
                ["gatekit"] = new System.Text.Json.Nodes.JsonObject { ["command"] = program, ["args"] = arguments },
            },
        };
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
            var server = new GateKitServer(Host(parse));
            var text = parse.GetValue(arguments);
            System.Text.Json.Nodes.JsonObject? json = null;
            if (!string.IsNullOrWhiteSpace(text))
            {
                System.Text.Json.Nodes.JsonNode? node;
                try { node = System.Text.Json.Nodes.JsonNode.Parse(text); }
                catch (System.Text.Json.JsonException e)
                {
                    // Usually the shell's doing: quotes eaten or doubled on the way in. Say where it broke
                    // and what arrived, rather than a parser's stack trace.
                    Console.Error.WriteLine($"ERROR: the arguments of {parse.GetValue(name)} are not JSON ({e.Message}). "
                        + $"What arrived: {text}\nThey are one JSON object, e.g. gatekit mcp call bg3_lua '{{\"code\": \"return 1\"}}' "
                        + "- in a shell, single quotes around it keep the double quotes inside.");
                    return 2;
                }
                if (node is not System.Text.Json.Nodes.JsonObject given)
                {
                    Console.Error.WriteLine($"ERROR: the arguments of {parse.GetValue(name)} are one JSON object ({{...}}), not {node?.GetValueKind().ToString() ?? "null"}. What arrived: {text}");
                    return 2;
                }
                json = given;
            }
            var result = await server.CallAsync(parse.GetValue(name)!, Args.From(json), cancel);
            foreach (var block in result.Content)
            {
                if (block is ModelContextProtocol.Protocol.TextContentBlock written) Console.Out.WriteLine(written.Text);
                else if (block is ModelContextProtocol.Protocol.ImageContentBlock image)
                    Console.Out.WriteLine($"(an image, {image.MimeType}: a command line cannot show it; the tool says where it is saved when it saves one)");
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

    /// <summary>`--installed-game` is the root's too: without an instance, tools that write into the
    /// installed game go on only when it is named.</summary>
    static bool InstalledGame(ParseResult parse)
    {
        try { return parse.GetValue<bool>(CopySettings.InstalledGameOption); }
        catch (ArgumentException) { return false; }
    }

    static ToolHost Host(ParseResult parse) => ToolHost.FromEnvironment(Instance(parse), InstalledGame(parse));

    static async Task<int> ServeAsync(ParseResult parse, CancellationToken cancel)
    {
        var standardOut = Console.Out;
        // Nothing but the protocol may reach stdout: a stray Console.Write goes to stderr instead.
        Console.SetOut(Console.Error);
        try
        {
            var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = false, NewLine = "\n" };
            await new GateKitServer(Host(parse)).ServeAsync(input, output, cancel);
            return 0;
        }
        catch (CopyException e)
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
            var host = Host(parse);
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
