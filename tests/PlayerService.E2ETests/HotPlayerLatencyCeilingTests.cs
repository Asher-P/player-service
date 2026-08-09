using System.Diagnostics;
using System.Net;
using Xunit;
using Xunit.Abstractions;

namespace PlayerService.E2E;

/// <summary>
/// Where the one-second line sits for a hot player. Two shapes of load, because "how much can it
/// take and still answer fast" has two honest readings: a single spike (everything arrives at
/// once, the queue drains) and a sustained rate (arrivals keep coming, the queue must not grow).
/// The assertions pin only the overload contract; the printed tables are the deliverable.
/// </summary>
/// <remarks>
/// Tagged <c>Category=Load</c> like <see cref="HotPlayerBurstTests"/>, and for the same reason.
/// Numbers are meaningful on a warm service - each test burns an unmeasured warm-up burst first.
/// </remarks>
[Collection(LiveServiceCollection.Name)]
public sealed class HotPlayerLatencyCeilingTests
{
    private const int Points = 1;

    private readonly PlayerServiceClient _api;
    private readonly ITestOutputHelper _output;

    public HotPlayerLatencyCeilingTests(LiveServiceFixture fixture, ITestOutputHelper output)
    {
        _api = fixture.Api;
        _output = output;
    }

    /// <summary>
    /// One spike, fully parallel, then silence: the largest size whose <b>max</b> latency stays
    /// under a second is the burst the player absorbs invisibly. Expect wall time to scale
    /// linearly with size - that is what "one activation, fixed service rate" predicts.
    /// </summary>
    [E2EFact]
    [Trait("Category", "Load")]
    public async Task Burst_size_a_player_absorbs_within_one_second()
    {
        await WarmUpAsync();

        foreach (var size in new[] { 64, 128, 256, 384, 512 })
        {
            // A fresh player per step, so one step's queue tail cannot bleed into the next.
            var player = await _api.NewPlayerAsync();
            var requestIds = Enumerable.Range(0, size)
                .Select(_ => PlayerServiceClient.NewId("req"))
                .ToArray();

            var clock = Stopwatch.StartNew();
            var outcomes = await Burst.FireAsync(size, async i =>
            {
                var started = clock.Elapsed;
                var result = await _api.AddScoreAsync(player, Points, requestIds[i]);
                return (result.Status, Ms: (clock.Elapsed - started).TotalMilliseconds);
            });
            clock.Stop();

            Report($"burst {size,4}", clock.Elapsed, outcomes);

            Assert.All(outcomes, o => Assert.True(
                o.Status is HttpStatusCode.OK or HttpStatusCode.ServiceUnavailable,
                $"unexpected status {(int)o.Status}"));
        }
    }

    /// <summary>
    /// Open-loop pacing: requests are launched on schedule whether or not earlier ones returned,
    /// exactly like independent clients. Below the service rate the queue stays shallow and
    /// latency flat; above it every second of load adds a second of queue - the knee between
    /// those two regimes is the sustainable ceiling.
    /// </summary>
    [E2EFact]
    [Trait("Category", "Load")]
    public async Task Sustained_rate_a_player_answers_within_one_second()
    {
        await WarmUpAsync();

        foreach (var rate in new[] { 250, 350, 450, 550 })
        {
            var player = await _api.NewPlayerAsync();
            const double seconds = 4;
            var total = (int)(rate * seconds);

            var clock = Stopwatch.StartNew();
            var inflight = new List<Task<(HttpStatusCode Status, double Ms)>>(total);

            for (var i = 0; i < total; i++)
            {
                var due = TimeSpan.FromSeconds(i / (double)rate);
                var wait = due - clock.Elapsed;
                if (wait > TimeSpan.FromMilliseconds(1))
                {
                    // Windows timer resolution is ~15ms, so each delay releases a small batch of
                    // overdue sends rather than exactly one - fine, the schedule stays honest.
                    await Task.Delay(wait);
                }

                inflight.Add(SendAsync(player, clock));
            }

            var offered = total / clock.Elapsed.TotalSeconds;
            var outcomes = await Task.WhenAll(inflight);
            clock.Stop();

            Report($"rate {rate,4}/s (offered {offered,3:F0}/s)", clock.Elapsed, outcomes);

            Assert.All(outcomes, o => Assert.True(
                o.Status is HttpStatusCode.OK or HttpStatusCode.ServiceUnavailable,
                $"unexpected status {(int)o.Status}"));
        }
    }

    private async Task<(HttpStatusCode Status, double Ms)> SendAsync(
        PlayerSession player, Stopwatch clock)
    {
        var started = clock.Elapsed;
        var result = await _api.AddScoreAsync(player, Points, PlayerServiceClient.NewId("req"));
        return (result.Status, (clock.Elapsed - started).TotalMilliseconds);
    }

    /// <summary>Sockets, JIT and the transaction pipeline all warmed by traffic that is not measured.</summary>
    private async Task WarmUpAsync()
    {
        await _api.WarmUpAsync(connections: 512);

        var warm = await _api.NewPlayerAsync();
        await Burst.FireAsync(128, _ =>
            _api.AddScoreAsync(warm, Points, PlayerServiceClient.NewId("warm")));
    }

    private void Report(
        string label, TimeSpan wall, IReadOnlyList<(HttpStatusCode Status, double Ms)> outcomes)
    {
        var latencies = outcomes.Select(o => o.Ms).OrderBy(ms => ms).ToArray();
        var ok = outcomes.Count(o => o.Status == HttpStatusCode.OK);

        _output.WriteLine(
            $"{label}: wall {wall.TotalMilliseconds,6:F0}ms  ok {ok,4}  503 {outcomes.Count - ok,4}  "
            + $"p50/p95/p99/max ms {Percentile(latencies, 50),5:F0}/{Percentile(latencies, 95),5:F0}"
            + $"/{Percentile(latencies, 99),5:F0}/{latencies[^1],5:F0}");
    }

    private static double Percentile(double[] sorted, int percentile) =>
        sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(percentile / 100.0 * sorted.Length) - 1)];
}
