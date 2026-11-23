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

    // A Noesis object as the game hands it out: it prints its type and address.
    const string Objects = """
        function World.object(kind, address, fields)
            return setmetatable(fields or {}, { __tostring = function() return kind .. " (" .. address .. ")" end })
        end
        function World.children(element, list)
            element.VisualChildrenCount = #list
            function element:VisualChild(i) return list[i] end
            return element
        end
        """;

    [Fact]
    public void Set_on_a_slider_answers_once_its_view_model_holds_the_value()
    {
        var game = Game();
        game.DoString(Objects);
        var set = Run(game, """
            local data = World.object("gui::DCGammaCalibration", "00A0", { Gamma = 1.0 })
            function data:GetAllProperties() return { Gamma = self.Gamma, PlayerId = 1 } end
            local slider = setmetatable({}, {
                __index = { Type = "Slider", Name = "Slider", Minimum = 0.5, Maximum = 1.5, DataContext = data, VisualChildrenCount = 0, Value = 1.0 },
                __newindex = function(t, k, v)
                    getmetatable(t).__index[k] = v
                    if k == "Value" then World.after(function() data.Gamma = v end) end   -- the binding, a frame later
                end })
            World.children(World.widget("GammaCalibration", data), { slider })
            return Settle(GK.Gui.Set("GammaCalibration", "Slider", 1.2))
            """);
        Assert.True(set.Get("ok").Boolean);
        Assert.Equal(1.2, set.Get("held").Table.Get("Gamma").Number, 6);

        var unbound = Run(Game(), """
            local data = { GetAllProperties = function() return { Gamma = 1.0 } end }
            local slider = { Type = "Slider", Name = "Slider", Minimum = 0.5, Maximum = 1.5, DataContext = data, VisualChildrenCount = 0, Value = 1.0 }
            local w = World.widget("GammaCalibration", data)
            w.VisualChildrenCount = 1
            function w:VisualChild() return slider end
            return Settle(GK.Gui.Set("GammaCalibration", "Slider", 1.2))
            """);
        Assert.True(unbound.Get("ok").Boolean);                       // the slider reads the value
        Assert.Equal(DataType.Nil, unbound.Get("held").Type);         // and no number of its data took it
        Assert.Equal(1.0, unbound.Get("holds").Table.Get("Gamma").Number);
    }

    [Fact]
    public void Look_says_a_page_is_laid_out_only_once_every_control_shown_is_arranged()
    {
        const string Page = """
            local function arranged(e, width) function e:DependencyProperties() return { ActualWidth = width } end return e end
            local list = arranged({ Type = "ListBox", Name = "Options", ItemsSource = { "a" }, VisualChildrenCount = 0 }, LIST)
            local next = arranged({ Type = "ls.LSButton", Name = "NextButton", VisualChildrenCount = 0, Content = "Next" }, 428.0)
            World.children(arranged(World.widget("SensitiveSettings", {}), 3950.0), { list, next })
            return GK.Gui.Look("SensitiveSettings", { layout = true })
            """;
        static Script WithObjects() { var game = Game(); game.DoString(Objects); return game; }
        var before = Run(WithObjects(), "local LIST = nil\n" + Page);      // the page arranged, its list not yet
        Assert.False(before.Get("laidOut").Boolean);
        var after = Run(WithObjects(), "local LIST = 1600.0\n" + Page);
        Assert.True(after.Get("laidOut").Boolean);
        Assert.Equal(DataType.Nil, Run(WithObjects(), "local LIST = 1600.0\n" + Page.Replace("{ layout = true }", "nil")).Get("laidOut").Type);
    }

    [Fact]
    public void Look_gives_a_combo_box_in_a_settings_list_as_one_choice_and_names_a_button_command()
    {
        var game = Game();
        game.DoString(Objects);
        var look = Run(game, """
            local custom = World.object("Noesis::BaseCommand", "0400", {})
            local data = World.object("gui::DCFirstTimeSetup", "0100", {})
            function data:GetAllProperties() return { CustomEvent = custom, OpenURLInBrowserCommand = World.object("Noesis::BaseCommand", "0500") } end
            local setting = World.object("ls.VMComboBoxSetting", "0200", { Text = "Show explicit content" })
            local answers = { "No", "Yes" }
            local opener = { Type = "ToggleButton", Name = "ComboBoxButton", DataContext = setting, VisualChildrenCount = 0, IsChecked = false }
            local combo = World.children({ Type = "ls.LSComboBox", Name = "comboBox", DataContext = setting, ItemsSource = answers, SelectedIndex = 1 }, { opener })
            local item = World.children({ Type = "ListBoxItem", DataContext = setting }, { combo })
            local list = World.children({ Type = "ListBox", Name = "Options", DataContext = data, ItemsSource = { setting } }, { item })
            local next = { Type = "ls.LSButton", Name = "NextButton", DataContext = data, VisualChildrenCount = 0, Content = "Next",
                           Command = World.object("Noesis::ICommand", "0410"), CommandParameter = "OpenGammaCalibration" }
            function next:DependencyProperties() return { Command = custom } end
            World.children(World.widget("SensitiveSettings", data), { list, next })
            return GK.Gui.Look("SensitiveSettings")
            """);
        var controls = look.Get("controls").Table.Values.Select(v => v.Table).ToList();
        Assert.Equal(new[] { "comboBox", "NextButton" }, controls.Select(c => c.Get("handle").String).ToArray());
        var combo = controls[0];
        Assert.Equal("list", combo.Get("kind").String);
        Assert.Equal("Yes", combo.Get("value").String);
        Assert.Equal(2, combo.Get("items").Table.Length);
        Assert.Equal("Options#1", combo.Get("in").String);
        Assert.Equal("CustomEvent", controls[1].Get("command").String);
        Assert.Equal("OpenGammaCalibration", controls[1].Get("parameter").String);
    }
}
