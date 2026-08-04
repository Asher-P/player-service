namespace PlayerService.Abstractions.Models;

/// <summary>
/// Result of <see cref="Grains.IPlayerGrain.AddPointsAsync"/>.
/// <paramref name="Replayed"/> is <c>true</c> when the idempotency ledger already held this
/// requestId, i.e. the stats are the <i>original</i> outcome replayed byte-for-byte.
/// </summary>
[GenerateSerializer, Immutable, Alias("score-outcome")]
public sealed record ScoreOutcome(
    [property: Id(0)] PlayerStats Stats,
    [property: Id(1)] bool Replayed);
