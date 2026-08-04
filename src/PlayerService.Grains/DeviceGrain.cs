using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Runtime;
using PlayerService.Abstractions.Errors;
using PlayerService.Abstractions.Grains;
using PlayerService.Abstractions.Models;
using PlayerService.Abstractions.Sessions;
using PlayerService.Grains.Configuration;

namespace PlayerService.Grains;

/// <summary>
/// The login gate, one activation per deviceId. Deliberately left non-reentrant: serializing
/// concurrent logins for the same device is the whole point, and a single activation gives that
/// across the entire cluster - which a per-process <c>ConcurrentDictionary.TryAdd</c> never could.
/// </summary>
public sealed class DeviceGrain : Grain, IDeviceGrain
{
    private readonly IOptionsMonitor<PlayerSessionOptions> _sessionOptions;
    private readonly TimeProvider _time;
    private readonly ILogger<DeviceGrain> _logger;

    private SessionGrant? _current;

    public DeviceGrain(
        IOptionsMonitor<PlayerSessionOptions> sessionOptions,
        TimeProvider time,
        ILogger<DeviceGrain> logger)
    {
        _sessionOptions = sessionOptions;
        _time = time;
        _logger = logger;
    }

    private string DeviceId => this.GetPrimaryKeyString();

    public async Task<LoginOutcome> LoginAsync(string playerId)
    {
        var now = _time.GetUtcNow();

        // The gate. Expiry is lazy - evaluated here on read, never swept by a timer - so a crashed
        // device is not locked out forever, it simply stops sliding and lapses after the TTL.
        if (_current is not null && _current.ExpiresAt > now)
        {
            _logger.LogInformation(
                "Device {DeviceId} rejected a login for player {PlayerId}: session already active",
                DeviceId,
                playerId);

            return LoginOutcome.Rejected(LoginRejection.DeviceSessionActive);
        }

        var grant = new SessionGrant(
            playerId,
            DeviceId,
            SessionToken.Mint(playerId),
            now + _sessionOptions.CurrentValue.Ttl);

        // Claim the slot before awaiting anything. This grain is non-reentrant, so no other login
        // can interleave regardless - but ReleaseAsync is [AlwaysInterleave] and can, and a release
        // for this very grant may arrive while the bind below is in flight (another device is
        // already superseding us). Publishing the grant first means that release sees it.
        var displaced = _current;
        _current = grant;

        SessionGrant? superseded;
        try
        {
            // Tree-shaped: device -> player. The player grain never calls back into a device grain,
            // so there is no cycle to deadlock on.
            superseded = await GrainFactory.GetGrain<IPlayerGrain>(playerId).BindSessionAsync(grant);
        }
        catch
        {
            // The claim never became a session; do not leave the device gated on a phantom.
            if (ReferenceEquals(_current, grant))
            {
                _current = displaced;
            }

            throw;
        }

        // Supersede policy: this player was signed in elsewhere, so that device's session is now
        // dead and it must be told, or it would keep rejecting its own logins until the TTL ran out.
        if (superseded is not null && !string.Equals(superseded.DeviceId, DeviceId, StringComparison.Ordinal))
        {
            await GrainFactory.GetGrain<IDeviceGrain>(superseded.DeviceId).ReleaseAsync(superseded.Token);
        }

        _logger.LogInformation("Device {DeviceId} bound a session for player {PlayerId}", DeviceId, playerId);

        return LoginOutcome.Granted(grant);
    }

    public Task ReleaseAsync(string token)
    {
        if (_current is not null && string.Equals(_current.Token, token, StringComparison.Ordinal))
        {
            _current = null;
            _logger.LogInformation("Device {DeviceId} released its session", DeviceId);
        }

        return Task.CompletedTask;
    }
}
