using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Streams;
using PlayerService.Abstractions.Events;
using PlayerService.Abstractions.Grains;
using PlayerService.Abstractions.Streaming;
using PlayerService.Grains.Configuration;

namespace PlayerService.Api.Services;

/// <summary>
/// The pod-local half of the push leaderboard: subscribes once to <c>leaderboard/top</c> and swaps
/// a snapshot reference on every broadcast. <c>GET /leaderboard</c> then reads that field —
/// O(1), wait-free, zero network hops, zero grain calls.
/// </summary>
/// <remarks>
/// Because the snapshot is always present and pre-computed there is no expiry to trigger a
/// recomputation, so cache stampede is eliminated by construction rather than mitigated. Priming
/// costs exactly one grain call per pod lifetime — not one per request.
/// </remarks>
public sealed class LeaderboardCache : IHostedService
{
    private readonly IClusterClient _client;
    private readonly IGrainFactory _grains;
    private readonly IOptionsMonitor<LeaderboardOptions> _options;
    private readonly ILogger<LeaderboardCache> _logger;

    // Reference assignment is atomic in .NET; volatile guarantees other threads see the new one.
    private volatile LeaderboardSnapshot _snapshot = LeaderboardSnapshot.Empty;

    private StreamSubscriptionHandle<LeaderboardSnapshot>? _subscription;

    public LeaderboardCache(
        IClusterClient client,
        IGrainFactory grains,
        IOptionsMonitor<LeaderboardOptions> options,
        ILogger<LeaderboardCache> logger)
    {
        _client = client;
        _grains = grains;
        _options = options;
        _logger = logger;
    }

    /// <summary>The wait-free read served by <c>GET /leaderboard</c>.</summary>
    public LeaderboardSnapshot Current => _snapshot;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Orleans is co-hosted and its silo hosted service is registered first, so the cluster is
        // up by the time this runs. Failures here are still non-fatal: an unprimed cache serves an
        // empty snapshot and self-corrects on the next broadcast.
        var stream = _client
            .GetStreamProvider(PlayerServiceStreams.ProviderName)
            .GetStream<LeaderboardSnapshot>(PlayerServiceStreams.Leaderboard);

        _subscription = await stream.SubscribeAsync(OnSnapshotAsync);

        try
        {
            _snapshot = await _grains
                .GetGrain<ILeaderboardGrain>(ILeaderboardGrain.SingletonKey)
                .GetTopAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Leaderboard cache could not prime; serving empty until first broadcast");
        }

        _logger.LogInformation(
            "Leaderboard cache subscribed to {Namespace}/{Key}, primed with {Count} of top {TopN}",
            PlayerServiceStreams.LeaderboardNamespace,
            PlayerServiceStreams.LeaderboardKey,
            _snapshot.Top.Length,
            _options.CurrentValue.TopN);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_subscription is not null)
        {
            await _subscription.UnsubscribeAsync();
            _subscription = null;
        }
    }

    private Task OnSnapshotAsync(LeaderboardSnapshot snapshot, StreamSequenceToken? token)
    {
        // Snapshots are absolute, so applying one is a plain reference swap. The only ordering the
        // cache cares about is never going backwards in time, which ComputedAt makes cheap to check
        // (stream delivery to a single subscriber is sequential, so this needs no interlock).
        if (snapshot.ComputedAt >= _snapshot.ComputedAt)
        {
            _snapshot = snapshot;
        }

        return Task.CompletedTask;
    }
}
