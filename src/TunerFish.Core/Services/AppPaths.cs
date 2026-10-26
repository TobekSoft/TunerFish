namespace TunerFish.Services;

/// <summary>
/// Where TunerFish keeps its files: the one place that knows each OS's folders. Linux follows XDG and is
/// what's used and tested; Windows (maybe one day) and macOS (left possible for others) are untested.
/// Nothing here creates a folder: each store makes its own when it first writes.
/// </summary>
public sealed record AppPaths(string SettingsFile, string DatabaseFile, string CoverDir, string LogDir)
{
    public enum Os { Linux, Windows, MacOS }

    private const string Folder = "TunerFish"; // not the display name: a rename keeps your files

    /// <summary>This machine's.</summary>
    public static AppPaths Current { get; } = For(
        OperatingSystem.IsWindows() ? Os.Windows : OperatingSystem.IsMacOS() ? Os.MacOS : Os.Linux,
        Environment.GetEnvironmentVariable,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <param name="env">Reads an environment variable (for tests).</param>
    public static AppPaths For(Os os, Func<string, string?> env, string home)
    {
        switch (os)
        {
            case Os.Windows:
                var roaming = Path.Join(Given(env("APPDATA")) ?? Path.Join(home, "AppData", "Roaming"), Folder);
                var local = Path.Join(Given(env("LOCALAPPDATA")) ?? Path.Join(home, "AppData", "Local"), Folder);
                return new(Path.Join(roaming, "settings.json"), Path.Join(local, "tunerfish.db"),
                           Path.Join(local, "covers"), Path.Join(local, "Logs"));

            case Os.MacOS:
                var library = Path.Join(home, "Library");
                var support = Path.Join(library, "Application Support", Folder);
                return new(Path.Join(support, "settings.json"), Path.Join(support, "tunerfish.db"),
                           Path.Join(library, "Caches", Folder, "covers"), Path.Join(library, "Logs", Folder));

            default:
                string Xdg(string variable, params string[] fallback) =>
                    Path.Join(Rooted(env(variable)) ?? Path.Join([home, .. fallback]), Folder);
                return new(Path.Join(Xdg("XDG_CONFIG_HOME", ".config"), "settings.json"),
                           Path.Join(Xdg("XDG_DATA_HOME", ".local", "share"), "tunerfish.db"),
                           Path.Join(Xdg("XDG_CACHE_HOME", ".cache"), "covers"),
                           Path.Join(Xdg("XDG_STATE_HOME", ".local", "state"), "logs"));
        }
    }

    private static string? Given(string? path) => string.IsNullOrEmpty(path) ? null : path;

    /// <summary>The XDG spec says to ignore a relative path.</summary>
    private static string? Rooted(string? path) => Given(path) is { } p && Path.IsPathRooted(p) ? p : null;
}
