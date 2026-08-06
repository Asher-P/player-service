using System.Net;
using Xunit;

namespace PlayerService.E2E;

/// <summary>
/// The two scenarios that need a genuinely offline recipient. Both run against sessions that were
/// allowed to lapse for real - see <see cref="OfflineRecipientFixture"/> for why that costs a TTL.
/// </summary>
[Collection(OfflineRecipientCollection.Name)]
public sealed class OfflineGiftTests
{
    private readonly OfflineRecipientFixture _scenario;

    public OfflineGiftTests(OfflineRecipientFixture scenario) => _scenario = scenario;

    private PlayerServiceClient Api => _scenario.Api;

    /// <summary>
    /// A replayed gift requestId after the recipient has gone offline: the original result, and no
    /// second transfer. The recipient's balance has moved on since (the fixture's probes credited
    /// it), so a service that answered from current state instead of from the recorded outcome
    /// would return different numbers here - which is exactly what the assertion catches.
    /// </summary>
    [E2EFact]
    public async Task A_replayed_gift_after_the_recipient_went_offline_returns_the_original_result()
    {
        var original = _scenario.OriginalGift;

        var replay = (await Api.SendGiftAsync(
                _scenario.Sender,
                _scenario.GiftedRecipientId,
                OfflineRecipientFixture.GiftPoints,
                _scenario.GiftRequestId))
            .Ok("replaying the original requestId");

        Assert.True(replay.Applied);
        Assert.True(replay.Replayed);
        Assert.Equal(original.SenderBalance, replay.SenderBalance);
        Assert.Equal(original.RecipientBalance, replay.RecipientBalance);

        // The recipient really is offline: a genuinely new attempt is refused right now. Without
        // this, the replay above would also pass against a service where nothing had expired.
        var fresh = await Api.SendGiftAsync(
            _scenario.Sender,
            _scenario.GiftedRecipientId,
            OfflineRecipientFixture.GiftPoints,
            PlayerServiceClient.NewId("req"));

        Assert.Equal("Recipient offline", fresh.Failed(HttpStatusCode.Conflict).Title);

        // No second transfer, checked on both sides. The recipient has to log back in to be
        // readable at all - a token only opens its own player's stats - which does not change the
        // balance the replay was supposed not to touch.
        Assert.Equal(
            PlayerServiceClient.SeedBalance - OfflineRecipientFixture.GiftPoints,
            await Api.GetBalanceAsync(_scenario.Sender));

        var recipient = await Api.LoginFromNewDeviceAsync(_scenario.GiftedRecipientId);

        Assert.Equal(
            PlayerServiceClient.SeedBalance
                + OfflineRecipientFixture.GiftPoints
                + _scenario.ProbePointsToGifted,
            await Api.GetBalanceAsync(recipient));

        // One gift sent, one received: the replay was an answer, not an event.
        Assert.Equal(1, (await Api.GetStatsAsync(_scenario.Sender)).Ok().GiftsSent);
    }

    /// <summary>
    /// A gift to an offline player: rejected with the 409 that says "retry once that changes", and
    /// - the part that actually matters - no points moved on either side.
    /// </summary>
    [E2EFact]
    public async Task A_gift_to_an_offline_player_is_rejected_and_moves_no_points()
    {
        const int points = 250;

        var senderBefore = await Api.GetBalanceAsync(_scenario.Sender);

        var rejected = await Api.SendGiftAsync(
            _scenario.Sender, _scenario.UntouchedRecipientId, points, PlayerServiceClient.NewId("req"));

        var problem = rejected.Failed(HttpStatusCode.Conflict, "gifting an offline player");
        Assert.Equal("Recipient offline", problem.Title);

        Assert.Equal(senderBefore, await Api.GetBalanceAsync(_scenario.Sender));

        var recipient = await Api.LoginFromNewDeviceAsync(_scenario.UntouchedRecipientId);

        Assert.Equal(
            PlayerServiceClient.SeedBalance + _scenario.ProbePointsToUntouched,
            await Api.GetBalanceAsync(recipient));

        // The counters are inside the same transaction as the transfer, so an aborted gift must not
        // have incremented them either.
        var recipientStats = (await Api.GetStatsAsync(recipient)).Ok();
        Assert.Equal(_scenario.ProbePointsToUntouched, recipientStats.GiftsReceived);
    }

    /// <summary>
    /// A rejection is replay-stable too: the same requestId answers the same way even after the
    /// recipient comes back, so a retrying client cannot turn a refusal into a transfer by luck.
    /// </summary>
    [E2EFact]
    public async Task A_rejected_gift_requestId_replays_as_the_same_rejection()
    {
        const int points = 60;

        var requestId = PlayerServiceClient.NewId("req");
        var recipientId = PlayerServiceClient.NewId("player");

        // A player who has never logged in is the one rejection that is reachable without waiting
        // out a TTL, and it is terminal in the same way an offline rejection is.
        var first = await Api.SendGiftAsync(_scenario.Sender, recipientId, points, requestId);
        Assert.Equal("Unknown recipient", first.Failed(HttpStatusCode.NotFound).Title);

        // The recipient now exists and is online - but the recorded outcome is terminal.
        var recipient = await Api.NewPlayerAsync(recipientId);

        var replay = await Api.SendGiftAsync(_scenario.Sender, recipientId, points, requestId);
        Assert.Equal("Unknown recipient", replay.Failed(HttpStatusCode.NotFound).Title);

        Assert.Equal(PlayerServiceClient.SeedBalance, await Api.GetBalanceAsync(recipient));
    }
}
