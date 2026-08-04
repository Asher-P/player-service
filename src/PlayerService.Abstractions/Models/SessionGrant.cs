namespace PlayerService.Abstractions.Models;

/// <summary>
/// The session a device grain minted and handed to the player grain to bind.
/// A player holds at most one grant at a time (plan §5.6: second device supersedes).
/// </summary>
[GenerateSerializer, Immutable, Alias("session-grant")]
public sealed record SessionGrant(
    [property: Id(0)] string PlayerId,
    [property: Id(1)] string DeviceId,
    [property: Id(2)] string Token,
    [property: Id(3)] DateTimeOffset ExpiresAt);
