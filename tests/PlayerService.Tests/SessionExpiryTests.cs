using Orleans;
using PlayerService.Abstractions.Grains;
using PlayerService.Abstractions.Models;
using Xunit;

namespace PlayerService.Tests;

/// <summary>
/// Session liveness policy (plan §5.6): a 2-minute sliding TTL, evaluated lazily on read. Time is
/// advanced by hand rather than slept through, so these assertions are exact instead of hopeful.
/// </summary>
[Collection(ManualClockClusterCollection.Name)]
public sealed class SessionExpiryTests
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(2);

    private readonly ManualClockClusterFixture _fixture;

    public SessionExpiryTests(ManualClockClusterFixture fixture) => _fixture = fixture;

    private IGrainFactory Grains => _fixture.Cluster.Client;

    /// <summary>
    /// The requirement is "a device idle for one minute keeps its session", and every
    /// authenticated hit slides the expiry - so activity, not age, is what keeps a session alive.
    /// </summary>
    [Fact]
    public async Task An_active_session_slides_and_outlives_the_ttl()
    {
        var playerId = NewId("player");
        var grant = await LoginAsync(NewId("device"), playerId);
        var player = Grains.GetGrain<IPlayerGrain>(playerId);

        // Three quiet minutes in total, but never more than 90 seconds without a request.
        for (var i = 0; i < 3; i++)
        {
            _fixture.Clock.Advance(TimeSpan.FromSeconds(90));
            Assert.True(await player.ValidateAndSlideSessionAsync(grant.Token));
        }

        Assert.True(await player.IsOnlineAsync());
    }

    /// <summary>
    /// A crashed device stops sliding, so its session lapses on its own - no sweeper, no timer per
    /// player. And it lapses rather than sticking, so the device is not locked out forever.
    /// </summary>
    [Fact]
    public async Task A_silent_session_expires_and_frees_its_device()
    {
        var playerId = NewId("player");
        var deviceId = NewId("device");
        var grant = await LoginAsync(deviceId, playerId);
        var player = Grains.GetGrain<IPlayerGrain>(playerId);

        _fixture.Clock.Advance(Ttl + TimeSpan.FromSeconds(1));

        Assert.False(await player.ValidateAndSlideSessionAsync(grant.Token));
        Assert.False(await player.IsOnlineAsync());

        // Lazy expiry also opens the device's gate again.
        var again = await Grains.GetGrain<IDeviceGrain>(deviceId).LoginAsync(playerId);
        Assert.True(again.Accepted);
    }

    /// <summary>
    /// Two devices superseding each other at the same moment, which is the one shape that could
    /// deadlock: each login awaits the other device's release. It terminates because
    /// <c>ReleaseAsync</c> is <c>[AlwaysInterleave]</c> and never queues behind a login.
    /// Reachable only once the old sessions have lapsed - a live session would be gated first -
    /// which is exactly why the hand-driven clock lives here.
    /// </summary>
    [Fact]
    public async Task Devices_superseding_each_other_simultaneously_do_not_deadlock()
    {
        var playerA = NewId("player");
        var playerB = NewId("player");
        var deviceX = NewId("device");
        var deviceY = NewId("device");

        // Cross the wires: X holds B, Y holds A.
        await LoginAsync(deviceX, playerB);
        await LoginAsync(deviceY, playerA);

        // Both sessions lapse, so both devices are free while both players' grants still name the
        // other device - the state in which each login must release the other device.
        _fixture.Clock.Advance(Ttl + TimeSpan.FromSeconds(1));

        var swap = Task.WhenAll(
            Grains.GetGrain<IDeviceGrain>(deviceX).LoginAsync(playerA),
            Grains.GetGrain<IDeviceGrain>(deviceY).LoginAsync(playerB));

        var outcomes = await swap.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.All(outcomes, o => Assert.True(o.Accepted));
        Assert.True(await Grains.GetGrain<IPlayerGrain>(playerA).ValidateAndSlideSessionAsync(outcomes[0].Grant!.Token));
        Assert.True(await Grains.GetGrain<IPlayerGrain>(playerB).ValidateAndSlideSessionAsync(outcomes[1].Grant!.Token));
    }

    private async Task<SessionGrant> LoginAsync(string deviceId, string playerId)
    {
        var outcome = await Grains.GetGrain<IDeviceGrain>(deviceId).LoginAsync(playerId);
        Assert.True(outcome.Accepted);
        return outcome.Grant!;
    }

    private static string NewId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";
}
