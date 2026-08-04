namespace PlayerService.Grains.Internal;

/// <summary>
/// The player's transactional state. Balance, counters and the idempotency ledger move together
/// with a gift, so they must live in the <b>same</b> transactional record — that is what makes
/// "the ledger entry commits with the debit it describes" true rather than aspirational.
/// </summary>
[GenerateSerializer, Alias("player-balance-state")]
public sealed class PlayerBalanceState
{
    /// <summary>Every player starts with 1000 points; a fresh state object <i>is</i> the seed,
    /// so first-login seeding needs no "does this player exist" race.</summary>
    [Id(0)] public int Balance { get; set; } = 1000;

    [Id(1)] public int GiftsSent { get; set; }

    [Id(2)] public int GiftsReceived { get; set; }

    [Id(3)] public IdempotencyLedger Ledger { get; set; } = new();
}
