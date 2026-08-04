using Orleans;
using PlayerService.Abstractions.Errors;
using PlayerService.Abstractions.Grains;
using PlayerService.Abstractions.Models;
using Xunit;

namespace PlayerService.Tests;

/// <summary>
/// Phase 2: the login gate and the supersede policy, exercised across a two-silo cluster so that
/// what is being tested is Orleans' single-activation guarantee rather than a process-local
/// dictionary that would pass just as happily on one silo.
/// </summary>
[Collection(ClusterCollection.Name)]
public sealed class SessionTests
{
    private const int Racers = 8;

    private readonly ClusterFixture _fixture;

    public SessionTests(ClusterFixture fixture) => _fixture = fixture;

    private IGrainFactory Grains => _fixture.Cluster.Client;

    /// <summary>
    /// The assignment's duplicate-login scenario: a chatty client fires the same login several
    /// times at once. Exactly one may win. No lock is involved - the device grain's single
    /// activation serializes the attempts, and the loser sees the winner's session already there.
    /// </summary>
    [Fact]
    public async Task Concurrent_logins_on_one_device_grant_exactly_one_session()
    {
        var playerId = NewId("player");
        var deviceId = NewId("device");
        var device = Grains.GetGrain<IDeviceGrain>(deviceId);

        var outcomes = await Task.WhenAll(
            Enumerable.Range(0, Racers).Select(_ => device.LoginAsync(playerId)));

        var granted = outcomes.Where(o => o.Accepted).ToArray();
        var rejected = outcomes.Where(o => !o.Accepted).ToArray();

        Assert.Single(granted);
        Assert.Equal(Racers - 1, rejected.Length);
        Assert.All(rejected, o => Assert.Equal(LoginRejection.DeviceSessionActive, o.Rejection));
        Assert.All(rejected, o => Assert.Null(o.Grant));

        // The one winning token is the live one.
        Assert.True(await Grains
            .GetGrain<IPlayerGrain>(playerId)
            .ValidateAndSlideSessionAsync(granted[0].Grant!.Token));
    }

    /// <summary>
    /// Supersede policy: a second device wins the player, and the first device's token stops
    /// validating immediately. This is what keeps "one online session per player" true, which is
    /// what lets the gift path treat online-ness as a single unambiguous read.
    /// </summary>
    [Fact]
    public async Task A_second_device_supersedes_the_first_players_token()
    {
        var playerId = NewId("player");
        var player = Grains.GetGrain<IPlayerGrain>(playerId);

        var first = await LoginAsync(NewId("device"), playerId);
        var second = await LoginAsync(NewId("device"), playerId);

        Assert.NotEqual(first.Token, second.Token);
        Assert.False(await player.ValidateAndSlideSessionAsync(first.Token));
        Assert.True(await player.ValidateAndSlideSessionAsync(second.Token));
        Assert.True(await player.IsOnlineAsync());
    }

    /// <summary>
    /// The other half of supersede: the displaced device is told to release, so it can log in
    /// again at once. Without that notification it would keep rejecting its own user's logins
    /// until the TTL ran out, for a session that is already dead.
    /// </summary>
    [Fact]
    public async Task A_superseded_device_can_log_in_again_immediately()
    {
        var playerId = NewId("player");
        var deviceId = NewId("device");

        var onFirstDevice = await LoginAsync(deviceId, playerId);
        await LoginAsync(NewId("device"), playerId);   // supersedes, and releases deviceId

        var outcome = await Grains.GetGrain<IDeviceGrain>(deviceId).LoginAsync(playerId);

        Assert.True(outcome.Accepted);
        Assert.NotEqual(onFirstDevice.Token, outcome.Grant!.Token);
    }

    /// <summary>A device that never released still rejects, so the release above proves something.</summary>
    [Fact]
    public async Task A_device_holding_a_live_session_rejects_a_different_player()
    {
        var deviceId = NewId("device");
        await LoginAsync(deviceId, NewId("player"));

        var outcome = await Grains.GetGrain<IDeviceGrain>(deviceId).LoginAsync(NewId("player"));

        Assert.False(outcome.Accepted);
        Assert.Equal(LoginRejection.DeviceSessionActive, outcome.Rejection);
    }

    private async Task<SessionGrant> LoginAsync(string deviceId, string playerId)
    {
        var outcome = await Grains.GetGrain<IDeviceGrain>(deviceId).LoginAsync(playerId);
        Assert.True(outcome.Accepted);
        return outcome.Grant!;
    }

    private static string NewId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";
}
