using Orleans.Dashboard;
using PlayerService.Api.Auth;
using PlayerService.Api.Configuration;
using PlayerService.Api.Extensions;

var builder = WebApplication.CreateBuilder(args);

var observability = builder.Configuration
    .GetSection(ObservabilityOptions.SectionName)
    .Get<ObservabilityOptions>() ?? new ObservabilityOptions();

// Co-hosted: this API pod IS an Orleans silo. Registered first, so the silo's hosted service
// starts before anything (LeaderboardCache) that talks to the cluster on startup.
builder.UseOrleans(silo =>
{
    silo.UseLocalhostClustering()   // prod: AddRedisClustering / AddAzureTableClustering
        .AddPlayerServiceOrleans();

    if (observability.DashboardEnabled)
    {
        // Collects the per-grain counters the dashboard renders. Only added when the dashboard is
        // actually served, so a production silo does not pay for sampling nobody reads.
        silo.AddDashboard();
    }
});

builder.Services.AddControllers(options => options.Filters.AddService<SessionAuthFilter>());
builder.Services.AddProblemDetails();

builder.Services
    .AddSessions(builder.Configuration)
    .AddGifting(builder.Configuration)
    .AddLeaderboard(builder.Configuration)
    .AddPlayerServiceObservability(builder.Configuration);

var app = builder.Build();

app.UseExceptionHandler();
app.MapControllers();

// A minimal-API endpoint rather than a controller action, so the session filter - which is
// registered on MVC only - does not apply. Container orchestration needs one unauthenticated
// endpoint to gate startup on, and every other route requires a token by design.
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

if (observability.DashboardEnabled)
{
    // Grain types, live activations, per-method call rates and latency - the cluster's own view,
    // complementing the metrics shipped to Prometheus. Unauthenticated: see DashboardEnabled.
    app.MapOrleansDashboard(observability.DashboardPath);
}

app.Run();

/// <summary>Exposed so integration tests can use <c>WebApplicationFactory&lt;Program&gt;</c>.</summary>
public partial class Program;
