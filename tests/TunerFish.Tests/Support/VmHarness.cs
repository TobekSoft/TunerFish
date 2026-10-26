using System.Collections.Concurrent;
using TunerFish.Services;
using TunerFish.ViewModels;

namespace TunerFish.Tests.Support;

/// <summary>
/// Builds MainViewModels the way the app does, but with settings, cover cache and database in a
/// private temp directory. Disposes every view model it made, then deletes the directory.
/// Creating a second view model over the same harness is how tests simulate restarting the app.
/// </summary>
public sealed class VmHarness : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly List<MainViewModel> _made = new();

    public FakeJellyfinServer Server { get; }
    public string SettingsPath => _dir.File("config", "settings.json");
    public string CoverDir => _dir.File("cache", "covers");
    public string DbPath => _dir.File("data", "tunerfish.db");

    public VmHarness(FakeJellyfinServer server)
    {
        Server = server;
        server.ResetUserData(); // each test starts with no marks and no plays
    }

    /// <summary>Stands in for the Avalonia bitmap decoder.</summary>
    public static object? Decode(string file, int size) => $"bitmap:{new FileInfo(file).Length}";

    public string SettingsText => File.ReadAllText(SettingsPath);

    /// <summary>What the view models wrote to the app's log, as "Area: message".</summary>
    public ConcurrentQueue<string> Logged { get; } = new();

    public MainViewModel Create(string appName = "TunerFish")
    {
        var vm = new MainViewModel(new SettingsStore(SettingsPath), Decode, silentAudio: true,
                                   coverCacheDir: CoverDir, appName: appName, databasePath: DbPath,
                                   log: (area, message) => Logged.Enqueue($"{area}: {message}"));
        _made.Add(vm);
        return vm;
    }

    /// <summary>
    /// Starts a view model and, if it has no saved session, signs in through the login form.
    /// <paramref name="listedArtists"/>: how many artists to wait for (fewer when some are hidden).
    /// </summary>
    public async Task<MainViewModel> SignedInAsync(int listedArtists = FakeJellyfinServer.ArtistCount)
    {
        var vm = Create();
        await vm.StartAsync();
        if (!vm.IsLoggedIn)
        {
            vm.ServerUrl = Server.Url;
            vm.UserName = FakeJellyfinServer.UserName;
            vm.Password = FakeJellyfinServer.Password;
            vm.LoginCommand.Execute(null);
        }
        await Wait.UntilAsync(() => vm.IsLoggedIn && vm.Artists.Count == listedArtists, "sign-in to load the album artists", state: () => Describe(vm));
        return vm;
    }

    public static async Task SelectArtistAsync(MainViewModel vm, string artistId, int expectedAlbums)
    {
        vm.SelectedArtist = vm.Artists.First(a => a.Id == artistId);
        await Wait.UntilAsync(() => vm.Albums.Count == expectedAlbums, $"{expectedAlbums} albums for {artistId}", state: () => Describe(vm));
    }

    public static async Task SelectAlbumAsync(MainViewModel vm, string albumId)
    {
        vm.SelectedAlbum = vm.Albums.First(a => a.Id == albumId);
        await Wait.UntilAsync(() => vm.Tracks.Count == 3, $"3 tracks for {albumId}", state: () => Describe(vm));
    }

    /// <summary>Signs in and opens The Beatles → Revolver (the album with a cover).</summary>
    public async Task<MainViewModel> OnRevolverAsync()
    {
        var vm = await SignedInAsync();
        await SelectArtistAsync(vm, "ar1", 3);
        await SelectAlbumAsync(vm, "al2");
        return vm;
    }

    public static string Describe(MainViewModel vm) =>
        $"IsLoggedIn={vm.IsLoggedIn}, Status=\"{vm.Status}\", Artists={vm.Artists.Count}, Albums={vm.Albums.Count}, " +
        $"Tracks={vm.Tracks.Count}, CurrentTrack={vm.CurrentTrack?.Id ?? "none"}, IsPaused={vm.IsPaused}, " +
        $"Position={vm.PositionText}/{vm.DurationText}";

    public static string ArtistNames(MainViewModel vm) => string.Join(" | ", vm.Artists.Select(a => a.Name));
    public static string AlbumIds(MainViewModel vm) => string.Join(",", vm.Albums.Select(a => a.Id));
    public static string AlbumTitles(MainViewModel vm) => string.Join(" | ", vm.Albums.Select(a => a.Title));

    public void Dispose()
    {
        foreach (var vm in _made) vm.Dispose();
        _dir.Dispose();
    }
}
