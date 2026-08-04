using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;

namespace PlayerService.Api.Contracts;

// HTTP DTOs are deliberately separate from the grain models: the wire contract of the API and the
// wire contract of the cluster evolve for different reasons and must be free to diverge.
//
// With <Nullable>enable</Nullable>, [ApiController] treats non-nullable reference properties as
// required, so a missing field is an automatic 400 with ProblemDetails.

/// <summary>Body of <c>POST /login</c>.</summary>
public sealed record LoginRequest(string PlayerId, string DeviceId);

/// <summary>Response of <c>POST /login</c>. The token goes in the <c>X-Session-Token</c> header of
/// every subsequent request.</summary>
public sealed record LoginResponse(string PlayerId, string Token, DateTimeOffset ExpiresAt);

// Validation attributes go on the record's constructor *parameters*, not on the generated
// properties: MVC binds positional records through the primary constructor and throws
// InvalidOperationException if it finds validation metadata stranded on a property.

/// <summary>Body of <c>POST /players/{playerId}/stats/score</c>.</summary>
public sealed record ScoreRequest(
    [Range(1, int.MaxValue)] int Points,
    [MaxLength(128)] string RequestId);

/// <summary>Body of <c>POST /players/{playerId}/gifts</c>.</summary>
public sealed record GiftRequest(
    string ToPlayerId,
    [Range(1, int.MaxValue)] int Points,
    [MaxLength(128)] string RequestId);

/// <summary>Response of the stats and score endpoints.</summary>
public sealed record StatsResponse(
    string PlayerId,
    int Score,
    int GiftsSent,
    int GiftsReceived,
    DateTimeOffset LastActive);

/// <summary>Response of <c>POST /players/{playerId}/gifts</c>.</summary>
public sealed record GiftResponse(
    bool Applied,
    int SenderBalance,
    int RecipientBalance,
    bool Replayed);

/// <summary>Response of <c>GET /leaderboard</c>. <see cref="ComputedAt"/> exposes the snapshot's
/// age so the documented staleness bound is observable by the client.</summary>
public sealed record LeaderboardResponse(
    ImmutableArray<LeaderboardEntry> Top,
    DateTimeOffset ComputedAt);

/// <summary>One row of <see cref="LeaderboardResponse"/>.</summary>
public sealed record LeaderboardEntry(int Rank, string PlayerId, int Score);
