using System.Collections.Immutable;
using Orleans;
using PlayerService.Abstractions.Events;
using PlayerService.Abstractions.Grains;
using PlayerService.Abstractions.Models;
using Xunit;

namespace PlayerService.Tests;

/// <summary>
/// Phase 5: stream ingest into the single leaderboard authority, and the push cache that makes
/// <c>GET /leaderboard</c> a wait-free local read on every pod.
/// </summary>
/// <remarks>
/// The leaderboard is eventually consistent by design, so every assertion here polls up to a
/// deadline rather than sleeping a fixed amount: a fixed sleep would either be flaky or slow, and
/// polling states the real contract - "converges within the staleness bound" - instead of guessing
/// at a single instant.
/// </remarks>
[Collection(LeaderboardClusterCollection.Name)]
public sealed class LeaderboardTests
{
    /// <summary>Generous next to the 1 s broadcast interval: the bound under test is convergence,
    /// not latency, and a tight deadline would only buy flakiness on a loaded machine.</summary>
    private static readonly TimeSpan ConvergenceTimeout = TimeSpan.FromSeconds(20);

    private const int SeedBalance = 1000;

    private readonly LeaderboardClusterFixture _fixture;

    public LeaderboardTests(LeaderboardClusterFixture fixture) => _fixture = fixture;

    private IGrainFactory Grains => _fixture.Cluster.Client;

    private ILeaderboardGrain Leaderboard =>
        Grains.GetGrain<ILeaderboardGrain>(ILeaderboardGrain.SingletonKey);

    /// <summary>
    /// A burst of concurrent score adds, then the board must agree with every player's own balance.
    /// Ingest is where a projection normally loses or double-counts updates; absolute scores plus a
    /// single-activation writer are what make that impossible here.
    /// </summary>
    [Fact]
    public async Task The_board_converges_on_the_true_scores_after_a_burst()
    {
        const int players = 12;

        var ids = await Task.WhenAll(Enumerable.Range(0, players).Select(_ => OnlinePlayerAsync()));

        // Distinct increments, several per player, all in flight at once.
        await Task.WhenAll(ids.SelectMany((id, index) => Enumerable.Range(1, 3).Select(round =>
            Grains.GetGrain<IPlayerGrain>(id).AddPointsAsync((index + 1) * round, NewId("req")))));

        var expected = await CurrentBalancesAsync(ids);

        await WaitUntilAsync(async () => Matches(await Leaderboard.GetTopAsync(), expected));

        var snapshot = await Leaderboard.GetTopAsync();
        AssertRankedDescending(snapshot.Top);
        Assert.True(snapshot.ComputedAt > DateTimeOffset.MinValue);
    }

    /// <summary>
    /// A gift is the case a naive projection gets wrong: it must move <b>two</b> players at once,
    /// one down and one up. The event carries both absolute scores, so the pair lands together.
    /// </summary>
    [Fact]
    public async Task A_gift_moves_both_players_on_the_board()
    {
        var sender = await OnlinePlayerAsync();
        var recipient = await OnlinePlayerAsync();

        var outcome = await _fixture.Gifts.SendGiftAsync(sender, recipient, 400, NewId("req"));
        Assert.True(outcome.Applied);

        var expected = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [sender] = SeedBalance - 400,
            [recipient] = SeedBalance + 400,
        };

        await WaitUntilAsync(async () => Matches(await Leaderboard.GetTopAsync(), expected));

