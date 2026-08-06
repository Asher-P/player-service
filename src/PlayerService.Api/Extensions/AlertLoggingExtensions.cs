using System.Diagnostics;
using PlayerService.Api.Auth;
using PlayerService.Api.Configuration;
using Serilog;
using Serilog.Events;

namespace PlayerService.Api.Extensions;

/// <summary>
/// Routes logging through Serilog and attaches a second sink that only accepts trouble, so that
/// every rejected request and failed operation ends up in one dated file per day.
/// </summary>
/// <remarks>
/// Two pieces make that work. The file sink here is level-filtered, which catches anything the
/// service logs at Warning or above. <see cref="UseAlertRequestLogging"/> supplies the other half:
/// one log event per request, at a level chosen from the response status, so a rejection is
/// recorded even where the code that produced it says nothing.
/// </remarks>
public static class AlertLoggingExtensions
{
    private const string ConsoleTemplate =
        "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}";

    /// <summary>
    /// Alert lines carry their structured properties as well as the message: a status code without
    /// the route, player and trace it came from is not something anyone can act on.
    /// </summary>
    private const string AlertTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {Message:lj} {Properties:j}{NewLine}{Exception}";

    /// <summary>Replaces the default logging pipeline with Serilog. Call before <c>Build()</c>.</summary>
    public static WebApplicationBuilder AddAlertLogging(this WebApplicationBuilder builder)
    {
        builder.Services.AddOptions<AlertLogOptions>()
            .Bind(builder.Configuration.GetSection(AlertLogOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Read directly as well: the logger is configured before the container exists, so it cannot
        // wait for IOptions.
        var options = builder.Configuration
            .GetSection(AlertLogOptions.SectionName)
            .Get<AlertLogOptions>() ?? new AlertLogOptions();

        builder.Host.UseSerilog((context, logger) =>
        {
            logger
                // Levels live in configuration (the "Serilog" section), sinks live here. Anything
                // the section does not set keeps Serilog's own default of Information.
                .ReadFrom.Configuration(context.Configuration)
                .Enrich.FromLogContext()
                .WriteTo.Console(outputTemplate: ConsoleTemplate);

            if (!options.Enabled)
            {
                return;
            }

            logger.WriteTo.File(
                // Serilog appends the rolling date before the extension, so this is "alerts-.log"
                // on disk only in the sense that the pattern names it: files are alerts-20260806.log.
                path: Path.Combine(ResolveDirectory(builder.Environment, options), "alerts-.log"),
                restrictedToMinimumLevel: options.MinimumLevel,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: options.RetainedFileCountLimit,
                // Tests start several hosts in one process, and a container may run more than one
                // replica against a mounted volume. Shared access makes those append instead of
                // fighting over the file lock.
                shared: true,
                outputTemplate: AlertTemplate);
        });

        return builder;
    }

    /// <summary>
    /// One log event per request, levelled by outcome. Register after the exception handler so the
    /// status code it produced is the one recorded.
    /// </summary>
    public static WebApplication UseAlertRequestLogging(this WebApplication app)
    {
        app.UseSerilogRequestLogging(options =>
        {
            options.MessageTemplate =
                "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms";

            // 4xx is the service telling a client no - the rejection this log exists to capture.
            // 5xx is the service itself failing, which is worse and reads as an error.
            options.GetLevel = (http, _, exception) =>
                exception is not null || http.Response.StatusCode >= StatusCodes.Status500InternalServerError
                    ? LogEventLevel.Error
                    : http.Response.StatusCode >= StatusCodes.Status400BadRequest
                        ? LogEventLevel.Warning
                        : LogEventLevel.Information;

            options.EnrichDiagnosticContext = (diagnostic, http) =>
            {
                // Set by SessionAuthFilter once a token has been validated; absent on the very
                // rejections that failed to authenticate, which is itself informative.
                if (http.Items[SessionAuthFilter.PlayerIdItem] is string playerId)
                {
                    diagnostic.Set("PlayerId", playerId);
                }

                // The same id Jaeger indexes the trace under, so an alert line leads straight to
                // the span that produced it.
                if (Activity.Current is { } activity)
                {
                    diagnostic.Set("TraceId", activity.TraceId.ToString());
                }
            };
        });

        return app;
    }

    private static string ResolveDirectory(IWebHostEnvironment environment, AlertLogOptions options) =>
        Path.IsPathRooted(options.Directory)
            ? options.Directory
            : Path.Combine(environment.ContentRootPath, options.Directory);
}
