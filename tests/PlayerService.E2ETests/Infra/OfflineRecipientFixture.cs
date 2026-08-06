using System.Diagnostics;
using System.Net;
using Xunit;
using Xunit.Sdk;

namespace PlayerService.E2E;

/// <summary>
/// Prepares the one state the API offers no shortcut to: a player who is <b>offline</b>.
/// </summary>
/// <remarks>
/// <para>
/// There is no logout endpoint - deliberately, since a session is a sliding lease and a crashed
/// device cannot be relied on to say goodbye. So over HTTP the only honest way to reach "offline"
/// is to stop touching the session and let its TTL lapse, which costs one wall-clock TTL
/// (3 minutes as shipped; set <c>Sessions:Ttl</c> to its 1-minute floor to cut it to a third, and
/// tell this suite via <see cref="E2EEnvironment.SessionTtlVariable"/>).
/// </para>
/// <para>
/// That wait is paid <i>once</i>, here, for both offline scenarios: two recipients are logged in
/// together and expire together, so each test gets its own untouched victim and the two tests stay
/// order-independent. Meanwhile xUnit runs the other collection in parallel, so the suite's wall
/// clock is roughly the TTL rather than the TTL plus everything else.
/// </para>
/// <para>
/// The transition is <i>observed</i>, not slept through: a probe player gifts 1 point at a time
/// until the answer flips from <c>200</c> to <c>409 Recipient offline</c>. The probes double as
/// keep-alive traffic on the recipients' activations, so the rejection under test is "offline" and
/// not "this activation was collected" (which is a 404 - a real distinction, documented in the
/// README). Every probe that lands is counted, so the final balances are still exact.
/// </para>
/// </remarks>
public sealed class OfflineRecipientFixture : IAsyncLifetime, IDisposable
{
    /// <summary>Points in the gift that is later replayed.</summary>
    public const int GiftPoints = 75;

    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(3);

    /// <summary>Headroom over the TTL for clock granularity and the last probe interval.</summary>
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(45);

    public PlayerServiceClient Api { get; } = PlayerServiceClient.Create();

    /// <summary>Sender of the original gift, kept alive across the wait by its own polling.</summary>
    public PlayerSession Sender { get; private set; } = null!;

    /// <summary>Recipient of the original gift; used by the replay-after-offline scenario.</summary>
    public string GiftedRecipientId { get; private set; } = null!;

    /// <summary>A recipient that never received anything; used by the rejection scenario.</summary>
    public string UntouchedRecipientId { get; private set; } = null!;

    public string GiftRequestId { get; private set; } = null!;

    /// <summary>The response the original gift returned, which a replay must reproduce exactly.</summary>
    public GiftBody OriginalGift { get; private set; } = null!;

    public int ProbePointsToGifted { get; private set; }

    public int ProbePointsToUntouched { get; private set; }

    /// <summary>How long the sessions actually took to lapse. Reported for the record.</summary>
    public TimeSpan TimeToOffline { get; private set; }

    public async Task InitializeAsync()
    {
        if (E2EEnvironment.SkipReason is not null)
        {
            // The tests are already skipped; do not add a fixture failure on top of the reason.
            return;
        }

        Sender = await Api.NewPlayerAsync();
        var gifted = await Api.NewPlayerAsync();
        var untouched = await Api.NewPlayerAsync();

        GiftedRecipientId = gifted.PlayerId;
        UntouchedRecipientId = untouched.PlayerId;
        GiftRequestId = PlayerServiceClient.NewId("req");

        OriginalGift = (await Api.SendGiftAsync(Sender, GiftedRecipientId, GiftPoints, GiftRequestId))
            .Ok("the gift that will later be replayed");

        Assert.True(OriginalGift.Applied);
        Assert.False(OriginalGift.Replayed);

        // From here on nobody uses the recipients' tokens: an authenticated request slides the
        // session, and sliding it is precisely what we are waiting not to happen.
        await WaitUntilOfflineAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose() => Api.Dispose();

    private async Task WaitUntilOfflineAsync()
    {
        var probe = await Api.NewPlayerAsync();
        var budget = E2EEnvironment.SessionTtl + Slack;
        var deadline = DateTimeOffset.UtcNow + budget;
        var elapsed = Stopwatch.StartNew();

        var giftedOnline = true;
        var untouchedOnline = true;

        while (giftedOnline || untouchedOnline)
        {
            // The sender must survive the wait, and every request it makes slides its own session -
            // so this poll is both the keep-alive and a standing check that nothing moved.
            var senderStats = (await Api.GetStatsAsync(Sender)).Ok("keeping the sender's session alive");
            Assert.Equal(PlayerServiceClient.SeedBalance - GiftPoints, senderStats.Score);

            if (giftedOnline && await ProbeAsync(probe, GiftedRecipientId))
            {
                ProbePointsToGifted++;
            }
            else
            {
                giftedOnline = false;
            }

            if (untouchedOnline && await ProbeAsync(probe, UntouchedRecipientId))
            {
                ProbePointsToUntouched++;
            }
            else
            {
                untouchedOnline = false;
            }

            if (!giftedOnline && !untouchedOnline)
            {
                break;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new XunitException(
                    $"the recipients were still online after {budget}. The service's Sessions:Ttl is "
                    + $"longer than the {E2EEnvironment.SessionTtl} this suite was told about - set "
                    + $"{E2EEnvironment.SessionTtlVariable} to match it.");
            }

            await Task.Delay(ProbeInterval);
        }

        TimeToOffline = elapsed.Elapsed;
    }

    /// <summary>
    /// One probe gift. <c>true</c> while the recipient is still online (and one point richer),
    /// <c>false</c> the moment the service reports the session gone.
    /// </summary>
    private async Task<bool> ProbeAsync(PlayerSession probe, string recipientId)
    {
        var result = await Api.SendGiftAsync(probe, recipientId, 1, PlayerServiceClient.NewId("req"));

        if (result.Status == HttpStatusCode.OK)
        {
            return true;
        }

        if (result.Status == HttpStatusCode.Conflict && result.Problem?.Title == "Recipient offline")
        {
            return false;
        }

        // 404 would mean the activation was collected mid-wait, which changes what the tests are
        // asserting; anything else is a genuine surprise. Either way, fail loudly rather than
        // proceed to assert on a state we did not actually reach.
        throw new XunitException(
            $"probing {recipientId} expected 200 or 409 'Recipient offline', got "
            + $"{(int)result.Status} {result.Status}: {result.Raw}");
    }
}

[CollectionDefinition(Name)]
public sealed class OfflineRecipientCollection : ICollectionFixture<OfflineRecipientFixture>
{
    public const string Name = "offline-recipients";
}
