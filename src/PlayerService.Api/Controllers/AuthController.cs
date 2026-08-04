using Microsoft.AspNetCore.Mvc;
using Orleans;
using PlayerService.Abstractions.Grains;
using PlayerService.Api.Auth;
using PlayerService.Api.Contracts;

namespace PlayerService.Api.Controllers;

/// <summary>
/// <c>POST /login</c> — the only endpoint reachable without a session token.
/// </summary>
[ApiController]
[Route("login")]
[AllowAnonymousSession]
public sealed class AuthController : ControllerBase
{
    private readonly IGrainFactory _grains;

    public AuthController(IGrainFactory grains) => _grains = grains;

    /// <summary>
    /// Routes to the device's own grain, whose single cluster-wide activation is the atomic gate
    /// that decides which of two simultaneous logins wins.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(LoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<LoginResponse>> LoginAsync(LoginRequest request)
    {
        var outcome = await _grains
            .GetGrain<IDeviceGrain>(request.DeviceId)
            .LoginAsync(request.PlayerId);

        if (!outcome.Accepted || outcome.Grant is null)
        {
            return Conflict(new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Login rejected",
                Detail = $"Device '{request.DeviceId}' already holds a live session ({outcome.Rejection}).",
            });
        }

        return Ok(new LoginResponse(
            outcome.Grant.PlayerId,
            outcome.Grant.Token,
            outcome.Grant.ExpiresAt));
    }
}
