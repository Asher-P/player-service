using Orleans.Hosting;
using Orleans.TestingHost;
using PlayerService.Api.Extensions;
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

    public async Task InitializeAsync()
    {
        var builder = new InProcessTestClusterBuilder(SiloCount);

        // The exact production silo composition - storage, transactions, streams, collection age.
        // Only clustering differs, and the test cluster supplies that itself.
        builder.ConfigureSilo((_, silo) => silo.AddPlayerServiceOrleans());

        Cluster = builder.Build();
        await Cluster.DeployAsync();
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
