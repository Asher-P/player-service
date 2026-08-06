namespace PlayerService.Api.Configuration;

/// <summary>
/// Telemetry export. Instruments and activities are always created; these settings only decide
/// where — if anywhere — the data is shipped. Nothing is ever written to the console: an exporter
/// printing on a timer buries the application's own logs and makes the service unusable to watch.
/// </summary>
public sealed class ObservabilityOptions
{
    public const string SectionName = "Observability";

    /// <summary>
    /// Whether to register OpenTelemetry providers at all. Off in tests, which start several
    /// clusters in one process and have no collector to ship to.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// OTLP endpoint for <b>traces</b> — a Jaeger instance's OTLP receiver
    /// (<c>http://localhost:4317</c> gRPC, or <c>http://localhost:4318</c> with
    /// <see cref="UseHttpProtobuf"/>). Empty disables trace export.
    /// </summary>
    public string? TracesOtlpEndpoint { get; set; }

    /// <summary>
    /// OTLP endpoint for <b>metrics</b>, empty by default and deliberately separate from
    /// <see cref="TracesOtlpEndpoint"/>. Jaeger stores traces, not metrics: pointing metrics at it
    /// would produce a steady stream of failed-export errors rather than data. Point this at a
    /// Prometheus/OTLP metrics collector when there is one; until then metrics are collected
    /// in-process and simply not shipped.
    /// </summary>
    public string? MetricsOtlpEndpoint { get; set; }

    /// <summary>
    /// Send OTLP over HTTP/protobuf (port 4318) instead of gRPC (port 4317). The endpoint port must
    /// match the protocol, so these two settings move together.
    /// </summary>
    public bool UseHttpProtobuf { get; set; }
}
