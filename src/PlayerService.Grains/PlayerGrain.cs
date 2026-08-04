using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Concurrency;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;
using PlayerService.Abstractions.Grains;
using PlayerService.Abstractions.Models;
using PlayerService.Grains.Configuration;
using PlayerService.Grains.Internal;

namespace PlayerService.Grains;

/// <summary>
/// One activation per player. Balance, counters and the idempotency ledger live in transactional
/// state; the session lives in plain activation state because it is read (never written) inside
/// gift transactions and therefore introduces no rollback hazard.
/// </summary>
/// <remarks>
/// <c>[Reentrant]</c> is an Orleans hard requirement for grains using <c>ITransactionalState</c>:
/// the transaction protocol delivers prepare/commit callbacks into the grain while it is still
/// awaiting other participants, and a non-reentrant grain would deadlock against its own
/// transaction. It buys interleaving <i>with the transaction runtime</i> — it is not what keeps
/// sender/recipient gifts from deadlocking (the tree-shaped orchestration in GiftService is).
/// Consequence: every method below that mutates a plain field completes its read-modify-write
/// within one turn, with no <c>await</c> in between.
/// </remarks>
[Reentrant]
public sealed class PlayerGrain : Grain, IPlayerGrain
{
    private readonly ITransactionalState<PlayerBalanceState> _balance;
    private readonly IOptionsMonitor<PlayerSessionOptions> _sessionOptions;
    private readonly TimeProvider _time;
    private readonly ILogger<PlayerGrain> _logger;

    // Plain (non-transactional) activation state.
    private SessionGrant? _session;
    private DateTimeOffset _lastActive;

    public PlayerGrain(
        [TransactionalState("balance", PlayerServiceStorage.TransactionStore)]
        ITransactionalState<PlayerBalanceState> balance,
        IOptionsMonitor<PlayerSessionOptions> sessionOptions,
        TimeProvider time,
        ILogger<PlayerGrain> logger)
    {
        _balance = balance;
        _sessionOptions = sessionOptions;
        _time = time;
        _logger = logger;
    }

    private string PlayerId => this.GetPrimaryKeyString();

    public Task<PlayerStats> GetStatsAsync() =>
        _balance.PerformRead(state => new PlayerStats(
            PlayerId,
            state.Balance,
            state.GiftsSent,
            state.GiftsReceived,
            _lastActive));

    // TODO(Phase 3): PerformUpdate -> ledger check under "score:{requestId}" -> apply -> ledger
    // write -> prune; publish PlayerScoreUpdated (absolute) to scores/global after commit.
    public Task<ScoreOutcome> AddPointsAsync(int points, string requestId) =>
        throw new NotImplementedException("Phase 3 - atomic + idempotent score updates.");

    // TODO(Phase 4): ledger check under "gift:{requestId}", funds check, debit, GiftsSent++ and
    // ledger write in a single PerformUpdate, so all of it commits or rolls back together.
    public Task<int> DebitForGiftAsync(string recipientId, int points, string requestId) =>
        throw new NotImplementedException("Phase 4 - gifting via Orleans transactions.");

    // TODO(Phase 4): online check evaluated here, on the recipient's own activation, inside the
    // gift transaction - throwing aborts the whole transfer so nothing is debited.
    public Task<int> CreditFromGiftAsync(string senderId, int points) =>
        throw new NotImplementedException("Phase 4 - gifting via Orleans transactions.");

    /// <summary>
    /// Single turn, no <c>await</c>: reads the session, decides, slides the expiry, returns.
    /// This is the invariant that makes <c>[AlwaysInterleave]</c> safe on a reentrant grain.
    /// </summary>
    public Task<bool> ValidateAndSlideSessionAsync(string token)
    {
        var now = _time.GetUtcNow();

        if (_session is null || !string.Equals(_session.Token, token, StringComparison.Ordinal))
        {
            return Task.FromResult(false);
        }

        if (_session.ExpiresAt <= now)
        {
            return Task.FromResult(false);
        }

        _session = _session with { ExpiresAt = now + _sessionOptions.CurrentValue.Ttl };
        _lastActive = now;
        return Task.FromResult(true);
    }

    /// <summary>
    /// Binds a new session, superseding any previous one - which is what keeps "one online session
    /// per player" true, and therefore keeps the gift online-check a single unambiguous read.
    /// </summary>
    public Task<SessionGrant?> BindSessionAsync(SessionGrant grant)
    {
        var previous = _session;
        _session = grant;
        _lastActive = _time.GetUtcNow();

        if (previous is not null && !string.Equals(previous.DeviceId, grant.DeviceId, StringComparison.Ordinal))
        {
            _logger.LogInformation(
                "Player {PlayerId} session superseded: device {PreviousDevice} -> {NewDevice}",
                PlayerId,
                previous.DeviceId,
                grant.DeviceId);
        }

        // Handing the displaced grant back lets the *caller* release that device. Notifying from
        // here would mean an outbound call inside an [AlwaysInterleave] method, breaking the
        // single-turn invariant that makes interleaving safe in the first place.
        return Task.FromResult(previous);
    }

    public Task<bool> IsOnlineAsync() =>
        Task.FromResult(_session is not null && _session.ExpiresAt > _time.GetUtcNow());
}
