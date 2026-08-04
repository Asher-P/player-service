using System.Collections.Immutable;
using PlayerService.Abstractions.Models;

namespace PlayerService.Abstractions.Events;

/// <summary>
/// Top-N broadcast on <c>leaderboard/top</c>, cached per pod and served O(1) by
/// <c>GET /leaderboard</c>. <see cref="ComputedAt"/> is returned to the client so the
/// documented staleness bound (&lt; ~1.1 s) is observable rather than implied.
/// </summary>
[GenerateSerializer, Immutable, Alias("leaderboard-snapshot")]
public sealed record LeaderboardSnapshot(
    [property: Id(0)] ImmutableArray<PlayerScore> Top,
    [property: Id(1)] DateTimeOffset ComputedAt)
{
    public static LeaderboardSnapshot Empty { get; } =
        new(ImmutableArray<PlayerScore>.Empty, DateTimeOffset.MinValue);
}
