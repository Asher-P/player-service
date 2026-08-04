using PlayerService.Abstractions.Models;

namespace PlayerService.Grains.Internal;

/// <summary>
/// A single applied request, keyed <c>score:{requestId}</c> or <c>gift:{requestId}</c>.
/// The stored value is the <b>exact outcome the original attempt returned</b>, so a replay is
/// byte-for-byte stable rather than merely "also successful".
/// </summary>
[GenerateSerializer, Alias("ledger-entry")]
public sealed class LedgerEntry
{
    [Id(0)] public DateTimeOffset RecordedAt { get; set; }

    [Id(1)] public ScoreOutcome? Score { get; set; }

    [Id(2)] public GiftOutcome? Gift { get; set; }
}

/// <summary>
/// Bounded per-player idempotency record set, living <b>inside</b> the grain's transactional state
/// so an entry commits (or rolls back) together with the effect it describes.
/// </summary>
/// <remarks>
/// Bounding (plan §5.3): FIFO capped at <c>maxEntries</c> with a TTL, pruned lazily on write —
/// no per-player timer, because millions of grain timers is itself an anti-pattern. Once a record
/// is gone, a late replay is treated as new; the TTL vastly exceeds the client retry window.
/// </remarks>
[GenerateSerializer, Alias("idempotency-ledger")]
public sealed class IdempotencyLedger
{
    [Id(0)] public Dictionary<string, LedgerEntry> Entries { get; set; } = new();

    /// <summary>Insertion order, oldest first — the FIFO eviction order.</summary>
    [Id(1)] public List<string> Order { get; set; } = new();

    public bool TryGet(string key, out LedgerEntry entry) => Entries.TryGetValue(key, out entry!);

    public void Record(string key, LedgerEntry entry)
    {
        if (Entries.TryAdd(key, entry))
        {
            Order.Add(key);
        }
        else
        {
            Entries[key] = entry;
        }
    }

    /// <summary>Drops TTL-expired entries, then trims oldest-first down to <paramref name="maxEntries"/>.</summary>
    public void Prune(DateTimeOffset now, int maxEntries, TimeSpan ttl)
    {
        var cutoff = now - ttl;
        var write = 0;

        for (var read = 0; read < Order.Count; read++)
        {
            var key = Order[read];
            var alive = Entries.TryGetValue(key, out var entry) && entry.RecordedAt > cutoff;
            var overCap = Order.Count - read > maxEntries - write;

            if (alive && !overCap)
            {
                Order[write++] = key;
            }
            else
            {
                Entries.Remove(key);
            }
        }

        Order.RemoveRange(write, Order.Count - write);
    }
}
