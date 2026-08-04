using System.Collections.Immutable;
using PlayerService.Abstractions.Models;

namespace PlayerService.Abstractions.Events;

/// <summary>
/// Published to <c>scores/global</c> after a balance change commits.
/// Carries one entry for a score add and <b>two</b> for a gift (sender lower, recipient higher),
/// so the leaderboard projection absorbs a paired update in a single, atomic-looking step.
/// Scores are absolute, which makes the projection idempotent under at-least-once delivery.
/// </summary>
[GenerateSerializer, Immutable, Alias("player-score-updated")]
public sealed record PlayerScoreUpdated(
    [property: Id(0)] ImmutableArray<PlayerScore> Scores);
