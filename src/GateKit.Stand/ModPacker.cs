using System.Diagnostics;
using System.Runtime.InteropServices;
using LSLib.LS;
using LSLib.LS.Enums;

namespace GateKit.Stand;

/// <summary>Packs a mod folder (one holding Mods/&lt;Folder&gt;/...) into a .pak. Behind an interface
/// so a stand may pack in-process (<see cref="LSLibPacker"/>) or with Divine
/// (<see cref="DivinePacker"/>) without anything else changing.</summary>
public interface IModPacker
{
    /// <summary>Writes <paramref name="pak"/> from <paramref name="modRoot"/>; throws <see cref="StandException"/> on failure.</summary>
    void Pack(string modRoot, string pak);
}

/// <summary>
/// Packs in-process with LSLib (the submodule under external/lslib, compiled by
/// external/lslib-core): a version 18 package compressed with LZ4 at its fast level, which is
/// what the game's own packages use and what Divine writes for `-g bg3 -c lz4`. Nothing native
/// and nothing of any one operating system: the compressor is managed, so this works everywhere
/// the stand does, and no tool has to be installed by hand.
/// </summary>
public sealed class LSLibPacker : IModPacker
{
    public void Pack(string modRoot, string pak)
    {
        var full = Path.GetFullPath(modRoot);
        if (!Directory.Exists(full)) throw new StandException($"nothing to pack: {full} does not exist");

        var build = new PackageBuildData
        {
            Version = PackageVersion.V18,
            Compression = CompressionMethod.LZ4,
            CompressionLevel = LSCompressionLevel.Fast,
            Priority = 0,
            Hash = false,
        };
        // Enumerated here rather than through LSLib's Packager: its own "hidden file" test looks
        // at the whole path, so a repository checked out under a folder starting with "." (a
        // worktree under .claude, say) would leave every file out. Here only a name inside the
        // mod root hides a file, which is what Divine was meant to do.
        foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            var name = Path.GetRelativePath(full, file).Replace('\\', '/');
            if (name.Split('/').Any(part => part.StartsWith('.'))) continue;
            build.Files.Add(PackageBuildInputFile.CreateFromFilesystem(file, name));
        }
        if (build.Files.Count == 0) throw new StandException($"nothing to pack: {full} holds no files");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(pak))!);
        if (File.Exists(pak)) File.Delete(pak);
        try
        {
            using var writer = PackageWriterFactory.Create(build, pak);
            writer.Write();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // A half-written package must not be left where a build would install it.
            try { File.Delete(pak); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw new StandException($"could not write {pak}: {e.Message}");
        }
    }
}

/// <summary>
/// Packs with LSLib's command line tool Divine, for anyone who would rather use their own copy
/// of it (GATEKIT_PACKER=divine; GATEKIT_DIVINE: Divine.exe, or Divine.dll run
/// through `dotnet`): `-g bg3 -a create-package -s SRC -d PAK -c lz4`. Divine targets an older
/// .NET; GATEKIT_DOTNET_ROOT names a private runtime for it, else it rolls forward onto the newest
/// one installed (DOTNET_ROLL_FORWARD=Major).
/// </summary>
public sealed class DivinePacker(string? divine, string? dotnetRoot) : IModPacker
{
    /// <summary>The size of a package with no files in it.</summary>
    const long EmptyPackage = 64;

    public void Pack(string modRoot, string pak)
    {
        // Divine leaves out every file whose path has a folder starting with "." - all of them
        // when the repository itself sits under one (.claude/worktrees, say): it then writes an
        // empty package and says it succeeded. Such a source is packed from a copy.
        var full = Path.GetFullPath(modRoot);
        if (full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p.StartsWith('.') && p != "." && p != ".."))
        {
            var staging = Path.Combine(Path.GetTempPath(), "gatekit-pack-" + Guid.NewGuid().ToString("N"));
            try
            {
                Platform.FileTree.CopyTree(full, staging);
                PackFrom(staging, pak);
            }
            finally { Platform.FileTree.TryRemoveTree(staging); }
            return;
        }
        PackFrom(full, pak);
    }

    void PackFrom(string modRoot, string pak)
    {
        if (string.IsNullOrEmpty(divine))
            throw new StandException("GATEKIT_PACKER asks for Divine: set GATEKIT_DIVINE to Divine.exe (or Divine.dll), "
                + "from https://github.com/Norbyte/lslib/releases, or unset GATEKIT_PACKER to pack with the built-in LSLib");
        if (!File.Exists(divine)) throw new StandException($"GATEKIT_DIVINE names {divine}, which does not exist");
        var start = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (divine.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            start.FileName = string.IsNullOrEmpty(dotnetRoot) ? "dotnet"
                : new[] { "dotnet.exe", "dotnet" }.Select(n => Path.Combine(dotnetRoot, n)).FirstOrDefault(File.Exists) ?? "dotnet";
            start.ArgumentList.Add(divine);
        }
        else start.FileName = divine;
        foreach (var arg in new[] { "-g", "bg3", "-a", "create-package", "-s", modRoot, "-d", pak, "-c", "lz4" })
            start.ArgumentList.Add(arg);
        if (!string.IsNullOrEmpty(dotnetRoot))
        {
            start.Environment["DOTNET_ROOT"] = dotnetRoot;
            // The host prefers DOTNET_ROOT_<ARCH>, and `dotnet run` hands its own down to us: a
            // stale one would send Divine to the wrong runtime.
            start.Environment["DOTNET_ROOT_" + RuntimeInformation.ProcessArchitecture.ToString().ToUpperInvariant()] = dotnetRoot;
        }
        else start.Environment["DOTNET_ROLL_FORWARD"] = "Major";

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(pak))!);
        if (File.Exists(pak)) File.Delete(pak);
        using var process = Process.Start(start) ?? throw new StandException($"could not start {divine}");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        if (!File.Exists(pak) || new FileInfo(pak).Length <= EmptyPackage)
            throw new StandException("Divine did not produce the pak (or an empty one):\n" + output.Result + error.Result);
    }
}
