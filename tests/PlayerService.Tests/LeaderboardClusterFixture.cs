using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans;
using Orleans.Hosting;
using Orleans.TestingHost;
using PlayerService.Api.Configuration;
using PlayerService.Api.Extensions;
using PlayerService.Api.Services;
using PlayerService.Grains.Configuration;
using Xunit;

namespace PlayerService.Tests;

/// <summary>
/// A cluster of its own for the leaderboard, with a <see cref="LeaderboardCache"/> per silo - the
/// pod-local half of the push model, and the only way to test that two pods converge on the same
/// Top-N rather than each computing its own.
/// </summary>
/// <remarks>
/// Separate from <see cref="ClusterFixture"/> because the leaderboard grain is a cluster singleton
/// holding <b>every</b> player: sharing a cluster would mean every other test's players landing on
/// the board under test, making its assertions depend on unrelated tests.
/// </remarks>
public sealed class LeaderboardClusterFixture : IAsyncLifetime
{
    private const short SiloCount = 2;

    public InProcessTestCluster Cluster { get; private set; } = null!;

    /// <summary>One cache per silo, exactly as one would exist per co-hosted API pod.</summary>
    public IReadOnlyList<LeaderboardCache> Pods { get; private set; } = null!;

    public LeaderboardOptions Options { get; } = new();

    /// <summary>Gifts feed the same stream as score adds, so the projection has to absorb both.</summary>
    public GiftService Gifts { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var builder = new InProcessTestClusterBuilder(SiloCount);
        builder.ConfigureSilo((_, silo) => silo.AddPlayerServiceOrleans());

        Cluster = builder.Build();
        await Cluster.DeployAsync();

        var first = ((InProcessSiloHandle)Cluster.Silos[0]).ServiceProvider;

        Gifts = new GiftService(
            first.GetRequiredService<ITransactionClient>(),
            first.GetRequiredService<IClusterClient>(),
            first.GetRequiredService<IGrainFactory>(),
            new StaticOptionsMonitor<GiftOptions>(new GiftOptions()),
            NullLogger<GiftService>.Instance);

        var pods = new List<LeaderboardCache>(SiloCount);

        foreach (var silo in Cluster.Silos)
        {
            var services = ((InProcessSiloHandle)silo).ServiceProvider;

            var pod = new LeaderboardCache(
                services.GetRequiredService<IClusterClient>(),
                services.GetRequiredService<IGrainFactory>(),
                new StaticOptionsMonitor<LeaderboardOptions>(Options),
                NullLogger<LeaderboardCache>.Instance);

            // Subscribes to leaderboard/top and primes itself - the same one-call-per-pod-lifetime
            // startup the hosted service performs in the API. Priming also activates the grain,
            // which is what puts the score-ingest subscription in place before any test publishes.
            await pod.StartAsync(CancellationToken.None);
            pods.Add(pod);
        }

        Pods = pods;
    }

    public async Task DisposeAsync()
    {
        foreach (var pod in Pods ?? Array.Empty<LeaderboardCache>())
        {
            await pod.StopAsync(CancellationToken.None);
        }

        if (Cluster is not null)
        {
            await Cluster.DisposeAsync();
        }
    }
}

[CollectionDefinition(Name)]
public sealed class LeaderboardClusterCollection : ICollectionFixture<LeaderboardClusterFixture>
{
    public const string Name = "orleans-cluster-leaderboard";
}
