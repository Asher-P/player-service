using Xunit;

namespace PlayerService.E2E;

/// <summary>
/// One HTTP client shared by the scenarios that need nothing but a running service.
/// </summary>
public sealed class LiveServiceFixture : IAsyncLifetime, IDisposable
{
    public PlayerServiceClient Api { get; } = PlayerServiceClient.Create();

    public async Task InitializeAsync()
    {
        if (E2EEnvironment.SkipReason is not null)
        {
            // Every test in the collection is already skipped; do not fail the fixture on top of it.
            return;
        }

        // Sockets opened here rather than inside the first burst.
        await Api.WarmUpAsync(connections: 16);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose() => Api.Dispose();
}

[CollectionDefinition(Name)]
public sealed class LiveServiceCollection : ICollectionFixture<LiveServiceFixture>
{
    public const string Name = "live-player-service";
}
