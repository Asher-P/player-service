using System.Collections.Immutable;
using Microsoft.AspNetCore.Mvc;
using PlayerService.Api.Contracts;
using PlayerService.Api.Services;

namespace PlayerService.Api.Controllers;

/// <summary>
/// <c>GET /leaderboard</c> — served entirely from pod-local memory. No grain call, no network hop,
/// no sort: the snapshot was already computed by the leaderboard grain and pushed here.
/// </summary>
[ApiController]
[Route("leaderboard")]
public sealed class LeaderboardController : ControllerBase
{
    private readonly LeaderboardCache _cache;

    public LeaderboardController(LeaderboardCache cache) => _cache = cache;

    [HttpGet]
    [ProducesResponseType(typeof(LeaderboardResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult<LeaderboardResponse> GetLeaderboard()
    {
        var snapshot = _cache.Current;

        var rows = ImmutableArray.CreateBuilder<LeaderboardEntry>(snapshot.Top.Length);
        for (var i = 0; i < snapshot.Top.Length; i++)
        {
            rows.Add(new LeaderboardEntry(i + 1, snapshot.Top[i].PlayerId, snapshot.Top[i].Score));
        }

        return Ok(new LeaderboardResponse(rows.ToImmutable(), snapshot.ComputedAt));
    }
}
