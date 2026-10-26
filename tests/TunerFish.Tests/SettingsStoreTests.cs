using TunerFish.Services;
using TunerFish.Tests.Support;

namespace TunerFish.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly TempDir _dir = new();
    private string SettingsPath => _dir.File("config", "settings.json");

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void MissingFile_GivesDefaults()
    {
        var s = new SettingsStore(SettingsPath).Load();

        Assert.Equal(nameof(AlbumSortMode.YearAscending), s.AlbumSort);
        Assert.Equal(80, s.Volume);
        Assert.Null(s.AccessToken);
    }

    [Fact]
    public void SaveThenLoad_RoundTrips_WithChoicesStoredAsWords()
    {
        var store = new SettingsStore(SettingsPath);
        var s = store.Load();
        s.AlbumSort = nameof(AlbumSortMode.YearDescending);
        s.Volume = 35;
        s.LastArtistId = "ar2";
        store.Save(s);

        var loaded = new SettingsStore(SettingsPath).Load();

        Assert.Equal("YearDescending", loaded.AlbumSort);
        Assert.Equal(35, loaded.Volume);
        Assert.Equal("ar2", loaded.LastArtistId);
        Assert.Equal(s.DeviceId, loaded.DeviceId);
        Assert.Contains("\"AlbumSort\": \"YearDescending\"", File.ReadAllText(SettingsPath));
        Assert.False(File.Exists(SettingsPath + ".tmp"));
    }

    [Fact]
    public void Save_MakesFileReadableByOwnerOnly()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes only");
        var store = new SettingsStore(SettingsPath);
        store.Save(store.Load());

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(SettingsPath));
    }

    [Fact]
    public void UnreadableFile_IsKeptAside_AndDefaultsUsed()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, "{ this is not json");
        var store = new SettingsStore(SettingsPath);

        var s = store.Load();

        Assert.Equal(80, s.Volume);
        Assert.NotNull(store.LoadWarning);
        var backup = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(SettingsPath)!, "settings.json.unreadable-*"));
        Assert.Equal("{ this is not json", File.ReadAllText(backup));
    }

    [Fact]
    public void UnknownKeysAndComments_AreTolerated()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, """
            {
              // from a newer version
              "SomeFutureSetting": 7,
              "Volume": 42,
            }
            """);

        var store = new SettingsStore(SettingsPath);
        var s = store.Load();

        Assert.Equal(42, s.Volume);
        Assert.Null(store.LoadWarning);
    }

    [Fact]
    public void RetiredFilingKeys_FromOlderVersions_AreIgnored()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, """
            { "IgnoreArticles": false, "SortArticles": "The, A", "IgnoreArticlesInAlbumTitles": true, "Volume": 42 }
            """);

        var store = new SettingsStore(SettingsPath);
        var s = store.Load();

        Assert.Equal(42, s.Volume);
        Assert.Null(store.LoadWarning);
    }
}
