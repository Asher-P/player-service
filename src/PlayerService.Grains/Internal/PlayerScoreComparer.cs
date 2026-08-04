using PlayerService.Abstractions.Models;

namespace PlayerService.Grains.Internal;

/// <summary>
/// Leaderboard ordering: score descending, playerId ascending as a stable tie-breaker.
/// The tie-breaker matters — a <see cref="SortedSet{T}"/> treats "compares equal" as "same
/// element", so without it two players on the same score would collapse into one entry.
/// </summary>
public sealed class PlayerScoreComparer : IComparer<PlayerScore>
{
    public static PlayerScoreComparer Instance { get; } = new();

    public int Compare(PlayerScore? x, PlayerScore? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return 1;
        }

        if (y is null)
        {
            return -1;
        }

        var byScore = y.Score.CompareTo(x.Score);
        return byScore != 0 ? byScore : string.CompareOrdinal(x.PlayerId, y.PlayerId);
    }
}
