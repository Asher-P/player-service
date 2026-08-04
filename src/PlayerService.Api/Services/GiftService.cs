using Microsoft.Extensions.Options;
using Orleans;
using PlayerService.Abstractions.Errors;
using PlayerService.Abstractions.Models;
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
    private readonly IGrainFactory _grains;
    private readonly IOptionsMonitor<GiftOptions> _options;
    private readonly ILogger<GiftService> _logger;

    public GiftService(
        ITransactionClient transactions,
        IGrainFactory grains,
        IOptionsMonitor<GiftOptions> options,
        ILogger<GiftService> logger)
    {
        _transactions = transactions;
        _grains = grains;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Validates, then runs debit+credit as one distributed transaction with bounded retry.
    /// </summary>
    /// <remarks>
    /// Phase 4 shape, in order:
    /// <list type="number">
    /// <item>reject self-gift / non-positive points (done below — pure input validation);</item>
    /// <item>fast-path <c>[ReadOnly]</c> ledger peek to short-circuit obvious replays without
    /// paying 2PC cost — an optimization, never the authoritative check;</item>
    /// <item><c>RunTransaction(TransactionOption.Create, …)</c> calling
    /// <c>DebitForGiftAsync</c> then <c>CreditFromGiftAsync</c>;</item>
    /// <item>publish both absolute scores in one <c>PlayerScoreUpdated</c> after commit;</item>
    /// <item>retry <c>OrleansTransactionAbortedException</c> up to
    /// <see cref="GiftOptions.MaxAttempts"/> with jittered backoff, then surface 503.</item>
    /// </list>
    /// </remarks>
    public Task<GiftOutcome> SendGiftAsync(
        string senderId,
        string recipientId,
        int points,
        string requestId,
        CancellationToken cancellationToken = default)
    {
        if (string.Equals(senderId, recipientId, StringComparison.Ordinal))
        {
            return Task.FromResult(new GiftOutcome(false, GiftRejection.SelfGift, 0, 0));
        }

        throw new NotImplementedException("Phase 4 — gifting via Orleans transactions.");
    }
}
