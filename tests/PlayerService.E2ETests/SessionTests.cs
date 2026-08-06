using System.Net;
using Xunit;

namespace PlayerService.E2E;

/// <summary>
/// The login gate over HTTP: one live session per device, and the 409 that says so.
/// </summary>
[Collection(LiveServiceCollection.Name)]
public sealed class SessionTests
{
    private readonly PlayerServiceClient _api;

    public SessionTests(LiveServiceFixture fixture) => _api = fixture.Api;

    /// <summary>A second login on a device that already holds a live session is refused.</summary>
    [E2EFact]
    public async Task A_duplicate_deviceId_login_is_rejected()
    {
        var deviceId = PlayerServiceClient.NewId("device");
        var first = await _api.LoginAsync(PlayerServiceClient.NewId("player"), deviceId);
        var firstToken = first.Ok("the first login on a free device").Token;

        // Same device, different player: still refused - the device is the thing that is occupied.
        var otherPlayer = await _api.LoginAsync(PlayerServiceClient.NewId("player"), deviceId);
        otherPlayer.Failed(HttpStatusCode.Conflict, "a second player on a live device");

        // Same device, same player: also refused, because "already logged in" is a state conflict
        // and not something a client should paper over by retrying.
        var samePlayer = await _api.LoginAsync(first.Ok().PlayerId, deviceId);
        samePlayer.Failed(HttpStatusCode.Conflict, "the same player logging in twice on one device");

        // And the refusals changed nothing: the original session still authenticates.
        var session = new PlayerSession(first.Ok().PlayerId, deviceId, firstToken);
        Assert.Equal(PlayerServiceClient.SeedBalance, await _api.GetBalanceAsync(session));
    }

    /// <summary>
    /// The same thing under a race, which is the version that can actually go wrong: N logins on
    /// one device fired together must produce exactly one token. Two winners would mean two live
    /// sessions for one device, and the gift online-check would stop having a single answer.
    /// </summary>
    [E2EFact]
    public async Task Simultaneous_logins_on_one_device_yield_exactly_one_token()
    {
        const int racers = 16;

        var playerId = PlayerServiceClient.NewId("player");
        var deviceId = PlayerServiceClient.NewId("device");

        var responses = await Burst.FireAsync(racers, _ => _api.LoginAsync(playerId, deviceId));

        var winner = Assert.Single(responses, r => r.Status == HttpStatusCode.OK);
        Assert.Equal(racers - 1, responses.Count(r => r.Status == HttpStatusCode.Conflict));

        // The one token that was issued is the one that works.
        var session = new PlayerSession(playerId, deviceId, winner.Ok().Token);
        Assert.Equal(PlayerServiceClient.SeedBalance, await _api.GetBalanceAsync(session));
    }

    /// <summary>
    /// A different device for the same player is allowed and supersedes: the policy is one session
    /// per <i>player</i>, enforced at the device. The displaced token stops working at once, which
    /// is what makes supersede a policy rather than a label.
    /// </summary>
    [E2EFact]
    public async Task A_second_device_supersedes_the_first_and_the_old_token_stops_working()
    {
        var player = await _api.NewPlayerAsync();
        var moved = await _api.LoginFromNewDeviceAsync(player.PlayerId);

        var stale = await _api.GetStatsAsync(player);
        stale.Failed(HttpStatusCode.Unauthorized, "the superseded token");

        Assert.Equal(PlayerServiceClient.SeedBalance, await _api.GetBalanceAsync(moved));
    }

    /// <summary>A token is a key to one player, not to every player.</summary>
    [E2EFact]
    public async Task A_token_cannot_read_another_players_stats()
    {
        var player = await _api.NewPlayerAsync();
        var other = await _api.NewPlayerAsync();

        var impersonated = new PlayerSession(other.PlayerId, player.DeviceId, player.Token);

        (await _api.GetStatsAsync(impersonated))
            .Failed(HttpStatusCode.Forbidden, "one player's token on another player's route");
    }
}
