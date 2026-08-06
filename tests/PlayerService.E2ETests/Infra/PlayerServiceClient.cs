using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit.Sdk;

namespace PlayerService.E2E;

/// <summary>
/// A game client, near enough: one <see cref="HttpClient"/> against the live service, one method
/// per endpoint, and no knowledge of Orleans, grains or the server's types.
/// </summary>
public sealed class PlayerServiceClient : IDisposable
{
    public const string SessionHeader = "X-Session-Token";

    /// <summary>The balance every player is seeded with on first activation.</summary>
    public const int SeedBalance = 1000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    private PlayerServiceClient(HttpClient http) => _http = http;

    public static PlayerServiceClient Create()
    {
        // The connection pool is load-bearing for every test in this suite: with the default
        // per-server cap, a "100 requests in parallel" burst would queue in the client and never
        // reach the server together, and the tests would be proving nothing.
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 512,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        };

        var http = new HttpClient(handler)
        {
            BaseAddress = E2EEnvironment.BaseUrl,

            // Generous on purpose: a gift under contention retries up to 8 times with backoff
            // before the server gives up and answers 503, and that answer is one of the things
            // under test - a client-side timeout would hide it.
            Timeout = TimeSpan.FromMinutes(2),
        };

        return new PlayerServiceClient(http);
    }

    public void Dispose() => _http.Dispose();

    /// <summary>Unique per run, so the suite can be re-run against a long-lived service without
    /// colliding with its own earlier players.</summary>
    public static string NewId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    public Task<ApiResult<LoginBody>> LoginAsync(string playerId, string deviceId) =>
        SendAsync<LoginBody>(
            HttpMethod.Post, "/login", token: null, JsonContent.Create(new { playerId, deviceId }));

    /// <summary>A brand-new player on a brand-new device, logged in and ready to act.</summary>
    public async Task<PlayerSession> NewPlayerAsync(string? playerId = null)
    {
        var id = playerId ?? NewId("player");
        var deviceId = NewId("device");

        var login = (await LoginAsync(id, deviceId)).Ok($"logging in {id}");

        return new PlayerSession(login.PlayerId, deviceId, login.Token);
    }

    /// <summary>Logs an existing player in from another device, superseding the prior session.</summary>
    public async Task<PlayerSession> LoginFromNewDeviceAsync(string playerId)
    {
        var deviceId = NewId("device");
        var login = (await LoginAsync(playerId, deviceId)).Ok($"re-logging in {playerId}");

        return new PlayerSession(login.PlayerId, deviceId, login.Token);
    }

    public Task<ApiResult<StatsBody>> GetStatsAsync(PlayerSession player) =>
        SendAsync<StatsBody>(HttpMethod.Get, $"/players/{player.PlayerId}/stats", player.Token);

    /// <summary>Balance only, asserting the call succeeded - the shape most assertions want.</summary>
    public async Task<int> GetBalanceAsync(PlayerSession player) =>
        (await GetStatsAsync(player)).Ok($"reading {player.PlayerId}'s stats").Score;

    public Task<ApiResult<StatsBody>> AddScoreAsync(PlayerSession player, int points, string requestId) =>
        SendAsync<StatsBody>(
            HttpMethod.Post,
            $"/players/{player.PlayerId}/stats/score",
            player.Token,
            JsonContent.Create(new { points, requestId }));

    public Task<ApiResult<GiftBody>> SendGiftAsync(
        PlayerSession sender, string toPlayerId, int points, string requestId) =>
        SendAsync<GiftBody>(
            HttpMethod.Post,
            $"/players/{sender.PlayerId}/gifts",
            sender.Token,
            JsonContent.Create(new { toPlayerId, points, requestId }));

    public Task<ApiResult<LeaderboardBody>> GetLeaderboardAsync(PlayerSession reader) =>
        SendAsync<LeaderboardBody>(HttpMethod.Get, "/leaderboard", reader.Token);

    /// <summary>
    /// Does exactly what the service's status-code map tells a client to do with a <c>503</c>:
    /// retry, with backoff, carrying the same requestId. Safe because both mutating endpoints are
    /// idempotent - which is the property that makes the 503 useful rather than merely honest.
    /// </summary>
    /// <remarks>
    /// The caller must mint the requestId <i>outside</i> the delegate, or this is not a retry.
    /// </remarks>
    public async Task<ApiResult<T>> RetryingOn503Async<T>(
        Func<Task<ApiResult<T>>> call, int attempts = 5)
    {
        for (var attempt = 1; ; attempt++)
        {
            var result = await call();

            if (result.Status != HttpStatusCode.ServiceUnavailable || attempt == attempts)
            {
                return result;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt - 1)));
        }
    }

    /// <summary>
    /// Opens <paramref name="connections"/> sockets before a burst, so the burst measures the
    /// service under concurrency rather than the client's TCP handshakes.
    /// </summary>
    public async Task WarmUpAsync(int connections)
    {
        await Burst.FireAsync(connections, async _ =>
        {
            using var response = await _http.GetAsync("/health");
            return response.StatusCode;
        });
    }

    private async Task<ApiResult<T>> SendAsync<T>(
        HttpMethod method, string path, string? token, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (token is not null)
        {
            request.Headers.TryAddWithoutValidation(SessionHeader, token);
        }

        using var response = await _http.SendAsync(request);
        var raw = await response.Content.ReadAsStringAsync();

        return new ApiResult<T>(
            response.StatusCode,
            response.IsSuccessStatusCode ? Deserialize<T>(raw, path) : default,
            response.IsSuccessStatusCode ? null : TryDeserialize<ProblemBody>(raw),
            raw);
    }

    private static T Deserialize<T>(string raw, string path) =>
        TryDeserialize<T>(raw) ?? throw new XunitException(
            $"{path} answered 200 with a body that is not a {typeof(T).Name}: {raw}");

    private static T? TryDeserialize<T>(string raw)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(raw, Json);
        }
        catch (JsonException)
        {
            // A rejection is allowed to answer with no body at all; the status is the contract.
            return default;
        }
    }
}

/// <summary>Fires N requests as close to simultaneously as a single process can.</summary>
internal static class Burst
{
    /// <summary>
    /// Builds every task first and releases them through one gate, so none of them is already
    /// finished by the time the last one is created - the difference between "N in parallel" and
    /// "N in a fast loop", which is exactly the distinction these tests exist to make.
    /// </summary>
    public static async Task<T[]> FireAsync<T>(int count, Func<int, Task<T>> action)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var racers = Enumerable.Range(0, count).Select(async i =>
        {
            await gate.Task;
            return await action(i);
        }).ToArray();

        gate.SetResult();

        return await Task.WhenAll(racers);
    }
}

/// <summary>Polls a condition to a deadline. The suite contains no fixed sleeps: every wait either
/// ends the moment the condition holds, or fails with what it last saw.</summary>
internal static class Poll
{
    public static async Task UntilAsync(
        TimeSpan within, TimeSpan interval, Func<Task<bool>> condition, Func<string> onTimeout)
    {
        var deadline = DateTimeOffset.UtcNow + within;

        while (true)
        {
            if (await condition())
            {
                return;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new XunitException($"condition still false after {within}: {onTimeout()}");
            }

            await Task.Delay(interval);
        }
    }
}
