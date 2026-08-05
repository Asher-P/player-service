using Orleans;
using PlayerService.Abstractions.Grains;
using Xunit;

namespace PlayerService.Tests;

/// <summary>
/// Phase 3: atomic, idempotent score updates on a two-silo cluster. Concurrency correctness here
/// is inherited from the grain's single activation plus transactional state, not from any lock
/// this test - or the grain - writes.
/// </summary>
[Collection(ClusterCollection.Name)]
public sealed class ScoreTests
{
    private const int Racers = 50;
    private const int SeedBalance = 1000;

    private readonly ClusterFixture _fixture;

    public ScoreTests(ClusterFixture fixture) => _fixture = fixture;

    private IGrainFactory Grains => _fixture.Cluster.Client;

    /// <summary>
    /// N distinct score posts land on the same activation one turn at a time, so nothing is lost
    /// even though every add is a read-modify-write of the same balance.
    /// </summary>
    [Fact]
    public async Task N_parallel_score_posts_sum_exactly()
    {
        var playerId = NewId("player");
        var player = Grains.GetGrain<IPlayerGrain>(playerId);

        var outcomes = await Task.WhenAll(Enumerable.Range(1, Racers)
            .Select(i => player.AddPointsAsync(i, NewId("req"))));

        Assert.All(outcomes, o => Assert.False(o.Replayed));

        var expectedTotal = SeedBalance + Enumerable.Range(1, Racers).Sum();
        var stats = await player.GetStatsAsync();
        Assert.Equal(expectedTotal, stats.Balance);
    }

    /// <summary>
    /// The same requestId fired 100x at once must apply exactly once; every caller gets the
    /// identical, original outcome back - not merely "also successful".
    /// </summary>
    [Fact]
    public async Task Duplicate_requestId_fired_in_parallel_applies_once()
    {
        var playerId = NewId("player");
        var player = Grains.GetGrain<IPlayerGrain>(playerId);
        var requestId = NewId("req");
        const int points = 42;
        const int duplicates = 100;

        var outcomes = await Task.WhenAll(Enumerable.Range(0, duplicates)
            .Select(_ => player.AddPointsAsync(points, requestId)));

        Assert.Single(outcomes, o => !o.Replayed);
        Assert.Equal(duplicates - 1, outcomes.Count(o => o.Replayed));
        Assert.All(outcomes, o => Assert.Equal(SeedBalance + points, o.Stats.Balance));

        var stats = await player.GetStatsAsync();
        Assert.Equal(SeedBalance + points, stats.Balance);
    }

    private static string NewId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";
}
