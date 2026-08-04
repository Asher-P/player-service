using System.ComponentModel.DataAnnotations;

namespace PlayerService.Grains.Configuration;

/// <summary>Leaderboard size and broadcast cadence (plan §5.5).</summary>
public sealed class LeaderboardOptions
{
    public const string SectionName = "Leaderboard";

    /// <summary>How many entries the Top-N broadcast carries.</summary>
    [Range(1, 10_000)]
    public int TopN { get; set; } = 100;

    /// <summary>
    /// Coalescing window for the Top-N broadcast. Read staleness is bounded by
    /// stream delivery + this interval.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:00.010", "00:01:00")]
    public TimeSpan BroadcastInterval { get; set; } = TimeSpan.FromSeconds(1);
}
