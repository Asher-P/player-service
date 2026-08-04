using Microsoft.AspNetCore.Mvc;
using Orleans;
using PlayerService.Abstractions.Grains;
using PlayerService.Abstractions.Models;
using PlayerService.Api.Auth;
using PlayerService.Api.Contracts;
using PlayerService.Api.Services;

namespace PlayerService.Api.Controllers;

/// <summary>
/// Per-player endpoints. Every action addresses exactly one grain, so a hot player serializes only
/// itself: reads of one player never queue behind writes to another.
/// </summary>
[ApiController]
[Route("players/{playerId}")]
public sealed class PlayersController : ControllerBase
{
    private readonly IGrainFactory _grains;
    private readonly GiftService _gifts;

    public PlayersController(IGrainFactory grains, GiftService gifts)
    {
        _grains = grains;
        _gifts = gifts;
    }

    [HttpGet("stats")]
    [ProducesResponseType(typeof(StatsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<StatsResponse>> GetStatsAsync(string playerId)
    {
        if (OwnershipFailure(playerId) is { } failure)
        {
            return failure;
        }

        var stats = await _grains.GetGrain<IPlayerGrain>(playerId).GetStatsAsync();
        return Ok(ToResponse(stats));
    }

    [HttpPost("stats/score")]
    [ProducesResponseType(typeof(StatsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<StatsResponse> AddScore(string playerId, ScoreRequest request)
    {
        if (OwnershipFailure(playerId) is { } failure)
        {
            return failure;
        }

        // TODO(Phase 3): var outcome = await _grains.GetGrain<IPlayerGrain>(playerId)
        //     .AddPointsAsync(request.Points, request.RequestId);
        //   return Ok(ToResponse(outcome.Stats));
        // A replay returns the stored original outcome, so this endpoint is retry-safe.
        return NotImplementedYet("Atomic, idempotent score updates land in Phase 3.");
    }

    [HttpPost("gifts")]
    [ProducesResponseType(typeof(GiftResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<GiftResponse> SendGift(string playerId, GiftRequest request)
    {
        if (OwnershipFailure(playerId) is { } failure)
        {
            return failure;
        }

        // TODO(Phase 4): var outcome = await _gifts.SendGiftAsync(
        //       playerId, request.ToPlayerId, request.Points, request.RequestId, ct);
        //   map: applied -> 200; SelfGift -> 400; Offline/InsufficientFunds -> 409;
        //        UnknownRecipient -> 404; retries exhausted -> 503.
        return NotImplementedYet("Gifting via Orleans transactions lands in Phase 4.");
    }

    /// <summary>
    /// A token authenticates one player, and that player may only act on their own resource.
    /// Without this, a valid token would be a key to every player's state.
    /// </summary>
    private ObjectResult? OwnershipFailure(string playerId)
    {
        var authenticated = HttpContext.Items[SessionAuthFilter.PlayerIdItem] as string;

        if (string.Equals(authenticated, playerId, StringComparison.Ordinal))
        {
            return null;
        }

        return new ObjectResult(new ProblemDetails
        {
            Status = StatusCodes.Status403Forbidden,
            Title = "Forbidden",
            Detail = "The session token does not belong to the player in the route.",
        })
        {
            StatusCode = StatusCodes.Status403Forbidden,
        };
    }

    private static StatsResponse ToResponse(PlayerStats stats) => new(
        stats.PlayerId,
        stats.Balance,
        stats.GiftsSent,
        stats.GiftsReceived,
        stats.LastActive);

    private ObjectResult NotImplementedYet(string detail) =>
        new(new ProblemDetails
        {
            Status = StatusCodes.Status501NotImplemented,
            Title = "Not implemented in Phase 1",
            Detail = detail,
        })
        {
            StatusCode = StatusCodes.Status501NotImplemented,
        };
}
