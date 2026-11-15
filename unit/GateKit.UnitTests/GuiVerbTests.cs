using GateKit.Copies;
using MoonSharp.Interpreter;

namespace GateKit.UnitTests;

/// <summary>
/// The GK.Gui verbs of the bridge, run in a Lua interpreter against a stand-in of the game's UI:
/// widgets under ContentRoot with their view models, commands, the client's game state. A verb
/// answers what it SAW: a command that does nothing is never reported as done.
/// </summary>
public sealed class GuiVerbTests
{
    const string World = """
        World = { state = "Menu", widgets = {}, later = {}, handlers = {} }
        local function each(list, f) for _, x in ipairs(list) do f(x) end end
        function World.after(f) World.later[#World.later + 1] = f end
        function World.tick() local due = World.later World.later = {} each(due, function(f) f() end) end
        function World.change(to)
            local from = World.state
            World.state = to
            each(World.handlers, function(h) h({ FromState = from, ToState = to }) end)
        end
        function World.widget(name, data, parts)
            local w = { Type = "ls.UIWidget", Name = name, DataContext = data, VisualChildrenCount = 0, parts = parts or {} }
            function w:Find(n) return self.parts[n] end
            World.widgets[#World.widgets + 1] = w
            return w
        end
        function World.close(name)
            for i, w in ipairs(World.widgets) do if w.Name == name then table.remove(World.widgets, i) return end end
        end
        function World.command(f) return { Execute = function(self, p) f(p) end } end

        local content = setmetatable({ Name = "ContentRoot" }, { __index = function(_, k)
            if k == "VisualChildrenCount" then return #World.widgets end end })
        function content:VisualChild(i) return World.widgets[i] end
        local root = { VisualChildrenCount = 1 }
        function root:Find(name) if name == "ContentRoot" then return content end end
        function root:VisualChild(i) return content end

        Ext = {
            Utils = { GetGameState = function() return World.state end, MonotonicTime = function() return 0 end,
                      GetGlobalSwitches = function() return {} end },
            UI = { GetRoot = function() return root end, Box = function(v) return v end, Defer = function(f) f() end },
            Events = { GameStateChanged = { Subscribe = function(_, f) World.handlers[#World.handlers + 1] = f end },
                       Tick = { Subscribe = function() end } },
            -- a timer never fires here: a command run from one does nothing in the game either
            Timer = { WaitFor = function() end },
            IO = { LoadFile = function() return nil end, SaveFile = function() end },
            Json = {}, Debug = {}, Loca = {},
        }

        -- what the file channel does with an observed answer: a look every tick, the last one final
        function Settle(answer)
            local value, check, within = GK.Unobserved(answer)
            if not check then return value end
            for i = 1, 10 do
                local seen = check(i * 16, false)
                if seen then return seen end
                World.tick()
            end
            return check(within, true)
        end
        """;

    static Script Game()
    {
        var lua = Path.Combine(CopySettings.RepositoryRoot()!, "mods", "GateKit", "Mods", "Gwynerva_GateKit", "ScriptExtender", "Lua");
        var script = new Script(CoreModules.Preset_Complete);
        script.DoString(World, null, "world");
        foreach (var file in new[] { "Shared/Bridge.lua", "Client/Windows.lua", "Client/Popups.lua" })
            script.DoString(File.ReadAllText(Path.Combine(lua, file)), null, file);
        return script;
    }

    static Table Run(Script game, string code) => game.DoString(code).Table;

    [Fact]
    public void New_game_answers_ok_only_once_the_settings_are_seen_open()
    {
        var game = Game();
        var opened = Run(game, """
            World.widget("MainMenu", { StartGameCommand = World.command(function() World.after(function() World.widget("DMSettings", {}) end) end) })
            return Settle(GK.Gui.NewGame())
            """);
        Assert.True(opened.Get("ok").Boolean);
        Assert.Contains("the settings are open", opened.Get("did").String);

        var dead = Run(Game(), """
            World.widget("MainMenu", { StartGameCommand = World.command(function() end) })
            return Settle(GK.Gui.NewGame())
            """);
        Assert.False(dead.Get("ok").Boolean);
        Assert.Equal("New Game was pressed and the new-game settings did not open", dead.Get("error").String);
    }

    [Fact]
    public void New_game_confirms_and_answers_ok_only_once_the_game_has_left_the_menu()
    {
        var started = Run(Game(), """
            local preset = {}
            local confirm = { Command = World.command(function(p) if p == preset then World.after(function() World.change("LoadLevel") end) end end),
                              CommandParameter = preset }
            World.widget("MainMenu", {})
            World.widget("DMSettings", {}, { ConfirmBtn = confirm })
            return Settle(GK.Gui.NewGame())
            """);
        Assert.True(started.Get("ok").Boolean);
        Assert.Equal("LoadLevel", started.Get("state").String);

        var stayed = Run(Game(), """
            World.widget("MainMenu", {})
            World.widget("DMSettings", {}, { ConfirmBtn = { Command = World.command(function() end), CommandParameter = {} } })
            return Settle(GK.Gui.NewGame())
            """);
        Assert.False(stayed.Get("ok").Boolean);
        Assert.Equal("Confirm was pressed and the game stayed in the menu", stayed.Get("error").String);
    }

    [Fact]
    public void Save_as_answers_saved_only_once_the_game_has_written_the_save()
    {
        var saved = Run(Game(), """
            World.state = "Running"
            World.widget("SaveGamePopup", { DoSaveWithName = World.command(function(name)
                World.after(function() World.change("Save") World.after(function() World.change("Running") end) end) end) })
            return Settle(GK.Gui.SaveAs("Before the fight"))
            """);
        Assert.True(saved.Get("ok").Boolean);
        Assert.Equal("Before the fight", saved.Get("saved").String);

        var ignored = Run(Game(), """
            World.state = "Running"
            World.widget("SaveGamePopup", { DoSaveWithName = World.command(function() end) })
            return Settle(GK.Gui.SaveAs("Before the fight"))
            """);
        Assert.False(ignored.Get("ok").Boolean);
        Assert.Equal("DoSaveWithName ran and the game did not begin to save", ignored.Get("error").String);

        var stuck = Run(Game(), """
            World.state = "Running"
            World.widget("SaveGamePopup", { DoSaveWithName = World.command(function() World.after(function() World.change("Save") end) end) })
            return Settle(GK.Gui.SaveAs("Before the fight"))
            """);
        Assert.False(stuck.Get("ok").Boolean);
        Assert.Equal("the save began and had not ended in 15 s", stuck.Get("error").String);
    }

    [Fact]
    public void Save_as_opens_the_name_box_and_says_so_only_when_it_is_open()
    {
        var game = Game();
        var named = Run(game, """
            World.state = "Running"
            World.widget("SaveGame", { CustomEvent = World.command(function(event)
                if event == "OpenNewSaveDialog" then World.after(function() World.widget("SaveGamePopup", {}) end) end end) })
            return Settle(GK.Gui.SaveAs("x"))
            """);
        Assert.True(named.Get("ok").Boolean);
        Assert.Equal("the name box is open", named.Get("did").String);

        var closed = Run(Game(), """
            World.state = "Running"
            World.widget("SaveGame", { CustomEvent = World.command(function() end) })
            return Settle(GK.Gui.SaveAs("x"))
            """);
        Assert.False(closed.Get("ok").Boolean);
        Assert.Equal("the name box did not open", closed.Get("error").String);
    }
}
