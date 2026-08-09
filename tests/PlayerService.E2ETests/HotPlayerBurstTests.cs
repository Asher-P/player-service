using System.Diagnostics;
using System.Net;
using Xunit;
using Xunit.Abstractions;

namespace PlayerService.E2E;

/// <summary>
/// One player pushed far past a single activation's transaction throughput — the load-shedding
/// story, measured through the front door. The assertions pin the contract (every request answered,
/// only 200 or 503, exact accounting); the output prints what actually happened (status mix,
/// offered rate, latency spread), because "what does a 10k burst at one player do" is a question
/// with an empirical answer that the pass/fail alone would hide.
/// </summary>
/// <remarks>
/// Tagged <c>Category=Load</c>: it deliberately saturates the service for a while, so a quick sweep
/// can leave it out with <c>--filter Category!=Load</c>.
/// </remarks>
[Collection(LiveServiceCollection.Name)]
public sealed class HotPlayerBurstTests
{
    private const int Posts = 10_000;
    private const int Points = 1;

    private readonly PlayerServiceClient _api;
    private readonly ITestOutputHelper _output;

    public HotPlayerBurstTests(LiveServiceFixture fixture, ITestOutputHelper output)
    {
        _api = fixture.Api;
        _output = output;
    }

    [E2EFact]
    [Trait("Category", "Load")]
    public async Task A_10k_burst_at_one_player_sheds_cleanly_and_loses_nothing()
    {
        var player = await _api.NewPlayerAsync();
        var requestIds = Enumerable.Range(0, Posts)
            .Select(_ => PlayerServiceClient.NewId("req"))
            .ToArray();

        // The pool must exist before the burst, or the first few hundred latencies measure TCP
        // handshakes instead of the service.
        await _api.WarmUpAsync(connections: 256);

        var clock = Stopwatch.StartNew();
        var outcomes = await Burst.FireAsync(Posts, async i =>
        {
            var started = clock.Elapsed;
            try
            {
                var result = await _api.AddScoreAsync(player, Points, requestIds[i]);
                return new Outcome(result.Status, (clock.Elapsed - started).TotalMilliseconds);
            }
            catch (Exception ex) when (ex is TaskCanceledException or HttpRequestException)
            {
                // A hung or torn request is a finding, not a crash: recorded here, failed on below.
                return new Outcome(null, (clock.Elapsed - started).TotalMilliseconds);
            }
        });
        clock.Stop();

        var applied = outcomes.Count(o => o.Status == HttpStatusCode.OK);
        var shed = outcomes.Count(o => o.Status == HttpStatusCode.ServiceUnavailable);
        var unanswered = outcomes.Count(o => o.Status is null);
        var other = Posts - applied - shed - unanswered;

        var latencies = outcomes.Select(o => o.ElapsedMs).OrderBy(ms => ms).ToArray();
        _output.WriteLine(
            $"burst: {Posts} posts in {clock.Elapsed.TotalSeconds:F1}s "
            + $"({Posts / clock.Elapsed.TotalSeconds:F0} req/s offered)");
        _output.WriteLine(
            $"  200 applied: {applied}   503 shed: {shed}   other: {other}   unanswered: {unanswered}");
        _output.WriteLine(
            $"  latency ms p50/p95/p99/max: "
            + $"{Percentile(latencies, 50):F0}/{Percentile(latencies, 95):F0}"
            + $"/{Percentile(latencies, 99):F0}/{latencies[^1]:F0}");

        // The overload contract: every request gets an answer, and the answer is either success or
        // the documented retryable shed - never a hang, never a 500, never anything else.
        Assert.Equal(0, unanswered);
        Assert.Equal(0, other);

        // Exact accounting at the half-way point: the balance reflects precisely the requests that
        // were told "applied". A 503 must mean nothing happened - an aborted transaction that
        // nevertheless moved points would surface right here.
        Assert.Equal(
            PlayerServiceClient.SeedBalance + (applied * Points),
            await _api.GetBalanceAsync(player));

        // The second half of the 503 contract: a client that retries with the same requestId
        // eventually loses nothing. Bounded concurrency, because a real client backs off rather
        // than re-bursting - and because this phase asserts recovery, not a second overload.
        var unresolved = requestIds
            .Where((_, i) => outcomes[i].Status == HttpStatusCode.ServiceUnavailable)
            .ToArray();

        clock.Restart();
        var gate = new SemaphoreSlim(32);
        var retried = await Task.WhenAll(unresolved.Select(async requestId =>
        {
            await gate.WaitAsync();
            try
            {
                return await _api.RetryingOn503Async(
                    () => _api.AddScoreAsync(player, Points, requestId), attempts: 8);
            }
            finally
            {
                gate.Release();
            }
        }));
        clock.Stop();

        _output.WriteLine(
            $"recovery: {unresolved.Length} shed request(s) retried to completion "
            + $"in {clock.Elapsed.TotalSeconds:F1}s");

        Assert.All(retried, r => Assert.Equal(HttpStatusCode.OK, r.Status));

        // End state: all 10k distinct requests applied exactly once, none lost, none doubled.
        Assert.Equal(
            PlayerServiceClient.SeedBalance + (Posts * Points),
            await _api.GetBalanceAsync(player));
    }

    private static double Percentile(double[] sorted, int percentile) =>
        sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(percentile / 100.0 * sorted.Length) - 1)];

    /// <summary>Status is null when the request never got an HTTP answer at all.</summary>
    private sealed record Outcome(HttpStatusCode? Status, double ElapsedMs);
}
