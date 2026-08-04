using PlayerService.Abstractions.Errors;

namespace PlayerService.Abstractions.Models;

/// <summary>Result of <see cref="Grains.IDeviceGrain.LoginAsync"/>.</summary>
[GenerateSerializer, Immutable, Alias("login-outcome")]
public sealed record LoginOutcome(
    [property: Id(0)] bool Accepted,
    [property: Id(1)] SessionGrant? Grant,
    [property: Id(2)] LoginRejection? Rejection)
{
    public static LoginOutcome Rejected(LoginRejection reason) => new(false, null, reason);

    public static LoginOutcome Granted(SessionGrant grant) => new(true, grant, null);
}
