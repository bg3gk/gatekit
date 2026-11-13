using System.Security.Cryptography;
using System.Text.Json.Nodes;
using GateKit.Platform;

namespace GateKit.Stand;

/// <summary>
/// Loads a locally built Script Extender instead of the stock one, and puts the stock one back.
/// The stock loader (bin/DWrite.dll) loads bin/BG3ScriptExtender.dll instead of the signed build
/// from its cache when bin/ScriptExtenderUpdaterConfig.json has "DebugLoadSE": true. So switching
/// is adding two files and removing them again; no file of the game or of Script Extender changes.
/// </summary>
public sealed class ScriptExtenderSwitch(string bin, string state)
{
    public string Dll => Path.Combine(bin, "BG3ScriptExtender.dll");
    public string Pdb => Path.Combine(bin, "BG3ScriptExtender.pdb");
    public string Config => Path.Combine(bin, "ScriptExtenderUpdaterConfig.json");
    /// <summary>What <see cref="Install"/> did, so <see cref="Remove"/> undoes exactly that.</summary>
    public string Record => Path.Combine(state, "sefork.json");
    /// <summary>The user's own loader config, if there was one.</summary>
    public string SavedConfig => Path.Combine(state, "sefork-updater-config.json");

    public sealed record Status(bool Fork, string? Dll, string? Source, string? Sha256, bool Managed);

    /// <summary>Which build the next start will load.</summary>
    public Status Current()
    {
        var record = File.Exists(Record) ? JsonFiles.ReadObject(Record) : [];
        var debugLoad = File.Exists(Config) && JsonFiles.ReadObject(Config)["DebugLoadSE"] is JsonValue v && v.TryGetValue<bool>(out var on) && on;
        var exists = File.Exists(Dll);
        return new Status(debugLoad && exists, exists ? Dll : null, record["source"]?.ToString(), exists ? Digest(Dll) : null, record.Count > 0);
    }

    static string Digest(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>Makes the next start load <paramref name="folder"/>/BG3ScriptExtender.dll (or that dll). The game must not run.</summary>
    public Status Install(string folder)
    {
        var built = File.Exists(folder) ? folder : Path.Combine(folder, "BG3ScriptExtender.dll");
        if (!File.Exists(built)) throw new StandException($"no BG3ScriptExtender.dll in {folder}");
        if (!File.Exists(Record))
        {
            // First install since the last remove: remember what was there before.
            if (File.Exists(Dll)) throw new StandException($"{Dll} exists and was not put there by the stand; move it away first");
            Directory.CreateDirectory(state);
            var hadConfig = File.Exists(Config);
            if (hadConfig) FileTree.CopyFile(Config, SavedConfig);
            JsonFiles.WriteObject(Record, new JsonObject { ["source"] = built, ["had_config"] = hadConfig });
        }
        else
        {
            JsonFiles.Edit(Record, r => r["source"] = built);
        }
        FileTree.CopyFile(built, Dll);
        var pdb = Path.ChangeExtension(built, ".pdb");
        if (File.Exists(pdb)) FileTree.CopyFile(pdb, Pdb);
        var config = File.Exists(SavedConfig) ? JsonFiles.ReadObject(SavedConfig) : [];
        config["DebugLoadSE"] = true;
        JsonFiles.WriteObject(Config, config);
        return Current();
    }

    /// <summary>Puts the stock Script Extender back; nothing to do if <see cref="Install"/> was not used.</summary>
    public Status Remove(TimeSpan? lockedFor = null)
    {
        if (!File.Exists(Record)) return Current();
        var record = JsonFiles.ReadObject(Record);
        // The loader's switch first: once it is gone the stock build loads, even if the dll
        // itself cannot be deleted yet.
        if (record["had_config"] is JsonValue had && had.TryGetValue<bool>(out var h) && h && File.Exists(SavedConfig))
            FileTree.CopyFile(SavedConfig, Config);
        else if (File.Exists(Config))
            File.Delete(Config);
        // A game just killed keeps the dll locked for a moment.
        var deadline = DateTime.UtcNow + (lockedFor ?? TimeSpan.FromSeconds(20));
        foreach (var path in new[] { Dll, Pdb })
        {
            while (File.Exists(path))
            {
                try { File.Delete(path); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    if (DateTime.UtcNow > deadline)
                        throw new StandException($"{path} is still in use; the stock Script Extender is already switched back, run `gatekit se remove` again later");
                    Thread.Sleep(500);
                }
            }
        }
        if (File.Exists(SavedConfig)) File.Delete(SavedConfig);
        File.Delete(Record);
        return Current();
    }

    /// <summary>Before a start: the chosen build (crash reports to Script Extender's author off), or stock.</summary>
    public string? ApplyForRun(string? folder, string seSettings)
    {
        if (folder is null)
        {
            Remove();
            return null;
        }
        Install(folder);
        JsonFiles.Edit(seSettings, s => s["SendCrashReports"] = false);
        return folder;
    }
}
