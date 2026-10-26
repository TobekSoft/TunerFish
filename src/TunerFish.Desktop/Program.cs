using System.Runtime.InteropServices;
using Avalonia;
using TunerFish.Services;

namespace TunerFish;

internal static class Program
{
    /// <summary>Where a crash goes: the log, until the main window hands it to its view model (which also marks it for the next start).</summary>
    internal static Action<Exception> OnCrash { get; set; } = e => AppLogFile.Write(LogArea.Crash, AppLog.Crash(e));

    [STAThread]
    public static void Main(string[] args)
    {
        AppLogFile.Start();
        AppLogFile.Write(LogArea.App, $"Started {AppInfo.Name} {AppInfo.Version} on {RuntimeInformation.OSDescription}, .NET {Environment.Version}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception error) OnCrash(error);
            AppLogFile.Stop();
        };
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            AppLogFile.Stop();
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // A fixed WM_CLASS lets the desktop panel match the window to tunerfish.desktop (StartupWMClass).
            .With(new X11PlatformOptions { WmClass = AppInfo.WmClass })
            .LogToTrace();
}
