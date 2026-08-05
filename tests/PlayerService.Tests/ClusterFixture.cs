using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Hosting;
using Orleans.TestingHost;
using PlayerService.Api.Configuration;
using PlayerService.Api.Extensions;
using PlayerService.Api.Services;
using Xunit;

namespace PlayerService.Tests;

/// <summary>
/// A real <b>two-silo</b> cluster, in process. Two silos rather than one is deliberate: with a
/// single silo every "single activation cluster-wide" claim would be indistinguishable from a
/// local dictionary, and none of the later phases (login gate, gift transactions, leaderboard
/// convergence) would actually be testing what they claim to test.
/// </summary>
public sealed class ClusterFixture : IAsyncLifetime
{
    private const short SiloCount = 2;

    public InProcessTestCluster Cluster { get; private set; } = null!;

    /// <summary>
    /// The production <see cref="GiftService"/>, built over the test cluster's own client. Gift
    /// orchestration lives in the API layer by design (that is what keeps the call graph a tree), so
    /// testing it means constructing the real class rather than reimplementing its transaction shape.
    /// </summary>
    public GiftService Gifts { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var builder = new InProcessTestClusterBuilder(SiloCount);

        // The exact production silo composition - storage, transactions, streams, collection age.
        // Only clustering differs, and the test cluster supplies that itself.
        builder.ConfigureSilo((_, silo) => silo.AddPlayerServiceOrleans());

        Cluster = builder.Build();
        await Cluster.DeployAsync();

        // Resolved from a silo's own container rather than the external client's, because that is
        // the production shape: the API pod *is* a silo, so its GiftService gets the silo's
        // transaction client and cluster client.
        var pod = ((InProcessSiloHandle)Cluster.Silos[0]).ServiceProvider;

        Gifts = new GiftService(
            pod.GetRequiredService<ITransactionClient>(),
            pod.GetRequiredService<IClusterClient>(),
            pod.GetRequiredService<IGrainFactory>(),
            new StaticOptionsMonitor<GiftOptions>(new GiftOptions()),
            NullLogger<GiftService>.Instance);
    }

    public async Task DisposeAsync()
    {
        if (Cluster is not null)
        {
            await Cluster.DisposeAsync();
        }
    }
}

[CollectionDefinition(Name)]
public sealed class ClusterCollection : ICollectionFixture<ClusterFixture>
{
    public const string Name = "orleans-cluster";
}

/// <summary>Fixed options, so a test reads the same documented defaults the API binds from config.</summary>
public sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
{
    public StaticOptionsMonitor(T value) => CurrentValue = value;

    public T CurrentValue { get; }

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
