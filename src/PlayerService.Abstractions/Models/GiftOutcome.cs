using PlayerService.Abstractions.Errors;

namespace PlayerService.Abstractions.Models;

/// <summary>
/// Result of a gift attempt. Terminal rejections are outcomes, not exceptions, so that a
/// replayed requestId can return exactly what the original attempt returned.
/// </summary>
[GenerateSerializer, Immutable, Alias("gift-outcome")]
public sealed record GiftOutcome(
    [property: Id(0)] bool Applied,
    [property: Id(1)] GiftRejection? Rejection,
    [property: Id(2)] int SenderBalance,
    [property: Id(3)] int RecipientBalance,
    [property: Id(4)] bool Replayed = false);
