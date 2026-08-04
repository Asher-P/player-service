using Orleans.Runtime;

namespace PlayerService.Abstractions.Streaming;

/// <summary>
/// Stream identity is shared by producers (player grains), the projection
/// (<c>LeaderboardGrain</c>) and pod-local consumers (<c>LeaderboardCache</c>),
/// so it is declared exactly once here.
/// </summary>
public static class PlayerServiceStreams
{
    /// <summary>The single memory stream provider configured on the silo.</summary>
    public const string ProviderName = "scores";

    /// <summary>Namespace/key of the score-ingest stream: <c>scores/global</c>.</summary>
    public const string ScoresNamespace = "scores";
    public const string ScoresKey = "global";

    /// <summary>Namespace/key of the Top-N broadcast stream: <c>leaderboard/top</c>.</summary>
    public const string LeaderboardNamespace = "leaderboard";
    public const string LeaderboardKey = "top";

    public static StreamId Scores { get; } = StreamId.Create(ScoresNamespace, ScoresKey);

    public static StreamId Leaderboard { get; } = StreamId.Create(LeaderboardNamespace, LeaderboardKey);
}
