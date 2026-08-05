using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using PlayerService.Api.Configuration;
using PlayerService.Api.Observability;

namespace PlayerService.Api.Extensions;

/// <summary>
/// Metrics and tracing, enough to answer the two questions this design raises in production:
/// how long grain calls take, and how often gift transactions abort under contention.
/// </summary>
public static class ObservabilityExtensions
{
    public static IServiceCollection AddPlayerServiceObservability(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<ObservabilityOptions>()
            .Bind(configuration.GetSection(ObservabilityOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        var options = configuration
            .GetSection(ObservabilityOptions.SectionName)
            .Get<ObservabilityOptions>() ?? new ObservabilityOptions();

        // Instruments are created once and reused; IMeterFactory gives them a lifetime tied to the
        // container rather than to a static, which is what keeps them collectable in tests.
        services.AddMetrics();
        services.AddSingleton<PlayerServiceMetrics>();

        if (!options.Enabled)
        {
            // Off by default in tests: an exporter writing to the console from a dozen in-process
            // clusters is noise, and the metrics objects above still work without a collector.
            return services;
        }

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("player-service"))
            .WithMetrics(metrics =>
            {
                metrics
                    // Grain call latency and counts, request/response rates, and the transaction
                    // agent's own counters all live under this meter.
                    .AddMeter("Microsoft.Orleans")
                    .AddMeter(PlayerServiceMetrics.MeterName)
                    .AddAspNetCoreInstrumentation();

                Export(metrics, options);
            })
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource("Microsoft.Orleans.Runtime")
                    .AddSource("Microsoft.Orleans.Application")
                    .AddAspNetCoreInstrumentation();

                Export(tracing, options);
            });

        return services;
    }

    private static void Export(MeterProviderBuilder metrics, ObservabilityOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.OtlpEndpoint))
        {
            metrics.AddConsoleExporter();
        }
        else
        {
            metrics.AddOtlpExporter(otlp => otlp.Endpoint = new Uri(options.OtlpEndpoint));
        }
    }

    private static void Export(TracerProviderBuilder tracing, ObservabilityOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.OtlpEndpoint))
        {
            tracing.AddConsoleExporter();
        }
        else
        {
            tracing.AddOtlpExporter(otlp => otlp.Endpoint = new Uri(options.OtlpEndpoint));
        }
    }
}
