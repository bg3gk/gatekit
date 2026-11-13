using System.CommandLine;
using GateKit.Platform;
using GateKit.Stand;

namespace GateKit.Deck;

/// <summary>`gatekit window list|maximize|grid|label|release`: naming the running copies and
/// telling a deck how to show them. Everything here works without a deck too, except the three
/// that move tiles about.</summary>
public static class WindowCommands
{
    public static Command CreateCommand()
    {
        var command = new Command("window", "the running game copies and how a deck shows them");
        command.Add(List());
        command.Add(Maximize());
        command.Add(Simple("grid", "every tile back in the grid, nothing held open", DeckApi.Grid));
        command.Add(Label());
        command.Add(Release());
        command.Add(Backing("mirror", "draw this copy from the frames it shares; its own window is not seen"));
        command.Add(Backing("adopt", "put this copy's own window back in its tile"));
        command.Add(Fps());
        command.SetAction(_ => Run(settings => Print(DeckApi.List(settings, GamePlatform.Current))));
        return command;
    }

    static Command List()
    {
        var json = new Option<bool>("--json") { Description = "the whole report as JSON" };
        var command = new Command("list", "every running game: instance, label, process id and its tile") { json };
        command.SetAction(parse => Run(settings =>
        {
            if (parse.GetValue(json)) Console.Out.WriteLine(DeckApi.Report(settings, GamePlatform.Current).ToJsonString());
            else Print(DeckApi.List(settings, GamePlatform.Current));
        }));
        return command;
    }

    static Command Maximize()
    {
        var instance = Instance("the instance whose tile fills the deck");
        var command = new Command("maximize", "give one copy the whole deck and keep its tile while it restarts") { instance };
        command.SetAction(parse => Run(settings => Console.Out.WriteLine(DeckApi.Maximize(settings, parse.GetValue(instance)!))));
        return command;
    }

    static Command Label()
    {
        var instance = Instance("the instance to name");
        var text = new Argument<string?>("label")
        {
            Description = "what this copy is for, e.g. \"Nautiloid run\"; leave it out to take the name away",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var command = new Command("label", "name a copy, so no tile has to be guessed at") { instance, text };
        command.SetAction(parse => Run(settings =>
            Console.Out.WriteLine(DeckApi.Label(settings, parse.GetValue(instance)!, parse.GetValue(text)))));
        return command;
    }

    static Command Release()
    {
        var instance = Instance("the instance whose window goes back to the desktop");
        var command = new Command("release", "give a copy its own window back and take its tile out of the deck") { instance };
        command.SetAction(parse => Run(settings => Console.Out.WriteLine(DeckApi.Release(settings, parse.GetValue(instance)!))));
        return command;
    }

    /// <summary>`window mirror NAME` / `window adopt NAME`: which backing fills that tile. The deck
    /// has a default for new tiles (GATEKIT_DECK_BACKING); this is one copy, now.</summary>
    static Command Backing(string name, string description)
    {
        var instance = Instance("the instance whose tile changes");
        var command = new Command(name, description) { instance };
        command.SetAction(parse => Run(settings =>
            Console.Out.WriteLine(DeckApi.Backing(settings, parse.GetValue(instance)!, name))));
        return command;
    }

    /// <summary>`window fps NAME [n]`: how fast one copy may draw. With no number it says what the
    /// copy's caps are now. Needs no deck - it goes through the bridge, like everything else that
    /// reads or drives a game - but a deck's own budget will overrule it on its next change of
    /// layout.</summary>
    static Command Fps()
    {
        var instance = Instance("the instance whose frame rate changes");
        var rate = new Argument<int?>("fps")
        {
            Description = "frames a second, or 0 for no cap (which also lifts the throttle the engine "
                + "puts a background copy under); leave it out to read what the copy has now",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var command = new Command("fps", "how fast one copy may draw, in front or not") { instance, rate };
        command.SetAction(parse => Run(settings =>
        {
            var name = parse.GetValue(instance)!;
            var wanted = parse.GetValue(rate);
            if (wanted is null)
            {
                var state = FrameCap.Read(settings, GamePlatform.Current, name);
                Console.Out.WriteLine(state is null
                    ? $"{name}: this copy cannot say what its frame cap is (it needs GateKit's Script Extender build)"
                    : $"{name}: {state}");
                return;
            }
            Console.Out.WriteLine(FrameCap.Set(settings, GamePlatform.Current, name, wanted.Value)
                ? wanted.Value == 0
                    ? $"{name} draws as fast as it can, in front or not"
                    : $"{name} draws at most {wanted.Value} frames a second, in front or not"
                : $"{name}: this copy would not take a frame cap (it needs GateKit's Script Extender build)");
        }));
        return command;
    }

    static Command Simple(string name, string description, Func<StandSettings, string> action)
    {
        var command = new Command(name, description);
        command.SetAction(_ => Run(settings => Console.Out.WriteLine(action(settings))));
        return command;
    }

    static Argument<string> Instance(string description) =>
        new("instance") { Description = description + " (the installed game is \"" + DeckRecord.InstalledGame + "\")" };

    static void Print(IEnumerable<string> lines)
    {
        foreach (var line in lines) Console.Out.WriteLine(line);
    }

    static int Run(Action<StandSettings> action)
    {
        try
        {
            action(StandSettings.Resolve(GamePlatform.Current));
            return 0;
        }
        catch (StandException e)
        {
            Console.Error.WriteLine("ERROR: " + e.Message);
            return 1;
        }
        catch (PlatformCapabilityException e)
        {
            Console.Error.WriteLine("ERROR: " + e.Message);
            return 1;
        }
    }
}
