using GateKit.Bridge;

namespace GateKit.UnitTests;

/// <summary>The host's limit in the game's ticks (GateKit.Bridge.TickGuard).</summary>
public sealed class TickGuardTests
{
    static void Beat(TempFolder channel, string side, double tick) =>
        channel.Write($"state_{side}.json", $$"""{"side":"{{side}}","boot":1,"time":1,"gameState":"Running","tick":{{tick}}}""");

    [Fact]
    public void The_limit_follows_the_servers_ticks_and_not_the_clients_frames()
    {
        // the night's defect: the guard took the side that ran most, the client's frames at 144 a
        // second, and 30 s of a running game ran out in 5
        using var channel = new TempFolder();
        Beat(channel, "server", 100);
        Beat(channel, "client", 1000);
        var guard = new TickGuard(new BridgeClient(channel.Path), TimeSpan.FromSeconds(1), capToClock: false);
        Beat(channel, "server", 110);
        Beat(channel, "client", 5000);
        Assert.Null(guard.Over());
        Assert.Equal(10, guard.Ticks);
        Beat(channel, "server", 131);
        Assert.NotNull(guard.Over());
    }

    [Fact]
    public void Without_a_server_the_clients_clock_counts()
    {
        using var channel = new TempFolder();
        Beat(channel, "client", 10);
        var guard = new TickGuard(new BridgeClient(channel.Path), TimeSpan.FromSeconds(1), capToClock: false);
        Beat(channel, "client", 25);
        Assert.Null(guard.Over());
        Assert.Equal(15, guard.Ticks);
        Beat(channel, "client", 41);
        Assert.NotNull(guard.Over());
    }

    [Fact]
    public void Ticks_faster_than_the_nominal_rate_count_no_faster_than_the_clock()
    {
        // the night's W3 round 6: the fast load lifts the server's tick period while it loads, and a
        // 120 s wait over a headless level change ran out in ~10 s of thousands of ticks a second
        using var channel = new TempFolder();
        Beat(channel, "server", 100);
        var guard = new TickGuard(new BridgeClient(channel.Path), TimeSpan.FromSeconds(60));
        Beat(channel, "server", 5000);
        Assert.Null(guard.Over());
        // what the clock allows since the guard began (a busy test host may take a while), never the 4,900
        Assert.InRange(guard.Ticks, 1, 600);
    }
}
