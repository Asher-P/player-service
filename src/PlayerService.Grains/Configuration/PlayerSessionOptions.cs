using System.ComponentModel.DataAnnotations;

namespace PlayerService.Grains.Configuration;

/// <summary>Session liveness policy (plan section 5.6). Bound from the <c>Sessions</c> section.</summary>
public sealed class PlayerSessionOptions
{
    public const string SectionName = "Sessions";

    /// <summary>
    /// Sliding TTL. Any authenticated request slides the expiry; a crashed device simply stops
    /// sliding and its session lapses. Must stay comfortably above 60s so an idle-for-a-minute
    /// device keeps its session.
    /// </summary>
    [Range(typeof(TimeSpan), "00:01:00", "01:00:00")]
    public TimeSpan Ttl { get; set; } = TimeSpan.FromMinutes(3);
}
