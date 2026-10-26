using TunerFish.Services;

namespace TunerFish.Tests;

public sealed class AppPathsTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] vars) =>
        name => vars.FirstOrDefault(v => v.Name == name).Value;

    [Fact]
    public void Linux_KeepsEverythingWhereItAlwaysWas_AndLogsGoInState()
    {
        var p = AppPaths.For(AppPaths.Os.Linux, Env(), "/home/me");

        Assert.Equal("/home/me/.config/TunerFish/settings.json", p.SettingsFile);
        Assert.Equal("/home/me/.local/share/TunerFish/tunerfish.db", p.DatabaseFile);
        Assert.Equal("/home/me/.cache/TunerFish/covers", p.CoverDir);
        Assert.Equal("/home/me/.local/state/TunerFish/logs", p.LogDir);
    }

    [Fact]
    public void Linux_FollowsTheXdgVariables_ButNotARelativeOne()
    {
        var p = AppPaths.For(AppPaths.Os.Linux, Env(("XDG_CONFIG_HOME", "/cfg"), ("XDG_DATA_HOME", "/data"),
                                                    ("XDG_CACHE_HOME", "/cache"), ("XDG_STATE_HOME", "relative/state")), "/home/me");

        Assert.Equal("/cfg/TunerFish/settings.json", p.SettingsFile);
        Assert.Equal("/data/TunerFish/tunerfish.db", p.DatabaseFile);
        Assert.Equal("/cache/TunerFish/covers", p.CoverDir);
        Assert.Equal("/home/me/.local/state/TunerFish/logs", p.LogDir);
    }

    [Fact]
    public void Windows_SettingsRoam_TheRestStaysOnTheMachine()
    {
        const string roaming = @"C:\Users\me\AppData\Roaming", local = @"C:\Users\me\AppData\Local";
        var p = AppPaths.For(AppPaths.Os.Windows, Env(("APPDATA", roaming), ("LOCALAPPDATA", local), ("XDG_CACHE_HOME", "/cache")), @"C:\Users\me");

        Assert.Equal(Path.Join(roaming, "TunerFish", "settings.json"), p.SettingsFile);
        Assert.Equal(Path.Join(local, "TunerFish", "tunerfish.db"), p.DatabaseFile);
        Assert.Equal(Path.Join(local, "TunerFish", "covers"), p.CoverDir); // no XDG on Windows
        Assert.Equal(Path.Join(local, "TunerFish", "Logs"), p.LogDir);
    }

    [Fact]
    public void MacOS_UsesTheLibraryFolders()
    {
        var p = AppPaths.For(AppPaths.Os.MacOS, Env(("XDG_CONFIG_HOME", "/cfg")), "/Users/me");

        Assert.Equal("/Users/me/Library/Application Support/TunerFish/settings.json", p.SettingsFile);
        Assert.Equal("/Users/me/Library/Application Support/TunerFish/tunerfish.db", p.DatabaseFile);
        Assert.Equal("/Users/me/Library/Caches/TunerFish/covers", p.CoverDir);
        Assert.Equal("/Users/me/Library/Logs/TunerFish", p.LogDir);
    }
}
