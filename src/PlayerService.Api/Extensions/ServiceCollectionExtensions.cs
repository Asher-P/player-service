using PlayerService.Api.Auth;
using PlayerService.Api.Configuration;
using PlayerService.Api.Services;
using PlayerService.Grains.Configuration;

namespace PlayerService.Api.Extensions;

/// <summary>
/// Feature-grouped registration so <c>Program.cs</c> stays a composition root rather than a wall of
/// <c>AddSingleton</c> calls, and so tests can reuse the exact production wiring.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Session validation on the request path plus the session TTL policy.</summary>
    public static IServiceCollection AddSessions(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PlayerSessionOptions>()
            .Bind(configuration.GetSection(PlayerSessionOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Stateless: one grain call per authenticated request, no per-request state.
        services.AddSingleton<SessionAuthFilter>();

        return services;
    }

    /// <summary>Gift orchestration and its bounded-retry policy.</summary>
    public static IServiceCollection AddGifting(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<GiftOptions>()
            .Bind(configuration.GetSection(GiftOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<GiftService>();

        return services;
    }

    /// <summary>The pod-local push cache that makes <c>GET /leaderboard</c> a wait-free local read.</summary>
    public static IServiceCollection AddLeaderboard(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<LeaderboardOptions>()
            .Bind(configuration.GetSection(LeaderboardOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // One instance, two roles: hosted stream subscriber and the object controllers read from.
        services.AddSingleton<LeaderboardCache>();
        services.AddHostedService(sp => sp.GetRequiredService<LeaderboardCache>());

        return services;
    }
}
