using System.Reflection;

namespace GateKit.UnitTests;

/// <summary>Test fixtures clean up after themselves. xunit 2 disposes a test class that is
/// IDisposable or IAsyncLifetime; one that is only IAsyncDisposable is never disposed.</summary>
public sealed class FixtureTests
{
    [Fact]
    public void Every_test_class_that_holds_a_temp_folder_is_one_xunit_disposes()
    {
        static bool Holds(Type type) => type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Any(f => f.FieldType == typeof(TempFolder) || f.FieldType == typeof(FakeGame));
        var leaking = typeof(FixtureTests).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && t.GetMethods().Any(m => m.GetCustomAttributes<FactAttribute>().Any()) && Holds(t))
            .Where(t => !typeof(IDisposable).IsAssignableFrom(t) && !typeof(IAsyncLifetime).IsAssignableFrom(t))
            .Select(t => t.Name).ToList();
        Assert.True(leaking.Count == 0, "not disposed by xunit 2 (IAsyncDisposable alone is not enough): " + string.Join(", ", leaking));
    }

    [Fact]
    public void A_fake_game_leaves_nothing_behind()
    {
        string folder;
        using (var game = new FakeGame())
        {
            folder = game.Temp.Path;
            game.GameCopy("one").Build();                       // state, cache and a copy's profile written
            Assert.True(Directory.Exists(folder));
        }
        Assert.False(Directory.Exists(folder), $"{folder} was left behind");
    }

    [Fact]
    public void A_folder_that_cannot_be_removed_fails_the_test_instead_of_staying()
    {
        var temp = new TempFolder();
        var held = File.Open(temp.Write("held.txt"), FileMode.Open, FileAccess.Read, FileShare.None);
        try
        {
            if (OperatingSystem.IsWindows())
                Assert.Throws<IOException>(temp.Dispose);    // Linux deletes a file that is open
        }
        finally
        {
            held.Dispose();
            temp.Dispose();
        }
        Assert.False(Directory.Exists(temp.Path));
    }
}
