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

    /// <summary>Total attempts before the request surfaces as 503.</summary>
    [Range(1, 10)]
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Base backoff between attempts; jitter is added per attempt.</summary>
    [Range(typeof(TimeSpan), "00:00:00.001", "00:00:05")]
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMilliseconds(25);
}
