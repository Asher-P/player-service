using PlayerService.Abstractions.Models;

namespace PlayerService.Abstractions.Errors;

/// <summary>
/// Thrown from inside a gift transaction to abort it with a terminal reason. Aborting is the point:
/// the debit and the credit are one unit, so refusing on the recipient's activation is what
/// guarantees nothing was taken from the sender (plan §5.4).
/// </summary>
/// <remarks>
/// A rejection is an <i>outcome</i> to the caller and an <i>exception</i> only to the transaction —
/// <see cref="Services"/>-layer code converts it back into a <see cref="GiftOutcome"/>, which is
/// what lets a replayed requestId return exactly what the original attempt returned.
/// </remarks>
[GenerateSerializer]
public sealed class GiftRejectedException : Exception
{
    public GiftRejectedException(GiftRejection rejection, string message)
        : base(message) => Rejection = rejection;

    [Id(0)]
    public GiftRejection Rejection { get; }
}

/// <summary>
/// Thrown when the sender's ledger already holds this requestId. It carries the original outcome so
/// the replay returns byte-for-byte what the first attempt returned, and it aborts the transaction
/// so the duplicate moves no points.
/// </summary>
[GenerateSerializer]
public sealed class GiftReplayException : Exception
{
    public GiftReplayException(GiftOutcome outcome)
        : base("This gift requestId has already been applied.") => Outcome = outcome;

    [Id(0)]
    public GiftOutcome Outcome { get; }
}
