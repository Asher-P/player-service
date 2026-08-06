using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Concurrency;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.Transactions.Abstractions;
using PlayerService.Abstractions.Errors;
using PlayerService.Abstractions.Events;
using PlayerService.Abstractions.Grains;
using PlayerService.Abstractions.Models;
using PlayerService.Abstractions.Streaming;
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
    private readonly IOptionsMonitor<IdempotencyOptions> _idempotencyOptions;
    private readonly TimeProvider _time;
    private readonly ILogger<PlayerGrain> _logger;

    // Plain (non-transactional) activation state.
    private SessionGrant? _session;
    private DateTimeOffset _lastActive;

    /// <summary>
    /// Terminal gift rejections, kept <b>outside</b> the transactional state on purpose: a rejected
    /// gift aborts its transaction, so anything written in-transaction would roll back with it.
    /// Same bounding as the transactional ledger; activation-local, so it is lost on deactivation -
    /// which is the same "replay after eviction is treated as new" trade-off documented in §5.3.
    /// </summary>
    private readonly IdempotencyLedger _giftRejections = new();

    public PlayerGrain(
        [TransactionalState("balance", PlayerServiceStorage.TransactionStore)]
        ITransactionalState<PlayerBalanceState> balance,
        IOptionsMonitor<PlayerSessionOptions> sessionOptions,
        IOptionsMonitor<IdempotencyOptions> idempotencyOptions,
        TimeProvider time,
        ILogger<PlayerGrain> logger)
    {
        _balance = balance;
        _sessionOptions = sessionOptions;
        _idempotencyOptions = idempotencyOptions;
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

    /// <summary>
    /// Ledger check, apply and ledger write happen in the same <c>PerformUpdate</c> against
    /// transactional state, so a duplicate <paramref name="requestId"/> can never be missed by two
    /// simultaneous callers - both land on this grain's single activation, and whichever runs
    /// second sees the entry the first one wrote (plan §5.3). The stream publish that follows only
    /// runs when this call actually applied a fresh change, never for a replay.
    /// </summary>
    public async Task<ScoreOutcome> AddPointsAsync(int points, string requestId)
    {
        var key = ScoreKey(requestId);
        var now = _time.GetUtcNow();
        var maxEntries = _idempotencyOptions.CurrentValue.MaxEntriesPerPlayer;
        var ttl = _idempotencyOptions.CurrentValue.Ttl;

        var outcome = await _balance.PerformUpdate(state =>
        {
            if (state.Ledger.TryGet(key, out var existing) && existing.Score is not null)
            {
                return existing.Score with { Replayed = true };
            }

            state.Balance += points;

            var stats = new PlayerStats(PlayerId, state.Balance, state.GiftsSent, state.GiftsReceived, now);
            var result = new ScoreOutcome(stats, false);

            state.Ledger.Record(key, new LedgerEntry { RecordedAt = now, Score = result });
            state.Ledger.Prune(now, maxEntries, ttl);

            return result;
        });

        if (!outcome.Replayed)
        {
            _lastActive = now;

            var provider = this.GetStreamProvider(PlayerServiceStreams.ProviderName);
            var stream = provider.GetStream<PlayerScoreUpdated>(PlayerServiceStreams.Scores);
            await stream.OnNextAsync(new PlayerScoreUpdated(
                ImmutableArray.Create(new PlayerScore(PlayerId, outcome.Stats.Balance))));
        }

        return outcome;
    }

    /// <summary>
    /// A write, not a read: the credit that follows needs an exclusive lock, and entering as a
    /// reader first would turn the credit into a lock <i>upgrade</i> — which Orleans answers with
    /// <c>OrleansTransactionLockUpgradeException</c> the moment another transaction shares the read
    /// lock. Mutating nothing costs nothing here, because a gift writes this state either way.
    /// </summary>
    public Task EnlistForGiftAsync() => _balance.PerformUpdate(_ => { });

    /// <summary>
    /// Funds check and debit are the <b>same statement</b> inside one <c>PerformUpdate</c>, so two
    /// concurrent 100-point gifts from a 100-point balance cannot both pass: serializable isolation
    /// means the second either sees the first's committed effect or conflicts and aborts. That is
    /// what makes "a balance never goes negative" structural rather than checked-then-hoped.
    /// </summary>
    public async Task<int> DebitForGiftAsync(string recipientId, int points, string requestId)
    {
        var key = GiftKey(requestId);

        // Rejections live outside the transaction (see RecordGiftRejectionAsync), so they are
        // checked separately - but they are just as terminal, and just as replayable.
        if (_giftRejections.TryGet(key, out var rejected) && rejected.Gift is not null)
        {
            throw new GiftReplayException(rejected.Gift);
        }

        return await _balance.PerformUpdate(state =>
        {
            if (state.Ledger.TryGet(key, out var existing) && existing.Gift is not null)
            {
                throw new GiftReplayException(existing.Gift);
            }

            if (state.Balance < points)
            {
                throw new GiftRejectedException(
                    GiftRejection.InsufficientFunds,
                    $"Balance {state.Balance} is short of the {points} points requested.");
            }

            state.Balance -= points;
            state.GiftsSent++;
            return state.Balance;
        });
    }

    /// <summary>
    /// The online check runs here, on the recipient's own activation, inside the same transaction as
    /// the credit - which is exactly what makes it non-stale. The guarantee is: if the recipient's
    /// session had not expired as of the commit point, the gift applies; an expiry strictly after
    /// commit does not roll it back.
    /// </summary>
    /// <remarks>
    /// The session is <i>read</i>, never written, inside the gift transaction, so the fact that it
    /// is plain state introduces no rollback hazard. Both reads happen before the first
    /// <c>await</c>, so nothing can interleave between deciding and acting.
    /// </remarks>
    public async Task<int> CreditFromGiftAsync(string senderId, int points)
    {
        var session = _session;
        var now = _time.GetUtcNow();

        if (session is null)
        {
            // Grains are virtual, so any playerId activates. "Never had a session bound" is the
            // closest observable equivalent of "this player does not exist".
            throw new GiftRejectedException(
                GiftRejection.UnknownRecipient,
                $"Player '{PlayerId}' has never logged in.");
        }

        if (session.ExpiresAt <= now)
        {
            throw new GiftRejectedException(
                GiftRejection.Offline,
                $"Player '{PlayerId}' has no live session.");
        }

        return await _balance.PerformUpdate(state =>
        {
            state.Balance += points;
            state.GiftsReceived++;
            return state.Balance;
        });
    }

    public Task CompleteGiftAsync(string requestId, GiftOutcome outcome)
    {
        var now = _time.GetUtcNow();
        var options = _idempotencyOptions.CurrentValue;

        return _balance.PerformUpdate(state =>
        {
            state.Ledger.Record(GiftKey(requestId), new LedgerEntry { RecordedAt = now, Gift = outcome });
            state.Ledger.Prune(now, options.MaxEntriesPerPlayer, options.Ttl);
        });
    }

    public Task RecordGiftRejectionAsync(string requestId, GiftOutcome outcome)
    {
        var now = _time.GetUtcNow();
        var options = _idempotencyOptions.CurrentValue;

        _giftRejections.Record(GiftKey(requestId), new LedgerEntry { RecordedAt = now, Gift = outcome });
        _giftRejections.Prune(now, options.MaxEntriesPerPlayer, options.Ttl);

        return Task.CompletedTask;
    }

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

    // The grain key already scopes these to one player, so "score:" and "gift:" are all the
    // namespacing {playerId, requestId} and {senderId, requestId} need to stay distinct.
    private static string ScoreKey(string requestId) => $"score:{requestId}";

    private static string GiftKey(string requestId) => $"gift:{requestId}";
}
