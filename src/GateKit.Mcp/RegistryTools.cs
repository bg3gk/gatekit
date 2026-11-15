using System.Text;
using System.Text.Json;
using GateKit.Symbols.Registry;
using ModelContextProtocol.Protocol;

namespace GateKit.Mcp;

/// <summary>The registry's live layer as tools, for agents working in the game: write what was
/// understood of the code, and ask what is known at an address or of an id - the committed registry
/// and every other agent's notes on this machine together (docs/contracts/cli.md, "registry").</summary>
static class RegistryTools
{
    public static IReadOnlyList<GateKitTool> Create() => [Note(), Withdraw(), At(), Show()];

    static JsonElement Schema(string text)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(text));
        return JsonElement.ParseValue(ref reader);
    }

    static RegistryStore? Store() => RegistryStore.Find(null);

    static Task<CallToolResult> Run(Func<System.Text.Json.Nodes.JsonNode> answer)
    {
        try
        {
            return Task.FromResult(Results.Text(answer()));
        }
        catch (Exception e) when (e is RegistryException or GateKit.Symbols.SymbolsException or GateKit.Json.JsonProblemException or GateKit.Home.HomeException)
        {
            throw new InvalidOperationException(e.Message);
        }
    }

    static GateKitTool Note() => new(
        new Tool
        {
            Name = "bg3_registry_note",
            Description =
                "Write what you understood of the game's code into the registry's live layer: every other agent on this machine "
                + "reads it at once, whatever checkout it works in. `id` is the name as the game writes it, scope by :: "
                + "(esv::RollSystem::RollDice); a thing with no name yet is <scope>::unnamed_<token> (`gatekit registry fingerprint` "
                + "gives the token). Give `at` (binary+0xRVA from a memory watch) for a function or global. `rung` says how sure it is: "
                + "confirmed needs evidence of kind watch, read or a test; a guess needs `rests_on` and `would_confirm` and is refused "
                + "without them. Below confirmation, `ask` what is open - above all how the game reaches the thing (what a player or "
                + "a test would do for it to run). Our own words only: never quote pseudo-code or the decompiler's names (param_1, "
                + "uVar3, FUN_...), never a date, a session or a machine's path. `gatekit registry commit` later writes it into a "
                + "checkout, without who, when and which task.",
            InputSchema = Schema("""
                {
                 "type": "object",
                 "required": ["id"],
                 "properties": {
                  "id": {"type": "string"},
                  "kind": {"type": "string", "enum": ["function", "type", "global"], "description": "Default function."},
                  "at": {"type": "string", "description": "binary+0xRVA, anywhere inside the function."},
                  "rung": {"type": "string", "enum": ["game", "toolkit", "confirmed", "inferred", "extender", "guess", "unknown"]},
                  "does": {"type": "string", "description": "A function: what it does, our sentence."},
                  "about": {"type": "string", "description": "A type or global: what it is."},
                  "evidence": {"type": "array", "items": {"type": "string"}, "description": "Each kind:sentence - watch, read, pairing, game-name, toolkit-name, extender-name, text."},
                  "tests": {"type": "array", "items": {"type": "string"}, "description": "Tests that prove it: path under tests/ without the extension, then case and step."},
                  "rests_on": {"type": "string"},
                  "would_confirm": {"type": "string"},
                  "ask": {"type": "array", "items": {"type": "string"}, "description": "Open questions."},
                  "ask_about": {"type": "string", "enum": ["reach", "conditions", "meaning", "other"], "description": "Default reach."},
                  "fields": {"type": "array", "items": {"type": "string"}, "description": "A type: offset:name[:type]."},
                  "who": {"type": "string", "description": "Who writes (default GATEKIT_WHO)."},
                  "task": {"type": "string", "description": "For which task (default GATEKIT_TASK)."}
                 }
                }
                """),
        },
        (args, _) => Run(() =>
        {
            var claim = LiveCommands.Note(new NoteRequest(args.Need("id"))
            {
                Kind = args.Str("kind") ?? "function", At = args.Str("at"), Rung = args.Str("rung"), Does = args.Str("does"), About = args.Str("about"),
                Evidence = args.Strings("evidence") ?? [], Tests = args.Strings("tests") ?? [], RestsOn = args.Str("rests_on"), WouldConfirm = args.Str("would_confirm"),
                Ask = args.Strings("ask") ?? [], AskAbout = args.Str("ask_about") ?? "reach", Fields = args.Strings("fields") ?? [], Who = args.Str("who"), Task = args.Str("task"),
            }, LiveLayer.OfHome(), Store(), TextWriter.Null);
            return LiveCommands.ShowJson(claim.Id, Store(), LiveLayer.OfHome());
        }));

    static GateKitTool Withdraw() => new(
        new Tool
        {
            Name = "bg3_registry_withdraw",
            Description = "Take back a note of the registry's live layer: the id's newest claim (the claim before it is what the id "
                + "says again), or every claim of it with `all`; `who` limits it to one writer's claims. Gone for every agent on this "
                + "machine at once; what a checkout already committed stays there.",
            InputSchema = Schema("""
                {
                 "type": "object",
                 "required": ["id"],
                 "properties": {
                  "id": {"type": "string"},
                  "all": {"type": "boolean", "description": "Every claim of the id, not only its newest. Default false."},
                  "who": {"type": "string", "description": "Only claims this writer made."}
                 }
                }
                """),
        },
        (args, _) => Run(() => LiveCommands.WithdrawJson(args.Need("id"), args.Flag("all"), args.Str("who"), LiveLayer.OfHome())));

    static GateKitTool At() => new(
        new Tool
        {
            Name = "bg3_registry_at",
            Description = "What is known at an address of the game's code (binary+0xRVA, as a memory watch or a debugger reports it): "
                + "the function that holds it, its names in the databases, the registry's ids there and every agent's live notes, "
                + "each marked live or committed, with its rung.",
            InputSchema = Schema("""{"type": "object", "required": ["address"], "properties": {"address": {"type": "string"}}}"""),
        },
        (args, _) => Run(() => LiveCommands.AtJson(args.Need("address"), Store(), LiveLayer.OfHome(), TextWriter.Null)));

    static GateKitTool Show() => new(
        new Tool
        {
            Name = "bg3_registry_show",
            Description = "What is known of an id of the registry - or of a name another source gives the thing (Script Extender's, "
                + "the Toolkit's): the committed entry, where it is in each build, and the live notes of every agent, marked live.",
            InputSchema = Schema("""{"type": "object", "required": ["id"], "properties": {"id": {"type": "string"}}}"""),
        },
        (args, _) => Run(() => LiveCommands.ShowJson(args.Need("id"), Store(), LiveLayer.OfHome())));
}
