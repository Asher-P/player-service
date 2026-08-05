using Orleans;
using PlayerService.Abstractions.Errors;
using PlayerService.Abstractions.Grains;
using PlayerService.Abstractions.Models;
using Xunit;

namespace PlayerService.Tests;

/// <summary>
/// Phase 4: gifting as a distributed transaction, on a two-silo cluster so sender and recipient can
/// genuinely live on different silos - the situation in which the old double-lock design could not
/// work at all.
/// </summary>
[Collection(ClusterCollection.Name)]
public sealed class GiftTests
{
    private const int SeedBalance = 1000;

    private readonly ClusterFixture _fixture;

    public GiftTests(ClusterFixture fixture) => _fixture = fixture;

    private IGrainFactory Grains => _fixture.Cluster.Client;

    [Fact]
    public async Task A_gift_moves_points_and_counts_on_both_sides()
    {
        var sender = await OnlinePlayerAsync();
        var recipient = await OnlinePlayerAsync();

        var outcome = await _fixture.Gifts.SendGiftAsync(sender, recipient, 250, NewId("req"));

        Assert.True(outcome.Applied);
        Assert.Equal(SeedBalance - 250, outcome.SenderBalance);
        Assert.Equal(SeedBalance + 250, outcome.RecipientBalance);

        var senderStats = await Grains.GetGrain<IPlayerGrain>(sender).GetStatsAsync();
        var recipientStats = await Grains.GetGrain<IPlayerGrain>(recipient).GetStatsAsync();

        Assert.Equal(SeedBalance - 250, senderStats.Balance);
        Assert.Equal(1, senderStats.GiftsSent);
        Assert.Equal(SeedBalance + 250, recipientStats.Balance);
        Assert.Equal(1, recipientStats.GiftsReceived);
    }

    /// <summary>
    /// The assignment's conservation requirement. Every gift is a debit+credit inside one
    /// transaction, so there is no code path that commits half of one - the sum cannot drift.
    /// </summary>
    [Fact]
    public async Task Many_concurrent_gifts_across_random_pairs_conserve_points()
    {
        const int players = 8;
        const int gifts = 60;

        var ids = await Task.WhenAll(Enumerable.Range(0, players).Select(_ => OnlinePlayerAsync()));
        var total = players * SeedBalance;
        var random = new Random(20260805);

        var pairs = Enumerable.Range(0, gifts)
            .Select(_ =>
            {
                var from = random.Next(players);
                var to = (from + 1 + random.Next(players - 1)) % players;
                return (From: ids[from], To: ids[to], Points: random.Next(1, 40));
            })
            .ToArray();

        // Awaited together, so retries of aborted transactions have all drained before we measure.
        var outcomes = await Task.WhenAll(pairs.Select(p =>
            _fixture.Gifts.SendGiftAsync(p.From, p.To, p.Points, NewId("req"))));

        Assert.All(outcomes, o => Assert.True(o.Applied));

        var balances = await Task.WhenAll(ids.Select(async id =>
            (await Grains.GetGrain<IPlayerGrain>(id).GetStatsAsync()).Balance));

        Assert.Equal(total, balances.Sum());
        Assert.All(balances, b => Assert.True(b >= 0, $"balance went negative: {b}"));
    }

    /// <summary>
    /// The classic deadlock shape: <c>p1 → p2</c> and <c>p2 → p1</c> in a tight loop from both
    /// sides at once, so every round has two transactions wanting the same two players' locks in
    /// opposite order. It terminates because no grain ever calls the other - the API layer opens the
    /// transaction, so the call graph is a tree and contention can only abort, never block.
    /// </summary>
    /// <remarks>
    /// Two loops rather than one 2N-wide burst, deliberately: the property under test is that the
    /// opposing pair always makes progress, not how many simultaneous transactions one pair of keys
    /// can absorb. A real deadlock would hang on the very first round either way.
    /// </remarks>
    [Fact]
    public async Task Gifts_in_both_directions_between_one_pair_do_not_deadlock()
    {
        const int rounds = 15;

        var p1 = await OnlinePlayerAsync();
        var p2 = await OnlinePlayerAsync();

        async Task LoopAsync(string from, string to)
        {
            for (var i = 0; i < rounds; i++)
            {
                var outcome = await _fixture.Gifts.SendGiftAsync(from, to, 5, NewId("req"));
                Assert.True(outcome.Applied);
            }
        }

        await Task.WhenAll(LoopAsync(p1, p2), LoopAsync(p2, p1)).WaitAsync(TimeSpan.FromSeconds(60));

        var b1 = (await Grains.GetGrain<IPlayerGrain>(p1).GetStatsAsync()).Balance;
        var b2 = (await Grains.GetGrain<IPlayerGrain>(p2).GetStatsAsync()).Balance;
        Assert.Equal(SeedBalance * 2, b1 + b2);
    }

