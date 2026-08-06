using System.ComponentModel.DataAnnotations;

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
    /// (<c>http://localhost:4327</c> gRPC, or <c>http://localhost:4328</c> with
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
    /// Send OTLP over HTTP/protobuf (port 4328) instead of gRPC (port 4327). The endpoint port must
    /// match the protocol, so these two settings move together.
    /// </summary>
    public bool UseHttpProtobuf { get; set; }

    /// <summary>
    /// How often metrics are pushed to <see cref="MetricsOtlpEndpoint"/>. This is the sample spacing
    /// the backend sees, so it is really a dashboard setting wearing an exporter's clothes.
    /// </summary>
    /// <remarks>
    /// The SDK default is 60s, which quietly breaks every <c>rate()</c> panel: Grafana's
    /// <c>$__rate_interval</c> is <c>max($__interval + scrape_interval, 4 * scrape_interval)</c>,
    /// which at the provisioned <c>timeInterval: 15s</c> bottoms out at 60s — a 60s window over
    /// 60s-spaced samples usually contains one point, and <c>rate()</c> needs two. It renders as
    /// "No data" rather than an error, which is why it is worth pinning here.
    /// <para>
    /// Keep this at or below the Grafana datasource's <c>timeInterval</c>
    /// (<c>deploy/grafana/provisioning/datasources/datasources.yml</c>); the two move together.
    /// </para>
    /// </remarks>
    [Range(1, 300)]
    public int MetricExportIntervalSeconds { get; set; } = 15;

    /// <summary>
    /// Serves the Orleans Dashboard at <see cref="DashboardPath"/>.
    /// </summary>
    /// <remarks>
    /// Opt-in, and deliberately so. The dashboard exposes cluster internals - grain types, live
    /// activation counts, per-method call rates and a live log stream - and it is mapped as a
    /// minimal-API endpoint, so the MVC session filter does not apply to it. That is fine behind a
    /// private network or an ingress that authenticates, and not fine on a public listener. Where
    /// real authentication exists, prefer <c>.RequireAuthorization()</c> on the mapped endpoint.
    /// </remarks>
    public bool DashboardEnabled { get; set; }

    /// <summary>Route prefix the dashboard is served from.</summary>
    public string DashboardPath { get; set; } = "/dashboard";
}
