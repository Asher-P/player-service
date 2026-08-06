using PlayerService.Api.Auth;
using PlayerService.Api.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Co-hosted: this API pod IS an Orleans silo. Registered first, so the silo's hosted service
// starts before anything (LeaderboardCache) that talks to the cluster on startup.
builder.UseOrleans(silo => silo
    .UseLocalhostClustering()   // prod: AddRedisClustering / AddAzureTableClustering
    .AddPlayerServiceOrleans());

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

app.Run();

/// <summary>Exposed so integration tests can use <c>WebApplicationFactory&lt;Program&gt;</c>.</summary>
public partial class Program;
