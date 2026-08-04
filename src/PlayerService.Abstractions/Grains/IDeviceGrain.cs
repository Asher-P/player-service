using Orleans;
using Orleans.Concurrency;
using PlayerService.Abstractions.Models;

namespace PlayerService.Abstractions.Grains;

/// <summary>
/// One activation per deviceId. Its single activation is the atomic gate that
/// <c>ConcurrentDictionary.TryAdd</c> used to be — except it now holds across the whole cluster.
/// Deliberately <b>not</b> reentrant: serializing logins is its entire job.
/// </summary>
[Alias("IDeviceGrain")]
public interface IDeviceGrain : IGrainWithStringKey
{
    /// <summary>
    /// Mints a session for <paramref name="playerId"/> on this device, or rejects when this device
    /// already holds a live one (HTTP 409). Two identical simultaneous logins are serialized into
    /// two turns, so exactly one can win.
    /// </summary>
    Task<LoginOutcome> LoginAsync(string playerId);

    /// <summary>
    /// Releases this device's session when the player logged in from another device
    /// (supersede policy, plan §5.6). No-op if the token is not the one currently held.
    /// </summary>
    /// <remarks>
    /// <c>[AlwaysInterleave]</c> is load-bearing, not decorative. The superseding device awaits
    /// this call, and two devices can supersede each other simultaneously (A takes over B's player
    /// while B takes over A's). If this method queued behind that device's own in-flight
    /// <see cref="LoginAsync"/>, those two grains would wait on each other - a genuine deadlock.
    /// Interleaving is safe here for the same reason as the player's session methods: the body is
    /// a single-turn plain-field write with no <c>await</c>.
    /// </remarks>
    [AlwaysInterleave]
    Task ReleaseAsync(string token);
}
