using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Orleans;
using PlayerService.Abstractions.Grains;
using PlayerService.Abstractions.Sessions;

namespace PlayerService.Api.Auth;

/// <summary>Marks an action or controller as reachable without a session token (i.e. login).</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class AllowAnonymousSessionAttribute : Attribute;

/// <summary>
/// Turns <c>X-Session-Token</c> into an authenticated playerId in exactly <b>one</b> grain call:
/// the token carries its playerId, so the filter resolves the right activation directly — no
/// token-lookup grain and no directory scan. The same call slides the sliding TTL.
/// </summary>
/// <remarks>
/// It is safe for this to run on a hot player: <c>ValidateAndSlideSessionAsync</c> is
/// <c>[AlwaysInterleave]</c>, so authentication never queues behind that player's in-flight
/// score or gift transactions.
/// </remarks>
public sealed class SessionAuthFilter : IAsyncAuthorizationFilter
{
    public const string HeaderName = "X-Session-Token";

    /// <summary>Key under which the authenticated playerId is stashed for the action to read.</summary>
    public const string PlayerIdItem = "playerId";

    private readonly IGrainFactory _grains;
    private readonly ILogger<SessionAuthFilter> _logger;

    public SessionAuthFilter(IGrainFactory grains, ILogger<SessionAuthFilter> logger)
    {
        _grains = grains;
        _logger = logger;
    }

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        if (context.ActionDescriptor.EndpointMetadata.OfType<AllowAnonymousSessionAttribute>().Any())
        {
            return;
        }

        var token = context.HttpContext.Request.Headers[HeaderName].ToString();

        if (!SessionToken.TryGetPlayerId(token, out var playerId))
        {
            // Logged with the reason rather than left to the request log's bare 401: a malformed
            // token is a client that never had one, and an expired token is a client that did.
            // Which of the two is happening decides whether anyone needs to act.
            _logger.LogWarning(
                "Rejected {Method} {Path}: missing or malformed session token",
                context.HttpContext.Request.Method, context.HttpContext.Request.Path);

            context.Result = Unauthorized("Missing or malformed session token.");
            return;
        }

        var valid = await _grains.GetGrain<IPlayerGrain>(playerId).ValidateAndSlideSessionAsync(token);
        if (!valid)
        {
            _logger.LogWarning(
                "Rejected {Method} {Path} for player {PlayerId}: session token expired, superseded or unknown",
                context.HttpContext.Request.Method, context.HttpContext.Request.Path, playerId);

            context.Result = Unauthorized("Session token is expired, superseded or unknown.");
            return;
        }

        context.HttpContext.Items[PlayerIdItem] = playerId;
    }

    private static UnauthorizedObjectResult Unauthorized(string detail) =>
        new(new ProblemDetails
        {
            Status = StatusCodes.Status401Unauthorized,
            Title = "Unauthenticated",
            Detail = detail,
        });
}
