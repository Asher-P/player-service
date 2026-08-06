using Orleans;
using Orleans.Concurrency;
using PlayerService.Abstractions.Models;

namespace PlayerService.Abstractions.Grains;

/// <summary>
/// One activation per player, cluster-wide, keyed by playerId. Every mutation of a player's
/// balance runs on this single activation, so "zero lost updates" is a property of the runtime
/// rather than of any lock we wrote.
/// </summary>
/// <remarks>
/// Reentrancy contract (plan §5.2): the implementation is <c>[Reentrant]</c> because
/// <c>ITransactionalState</c> requires it. Any method that mutates <b>plain</b> (non-transactional)
/// fields must therefore complete its read-modify-write inside a single turn — no <c>await</c>
/// between reading a session field and writing it.
/// </remarks>
[Alias("IPlayerGrain")]
public interface IPlayerGrain : IGrainWithStringKey
{
    /// <summary>
    /// Adds points idempotently under <c>score:{requestId}</c>.
    /// <c>CreateOrJoin</c>: usually creates its own single-participant transaction, but joins the
    /// ambient one if a score add is ever issued inside a larger unit of work.
    /// </summary>
    [Transaction(TransactionOption.CreateOrJoin)]
    Task<ScoreOutcome> AddPointsAsync(int points, string requestId);

    /// <summary>
    /// Takes this player's transactional-state lock without reading or changing anything, so the
    /// gift transaction can acquire its two locks in a fixed global order. Called on the recipient,
    /// and only when the recipient sorts before the sender — the debit/credit pair already runs in
    /// ascending key order otherwise. See <c>GiftService.SendGiftAsync</c> for why the order matters.
    /// </summary>
    [Transaction(TransactionOption.Join)]
    [ResponseTimeout("00:00:05")]
    Task EnlistForGiftAsync();

    /// <summary>
    /// Sender side of a gift: idempotency check, then funds check and debit in one
    /// <c>PerformUpdate</c>. <c>Join</c> — it is only ever called inside the gift transaction the
    /// API layer opened. Returns the sender's post-debit balance.
    /// </summary>
    /// <exception cref="Errors.GiftReplayException">This requestId already has a recorded outcome.</exception>
    /// <exception cref="Errors.GiftRejectedException">The balance would have gone negative.</exception>
    [Transaction(TransactionOption.Join)]
    [ResponseTimeout("00:00:05")]
    Task<int> DebitForGiftAsync(string recipientId, int points, string requestId);

    /// <summary>
    /// Recipient side of a gift: the online check runs here, on the recipient's own activation,
    /// inside the same transaction as the credit — which is what makes it non-stale. Throws to
    /// abort the whole transaction when the recipient is offline. Returns the post-credit balance.
    /// </summary>
    /// <exception cref="Errors.GiftRejectedException">The recipient is offline or unknown.</exception>
    [Transaction(TransactionOption.Join)]
    [ResponseTimeout("00:00:05")]
    Task<int> CreditFromGiftAsync(string senderId, int points);

    /// <summary>
    /// Writes the sender's ledger entry for a gift that is about to commit. Split from
    /// <see cref="DebitForGiftAsync"/> because the stored outcome includes the <i>recipient's</i>
    /// post-credit balance, which does not exist until the credit has run — and the entry must be
    /// the exact outcome the caller receives, or a replay would not be byte-for-byte stable.
    /// Being in the same transaction is what makes the record commit or roll back with the transfer.
    /// </summary>
    [Transaction(TransactionOption.Join)]
    [ResponseTimeout("00:00:05")]
    Task CompleteGiftAsync(string requestId, GiftOutcome outcome);

    /// <summary>
    /// Records a <b>terminal rejection</b> outside any transaction (plan §7.4). A rejected gift
    /// aborts its transaction, so an in-transaction ledger entry would roll back with it and the
    /// replay would re-run the whole attempt. This deliberate asymmetry is what keeps a rejected
    /// requestId replay-stable. Plain activation state, single turn, no <c>await</c>.
    /// </summary>
    [AlwaysInterleave]
    Task RecordGiftRejectionAsync(string requestId, GiftOutcome outcome);

    /// <summary>
    /// Reads balance and counters. Transactional rather than <c>[ReadOnly]</c> because
    /// <c>ITransactionalState</c> may only be read inside a transaction context.
    /// </summary>
    [Transaction(TransactionOption.CreateOrJoin)]
    Task<PlayerStats> GetStatsAsync();

    /// <summary>
    /// Validates the presented token against the live session and slides its expiry.
    /// <c>[AlwaysInterleave]</c>: authentication must not queue behind a slow score transaction on
    /// a hot player. Safe because the body touches only plain session fields and never awaits.
    /// </summary>
    [AlwaysInterleave]
    Task<bool> ValidateAndSlideSessionAsync(string token);

    /// <summary>
    /// Binds (and thereby supersedes any prior) session for this player, returning the grant it
    /// displaced so the caller can release that device. Returning it rather than notifying from
    /// here keeps this method a single turn with no outbound call - see the reentrancy contract.
    /// </summary>
    [AlwaysInterleave]
    Task<SessionGrant?> BindSessionAsync(SessionGrant grant);

    /// <summary>True while the player's session has not expired. Used by the gift online check.</summary>
    [AlwaysInterleave]
    Task<bool> IsOnlineAsync();
}
