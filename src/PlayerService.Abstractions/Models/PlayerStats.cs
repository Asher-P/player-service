namespace PlayerService.Abstractions.Models;

/// <summary>
/// Read model returned by <c>GET /players/{playerId}/stats</c>.
/// </summary>
[GenerateSerializer, Immutable, Alias("player-stats")]
public sealed record PlayerStats(
    [property: Id(0)] string PlayerId,
    [property: Id(1)] int Balance,
    [property: Id(2)] int GiftsSent,
    [property: Id(3)] int GiftsReceived,
    [property: Id(4)] DateTimeOffset LastActive);
