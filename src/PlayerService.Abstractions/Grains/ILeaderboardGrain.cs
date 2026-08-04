using Orleans;
using Orleans.Concurrency;
using PlayerService.Abstractions.Events;

namespace PlayerService.Abstractions.Grains;

/// <summary>
/// The cluster's single leaderboard authority (key <see cref="SingletonKey"/>). It is fed by the
/// <c>scores/global</c> stream and broadcasts Top-N to <c>leaderboard/top</c>; it is never on the
/// read path of <c>GET /leaderboard</c> — that would be the hot-grain anti-pattern this design
/// exists to avoid. The one call it serves is a per-pod priming pull at startup.
/// </summary>
[Alias("ILeaderboardGrain")]
public interface ILeaderboardGrain : IGrainWithIntegerKey
{
    /// <summary>The only valid key: the leaderboard is a cluster singleton.</summary>
    public const long SingletonKey = 0;

    /// <summary>Primes a pod's local cache once per pod lifetime. Not a per-request call.</summary>
    [ReadOnly]
    Task<LeaderboardSnapshot> GetTopAsync();
}
