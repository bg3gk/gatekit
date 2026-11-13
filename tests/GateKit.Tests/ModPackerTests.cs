using System.Text;
using GateKit.GameData.Packages;
using LSLib.LS;
using GateKit.Stand;

namespace GateKit.Tests;

/// <summary>
/// The built-in packer writes a package the rest of GateKit can read back: the same files, the
/// same bytes, in the version and compression the game expects. No game data is needed - the
/// mod folder is made up here, and GateKit.GameData reads the result.
/// </summary>
public sealed class ModPackerTests : IDisposable
{
    readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>A mod root with a few files of different kinds, including one too small and one
    /// large enough that compression has something to do.</summary>
    Dictionary<string, byte[]> MakeMod(string root)
    {
        var expected = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["Mods/Made_Up/meta.lsx"] = Encoding.UTF8.GetBytes("<save><region id=\"Config\"/></save>"),
            ["Mods/Made_Up/ScriptExtender/Lua/Boot.lua"] = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("print('hello from the made-up mod')\n", 400))),
            ["Mods/Made_Up/ScriptExtender/Config.json"] = Encoding.UTF8.GetBytes("{\"RequiredVersion\":21}"),
            ["Public/Made_Up/empty.txt"] = [],
            ["Public/Made_Up/binary.bin"] = [.. Enumerable.Range(0, 5000).Select(i => (byte)(i * 7))],
        };
        foreach (var (path, body) in expected)
        {
            var full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, body);
        }
        // Hidden entries are left out, as Divine leaves them out.
        var hidden = Path.Combine(root, ".git");
        Directory.CreateDirectory(hidden);
        File.WriteAllText(Path.Combine(hidden, "HEAD"), "ref: refs/heads/main");
        File.WriteAllText(Path.Combine(root, ".gitignore"), "*.pak");
        return expected;
    }

    [Fact]
    public void A_packed_mod_reads_back_file_by_file()
    {
        var root = _temp["mod"];
        var expected = MakeMod(root);
        var pak = _temp["out/Made_Up.pak"];

        new LSLibPacker().Pack(root, pak);

        Assert.True(File.Exists(pak));
        using var packages = new GamePackages(_temp["out"]);
        var file = new PackageFile("Made_Up", pak);
        var listed = packages.List(file);

        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), listed.Select(f => f.Path).Order(StringComparer.Ordinal));
        foreach (var entry in listed)
        {
            Assert.True(entry.Readable, $"{entry.Path} cannot be read back");
            Assert.Equal(expected[entry.Path], GamePackages.ReadAll(entry.Info!));
            Assert.Equal(expected[entry.Path].Length, entry.Size);
        }
    }

    [Fact]
    public void The_package_is_the_version_and_compression_the_game_uses()
    {
        var root = _temp["mod"];
        MakeMod(root);
        var pak = _temp["out/Made_Up.pak"];
        new LSLibPacker().Pack(root, pak);

        using var package = new LSLib.LS.PackageReader().Read(pak);
        Assert.Equal((uint)LSLib.LS.Enums.PackageVersion.V18, package.Metadata.Version);
        var lua = package.Files.Single(f => f.Name.Replace('\\', '/').EndsWith("Boot.lua", StringComparison.Ordinal));
        Assert.Equal(LSLib.LS.CompressionMethod.LZ4, lua.Flags.Method());
        Assert.True(lua.SizeOnDisk < lua.UncompressedSize, "the file list says it was not compressed");
    }

    [Fact]
    public void A_source_folder_with_nothing_in_it_is_an_error_rather_than_an_empty_pak()
    {
        var root = _temp["empty"];
        Directory.CreateDirectory(root);
        var error = Assert.Throws<StandException>(() => new LSLibPacker().Pack(root, _temp["out/Empty.pak"]));
        Assert.Contains("nothing to pack", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pak_is_written_again_over_an_older_one()
    {
        var root = _temp["mod"];
        MakeMod(root);
        var pak = _temp["out/Made_Up.pak"];
        new LSLibPacker().Pack(root, pak);
        var first = new FileInfo(pak).Length;

        File.WriteAllText(Path.Combine(root, "Mods", "Made_Up", "meta.lsx"), new string('x', 20000));
        new LSLibPacker().Pack(root, pak);

        Assert.NotEqual(first, new FileInfo(pak).Length);
        using var packages = new GamePackages(_temp["out"]);
        var listed = packages.List(new PackageFile("Made_Up", pak));
        Assert.Equal(20000, listed.Single(f => f.Path.EndsWith("meta.lsx", StringComparison.Ordinal)).Size);
    }
}
