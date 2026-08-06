using System.Net;
using Xunit;

namespace PlayerService.E2E;

/// <summary>
/// The leaderboard after a burst of concurrent scores and gifts. It is a push projection fed by a
/// stream, so it is eventually consistent by design - the assertion is that it converges on the
/// truth, and the truth is whatever <c>/players/{id}/stats</c> says.
/// </summary>
[Collection(LiveServiceCollection.Name)]
public sealed class LeaderboardTests
{
    /// <summary>How long the projection gets to catch up. Staleness is bounded by stream delivery
    /// plus the broadcast interval (1 s shipped); this is that bound with room to spare.</summary>
    private static readonly TimeSpan ConvergenceBudget = TimeSpan.FromSeconds(30);

    private readonly PlayerServiceClient _api;

    public LeaderboardTests(LiveServiceFixture fixture) => _api = fixture.Api;

    [E2EFact]
    public async Task The_leaderboard_is_correct_after_a_burst_of_concurrent_scores_and_gifts()
    {
        const int players = 5;
        const int giftPoints = 100;

        var sessions = await Task.WhenAll(
            Enumerable.Range(0, players).Select(_ => _api.NewPlayerAsync()));

        // Scores well clear of the seeded 1000 so these players sit inside Top-N even when the
        // service has been serving other traffic - the projection holds everyone, but only Top-N
        // is broadcast, and a test that silently depended on an empty service would be a trap.
        var scorePlan = Enumerable.Range(0, players)
            .Select(i => (Player: sessions[i], Points: (players - i) * 1_000))
            .ToArray();

        var before = (await _api.GetLeaderboardAsync(sessions[0])).Ok("reading the pre-burst snapshot");

        // Scores and gifts in the same burst: gifts move two balances at once through a different
        // code path than score posts, and the projection has to absorb both without ordering help.
        //
        // Both calls go through the retry helper because this burst deliberately puts a score
        // transaction and a gift transaction on the same player at the same time - the one case the
        // service answers with 503 and the documented client behaviour is "retry with the same
        // requestId". Doing what the contract says is not weakening the test: the retry is
        // idempotent, so a lost or duplicated update would still show up in the totals below.
        var scorePosts = scorePlan.Select(async plan =>
        {
            // Minted once, outside the retry, or the retry would not be a retry.
            var requestId = PlayerServiceClient.NewId("req");

            return Summarize(await _api.RetryingOn503Async(
                () => _api.AddScoreAsync(plan.Player, plan.Points, requestId)));
        });

        var gifts = Enumerable.Range(0, players).Select(async i =>
        {
            var requestId = PlayerServiceClient.NewId("req");

            return Summarize(await _api.RetryingOn503Async(() => _api.SendGiftAsync(
                sessions[i], sessions[(i + 1) % players].PlayerId, giftPoints, requestId)));
        });

        var responses = await Task.WhenAll(scorePosts.Concat(gifts));
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.Status));

        // The truth, read per player through the API that owns it.
        var expected = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var session in sessions)
        {
            expected[session.PlayerId] = await _api.GetBalanceAsync(session);
        }

        // A ring of equal gifts is a closed loop, so every player ends on exactly their score total.
        Assert.All(scorePlan, plan => Assert.Equal(
            PlayerServiceClient.SeedBalance + plan.Points, expected[plan.Player.PlayerId]));

        LeaderboardBody? converged = null;

        await Poll.UntilAsync(
            within: ConvergenceBudget,
            interval: TimeSpan.FromMilliseconds(250),
            condition: async () =>
            {
                converged = (await _api.GetLeaderboardAsync(sessions[0])).Ok("reading the leaderboard");

                var mine = converged.Top
                    .Where(row => expected.ContainsKey(row.PlayerId))
                    .ToArray();

                return mine.Length == players
                    && mine.All(row => row.Score == expected[row.PlayerId]);
            },
            onTimeout: () => Describe(converged, expected));

        var board = converged!;

        // Ranks are dense, 1-based and ordered - a client renders them directly, so a gap or an
        // out-of-order row is a bug even when every score is individually right.
        for (var i = 0; i < board.Top.Count; i++)
        {
            Assert.Equal(i + 1, board.Top[i].Rank);

            if (i > 0)
            {
                Assert.True(
                    board.Top[i - 1].Score >= board.Top[i].Score,
                    $"rank {i} scored {board.Top[i - 1].Score} but rank {i + 1} scored {board.Top[i].Score}");
            }
        }

        Assert.Equal(board.Top.Count, board.Top.Select(row => row.PlayerId).Distinct().Count());

        // Our five in the order their balances imply, established against the full board rather
        // than against each other, so a mis-sorted neighbour cannot hide between them.
        var ours = board.Top.Where(row => expected.ContainsKey(row.PlayerId)).ToArray();
        Assert.Equal(
            expected.OrderByDescending(entry => entry.Value).Select(entry => entry.Key).ToArray(),
            ours.Select(row => row.PlayerId).ToArray());

        // Compared against the service's own earlier timestamp, never against the test host's
        // clock: the snapshot is provably newer than the burst without assuming synced clocks.
        Assert.True(
            board.ComputedAt > before.ComputedAt,
            $"the snapshot was not recomputed after the burst (still {board.ComputedAt:O})");
    }

    /// <summary>Flattens two differently-typed results into the only part this test asserts on.</summary>
    private static (HttpStatusCode Status, string Raw) Summarize<T>(ApiResult<T> result) =>
        (result.Status, result.Raw);

    private static string Describe(LeaderboardBody? board, IReadOnlyDictionary<string, int> expected)
    {
        if (board is null)
        {
            return "the leaderboard was never read successfully.";
        }

        var found = board.Top
            .Where(row => expected.ContainsKey(row.PlayerId))
            .Select(row => $"{row.PlayerId}={row.Score} (expected {expected[row.PlayerId]})");

        return $"top {board.Top.Count} rows, computed at {board.ComputedAt:O}; "
             + $"ours: [{string.Join(", ", found)}] of {expected.Count} expected.";
    }
}