    /// <summary>
    /// A chatty client's duplicate: the same requestId applied once, and the duplicate returns the
    /// original outcome rather than a second transfer.
    /// </summary>
    [Fact]
    public async Task A_replayed_requestId_returns_the_original_outcome_and_moves_nothing()
    {
        var sender = await OnlinePlayerAsync();
        var recipient = await OnlinePlayerAsync();
        var requestId = NewId("req");

        var first = await _fixture.Gifts.SendGiftAsync(sender, recipient, 100, requestId);
        var replay = await _fixture.Gifts.SendGiftAsync(sender, recipient, 100, requestId);

        Assert.True(first.Applied);
        Assert.False(first.Replayed);
        Assert.True(replay.Applied);
        Assert.True(replay.Replayed);
        Assert.Equal(first.SenderBalance, replay.SenderBalance);
        Assert.Equal(first.RecipientBalance, replay.RecipientBalance);

        var senderStats = await Grains.GetGrain<IPlayerGrain>(sender).GetStatsAsync();
        Assert.Equal(SeedBalance - 100, senderStats.Balance);
        Assert.Equal(1, senderStats.GiftsSent);
    }

    /// <summary>
    /// The deliverable's exact scenario: the recipient goes offline <i>after</i> a successful gift,
    /// then the duplicate arrives. The ledger check fires before the recipient is ever consulted,
    /// so the replay is stable even though a fresh gift would now be rejected.
    /// </summary>
    [Fact]
    public async Task A_gift_replayed_after_the_recipient_went_offline_still_returns_the_original()
    {
        var sender = await OnlinePlayerAsync();
        var (recipient, recipientDevice) = await OnlinePlayerWithDeviceAsync();
        var requestId = NewId("req");

        var first = await _fixture.Gifts.SendGiftAsync(sender, recipient, 75, requestId);
        Assert.True(first.Applied);

        // Supersede the recipient's session onto a device that then releases it - the session is
        // superseded, so the original grant no longer validates.
        await TakeOfflineAsync(recipient, recipientDevice);

        var replay = await _fixture.Gifts.SendGiftAsync(sender, recipient, 75, requestId);

        Assert.True(replay.Applied);
        Assert.True(replay.Replayed);
        Assert.Equal(first.SenderBalance, replay.SenderBalance);
        Assert.Equal(first.RecipientBalance, replay.RecipientBalance);

        // And a genuinely new attempt is now rejected, proving the recipient really is offline.
        var fresh = await _fixture.Gifts.SendGiftAsync(sender, recipient, 75, NewId("req"));
        Assert.False(fresh.Applied);
        Assert.Equal(GiftRejection.Offline, fresh.Rejection);
    }

    /// <summary>Gift to an offline player: rejected, and - the part that matters - nothing moved.</summary>
    [Fact]
    public async Task A_gift_to_an_offline_player_is_rejected_and_moves_no_points()
    {
        var sender = await OnlinePlayerAsync();
        var (recipient, device) = await OnlinePlayerWithDeviceAsync();
        await TakeOfflineAsync(recipient, device);

        var outcome = await _fixture.Gifts.SendGiftAsync(sender, recipient, 100, NewId("req"));

        Assert.False(outcome.Applied);
        Assert.Equal(GiftRejection.Offline, outcome.Rejection);
        Assert.Equal(SeedBalance, (await Grains.GetGrain<IPlayerGrain>(sender).GetStatsAsync()).Balance);
        Assert.Equal(SeedBalance, (await Grains.GetGrain<IPlayerGrain>(recipient).GetStatsAsync()).Balance);
    }

    /// <summary>A rejection is replay-stable too, which is why it is recorded outside the aborted
    /// transaction rather than inside it.</summary>
    [Fact]
    public async Task A_replayed_rejected_requestId_returns_the_original_rejection()
    {
        var sender = await OnlinePlayerAsync();
        var (recipient, device) = await OnlinePlayerWithDeviceAsync();
        await TakeOfflineAsync(recipient, device);
        var requestId = NewId("req");

        var first = await _fixture.Gifts.SendGiftAsync(sender, recipient, 100, requestId);
        Assert.Equal(GiftRejection.Offline, first.Rejection);

        // The recipient comes back, but the recorded requestId is terminal: a client that wants a
        // genuinely new attempt must use a new requestId.
        await Grains.GetGrain<IDeviceGrain>(NewId("device")).LoginAsync(recipient);

        var replay = await _fixture.Gifts.SendGiftAsync(sender, recipient, 100, requestId);

        Assert.False(replay.Applied);
        Assert.Equal(GiftRejection.Offline, replay.Rejection);
        Assert.True(replay.Replayed);
        Assert.Equal(SeedBalance, (await Grains.GetGrain<IPlayerGrain>(sender).GetStatsAsync()).Balance);
    }

