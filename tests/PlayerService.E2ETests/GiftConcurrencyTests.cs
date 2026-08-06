using System.Net;
using Xunit;
using Xunit.Sdk;

namespace PlayerService.E2E;

/// <summary>
/// Gifting under real concurrency: points are conserved, balances never go negative, and a pair
/// gifting each other from both sides at once keeps making progress.
/// </summary>
[Collection(LiveServiceCollection.Name)]
public sealed class GiftConcurrencyTests
{
    private readonly PlayerServiceClient _api;

    public GiftConcurrencyTests(LiveServiceFixture fixture) => _api = fixture.Api;

    /// <summary>
    /// The assignment's conservation requirement, asserted where the assignment states it: across
    /// the API, on random pairs, all in flight together. Every gift is a debit and a credit inside
    /// one transaction, so there is no path that commits half of one - the sum cannot drift.
    /// </summary>
    [E2EFact]
    public async Task Many_parallel_gifts_across_random_pairs_conserve_points_and_never_go_negative()
    {
        const int players = 8;
        const int gifts = 60;

        var sessions = await Task.WhenAll(
            Enumerable.Range(0, players).Select(_ => _api.NewPlayerAsync()));

        // Seeded, so a failure is reproducible rather than a story about "some pairing".
        var random = new Random(20260806);
        var plan = Enumerable.Range(0, gifts).Select(_ =>
        {
            var from = random.Next(players);
            var to = (from + 1 + random.Next(players - 1)) % players;
            return (From: from, To: to, Points: random.Next(1, 30));
        }).ToArray();

        var responses = await Burst.FireAsync(gifts, i => _api.SendGiftAsync(
            sessions[plan[i].From],
            sessions[plan[i].To].PlayerId,
            plan[i].Points,
            PlayerServiceClient.NewId("req")));

        // 503 is a legitimate answer under contention, but it must not be silent: the retry budget
        // is sized to absorb a burst this size, and quietly tolerating exhaustion here would let
        // that stop being true without anything noticing.
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.Status));
        Assert.All(responses, r => Assert.True(r.Ok().Applied, "a 200 must mean the gift applied"));

        var balances = await Task.WhenAll(sessions.Select(_api.GetBalanceAsync));

        Assert.Equal(players * PlayerServiceClient.SeedBalance, balances.Sum());
        Assert.All(balances, balance => Assert.True(balance >= 0, $"balance went negative: {balance}"));

        // Gift counters are part of the same transaction, so they must agree with the plan exactly.
        var stats = await Task.WhenAll(sessions.Select(async s => (await _api.GetStatsAsync(s)).Ok()));
        Assert.Equal(gifts, stats.Sum(s => s.GiftsSent));
        Assert.Equal(gifts, stats.Sum(s => s.GiftsReceived));
    }

    /// <summary>
    /// The classic deadlock shape: <c>p1 → p2</c> and <c>p2 → p1</c> in a tight loop from both
    /// sides at once, so every round has two transactions wanting the same two players in opposite
    /// order. It must terminate, and every round must apply - a service that "avoided" the deadlock
    /// by rejecting one direction would satisfy a liveness check but not this one.
    /// </summary>
    [E2EFact]
    public async Task p1_and_p2_gifting_each_other_in_a_tight_loop_do_not_deadlock()
    {
        const int rounds = 20;
        const int points = 5;
        var budget = TimeSpan.FromMinutes(2);

        var p1 = await _api.NewPlayerAsync();
        var p2 = await _api.NewPlayerAsync();

        async Task LoopAsync(PlayerSession from, PlayerSession to)
        {
            for (var round = 0; round < rounds; round++)
            {
                var response = await _api.SendGiftAsync(
                    from, to.PlayerId, points, PlayerServiceClient.NewId("req"));

                Assert.True(
                    response.Ok($"{from.PlayerId} -> {to.PlayerId}, round {round}").Applied,
                    "every round must actually move points");
            }
        }

        var loops = Task.WhenAll(LoopAsync(p1, p2), LoopAsync(p2, p1));

        try
        {
            await loops.WaitAsync(budget);
        }
        catch (TimeoutException)
        {
            throw new XunitException(
                $"{rounds} rounds in each direction did not finish within {budget} - the pair is deadlocked "
                + "or contention is not resolving.");
        }

        // Symmetric traffic, so the pair ends where it started - and the sum is conserved either way.
        var b1 = await _api.GetBalanceAsync(p1);
        var b2 = await _api.GetBalanceAsync(p2);

        Assert.Equal(2 * PlayerServiceClient.SeedBalance, b1 + b2);
        Assert.Equal(PlayerServiceClient.SeedBalance, b1);
        Assert.Equal(PlayerServiceClient.SeedBalance, b2);
    }

    /// <summary>
    /// Two gifts that each fit the balance but together do not, fired simultaneously. Exactly one
    /// may apply; the other must be told <c>409</c> rather than being allowed to overdraw. This is
    /// the negative-balance guarantee at its only interesting moment.
    /// </summary>
    [E2EFact]
    public async Task Two_simultaneous_gifts_that_together_overdraw_let_exactly_one_through()
    {
        const int points = 700;

        var sender = await _api.NewPlayerAsync();
        var first = await _api.NewPlayerAsync();
        var second = await _api.NewPlayerAsync();
        var recipients = new[] { first, second };

        var responses = await Burst.FireAsync(2, i => _api.SendGiftAsync(
            sender, recipients[i].PlayerId, points, PlayerServiceClient.NewId("req")));

        Assert.Single(responses, r => r.Status == HttpStatusCode.OK);

        var rejection = Assert.Single(responses, r => r.Status == HttpStatusCode.Conflict);
        Assert.Equal("Insufficient funds", rejection.Problem?.Title);

        Assert.Equal(PlayerServiceClient.SeedBalance - points, await _api.GetBalanceAsync(sender));
        Assert.Equal(
            (2 * PlayerServiceClient.SeedBalance) + points,
            await _api.GetBalanceAsync(first) + await _api.GetBalanceAsync(second));
    }

    /// <summary>
    /// The same gift requestId fired in parallel: one transfer, and every caller gets the same
    /// numbers back. The offline variant of this is in <see cref="OfflineGiftTests"/>; this one
    /// establishes the baseline while both players are live.
    /// </summary>
    [E2EFact]
    public async Task A_duplicate_gift_requestId_fired_in_parallel_transfers_once()
    {
        const int duplicates = 20;
        const int points = 120;

        var sender = await _api.NewPlayerAsync();
        var recipient = await _api.NewPlayerAsync();
        var requestId = PlayerServiceClient.NewId("req");

        var responses = await Burst.FireAsync(duplicates, _ =>
            _api.SendGiftAsync(sender, recipient.PlayerId, points, requestId));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.Status));

        var bodies = responses.Select(r => r.Ok()).ToArray();
        Assert.All(bodies, body => Assert.True(body.Applied));
        Assert.All(bodies, body => Assert.Equal(PlayerServiceClient.SeedBalance - points, body.SenderBalance));
        Assert.All(bodies, body => Assert.Equal(PlayerServiceClient.SeedBalance + points, body.RecipientBalance));

        // Exactly one of the racers did the work; the rest are told they are replays.
        Assert.Single(bodies, body => !body.Replayed);

        Assert.Equal(PlayerServiceClient.SeedBalance - points, await _api.GetBalanceAsync(sender));
        Assert.Equal(PlayerServiceClient.SeedBalance + points, await _api.GetBalanceAsync(recipient));
    }
}
