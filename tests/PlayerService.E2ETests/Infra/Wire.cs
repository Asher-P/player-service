using System.Net;
using Xunit.Sdk;

namespace PlayerService.E2E;

// The wire contract as a *client* sees it, re-declared rather than referenced. If the server ever
// renames a field, these tests fail on the deserialized value instead of quietly following along.

/// <summary>Body of <c>POST /login</c>.</summary>
public sealed record LoginBody(string PlayerId, string Token, DateTimeOffset ExpiresAt);

/// <summary>Body of <c>GET /players/{id}/stats</c> and <c>POST /players/{id}/stats/score</c>.</summary>
public sealed record StatsBody(
    string PlayerId, int Score, int GiftsSent, int GiftsReceived, DateTimeOffset LastActive);

/// <summary>Body of <c>POST /players/{id}/gifts</c>.</summary>
public sealed record GiftBody(bool Applied, int SenderBalance, int RecipientBalance, bool Replayed);

/// <summary>Body of <c>GET /leaderboard</c>.</summary>
public sealed record LeaderboardBody(IReadOnlyList<LeaderboardRow> Top, DateTimeOffset ComputedAt);

/// <summary>One row of the leaderboard.</summary>
public sealed record LeaderboardRow(int Rank, string PlayerId, int Score);

/// <summary>RFC 7807 body returned by every rejection. <see cref="Title"/> is what distinguishes
/// the two different 409s (offline recipient vs. insufficient funds).</summary>
public sealed record ProblemBody(string? Title, string? Detail, int? Status);

/// <summary>A logged-in player: the identity plus the bearer token every later call carries.</summary>
public sealed record PlayerSession(string PlayerId, string DeviceId, string Token);

/// <summary>
/// One HTTP exchange: the status the client must branch on, the parsed body, and the raw text kept
/// for failure messages - an assertion that prints only "expected 200, got 409" is a wasted trip.
/// </summary>
public sealed record ApiResult<T>(HttpStatusCode Status, T? Body, ProblemBody? Problem, string Raw)
{
    /// <summary>Asserts <c>200 OK</c> and returns the body.</summary>
    public T Ok(string? because = null)
    {
        if (Status != HttpStatusCode.OK || Body is null)
        {
            throw new XunitException(
                $"expected 200 OK{Suffix(because)}, got {(int)Status} {Status}: {Raw}");
        }

        return Body;
    }

    /// <summary>Asserts a specific failure status and returns the problem body.</summary>
    public ProblemBody Failed(HttpStatusCode expected, string? because = null)
    {
        if (Status != expected)
        {
            throw new XunitException(
                $"expected {(int)expected} {expected}{Suffix(because)}, got {(int)Status} {Status}: {Raw}");
        }

        return Problem ?? new ProblemBody(null, null, (int)Status);
    }

    private static string Suffix(string? because) => because is null ? string.Empty : $" ({because})";
}
