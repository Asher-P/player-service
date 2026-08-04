using Orleans;
using PlayerService.Abstractions.Grains;
using PlayerService.Abstractions.Sessions;
using Xunit;

namespace PlayerService.Tests;

/// <summary>
/// Phase 1 proves the topology is real and wired, not that the business logic works - the logic
/// lands in phases 2 to 5. Each test here fails loudly if a piece of Orleans configuration is
/// missing, which is exactly the failure mode a skeleton exists to rule out.
/// </summary>
[Collection(ClusterCollection.Name)]
public sealed class TopologyTests
{
    private readonly ClusterFixture _fixture;

    public TopologyTests(ClusterFixture fixture) => _fixture = fixture;

    private IGrainFactory Grains => _fixture.Cluster.Client;

    [Fact]
    public void Cluster_runs_two_silos()
    {
        Assert.Equal(2, _fixture.Cluster.Silos.Count);
    }

    /// <summary>
    /// Resolves a player grain from the client and reads its transactional state. Passing means
    /// the transaction manager, the "TransactionStore" provider and serialization are all wired:
    /// an unconfigured provider or a missing [Reentrant] would throw here rather than return 1000.
    /// </summary>
    [Fact]
    public async Task Player_grain_activates_with_the_seeded_balance()
    {
        var playerId = $"player-{Guid.NewGuid():N}";

        var stats = await Grains.GetGrain<IPlayerGrain>(playerId).GetStatsAsync();

        Assert.Equal(playerId, stats.PlayerId);
        Assert.Equal(1000, stats.Balance);
        Assert.Equal(0, stats.GiftsSent);
        Assert.Equal(0, stats.GiftsReceived);
    }

    /// <summary>
    /// Activating the leaderboard grain runs its OnActivateAsync, which subscribes to the score
    /// stream - so this call transitively proves the memory stream provider and the PubSubStore
    /// are configured. Phase 1 has no producers, so the snapshot is legitimately empty.
    /// </summary>
    [Fact]
    public async Task Leaderboard_grain_activates_and_subscribes_to_its_stream()
    {
        var snapshot = await Grains
            .GetGrain<ILeaderboardGrain>(ILeaderboardGrain.SingletonKey)
            .GetTopAsync();

        Assert.Empty(snapshot.Top);
    }

    /// <summary>
    /// Login goes device grain -> player grain across the cluster, and the minted token must then
    /// validate on the player's own activation. This is the seam phase 2 fills in; what it proves
    /// today is that the tree-shaped call path and the token format agree end to end.
    /// </summary>
    [Fact]
    public async Task Login_binds_a_session_that_the_player_grain_validates()
    {
        var playerId = $"player-{Guid.NewGuid():N}";
        var deviceId = $"device-{Guid.NewGuid():N}";

        var outcome = await Grains.GetGrain<IDeviceGrain>(deviceId).LoginAsync(playerId);

        Assert.True(outcome.Accepted);
        Assert.NotNull(outcome.Grant);
        Assert.True(SessionToken.TryGetPlayerId(outcome.Grant!.Token, out var parsed));
        Assert.Equal(playerId, parsed);

        var player = Grains.GetGrain<IPlayerGrain>(playerId);

        Assert.True(await player.ValidateAndSlideSessionAsync(outcome.Grant.Token));
        Assert.False(await player.ValidateAndSlideSessionAsync($"{playerId}.not-the-real-token"));
        Assert.True(await player.IsOnlineAsync());
    }
}