        // The receiver outranks the giver - the ordering really did follow the transfer.
        var top = (await Leaderboard.GetTopAsync()).Top;
        Assert.True(IndexOf(top, recipient) < IndexOf(top, sender));
    }

    /// <summary>
    /// The point of the push model: <b>every</b> pod ends up with the same Top-N, because one
    /// activation computes it and broadcasts it. Each pod computing its own from its own events -
    /// the single-process design - would diverge here, which is exactly why this test uses two.
    /// </summary>
    [Fact]
    public async Task Both_pods_converge_on_the_same_snapshot()
    {
        var ids = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => OnlinePlayerAsync()));

        await Task.WhenAll(ids.Select((id, index) =>
            Grains.GetGrain<IPlayerGrain>(id).AddPointsAsync(100 + (index * 25), NewId("req"))));

        var expected = await CurrentBalancesAsync(ids);

        // Two conditions, and both are load-bearing. Each pod must reach the *truth* - agreeing on
        // stale data would satisfy convergence alone - and they must land on the same broadcast,
        // since two pods one broadcast apart are legitimately different for a moment. Once writes
        // stop, the grain stops broadcasting and every pod settles on the same final snapshot.
        await WaitUntilAsync(() => Task.FromResult(
            _fixture.Pods.All(pod => Matches(pod.Current, expected)) &&
            _fixture.Pods.Select(pod => pod.Current.ComputedAt).Distinct().Count() == 1));

        var snapshots = _fixture.Pods.Select(pod => pod.Current).ToArray();
        var reference = snapshots[0];

        foreach (var snapshot in snapshots.Skip(1))
        {
            Assert.Equal(reference.ComputedAt, snapshot.ComputedAt);

            // Compared as sequences on purpose: ImmutableArray<T>.Equals is reference equality over
            // the underlying array, so comparing the structs directly would fail on two identical
            // snapshots that simply are not the same instance.
            Assert.Equal(reference.Top.ToArray(), snapshot.Top.ToArray());
        }

        AssertRankedDescending(reference.Top);
    }

    /// <summary>
    /// A pod that starts late is not left blank until the next broadcast: it primes itself with one
    /// direct pull. That single call per pod lifetime is what removes the "empty cache triggers a
    /// recomputation" path, and with it cache stampede.
    /// </summary>
    [Fact]
    public async Task A_pod_joining_late_primes_itself_from_the_grain()
    {
        var player = await OnlinePlayerAsync();
        await Grains.GetGrain<IPlayerGrain>(player).AddPointsAsync(5_000, NewId("req"));

        await WaitUntilAsync(async () => Contains(await Leaderboard.GetTopAsync(), player));

        var primed = await Leaderboard.GetTopAsync();

        Assert.NotEmpty(primed.Top);
        Assert.Contains(primed.Top, entry => entry.PlayerId == player);
    }

    private async Task<Dictionary<string, int>> CurrentBalancesAsync(IEnumerable<string> ids)
    {
        var stats = await Task.WhenAll(ids.Select(id => Grains.GetGrain<IPlayerGrain>(id).GetStatsAsync()));
        return stats.ToDictionary(s => s.PlayerId, s => s.Balance, StringComparer.Ordinal);
    }

    /// <summary>
    /// Checks the players this test owns, not the whole board: the leaderboard grain is a cluster
    /// singleton holding every player that ever scored, so asserting on its exact contents would
    /// couple each test to the others sharing the fixture.
    /// </summary>
    private static bool Matches(LeaderboardSnapshot snapshot, Dictionary<string, int> expected)
    {
        foreach (var (playerId, score) in expected)
        {
            var entry = snapshot.Top.FirstOrDefault(e => e.PlayerId == playerId);
            if (entry is null || entry.Score != score)
            {
                return false;
            }
        }

        return true;
    }

    private static bool Contains(LeaderboardSnapshot snapshot, string playerId) =>
        snapshot.Top.Any(entry => entry.PlayerId == playerId);

    private static int IndexOf(ImmutableArray<PlayerScore> top, string playerId)
    {
        for (var i = 0; i < top.Length; i++)
        {
            if (top[i].PlayerId == playerId)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Score descending, playerId ascending on ties - the ordering the whole read path
    /// depends on, asserted over the board as a whole rather than the entries one test added.</summary>
    private static void AssertRankedDescending(ImmutableArray<PlayerScore> top)
    {
        for (var i = 1; i < top.Length; i++)
        {
            var previous = top[i - 1];
            var current = top[i];

            Assert.True(
                previous.Score > current.Score ||
                (previous.Score == current.Score && string.CompareOrdinal(previous.PlayerId, current.PlayerId) < 0),
                $"out of order at {i}: {previous.PlayerId}={previous.Score} before {current.PlayerId}={current.Score}");
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + ConvergenceTimeout;

        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail($"the leaderboard did not converge within {ConvergenceTimeout}");
    }

    private async Task<string> OnlinePlayerAsync()
    {
        var playerId = NewId("player");
        var outcome = await Grains.GetGrain<IDeviceGrain>(NewId("device")).LoginAsync(playerId);
        Assert.True(outcome.Accepted);
        return playerId;
    }

    private static string NewId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";
}
