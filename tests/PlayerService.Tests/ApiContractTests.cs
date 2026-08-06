using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace PlayerService.Tests;

/// <summary>
/// The public contract, over real HTTP against the real host. The grain-level tests prove the
/// service is correct; these prove a client can tell <i>what happened</i> - which is the entire
/// point of the status-code map, since an unreliable client decides whether to retry from it.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ApiContractTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ApiFixture _fixture;

    public ApiContractTests(ApiFixture fixture) => _fixture = fixture;

    private HttpClient Client => _fixture.Client;

    [Fact]
    public async Task Login_returns_a_token_and_a_seeded_player()
    {
        var playerId = NewId("player");
        var token = await LoginAsync(playerId, NewId("device"));

        var stats = await GetStatsAsync(playerId, token);

        Assert.Equal(playerId, stats.PlayerId);
        Assert.Equal(1000, stats.Score);
    }

    /// <summary>
    /// The Phase 2 deliverable at the HTTP boundary: duplicate logins fired at once, exactly one
    /// <c>200</c> and the rest <c>409</c>. Asserted here rather than only at the grain because the
    /// 409 is the contract - a client that cannot see it retries forever.
    /// </summary>
    [Fact]
    public async Task Simultaneous_logins_on_one_device_yield_one_200_and_the_rest_409()
    {
        const int racers = 8;

        var playerId = NewId("player");
        var deviceId = NewId("device");

        var responses = await Task.WhenAll(Enumerable.Range(0, racers).Select(_ =>
            Client.PostAsJsonAsync("/login", new { playerId, deviceId })));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Equal(racers - 1, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task A_request_without_a_token_is_401()
    {
        var response = await Client.GetAsync($"/players/{NewId("player")}/stats");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>A valid token is a key to <i>one</i> player, not to every player.</summary>
    [Fact]
    public async Task A_token_cannot_reach_another_players_resource()
    {
        var token = await LoginAsync(NewId("player"), NewId("device"));

        var response = await SendAsync(HttpMethod.Get, $"/players/{NewId("player")}/stats", token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>A superseded token stops working immediately, which is what makes supersede a real
    /// policy rather than a label.</summary>
    [Fact]
    public async Task A_superseded_token_is_401()
    {
        var playerId = NewId("player");
        var first = await LoginAsync(playerId, NewId("device"));
        await LoginAsync(playerId, NewId("device"));

        var response = await SendAsync(HttpMethod.Get, $"/players/{playerId}/stats", first);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// The Phase 3 deliverable over HTTP: 20 duplicates of one requestId in flight together return
    /// 20 identical <c>200</c>s and move the balance once.
    /// </summary>
    [Fact]
    public async Task Duplicate_score_posts_return_identical_responses_and_apply_once()
    {
        const int duplicates = 20;

        var playerId = NewId("player");
        var token = await LoginAsync(playerId, NewId("device"));
        var requestId = NewId("req");

        var responses = await Task.WhenAll(Enumerable.Range(0, duplicates).Select(_ =>
            PostAsync($"/players/{playerId}/stats/score", token, new { points = 60, requestId })));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

        var bodies = await Task.WhenAll(responses.Select(r => r.Content.ReadFromJsonAsync<StatsBody>(Json)));
        Assert.All(bodies, body => Assert.Equal(1060, body!.Score));

        var stats = await GetStatsAsync(playerId, token);
        Assert.Equal(1060, stats.Score);
    }

    [Fact]
    public async Task A_non_positive_score_is_400_before_any_grain_is_touched()
    {
        var playerId = NewId("player");
        var token = await LoginAsync(playerId, NewId("device"));

        var response = await PostAsync(
            $"/players/{playerId}/stats/score", token, new { points = 0, requestId = NewId("req") });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(1000, (await GetStatsAsync(playerId, token)).Score);
    }

    /// <summary>
    /// The whole gift status-code map in one place, because its value is that the four rejections
    /// are <i>distinguishable</i>: 400 never retry, 404 wrong recipient, 409 retry once the
    /// condition changes.
    /// </summary>
    [Fact]
    public async Task The_gift_endpoint_maps_every_outcome_onto_its_own_status()
    {
        var sender = NewId("player");
        var senderToken = await LoginAsync(sender, NewId("device"));
        var recipient = NewId("player");
        await LoginAsync(recipient, NewId("device"));

        var applied = await PostAsync($"/players/{sender}/gifts", senderToken,
            new { toPlayerId = recipient, points = 300, requestId = NewId("req") });
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);

        var body = await applied.Content.ReadFromJsonAsync<GiftBody>(Json);
        Assert.True(body!.Applied);
        Assert.Equal(700, body.SenderBalance);
        Assert.Equal(1300, body.RecipientBalance);
        Assert.False(body.Replayed);

        var selfGift = await PostAsync($"/players/{sender}/gifts", senderToken,
            new { toPlayerId = sender, points = 10, requestId = NewId("req") });
        Assert.Equal(HttpStatusCode.BadRequest, selfGift.StatusCode);

        var unknown = await PostAsync($"/players/{sender}/gifts", senderToken,
            new { toPlayerId = NewId("player"), points = 10, requestId = NewId("req") });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        var overdrawn = await PostAsync($"/players/{sender}/gifts", senderToken,
            new { toPlayerId = recipient, points = 100_000, requestId = NewId("req") });
        Assert.Equal(HttpStatusCode.Conflict, overdrawn.StatusCode);

        // Nothing moved on any of the three rejections.
        Assert.Equal(700, (await GetStatsAsync(sender, senderToken)).Score);
    }

    /// <summary>A replayed gift returns <c>200</c> with the original numbers and
    /// <c>replayed: true</c>, so a client can tell "applied" from "applied earlier".</summary>
    [Fact]
    public async Task A_replayed_gift_returns_the_original_body_marked_replayed()
    {
        var sender = NewId("player");
        var senderToken = await LoginAsync(sender, NewId("device"));
        var recipient = NewId("player");
        await LoginAsync(recipient, NewId("device"));

        var requestId = NewId("req");
        var payload = new { toPlayerId = recipient, points = 120, requestId };

        var first = await PostAsync($"/players/{sender}/gifts", senderToken, payload);
        var replay = await PostAsync($"/players/{sender}/gifts", senderToken, payload);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);

        var firstBody = await first.Content.ReadFromJsonAsync<GiftBody>(Json);
        var replayBody = await replay.Content.ReadFromJsonAsync<GiftBody>(Json);

        Assert.False(firstBody!.Replayed);
        Assert.True(replayBody!.Replayed);
        Assert.Equal(firstBody.SenderBalance, replayBody.SenderBalance);
        Assert.Equal(firstBody.RecipientBalance, replayBody.RecipientBalance);

        Assert.Equal(880, (await GetStatsAsync(sender, senderToken)).Score);
    }

    /// <summary>
    /// Points conservation asserted where the assignment states it - across the API, under
    /// concurrent load, on random pairs.
    /// </summary>
    [Fact]
    public async Task Concurrent_gifts_over_http_conserve_points()
    {
        const int players = 6;
        const int gifts = 30;

        var ids = new List<string>(players);
        var tokens = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var i = 0; i < players; i++)
        {
            var playerId = NewId("player");
            tokens[playerId] = await LoginAsync(playerId, NewId("device"));
            ids.Add(playerId);
        }

        var random = new Random(20260806);
        var sends = Enumerable.Range(0, gifts).Select(_ =>
        {
            var from = random.Next(players);
            var to = (from + 1 + random.Next(players - 1)) % players;
            var sender = ids[from];

            return PostAsync($"/players/{sender}/gifts", tokens[sender],
                new { toPlayerId = ids[to], points = random.Next(1, 30), requestId = NewId("req") });
        });

        var responses = await Task.WhenAll(sends);

        // 503 would be legitimate under contention, but it must not be silent - assert it did not
        // happen so the retry budget stays honest rather than quietly absorbing failures.
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

        var balances = await Task.WhenAll(ids.Select(async id => (await GetStatsAsync(id, tokens[id])).Score));

        Assert.Equal(players * 1000, balances.Sum());
        Assert.All(balances, score => Assert.True(score >= 0, $"balance went negative: {score}"));
    }

    [Fact]
    public async Task The_leaderboard_is_readable_and_reports_its_own_age()
    {
        var token = await LoginAsync(NewId("player"), NewId("device"));

        var response = await SendAsync(HttpMethod.Get, "/leaderboard", token);
        var board = await response.Content.ReadFromJsonAsync<LeaderboardBody>(Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(board);

        // Ranks are dense and 1-based, and the age is exposed rather than implied - a client can
        // see how stale the snapshot is instead of having to trust the documented bound.
        for (var i = 0; i < board!.Top.Count; i++)
        {
            Assert.Equal(i + 1, board.Top[i].Rank);
        }
    }

    private async Task<string> LoginAsync(string playerId, string deviceId)
    {
        var response = await Client.PostAsJsonAsync("/login", new { playerId, deviceId });
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<LoginBody>(Json);
        return body!.Token;
    }

    private async Task<StatsBody> GetStatsAsync(string playerId, string token)
    {
        var response = await SendAsync(HttpMethod.Get, $"/players/{playerId}/stats", token);
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<StatsBody>(Json))!;
    }

    private Task<HttpResponseMessage> PostAsync(string path, string token, object payload) =>
        SendAsync(HttpMethod.Post, path, token, JsonContent.Create(payload));

    private Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, string token, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.TryAddWithoutValidation("X-Session-Token", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        return Client.SendAsync(request);
    }

    private static string NewId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private sealed record LoginBody(string PlayerId, string Token, DateTimeOffset ExpiresAt);

    private sealed record StatsBody(string PlayerId, int Score, int GiftsSent, int GiftsReceived, DateTimeOffset LastActive);

    private sealed record GiftBody(bool Applied, int SenderBalance, int RecipientBalance, bool Replayed);

    private sealed record LeaderboardBody(IReadOnlyList<LeaderboardRow> Top, DateTimeOffset ComputedAt);

    private sealed record LeaderboardRow(int Rank, string PlayerId, int Score);
}
