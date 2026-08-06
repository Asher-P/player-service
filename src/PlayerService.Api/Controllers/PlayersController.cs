using Microsoft.AspNetCore.Mvc;
using Orleans;
using Orleans.Transactions;
using PlayerService.Abstractions.Errors;
using PlayerService.Abstractions.Grains;
using PlayerService.Abstractions.Models;
using PlayerService.Api.Auth;
using PlayerService.Api.Contracts;
using PlayerService.Api.Observability;
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
    private readonly PlayerServiceMetrics _metrics;

    public PlayersController(IGrainFactory grains, GiftService gifts, PlayerServiceMetrics metrics)
    {
        _grains = grains;
        _gifts = gifts;
        _metrics = metrics;
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
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<StatsResponse>> AddScoreAsync(string playerId, ScoreRequest request)
    {
        if (OwnershipFailure(playerId) is { } failure)
        {
            return failure;
        }

        try
        {
            // A replay returns the stored original outcome byte-for-byte, so this endpoint is
            // retry-safe: the same requestId always yields the same response.
            var outcome = await _grains.GetGrain<IPlayerGrain>(playerId)
                .AddPointsAsync(request.Points, request.RequestId);

            // Replay rate is the cheapest available read on how chatty the clients actually are.
            _metrics.ScoreApplied(outcome.Replayed);
            return Ok(ToResponse(outcome.Stats));
        }
        catch (OrleansTransactionAbortedException)
        {
            return TransactionAborted();
        }
    }

    [HttpPost("gifts")]
    [ProducesResponseType(typeof(GiftResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<GiftResponse>> SendGiftAsync(
        string playerId,
        GiftRequest request,
        CancellationToken cancellationToken)
    {
        if (OwnershipFailure(playerId) is { } failure)
        {
            return failure;
        }

        GiftOutcome outcome;
        try
        {
            outcome = await _gifts.SendGiftAsync(
                playerId, request.ToPlayerId, request.Points, request.RequestId, cancellationToken);
        }
        catch (OrleansTransactionAbortedException)
        {
            return TransactionAborted();
        }

        if (outcome.Applied)
        {
            return Ok(new GiftResponse(true, outcome.SenderBalance, outcome.RecipientBalance, outcome.Replayed));
        }

        // The rejection reason is what tells the client whether retrying could ever help: a self-gift
        // never can, an offline recipient or a short balance might once that changes.
        return outcome.Rejection switch
        {
            GiftRejection.SelfGift => Rejected(
                StatusCodes.Status400BadRequest, "Self-gift", "A player cannot gift points to themselves."),
            GiftRejection.UnknownRecipient => Rejected(
                StatusCodes.Status404NotFound, "Unknown recipient", "The recipient has never logged in."),
            GiftRejection.Offline => Rejected(
                StatusCodes.Status409Conflict, "Recipient offline", "The recipient had no live session; no points moved."),
            GiftRejection.InsufficientFunds => Rejected(
                StatusCodes.Status409Conflict, "Insufficient funds", "The sender's balance is short of the points requested."),
            _ => Rejected(StatusCodes.Status409Conflict, "Gift rejected", "The gift was not applied."),
        };
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

    private static ObjectResult Rejected(int statusCode, string title, string detail) =>
        new(new ProblemDetails { Status = statusCode, Title = title, Detail = detail })
        {
            StatusCode = statusCode,
        };

    /// <summary>Transient cluster contention, not a client bug - safe to retry with backoff because
    /// the operation is idempotent (plan §5.7).</summary>
    private ObjectResult TransactionAborted() =>
        new(new ProblemDetails
        {
            Status = StatusCodes.Status503ServiceUnavailable,
            Title = "Transaction aborted",
            Detail = "The update conflicted with concurrent activity on this player. Retry with the same requestId.",
        })
        {
            StatusCode = StatusCodes.Status503ServiceUnavailable,
        };
}
