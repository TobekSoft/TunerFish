using System.Reflection;

namespace TunerFish;

/// <summary>
/// The app's user-visible identity, in one place. Renaming the app means changing <see cref="Name"/>
/// (plus Name= in tunerfish.desktop). Namespaces, the executable and the data folders
/// (~/.config/TunerFish etc.) deliberately don't follow it, so a rename keeps your settings.
/// </summary>
public static class AppInfo
{
    public const string Name = "TunerFish";

    public const string Description = "Album-first music player for Jellyfin.";

    /// <summary>Shown in About; the Android app's About says the same.</summary>
    public const string Credit = "Design by Tobeksoft. Code by Claude (Anthropic).";

    public const string Copyright = "© 2026 Tobeksoft. MIT License.";

    /// <summary>X11 WM_CLASS. Must match StartupWMClass in tunerfish.desktop; keep it stable across renames.</summary>
    public const string WmClass = "tunerfish";

    /// <summary>From the assembly's informational version (Version in the csproj), without any +commit suffix.</summary>
    public static string Version { get; } = VersionOf(typeof(AppInfo).Assembly);

    public static string VersionOf(Assembly assembly)
    {
        var v = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? assembly.GetName().Version?.ToString() ?? "?";
        var plus = v.IndexOf('+');
        return plus >= 0 ? v[..plus] : v;
    }
}
