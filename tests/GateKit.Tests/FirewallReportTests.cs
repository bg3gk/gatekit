using GateKit.Stand;

namespace GateKit.Tests;

/// <summary>
/// The firewall report is here because of one fact that cost an evening of pop-ups: a rule is
/// held against an executable's path, and every game copy is a path of its own. Allowing the
/// installed game says nothing about a copy, so each one is asked about separately - once, in a
/// dialog that arrives elevated, over whatever the person was doing.
/// </summary>
public sealed class FirewallReportTests
{
    [Fact]
    public void A_copy_is_its_own_path_so_the_installed_game_being_allowed_does_not_cover_it()
    {
        using var game = new FakeGame();
        var settings = game.Settings();
        game.Stand("deck1").CreateInstance();
        game.Stand("deck2").CreateInstance();

        // Only the installed game has a rule, which is what the machine looks like after somebody
        // has played Baldur's Gate 3 once and never used GateKit.
        var installed = Path.Combine(settings.GameFolder, "bin", settings.ExeName);
        game.Platform.FirewallFor = path =>
            string.Equals(path, installed, StringComparison.OrdinalIgnoreCase);

        var report = FirewallReport.Read(settings, game.Platform);
        Assert.Equal(3, report.Copies.Count);
        Assert.Equal(["deck1", "deck2"], report.Missing.Select(c => c.Name).Order());

        // One line covers both, each rule named after the copy it is for.
        var fix = report.FixCommand!;
        Assert.Contains("GateKit: deck1", fix, StringComparison.Ordinal);
        Assert.Contains("GateKit: deck2", fix, StringComparison.Ordinal);
        Assert.DoesNotContain("(installed game)", fix, StringComparison.Ordinal);
        Assert.Contains("-Action Allow", fix, StringComparison.Ordinal);
    }

    [Fact]
    public void With_every_copy_allowed_there_is_nothing_to_run()
    {
        using var game = new FakeGame();
        var settings = game.Settings();
        game.Stand("deck1").CreateInstance();
        game.Platform.FirewallFor = _ => true;

        var report = FirewallReport.Read(settings, game.Platform);
        Assert.Null(report.FixCommand);
        Assert.Contains(report.Lines(), l => l.Contains("will not ask", StringComparison.Ordinal));
    }

    [Fact]
    public void A_system_whose_rules_cannot_be_read_is_reported_as_such_and_not_guessed_at()
    {
        using var game = new FakeGame();
        game.Platform.FirewallFor = _ => null;

        var report = FirewallReport.Read(game.Settings(), game.Platform);
        Assert.False(report.Readable);
        Assert.Contains(report.Lines(), l => l.Contains("cannot be read", StringComparison.Ordinal));
    }
}