    [Fact]
    public async Task A_gift_beyond_the_balance_is_rejected_and_never_goes_negative()
    {
        var sender = await OnlinePlayerAsync();
        var recipient = await OnlinePlayerAsync();

        var outcome = await _fixture.Gifts.SendGiftAsync(sender, recipient, SeedBalance + 1, NewId("req"));

        Assert.False(outcome.Applied);
        Assert.Equal(GiftRejection.InsufficientFunds, outcome.Rejection);
        Assert.Equal(SeedBalance, (await Grains.GetGrain<IPlayerGrain>(sender).GetStatsAsync()).Balance);
    }

    /// <summary>
    /// Two simultaneous gifts that each fit the balance but together do not. Serializable isolation
    /// means the second sees the first's committed effect or aborts and retries into it - so exactly
    /// one can win, without any check-then-act window.
    /// </summary>
    [Fact]
    public async Task Two_simultaneous_gifts_that_together_overdraw_let_only_one_through()
    {
        var sender = await OnlinePlayerAsync();
        var first = await OnlinePlayerAsync();
        var second = await OnlinePlayerAsync();

        var outcomes = await Task.WhenAll(
            _fixture.Gifts.SendGiftAsync(sender, first, 700, NewId("req")),
            _fixture.Gifts.SendGiftAsync(sender, second, 700, NewId("req")));

        Assert.Single(outcomes, o => o.Applied);
        Assert.Single(outcomes, o => o.Rejection == GiftRejection.InsufficientFunds);
        Assert.Equal(SeedBalance - 700, (await Grains.GetGrain<IPlayerGrain>(sender).GetStatsAsync()).Balance);
    }

    [Fact]
    public async Task A_self_gift_is_rejected_without_opening_a_transaction()
    {
        var player = await OnlinePlayerAsync();

        var outcome = await _fixture.Gifts.SendGiftAsync(player, player, 10, NewId("req"));

        Assert.False(outcome.Applied);
        Assert.Equal(GiftRejection.SelfGift, outcome.Rejection);
        Assert.Equal(SeedBalance, (await Grains.GetGrain<IPlayerGrain>(player).GetStatsAsync()).Balance);
    }

    /// <summary>A player who has never logged in is not merely offline - it is unobservable as a
    /// player at all, which is the one case that maps to 404 rather than 409.</summary>
    [Fact]
    public async Task A_gift_to_a_player_who_never_logged_in_is_rejected_as_unknown()
    {
        var sender = await OnlinePlayerAsync();

        var outcome = await _fixture.Gifts.SendGiftAsync(sender, NewId("player"), 10, NewId("req"));

        Assert.False(outcome.Applied);
        Assert.Equal(GiftRejection.UnknownRecipient, outcome.Rejection);
        Assert.Equal(SeedBalance, (await Grains.GetGrain<IPlayerGrain>(sender).GetStatsAsync()).Balance);
    }

    private async Task<string> OnlinePlayerAsync() => (await OnlinePlayerWithDeviceAsync()).PlayerId;

    private async Task<(string PlayerId, string DeviceId)> OnlinePlayerWithDeviceAsync()
    {
        var playerId = NewId("player");
        var deviceId = NewId("device");

        var outcome = await Grains.GetGrain<IDeviceGrain>(deviceId).LoginAsync(playerId);
        Assert.True(outcome.Accepted);

        return (playerId, deviceId);
    }

    /// <summary>
    /// Ends the player's session without waiting out the TTL: binding a grant that is already
    /// expired supersedes the live one, so <c>IsOnline</c> is false from the next read onwards.
    /// </summary>
    private async Task TakeOfflineAsync(string playerId, string deviceId)
    {
        var expired = new SessionGrant(
            playerId, deviceId, $"{playerId}.expired", DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1));

        await Grains.GetGrain<IPlayerGrain>(playerId).BindSessionAsync(expired);
        Assert.False(await Grains.GetGrain<IPlayerGrain>(playerId).IsOnlineAsync());
    }

    private static string NewId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";
}
