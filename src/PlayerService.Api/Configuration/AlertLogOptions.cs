using System.ComponentModel.DataAnnotations;
using Serilog.Events;

namespace PlayerService.Api.Configuration;

/// <summary>
/// The alert log: a file that only ever contains things that went wrong — rejected requests,
/// failed operations, unhandled exceptions. Everything the service logs still goes to the console;
/// this is a second, deliberately thin stream so an operator can read a whole day of trouble
/// without scrolling past a day of success.
/// </summary>
/// <remarks>
/// The cut-off is <see cref="MinimumLevel"/>, not a list of interesting events. Any component that
/// logs at Warning or above lands here for free — including code written after this file.
/// </remarks>
public sealed class AlertLogOptions
{
    public const string SectionName = "AlertLog";

    /// <summary>Whether the alert file sink is attached at all. The console sink is unaffected.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Where the files are written. Relative paths resolve against the content root, so a service
    /// started with <c>dotnet run</c> writes beside its project and a container writes under its
    /// working directory. Give an absolute path to pin them somewhere else — a mounted volume, say.
    /// </summary>
    public string Directory { get; set; } = "Alert-Logs";

    /// <summary>
    /// Level at which a log event is considered an alert. Warning covers the rejections (401, 403,
    /// a refused gift); Error and Fatal come along with it, since they are strictly worse.
    /// </summary>
    public LogEventLevel MinimumLevel { get; set; } = LogEventLevel.Warning;

    /// <summary>
    /// How many daily files to keep before the oldest is deleted. One file per day, so this is a
    /// retention window in days.
    /// </summary>
    [Range(1, 3650)]
    public int RetainedFileCountLimit { get; set; } = 30;
}
