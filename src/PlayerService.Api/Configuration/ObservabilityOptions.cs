namespace PlayerService.Api.Configuration;

/// <summary>Telemetry export. Collection is always on; only shipping it anywhere is optional.</summary>
public sealed class ObservabilityOptions
{
    public const string SectionName = "Observability";

    /// <summary>
    /// Whether to register OpenTelemetry providers at all. Off by default so the test suite - which
    /// starts several clusters in one process - is not competing with console exporters.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// OTLP collector endpoint. Empty falls back to the console exporter, which is enough to see
    /// grain call latency and gift abort rate locally without running a collector.
    /// </summary>
    public string? OtlpEndpoint { get; set; }
}
