using System.ComponentModel.DataAnnotations;

namespace PlayerService.Grains.Configuration;

/// <summary>Bounds on the per-player idempotency ledger (plan §5.3).</summary>
public sealed class IdempotencyOptions
{
    public const string SectionName = "Idempotency";

    /// <summary>FIFO cap per player. Keeps the transactional state payload bounded.</summary>
    [Range(16, 4096)]
    public int MaxEntriesPerPlayer { get; set; } = 256;

    /// <summary>Record lifetime. Must vastly exceed the client retry window (seconds).</summary>
    [Range(typeof(TimeSpan), "00:00:30", "24:00:00")]
    public TimeSpan Ttl { get; set; } = TimeSpan.FromMinutes(10);
}
