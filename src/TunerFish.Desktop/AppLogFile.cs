using Serilog;
using TunerFish.Services;

namespace TunerFish;

/// <summary>
/// Writes the app's log (<see cref="AppLog"/>) to daily files in <see cref="AppPaths.LogDir"/>, a month of them.
/// Each line is written straight away, so a crash can't lose it; several running copies can share a file.
/// </summary>
internal static class AppLogFile
{
    public static string Directory => AppPaths.Current.LogDir;

    public static void Start()
    {
        Log.Logger = new LoggerConfiguration()
            .WriteTo.File(Path.Combine(Directory, "tunerfish-.log"), rollingInterval: RollingInterval.Day,
                          retainedFileCountLimit: 31, shared: true,
                          outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss}  {Area,-9:l} {Message:l}{NewLine}")
            .CreateLogger();
    }

    /// <summary>Never fails: a log mustn't break the app.</summary>
    public static void Write(LogArea area, string message)
    {
        try { Log.ForContext("Area", area.ToString()).Information("{Text}", AppLog.Clean(message)); }
        catch { /* nowhere to say so */ }
    }

    public static void Stop() => Log.CloseAndFlush();
}
