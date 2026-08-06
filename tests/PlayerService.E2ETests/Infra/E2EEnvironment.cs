using Xunit;

namespace PlayerService.E2E;

/// <summary>
/// Where the live service is, how long its sessions last, and whether it is running at all.
/// </summary>
/// <remarks>
/// Unlike <c>tests/PlayerService.Tests</c>, nothing here hosts the service: these tests talk to a
/// process someone else started, over the network, exactly as a game client would. That is the
/// whole point - the guarantees are asserted through the same door real traffic uses.
/// </remarks>
public static class E2EEnvironment
{
    /// <summary>Override to point the suite at a deployed pod instead of a local <c>dotnet run</c>.</summary>
    public const string BaseUrlVariable = "PLAYERSERVICE_E2E_BASEURL";

    /// <summary>
    /// Must match the service's <c>Sessions:Ttl</c>. It is not discoverable over the API, and two
    /// scenarios (replay-after-offline, gift-to-offline) can only reach "offline" by waiting the
    /// session out - there is no logout endpoint, by design.
    /// </summary>
    public const string SessionTtlVariable = "PLAYERSERVICE_E2E_SESSION_TTL";

    public const string DefaultBaseUrl = "http://localhost:5080";

    private static readonly Lazy<string?> Availability =
        new(Probe, LazyThreadSafetyMode.ExecutionAndPublication);

    public static Uri BaseUrl { get; } =
        new(Environment.GetEnvironmentVariable(BaseUrlVariable) ?? DefaultBaseUrl);

    /// <summary>The service's configured session TTL, as told to us. Defaults to the shipped 3 minutes.</summary>
    public static TimeSpan SessionTtl { get; } =
        TimeSpan.TryParse(Environment.GetEnvironmentVariable(SessionTtlVariable), out var ttl)
            ? ttl
            : TimeSpan.FromMinutes(3);

    /// <summary>
    /// Non-null when the service is unreachable, in which case every <see cref="E2EFactAttribute"/>
    /// reports as skipped rather than failed. A red suite should mean "the service is wrong", never
    /// "the service is not running".
    /// </summary>
    public static string? SkipReason => Availability.Value;

    private static string? Probe()
    {
        // Discovery-time, so it must be synchronous and cheap. One request, short timeout, cached
        // for the whole run by the surrounding Lazy.
        using var http = new HttpClient { BaseAddress = BaseUrl, Timeout = TimeSpan.FromSeconds(5) };

        try
        {
            var response = http.GetAsync("/health").GetAwaiter().GetResult();

            return response.IsSuccessStatusCode
                ? null
                : $"{BaseUrl}health answered {(int)response.StatusCode}; expected 200.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return $"No Player Service at {BaseUrl} - start it with "
                 + "`dotnet run --project src/PlayerService.Api` "
                 + $"(or set {BaseUrlVariable}).";
        }
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> that skips itself when the service is not up, so
/// <c>dotnet test</c> on the whole solution stays green on a machine where nothing is running.
/// </summary>
public sealed class E2EFactAttribute : FactAttribute
{
    public override string? Skip
    {
        get => base.Skip ?? E2EEnvironment.SkipReason;
        set => base.Skip = value;
    }
}
