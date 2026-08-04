using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Configuration;
using Orleans.Hosting;
using PlayerService.Abstractions.Streaming;
using PlayerService.Grains;
using PlayerService.Grains.Configuration;

namespace PlayerService.Api.Extensions;

/// <summary>
/// Silo composition, kept out of <c>Program.cs</c> and — more importantly — kept identical between
/// the API host and the test cluster. Clustering is deliberately <b>not</b> configured here: the
/// API uses localhost clustering while the in-process test cluster supplies its own.
/// </summary>
public static class OrleansHostExtensions
{
    /// <summary>Providers, transactions, streams and grain lifetime — everything except clustering.</summary>
    public static ISiloBuilder AddPlayerServiceOrleans(this ISiloBuilder silo)
    {
        silo.AddMemoryGrainStorage(PlayerServiceStorage.Default)          // prod: AddRedisGrainStorage
            .AddMemoryGrainStorage(PlayerServiceStorage.TransactionStore) // backs ITransactionalState
            .AddMemoryGrainStorage(PlayerServiceStorage.PubSubStore)      // required by stream pub-sub
            .UseTransactions()
            .AddMemoryStreams(PlayerServiceStreams.ProviderName)          // prod: AddEventHubStreams
            .Configure<GrainCollectionOptions>(options =>
            {
                // An idle player's activation — and with it, its idempotency ledger — leaves memory.
                options.CollectionAge = TimeSpan.FromMinutes(15);
            });

        // Grain-side-only policy, so it is bound with the silo rather than with an API feature.
        // BindConfiguration resolves IConfiguration from the host, which keeps this working
        // unchanged inside the test cluster (missing section => documented defaults).
        silo.Services.AddOptions<IdempotencyOptions>()
            .BindConfiguration(IdempotencyOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Grains take a dependency on TimeProvider so session expiry is testable without sleeping.
        silo.Services.TryAddSingleton(TimeProvider.System);

        return silo;
    }
}
