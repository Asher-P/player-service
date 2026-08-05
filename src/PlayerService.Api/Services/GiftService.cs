using System.Collections.Immutable;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Transactions;
using PlayerService.Abstractions.Errors;
using PlayerService.Abstractions.Events;
using PlayerService.Abstractions.Grains;
using PlayerService.Abstractions.Models;
using PlayerService.Abstractions.Streaming;
using PlayerService.Api.Configuration;

namespace PlayerService.Api.Services;

/// <summary>
/// Orchestrates gifting from the <b>API layer</b>, which is what makes the call graph a tree —
/// <c>GiftService → sender</c> and <c>GiftService → recipient</c>, never sender → recipient.
/// With no cycle in the call graph, <c>p1→p2</c> and <c>p2→p1</c> firing simultaneously cannot
/// deadlock at the activation level; they can only conflict at the transactional-state layer,
/// which Orleans resolves by aborting one, never by blocking both.
/// </summary>
public sealed class GiftService
{
    private readonly ITransactionClient _transactions;
    private readonly IClusterClient _client;
    private readonly IGrainFactory _grains;
    private readonly IOptionsMonitor<GiftOptions> _options;
    private readonly ILogger<GiftService> _logger;

    public GiftService(
        ITransactionClient transactions,
        IClusterClient client,
        IGrainFactory grains,
        IOptionsMonitor<GiftOptions> options,
        ILogger<GiftService> logger)
    {
        _transactions = transactions;
        _client = client;
        _grains = grains;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Validates, then runs debit + credit + ledger write as one distributed transaction with
    /// bounded retry. Every terminal answer — applied, rejected, or replayed — comes back as a
    /// <see cref="GiftOutcome"/>; only exhausted retries surface as an exception (503).
    /// </summary>
    /// <exception cref="OrleansTransactionAbortedException">
    /// Contention outlasted the retry budget. Safe for the client to retry with the same requestId,
    /// because the operation is idempotent.
    /// </exception>
    public async Task<GiftOutcome> SendGiftAsync(
        string senderId,
        string recipientId,
        int points,
        string requestId,
        CancellationToken cancellationToken = default)
    {
        if (string.Equals(senderId, recipientId, StringComparison.Ordinal))
        {
            return new GiftOutcome(false, GiftRejection.SelfGift, 0, 0);
        }

        var options = _options.CurrentValue;
        var sender = _grains.GetGrain<IPlayerGrain>(senderId);
        var recipient = _grains.GetGrain<IPlayerGrain>(recipientId);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                GiftOutcome? outcome = null;

                await _transactions.RunTransaction(TransactionOption.Create, async () =>
                {
                    // Sender first: a replay or an insufficient balance is then caught before the
                    // recipient is touched at all.
                    var senderBalance = await sender.DebitForGiftAsync(recipientId, points, requestId);
                    var recipientBalance = await recipient.CreditFromGiftAsync(senderId, points);

                    outcome = new GiftOutcome(true, null, senderBalance, recipientBalance);

                    // Recorded inside the transaction, so the proof that this gift happened commits
                    // with the gift itself - or rolls back with it.
                    await sender.CompleteGiftAsync(requestId, outcome);
                });

                // Published only after commit, so an aborted transaction produces no event. One
                // event carrying both absolute scores, so the projection absorbs the pair together.
                await PublishScoresAsync(
                    new PlayerScore(senderId, outcome!.SenderBalance),
                    new PlayerScore(recipientId, outcome.RecipientBalance));

                return outcome;
            }
            catch (Exception ex) when (Unwrap<GiftReplayException>(ex) is { } replay)
            {
                // The duplicate aborted its own transaction, so it moved nothing; the caller gets
                // exactly what the original attempt returned.
                return replay.Outcome with { Replayed = true };
            }
            catch (Exception ex) when (Unwrap<GiftRejectedException>(ex) is { } rejected)
            {
                var outcome = new GiftOutcome(false, rejected.Rejection, 0, 0);

                // Outside the (now aborted) transaction, so the record survives the rollback that
                // the rejection itself caused - see IPlayerGrain.RecordGiftRejectionAsync.
                await sender.RecordGiftRejectionAsync(requestId, outcome);

                _logger.LogInformation(
                    "Gift {RequestId} from {Sender} to {Recipient} rejected: {Rejection}",
                    requestId, senderId, recipientId, rejected.Rejection);

                return outcome;
            }
            catch (OrleansTransactionAbortedException) when (attempt < options.MaxAttempts)
            {
                // Contention, not failure: retrying is safe precisely because the operation is
                // idempotent - if the aborted attempt had in fact committed, the retry finds the
                // ledger entry and returns the original outcome.
                _logger.LogDebug(
                    "Gift {RequestId} aborted on attempt {Attempt}/{MaxAttempts}; retrying",
                    requestId, attempt, options.MaxAttempts);

                await Task.Delay(BackoffFor(attempt, options.RetryBaseDelay), cancellationToken);
            }
        }
    }

    private async Task PublishScoresAsync(params PlayerScore[] scores)
    {
        var stream = _client
            .GetStreamProvider(PlayerServiceStreams.ProviderName)
            .GetStream<PlayerScoreUpdated>(PlayerServiceStreams.Scores);

        await stream.OnNextAsync(new PlayerScoreUpdated(ImmutableArray.Create(scores)));
    }

    /// <summary>Exponential with full jitter, so retries of a contended pair spread out instead of
    /// colliding again in lockstep.</summary>
    private static TimeSpan BackoffFor(int attempt, TimeSpan baseDelay)
    {
        var window = baseDelay * Math.Pow(2, attempt - 1);
        return window * Random.Shared.NextDouble();
    }

    /// <summary>
    /// Finds our signal in whatever Orleans wrapped it in. A grain exception crossing a transaction
    /// boundary can arrive nested inside an abort or an aggregate, so matching on the top-level type
    /// alone would silently turn a rejection into a 503.
    /// </summary>
    private static T? Unwrap<T>(Exception? exception) where T : Exception
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is T match)
            {
                return match;
            }

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    if (Unwrap<T>(inner) is { } nested)
                    {
                        return nested;
                    }
                }
            }
        }

        return null;
    }
}
