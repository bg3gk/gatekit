using GateKit.Platform;
using GateKit.Stand;

namespace GateKit.Tests;

/// <summary>A folder under the system temp folder, deleted (links as links) at the end of a test.</summary>
public sealed class TempFolder : IDisposable
{
    public TempFolder() => Directory.CreateDirectory(Path);

    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gatekit-tests", Guid.NewGuid().ToString("N"));

    public string this[string relative] => System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

    /// <summary>Writes a file (folders created) and returns its path.</summary>
    public string Write(string relative, string text = "x")
    {
        var path = this[relative];
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    public void Dispose()
    {
        try { FileTree.RemoveTree(Path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>The real platform for links (they are what is tested), everything else faked:
/// game processes are a list the test fills, Steam is a folder of the test's.</summary>
public sealed class FakePlatform : IGamePlatform
{
    readonly IGamePlatform _real = GamePlatform.Current;

    public string Name => "fake";
    public StringComparison PathComparison => _real.PathComparison;
    public string? Steam { get; set; }
    public List<GameProcess> Processes { get; } = [];
    public List<GameLaunch> Started { get; } = [];
    public string? Profile { get; set; }

    public string? SteamRoot() => Steam;
    public IReadOnlyList<string> SteamLibraries() => Steam is null ? [] : GateKit.Platform.Steam.Libraries(Steam);
    public string ProfileFolder(string gameFolder) => Profile ?? System.IO.Path.Combine(gameFolder, "..", "profile");
    public string? ScriptExtenderCache(string gameFolder) => null;
    public string ToGamePath(string hostPath) => hostPath;
    public string InstanceProfileFolder(string instanceRoot) => System.IO.Path.Combine(instanceRoot, "home", "profile");
    public void PrepareInstanceHome(string instanceRoot) => Directory.CreateDirectory(System.IO.Path.Combine(instanceRoot, "home"));
    public Action<GameLaunch>? OnStart { get; set; }

    public void StartGame(GameLaunch launch)
    {
        Started.Add(launch);
        Processes.Add(new GameProcess(1000 + Started.Count, launch.Executable));
        OnStart?.Invoke(launch);
    }
    public IReadOnlyList<GameProcess> GameProcesses() => [.. Processes];
    public void Kill(int pid) => Processes.RemoveAll(p => p.Pid == pid);
    public void HardLink(string existingFile, string newLink) => _real.HardLink(existingFile, newLink);
    public void LinkFolder(string existingFolder, string newLink) => _real.LinkFolder(existingFolder, newLink);
    public bool CanPostKeys => false;
    public void PostKey(int pid, int virtualKey) => throw new PlatformCapabilityException(WhyNot("key"));

    /// <summary>Window and console: off like a headless platform unless a test turns them on
    /// (the scenario runner saves a screenshot and the console text when a scenario fails).</summary>
    public bool Window { get; set; }
    public List<string> Console { get; } = ["a console line"];

    public bool CanCaptureWindow => Window;
    public Screenshot CaptureWindow(int pid) => Window
        ? new Screenshot(2, 2, new byte[2 * 2 * 4])
        : throw new PlatformCapabilityException(WhyNot("shot"));
    public bool CanReadConsole => Window;
    public IReadOnlyList<string> ReadConsole(int pid) => Window ? Console : throw new PlatformCapabilityException(WhyNot("console"));

    /// <summary>A deck: windows the test gives out by pid, adopted into a list instead of a window.</summary>
    public Dictionary<int, nint> Windows { get; } = [];
    public List<WindowAdoption> Adopted { get; } = [];
    public List<(nint Window, int X, int Y, int Width, int Height)> Placed { get; } = [];
    public bool? Firewall { get; set; }
    public int ForegroundKept { get; private set; }

    public bool CanHostWindows => Window;
    public nint FindGameWindow(int pid) => Windows.TryGetValue(pid, out var window) ? window : 0;

    public WindowAdoption AdoptWindow(nint window, nint host)
    {
        var adoption = new WindowAdoption(window, 0, 0, (0, 0, 800, 600));
        Adopted.Add(adoption);
        return adoption;
    }

    public void PlaceWindow(nint window, int x, int y, int width, int height, bool front) => Placed.Add((window, x, y, width, height));
    public void ReleaseWindow(WindowAdoption adoption) => Adopted.RemoveAll(a => a.Window == adoption.Window);
    /// <summary>A rule is held against a path, so a fake that answers per path is the only one
    /// that can tell a copy's executable from the installed game's.</summary>
    public Func<string, bool?>? FirewallFor { get; set; }

    public bool? FirewallAllows(string executable) => FirewallFor is null ? Firewall : FirewallFor(executable);

    public IDisposable KeepForeground(string executable, TextWriter? say = null)
    {
        ForegroundKept++;
        return new Kept();
    }

    sealed class Kept : IDisposable
    {
        public void Dispose() { }
    }

    public string WhyNot(string capability) => $"{capability}: not on the fake platform";
}

/// <summary>A fake installed game, profile and state folder in a temp folder.</summary>
public sealed class FakeGame : IDisposable
{
    public const string ModList = """
        <?xml version="1.0" encoding="UTF-8"?>
        <save>
            <version major="4" minor="8" revision="0" build="10"/>
            <region id="ModuleSettings">
                <node id="root">
                    <children>
                        <node id="Mods">
                            <children>
                                <node id="ModuleShortDesc">
                                    <attribute id="Folder" type="LSString" value="GustavX"/>
                                    <attribute id="UUID" type="guid" value="cb555efe-2d9e-131f-8195-a89329d218ea"/>
                                </node>
                                <node id="ModuleShortDesc">
                                    <attribute id="Folder" type="LSString" value="SomeMod"/>
                                    <attribute id="UUID" type="guid" value="11111111-2222-3333-4444-555555555555"/>
                                </node>
                                <node id="ModuleShortDesc">
                                    <attribute id="Folder" type="LSString" value="OtherMod"/>
                                    <attribute id="UUID" type="guid" value="66666666-2222-3333-4444-555555555555"/>
                                </node>
                            </children>
                        </node>
                    </children>
                </node>
            </region>
        </save>

        """;

    public FakeGame()
    {
        Temp.Write("game/bin/bg3_dx11.exe", "exe");
        Temp.Write("game/bin/steam_appid.txt", "1086940");
        Temp.Write("game/bin/ScriptExtenderSettings.json", "{\"Keep\": 1}");
        Temp.Write("game/bin/sub/library.dll", "dll");
        Temp.Write("game/bin/BG3ScriptExtender.dll", "not ours");
        Temp.Write("game/bin/crash.log", "log");
        Temp.Write("game/Data/Gustav.pak", "pak data");
        Temp.Write("game/Data/Localization/English.pak", "loca");
        Temp.Write("game/Data/Mods/Loose/meta.lsx", "loose");
        Temp.Write("game/Data/Video/Intro.bk2", "video");
        Temp.Write("game/Data/Video/Splash_Logo_Larian.bk2", "");
        Temp.Write("profile/PlayerProfiles/Public/modsettings.lsx", ModList.Replace("\r\n", "\n"));
        Temp.Write("profile/PlayerProfiles/Public/Savegames/Story/Save1/Save1.lsv", "save");
        Temp.Write("profile/PlayerProfiles/Public/UILayout.lsx", "layout");
        Temp.Write("profile/graphicSettings.lsx", "graphics");
        Temp.Write("profile/Mods/SomeMod.pak", "mod");
        Temp.Write("profile/Mods/Gwynerva_GateKit.pak", "bridge of the installed game");
        Temp.Write("mods/GateKit/Mods/Gwynerva_GateKit/ScriptExtender/Lua/BootstrapServer.lua", "-- bridge");
        Temp.Write("mods/GateKit/Mods/Gwynerva_GateKit/meta.lsx", Meta("Gwynerva_GateKit", "GateKit Bridge", ModSettings.BridgeUuid, WeaveUuid));
        Temp.Write("mods/Weave/Mods/Gwynerva_Weave/ScriptExtender/Lua/BootstrapClient.lua", "-- weave");
        Temp.Write("mods/Weave/Mods/Gwynerva_Weave/meta.lsx", Meta("Gwynerva_Weave", "Weave", WeaveUuid, null));
        Platform.Profile = Temp["profile"];
    }

    public const string WeaveUuid = "8ee3d1c7-0362-41bc-b7c9-b10d85c62f4b";

    /// <summary>A meta.lsx shaped like the real ones: dependencies first, then ModuleInfo.</summary>
    public static string Meta(string folder, string name, string uuid, string? dependency) => $$"""
        <?xml version="1.0" encoding="UTF-8"?>
        <save>
          <region id="Config">
            <node id="root">
              <children>
                {{(dependency is null ? "<node id=\"Dependencies\"/>" : $"<node id=\"Dependencies\">\n<children>\n<node id=\"ModuleShortDesc\">\n<attribute id=\"Folder\" type=\"LSString\" value=\"Dep\"/>\n<attribute id=\"UUID\" type=\"FixedString\" value=\"{dependency}\"/>\n</node>\n</children>\n</node>")}}
                <node id="ModuleInfo">
                  <attribute id="Folder" type="LSString" value="{{folder}}"/>
                  <attribute id="Name" type="LSString" value="{{name}}"/>
                  <attribute id="UUID" type="FixedString" value="{{uuid}}"/>
                  <attribute id="Version64" type="int64" value="36028797018963968"/>
                  <children><node id="PublishVersion"><attribute id="Version64" type="int64" value="1"/></node></children>
                </node>
              </children>
            </node>
          </region>
        </save>
        """;

    public TempFolder Temp { get; } = new();
    public FakePlatform Platform { get; } = new();

    public StandSettings Settings(string? instance = null) => StandSettings.Resolve(Platform, name => name switch
    {
        "BG3_GAME" => Temp["game"],
        "GATEKIT_STATE" => Temp["state"],
        "GATEKIT_INSTANCES" => Temp["instances"],
        "GATEKIT_BRIDGE" => Temp["mods"],
        "GATEKIT_CONFIG" => null,
        _ => null,
    }, workingDirectory: Temp.Path, instanceOverride: instance);

    /// <summary>A stand on this game; its output goes to <see cref="Output"/>.</summary>
    public Stand.Stand Stand(string? instance = null) => new(Settings(instance), Platform, Output, new CopyPacker());

    public StringWriter Output { get; } = new();

    /// <summary>Pretends the stand's game runs (a process in its bin).</summary>
    public void Running(Stand.Stand stand) => Platform.Processes.Add(new GameProcess(4242, stand.Layout.Exe));

    public void Dispose() => Temp.Dispose();
}

/// <summary>"Packs" by writing the list of source files: enough to see that build ran.</summary>
public sealed class CopyPacker : IModPacker
{
    public int Packed { get; private set; }

    public void Pack(string modRoot, string pak)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(pak)!);
        File.WriteAllLines(pak, Directory.EnumerateFiles(modRoot, "*", SearchOption.AllDirectories));
        Packed++;
    }
}
