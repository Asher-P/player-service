using System.Net;
using Xunit;

namespace PlayerService.E2E;

/// <summary>
/// Score updates under real concurrency, over real HTTP: nothing lost, nothing applied twice.
/// </summary>
[Collection(LiveServiceCollection.Name)]
public sealed class ScoreConcurrencyTests
{
    private readonly PlayerServiceClient _api;

    public ScoreConcurrencyTests(LiveServiceFixture fixture) => _api = fixture.Api;

    /// <summary>
    /// N distinct posts fired together. The total is the strong assertion, but the weaker-looking
    /// one below it is the sharper test: because every add is serialized on the player's single
    /// activation and each response carries the balance <i>after</i> that add, the N responses must
    /// be N <b>distinct</b> balances forming the exact ladder from seed to total. A lost update
    /// would show up as a duplicate rung, which a sum check alone can miss when two updates cancel.
    /// </summary>
    [E2EFact]
    public async Task N_parallel_score_posts_add_up_exactly()
    {
        const int posts = 50;
        const int points = 7;

        var player = await _api.NewPlayerAsync();

        var responses = await Burst.FireAsync(posts, _ =>
            _api.AddScoreAsync(player, points, PlayerServiceClient.NewId("req")));

        // A 503 here would be legitimate (transient contention, retry with the same requestId) but
        // it must never be silent: the point of the assertion is that the service does not need the
        // client's help to keep a single player's own updates from conflicting.
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.Status));

        var expectedTotal = PlayerServiceClient.SeedBalance + (posts * points);
        var expectedLadder = Enumerable.Range(1, posts)
            .Select(i => PlayerServiceClient.SeedBalance + (i * points))
            .ToHashSet();

        var observed = responses.Select(r => r.Ok().Score).ToArray();

        Assert.Equal(expectedLadder, observed.ToHashSet());
        Assert.Equal(posts, observed.Distinct().Count());
        Assert.Equal(expectedTotal, await _api.GetBalanceAsync(player));
    }

    /// <summary>
    /// The deliverable's exact wording: the same requestId 100 times in parallel, applied once,
    /// with 100 consistent responses. Consistency is asserted on the body, not just the status -
    /// a client that retried is entitled to the same answer, not merely to another success.
    /// </summary>
    [E2EFact]
    public async Task The_same_requestId_fired_100_times_in_parallel_is_applied_once()
    {
        const int duplicates = 100;
        const int points = 42;

        var player = await _api.NewPlayerAsync();
        var requestId = PlayerServiceClient.NewId("req");

        var responses = await Burst.FireAsync(duplicates, _ =>
            _api.AddScoreAsync(player, points, requestId));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.Status));

        var expected = PlayerServiceClient.SeedBalance + points;
        var bodies = responses.Select(r => r.Ok()).ToArray();

        Assert.All(bodies, body => Assert.Equal(expected, body.Score));
        Assert.All(bodies, body => Assert.Equal(player.PlayerId, body.PlayerId));

        // Applied once, checked from the outside afterwards - the 100 identical bodies above would
        // also be satisfied by a service that answered from a cache without persisting anything.
        Assert.Equal(expected, await _api.GetBalanceAsync(player));
    }

    /// <summary>
    /// The two properties together: many distinct requestIds and many duplicates of each, all in
    /// one burst. The total can only be right if de-duplication and accumulation are correct at the
    /// same time, which is the situation a retrying mobile client actually creates.
    /// </summary>
    [E2EFact]
    public async Task Duplicates_interleaved_with_distinct_posts_still_total_exactly()
    {
        const int distinct = 20;
        const int copiesEach = 5;
        const int points = 3;

        var player = await _api.NewPlayerAsync();
        var requestIds = Enumerable.Range(0, distinct)
            .Select(_ => PlayerServiceClient.NewId("req"))
            .ToArray();

        var responses = await Burst.FireAsync(distinct * copiesEach, i =>
            _api.AddScoreAsync(player, points, requestIds[i % distinct]));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.Status));

        Assert.Equal(
            PlayerServiceClient.SeedBalance + (distinct * points),
            await _api.GetBalanceAsync(player));
    }
}
