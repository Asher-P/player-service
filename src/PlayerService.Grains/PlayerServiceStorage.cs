namespace PlayerService.Grains;

/// <summary>
/// Grain storage provider names. Declared once because they are referenced from two places that
/// must agree: the <c>[TransactionalState]</c> attributes here and the silo configuration.
/// </summary>
public static class PlayerServiceStorage
{
    /// <summary>Backing store for transactional grain state.</summary>
    public const string TransactionStore = "TransactionStore";

    /// <summary>Required by Orleans stream pub-sub.</summary>
    public const string PubSubStore = "PubSubStore";

    /// <summary>Default provider, for any plain <c>IPersistentState</c> added later.</summary>
    public const string Default = "Default";
}
