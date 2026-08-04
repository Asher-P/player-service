namespace PlayerService.Abstractions.Sessions;

/// <summary>
/// Token format is <c>{playerId}.{guid}</c> so the API layer can recover the playerId
/// locally and make exactly <b>one</b> grain call to validate — no token-lookup grain,
/// no directory scan (plan §5.6).
/// </summary>
public static class SessionToken
{
    private const char Separator = '.';

    public static string Mint(string playerId) => $"{playerId}{Separator}{Guid.NewGuid():N}";

    /// <summary>Recovers the playerId a token claims to belong to. Claiming is not proof —
    /// only the player grain can confirm the token is the live one.</summary>
    public static bool TryGetPlayerId(string? token, out string playerId)
    {
        playerId = string.Empty;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var separator = token.LastIndexOf(Separator);
        if (separator <= 0 || separator == token.Length - 1)
        {
            return false;
        }

        playerId = token[..separator];
        return true;
    }
}
