using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Runtime;
using Orleans.Streams;
using PlayerService.Abstractions.Events;
using PlayerService.Abstractions.Grains;
using PlayerService.Abstractions.Models;
using PlayerService.Abstractions.Streaming;
using PlayerService.Grains.Configuration;
using PlayerService.Grains.Internal;

namespace PlayerService.Grains;

/// <summary>
/// The cluster's single leaderboard writer. Default (non-reentrant) concurrency makes this
/// activation the sole mutator of both collections, so no locks and no concurrent collections are
/// needed — the single-writer property the old in-process <c>BackgroundService</c> had, now with
/// cluster-wide scope.
/// </summary>
/// <remarks>
/// Read traffic never reaches this grain: it broadcasts Top-N on a stream and every pod serves
/// <c>GET /leaderboard</c> from a local snapshot. Inverting pull to push is what removes the
/// hot-grain bottleneck, and a permanently pre-computed snapshot is what removes cache stampede.
/// </remarks>
public sealed class LeaderboardGrain : Grain, ILeaderboardGrain
{
    /// <summary>O(1) lookup of a player's current entry, so the stale one can be removed from the set.</summary>
    private readonly Dictionary<string, PlayerScore> _current = new(StringComparer.Ordinal);

    /// <summary>All players, not just the top N — a player who gifts points away must be able to
    /// fall out of the top N and later climb back in. O(log U) remove + add.</summary>
    private readonly SortedSet<PlayerScore> _ranked = new(PlayerScoreComparer.Instance);

    private readonly IOptionsMonitor<LeaderboardOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<LeaderboardGrain> _logger;

    private IAsyncStream<LeaderboardSnapshot>? _broadcast;
    private StreamSubscriptionHandle<PlayerScoreUpdated>? _subscription;
    private IGrainTimer? _publishTimer;
    private LeaderboardSnapshot _snapshot = LeaderboardSnapshot.Empty;
    private bool _dirty;

    public LeaderboardGrain(
        IOptionsMonitor<LeaderboardOptions> options,
        TimeProvider time,
        ILogger<LeaderboardGrain> logger)
    {
        _options = options;
        _time = time;
        _logger = logger;
    }

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var provider = this.GetStreamProvider(PlayerServiceStreams.ProviderName);

        var scores = provider.GetStream<PlayerScoreUpdated>(PlayerServiceStreams.Scores);

        // Resume rather than re-subscribe when this activation is a re-incarnation: the explicit
        // subscription survives in the pub-sub store, so subscribing again would double-deliver.
        var handles = await scores.GetAllSubscriptionHandles();
        if (handles.Count > 0)
        {
            _subscription = await handles[0].ResumeAsync(OnScoreUpdatedAsync);
            for (var i = 1; i < handles.Count; i++)
            {
                await handles[i].UnsubscribeAsync();
            }
        }
        else
        {
            _subscription = await scores.SubscribeAsync(OnScoreUpdatedAsync);
        }

        _broadcast = provider.GetStream<LeaderboardSnapshot>(PlayerServiceStreams.Leaderboard);

        // Activation-local, non-durable timer — correct here because the leaderboard is a
        // rebuildable projection, not durable business state. A reminder would be overkill.
        var interval = _options.CurrentValue.BroadcastInterval;
        _publishTimer = this.RegisterGrainTimer(PublishIfChangedAsync, interval, interval);

        _logger.LogInformation(
            "Leaderboard grain activated; subscribed to {Namespace}/{Key}, broadcasting every {Interval}",
            PlayerServiceStreams.ScoresNamespace,
            PlayerServiceStreams.ScoresKey,
            interval);

        await base.OnActivateAsync(cancellationToken);
    }

    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        _publishTimer?.Dispose();

        // The subscription is intentionally NOT cancelled: it must survive deactivation so a later
        // activation resumes the same subscription instead of losing the ingest stream.
        _subscription = null;

        return base.OnDeactivateAsync(reason, cancellationToken);
    }

    public Task<LeaderboardSnapshot> GetTopAsync() => Task.FromResult(_snapshot);

    /// <summary>
    /// Ingest. Orleans delivers stream items one batch at a time onto this single activation, so
    /// applying every entry and recomputing Top-N once per batch is natural micro-batching.
    /// </summary>
    /// <remarks>
    /// Absolute scores are what make this safe under at-least-once delivery: a redelivered event
    /// re-states the truth rather than double-counting, so the projection is idempotent and
    /// self-healing without any dedupe bookkeeping. A gift arrives as one event carrying both
    /// sides, so the pair is absorbed together instead of showing a moment where points exist twice.
    /// </remarks>
    private Task OnScoreUpdatedAsync(PlayerScoreUpdated update, StreamSequenceToken? token)
    {
        foreach (var score in update.Scores)
        {
            if (_current.TryGetValue(score.PlayerId, out var stale))
            {
                if (stale.Score == score.Score)
                {
                    // Same truth restated - nothing to re-rank, and nothing to broadcast.
                    continue;
                }

                // The set is keyed by (score, playerId), so the entry must be removed at its *old*
                // score before being reinserted at the new one. Holding every player, not just the
                // top N, is what lets someone who gifted points away climb back in later.
                _ranked.Remove(stale);
            }

            _current[score.PlayerId] = score;
            _ranked.Add(score);
            _dirty = true;
        }

        _logger.LogDebug("Leaderboard ingest applied {Count} score(s)", update.Scores.Length);
        return Task.CompletedTask;
    }

    /// <summary>Publish-on-change, coalesced by the timer interval — the staleness bound.</summary>
    private async Task PublishIfChangedAsync(CancellationToken cancellationToken)
    {
        if (!_dirty || _broadcast is null)
        {
            return;
        }

        _snapshot = new LeaderboardSnapshot(BuildTop(), _time.GetUtcNow());
        _dirty = false;

        await _broadcast.OnNextAsync(_snapshot);
    }

    private ImmutableArray<PlayerScore> BuildTop()
    {
        var topN = _options.CurrentValue.TopN;
        var builder = ImmutableArray.CreateBuilder<PlayerScore>(Math.Min(topN, _ranked.Count));

        foreach (var entry in _ranked)
        {
            if (builder.Count == topN)
            {
                break;
            }

            builder.Add(entry);
        }

        return builder.ToImmutable();
    }
}
