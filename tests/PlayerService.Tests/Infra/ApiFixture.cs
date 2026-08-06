using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Xunit;

namespace PlayerService.Tests;

/// <summary>
/// The real API host - the actual <c>Program.cs</c>, its silo, its filters, its status-code map -
/// behind an <see cref="HttpClient"/>. Everything below this line has been tested against grain
/// interfaces; this is the only fixture that tests what a client actually receives.
/// </summary>
/// <remarks>
/// One silo, not two. Cluster-wide guarantees are covered by the in-process two-silo tests, where
/// they can be asserted directly; what only the HTTP surface can show is the request pipeline -
/// auth, ownership, model validation, and the mapping of domain outcomes onto status codes.
/// </remarks>
public sealed class ApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    /// <summary>
    /// Ports well away from the Orleans defaults, so running the suite never fights a
    /// <c>dotnet run</c> silo that happens to be up on the developer's machine.
    /// </summary>
    private const int SiloPort = 21_111;
    private const int GatewayPort = 21_112;

    public HttpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Client = CreateClient();

        // Force host startup here rather than on the first test's request: the silo, the streams
        // and the leaderboard cache all come up on this call, and a failure now is far easier to
        // read than the same failure attributed to whichever test ran first.
        await Client.GetAsync("/leaderboard");
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        Client?.Dispose();
        await DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            // Exporters would write to the test runner's console on a timer; the instruments
            // themselves stay live, so the metrics code is still on the tested path.
            ["Observability:Enabled"] = "false",

            // Nothing here asserts on the dashboard, and its per-grain counter sampling is pure
            // overhead in a suite that starts a host per collection.
            ["Observability:DashboardEnabled"] = "false",
        }));

        builder.ConfigureServices(services => services.Configure<EndpointOptions>(options =>
        {
            options.SiloPort = SiloPort;
            options.GatewayPort = GatewayPort;
        }));
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>
{
    public const string Name = "player-service-api";
}
