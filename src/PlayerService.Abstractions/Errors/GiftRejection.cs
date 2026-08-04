namespace PlayerService.Abstractions.Errors;

/// <summary>Terminal (non-retryable-as-is) reasons a gift was not applied.</summary>
[GenerateSerializer, Alias("gift-rejection")]
public enum GiftRejection
{
    /// <summary>Recipient had no live session at the transaction's commit point.</summary>
    Offline = 0,

    /// <summary>Sender's balance would have gone negative.</summary>
    InsufficientFunds = 1,

    /// <summary>Sender and recipient are the same player.</summary>
    SelfGift = 2,

    /// <summary>Recipient has never logged in, so the player does not exist.</summary>
    UnknownRecipient = 3,
}
