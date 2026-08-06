using OpenTelemetry.Exporter;
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
/// <remarks>
/// There is no console exporter, by choice. Metric export runs on a timer, so a console exporter
/// prints continuously whether or not anything happened, drowning the service's own logs. Telemetry
/// goes to a collector or nowhere.
/// </remarks>
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
            // The metrics objects above still work without a provider - recording to an unobserved
            // instrument is a no-op, so the instrumentation stays on the tested path.
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

                // Absent an endpoint, metrics are collected and left unexported rather than printed.
                if (!string.IsNullOrWhiteSpace(options.MetricsOtlpEndpoint))
                {
                    metrics.AddOtlpExporter((otlp, reader) =>
                    {
                        Configure(otlp, options.MetricsOtlpEndpoint, options);

                        // Export cadence is the sample spacing Prometheus stores, and therefore what
                        // decides whether rate() has two points to work with. See
                        // ObservabilityOptions.MetricExportIntervalSeconds.
                        reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds =
                            options.MetricExportIntervalSeconds * 1000;
                    });
                }
            })
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource("Microsoft.Orleans.Runtime")
                    .AddSource("Microsoft.Orleans.Application")
                    .AddAspNetCoreInstrumentation();

                if (!string.IsNullOrWhiteSpace(options.TracesOtlpEndpoint))
                {
                    tracing.AddOtlpExporter(otlp => Configure(otlp, options.TracesOtlpEndpoint, options));
                }
            });

        return services;
    }

    private static void Configure(OtlpExporterOptions otlp, string endpoint, ObservabilityOptions options)
    {
        otlp.Endpoint = new Uri(endpoint);
        otlp.Protocol = options.UseHttpProtobuf
            ? OtlpExportProtocol.HttpProtobuf
            : OtlpExportProtocol.Grpc;
    }
}
