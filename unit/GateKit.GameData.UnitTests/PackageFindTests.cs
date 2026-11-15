using GateKit.GameData.Packages;

namespace GateKit.GameData.UnitTests;

/// <summary>
/// Where the index looks for packages. Nothing is opened here — `Find` only names files — so empty
/// files stand in for packages and no game is needed.
/// </summary>
public sealed class PackageFindTests : IDisposable
{
    private readonly string _root = GateKit.Testing.Scratch.NewPath("find");

    private string Touch(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []);
        return path;
    }

    [Fact]
    public void ALanguageCanComeFromAFolderOutsideTheInstallation()
    {
        Touch("Data", "Gustav.pak");
        var english = Touch("Data", "Localization", "English", "English.pak");
        // A Steam depot downloaded on its own keeps the installation's shape under its own folder.
        var french = Touch("depot_1086943", "Data", "Localization", "French", "French.pak");

        using var packages = new GamePackages(Path.Combine(_root, "Data"));
        var found = packages.Find(["English", "French"], [Path.Combine(_root, "depot_1086943", "Data", "Localization")]);

        Assert.Equal(english, found["English"].Path);
        Assert.Equal(french, found["French"].Path);
        Assert.True(found.ContainsKey("Gustav"));
    }

    [Fact]
    public void WithoutTheExtraFolderTheLanguageIsSimplyNotThere()
    {
        Touch("Data", "Gustav.pak");
        Touch("depot_1086943", "Data", "Localization", "French", "French.pak");

        using var packages = new GamePackages(Path.Combine(_root, "Data"));
        Assert.False(packages.Find(["English", "French"]).ContainsKey("French"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }
}
