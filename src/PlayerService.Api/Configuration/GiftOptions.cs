using System.ComponentModel.DataAnnotations;

namespace PlayerService.Api.Configuration;

/// <summary>
/// Retry policy for gift transactions (plan §5.4). Contention on the same pair resolves by
/// <b>abort</b>, never by blocking, so a bounded retry is the correct response — and it is safe
/// precisely because the operation is idempotent: if the first attempt actually committed, the
/// retry finds the ledger entry and returns the original outcome.
/// </summary>
public sealed class GiftOptions
{
    public const string SectionName = "Gifting";

    /// <summary>
    /// Total attempts before the request surfaces as 503. Aborts are the <i>expected</i> resolution
    /// of a contended pair rather than a failure signal, so the budget is sized for a burst on one
    /// hot pair, not for a one-off glitch.
    /// </summary>
    [Range(1, 16)]
    public int MaxAttempts { get; set; } = 8;

    /// <summary>Base backoff between attempts; grows exponentially and is fully jittered, so
    /// retries of the same contended pair spread out instead of colliding again in lockstep.</summary>
    [Range(typeof(TimeSpan), "00:00:00.001", "00:00:05")]
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMilliseconds(100);
}
