namespace PlayerService.Abstractions.Models;

/// <summary>
/// A single leaderboard entry. Always carries an <b>absolute</b> score, never a delta,
/// so that a redelivered or reordered stream event is harmless (see plan §2.4).
/// </summary>
[GenerateSerializer, Immutable, Alias("player-score")]
public sealed record PlayerScore(
    [property: Id(0)] string PlayerId,
    [property: Id(1)] int Score);
