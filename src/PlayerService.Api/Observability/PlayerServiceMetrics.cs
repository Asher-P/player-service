using System.Diagnostics.Metrics;
using PlayerService.Abstractions.Errors;

namespace PlayerService.Api.Observability;

/// <summary>
/// Domain metrics for the two things this service's design actually trades on: how often gift
/// transactions abort under contention, and how much of the traffic is duplicate requests.
/// </summary>
/// <remarks>
/// Orleans' own <c>Microsoft.Orleans</c> meter already reports grain call latency and counts, which
/// covers "is the cluster healthy". It cannot report <i>abort rate</i> in the sense that matters
/// here, because an abort-then-successful-retry is invisible to the caller and is not an error -
/// it is the design working as intended. Measuring it is how you tell "healthy contention" from
/// "the retry budget is about to start returning 503s", which is a config decision, not a code one.
/// </remarks>
public sealed class PlayerServiceMetrics
{
    /// <summary>Meter name, so a collector can subscribe to it by name alongside Microsoft.Orleans.</summary>
    public const string MeterName = "PlayerService";

    /// <summary>
    /// Named rather than inlined because the histogram is useless without the matching bucket
    /// boundaries configured against this exact name — see <c>ObservabilityExtensions</c>. A typo in
    /// one of the two would not fail anything; it would silently restore the default buckets, and
    /// the quantiles would go back to reporting constants.
    /// </summary>
    public const string AttemptsPerRequestInstrument = "playerservice.gift.attempts_per_request";

    private readonly Counter<long> _giftAttempts;
    private readonly Counter<long> _giftAborts;
    private readonly Counter<long> _giftOutcomes;
    private readonly Counter<long> _scoreOutcomes;
    private readonly Histogram<int> _giftAttemptsPerRequest;

    public PlayerServiceMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);

        _giftAttempts = meter.CreateCounter<long>(
            "playerservice.gift.attempts",
            unit: "{attempt}",
            description: "Gift transaction attempts, including retries.");

        _giftAborts = meter.CreateCounter<long>(
            "playerservice.gift.aborts",
            unit: "{abort}",
            description: "Gift transactions aborted by contention. Expected under load, not an error.");

        _giftOutcomes = meter.CreateCounter<long>(
            "playerservice.gift.outcomes",
            unit: "{gift}",
            description: "Terminal gift outcomes, tagged by result.");

        _scoreOutcomes = meter.CreateCounter<long>(
            "playerservice.score.outcomes",
            unit: "{update}",
            description: "Score updates, tagged by whether they were a replay.");

        _giftAttemptsPerRequest = meter.CreateHistogram<int>(
            AttemptsPerRequestInstrument,
            unit: "{attempt}",
            description: "Attempts a gift needed before reaching a terminal outcome. The distribution's tail is what predicts 503s.");
    }

    public void GiftAttempted() => _giftAttempts.Add(1);

    public void GiftAborted() => _giftAborts.Add(1);

    public void GiftApplied(int attempts, bool replayed)
    {
        _giftOutcomes.Add(1, new KeyValuePair<string, object?>("result", replayed ? "replayed" : "applied"));
        _giftAttemptsPerRequest.Record(attempts);
    }

    public void GiftRejected(GiftRejection rejection, int attempts)
    {
        _giftOutcomes.Add(1, new KeyValuePair<string, object?>("result", rejection.ToString()));
        _giftAttemptsPerRequest.Record(attempts);
    }

    /// <summary>Retries exhausted - the only gift path that surfaces as a 503.</summary>
    public void GiftExhausted(int attempts)
    {
        _giftOutcomes.Add(1, new KeyValuePair<string, object?>("result", "exhausted"));
        _giftAttemptsPerRequest.Record(attempts);
    }

    public void ScoreApplied(bool replayed) =>
        _scoreOutcomes.Add(1, new KeyValuePair<string, object?>("result", replayed ? "replayed" : "applied"));
}
