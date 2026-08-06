using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.TestingHost;
using PlayerService.Api.Extensions;
using Xunit;

namespace PlayerService.Tests;

/// <summary>
/// A clock the test drives by hand. Only <see cref="GetUtcNow"/> is overridden, so timers created
/// through this provider still fire on real time - which matters, because Orleans itself resolves
/// <see cref="TimeProvider"/> from the container (activation collection) and must keep working.
/// </summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private long _utcTicks = DateTimeOffset.UtcNow.UtcTicks;

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);

    public void Advance(TimeSpan by) => Interlocked.Add(ref _utcTicks, by.Ticks);
}

/// <summary>
/// A second two-silo cluster whose grains read a hand-driven clock, so session expiry is tested by
/// advancing time rather than by sleeping. It is separate from <see cref="ClusterFixture"/> because
/// a frozen clock must not leak into tests that assume real time.
/// </summary>
public sealed class ManualClockClusterFixture : IAsyncLifetime
{
    private const short SiloCount = 2;

    public ManualTimeProvider Clock { get; } = new();

    public InProcessTestCluster Cluster { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var builder = new InProcessTestClusterBuilder(SiloCount);

        builder.ConfigureSilo((_, silo) =>
        {
            // Registered before the production wiring, whose TryAddSingleton then stands down.
            silo.Services.AddSingleton<TimeProvider>(Clock);
            silo.AddPlayerServiceOrleans();
        });

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
public sealed class ManualClockClusterCollection : ICollectionFixture<ManualClockClusterFixture>
{
    public const string Name = "orleans-cluster-manual-clock";
}
