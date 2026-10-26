using TunerFish.Services;
using TunerFish.Tests.Support;
using TunerFish.ViewModels;

namespace TunerFish.Tests;

/// <summary>The search box: artist names as you type, album and track titles from Jellyfin's search.</summary>
[Collection(AudioEngineCollection.Name)]
public sealed class MainViewModelSearchTests : IDisposable
{
    private readonly VmHarness _h;

    public MainViewModelSearchTests(FakeJellyfinServer server) => _h = new VmHarness(server);

    public void Dispose() => _h.Dispose();

    private static string ArtistIds(MainViewModel vm) => string.Join(",", vm.Artists.Select(a => a.Id));

    private static Task SearchAsync(MainViewModel vm, string text, string expectedArtists)
    {
        vm.ArtistFilter = text;
        return Wait.UntilAsync(() => !vm.IsSearching && ArtistIds(vm) == expectedArtists, $"\"{text}\" to find {expectedArtists}",
                               state: () => $"{VmHarness.Describe(vm)}, Artists={ArtistIds(vm)}");
    }

    [Fact]
    public async Task AnAlbumTitle_ListsItsArtist_SayingWhatMatched_WithOnlyThatAlbum_UnlessShowAll()
    {
        var vm = await _h.SignedInAsync();

        await SearchAsync(vm, "abbey", "ar1");
        Assert.Equal("Abbey Road", vm.Artists[0].Note);

        vm.SelectedArtist = vm.Artists[0];
        await Wait.UntilAsync(() => vm.AlbumsLimited && vm.Albums.Count == 1, "only Abbey Road", state: () => VmHarness.Describe(vm));
        Assert.Equal("al1", VmHarness.AlbumIds(vm));
        Assert.Equal("Matching: 1 of 3 albums", vm.AlbumsLimitText);

        vm.ToggleShowAllAlbumsCommand.Execute(null);
        Assert.Equal(3, vm.Albums.Count);
        Assert.Equal("Matching only", vm.ShowAllAlbumsLabel);
    }

    [Fact]
    public async Task AnArtistName_KeepsAllItsAlbums_AndNeedsNoServer()
    {
        var vm = await _h.SignedInAsync();

        vm.ArtistFilter = "e";       // one letter: artist names only
        Assert.False(vm.IsSearching);
        await SearchAsync(vm, "beatles", "ar1");
        Assert.Null(vm.Artists[0].Note);

        await VmHarness.SelectArtistAsync(vm, "ar1", 3);
        Assert.False(vm.AlbumsLimited);
    }

    [Fact]
    public async Task Tracks_FindEveryArtistWithTheTitle_AndTheScopeIsRemembered()
    {
        var vm = await _h.SignedInAsync();
        Assert.True(vm.SearchArtists && vm.SearchAlbums && !vm.SearchTracks);

        vm.SearchTracks = true;
        await SearchAsync(vm, "song 2", "ar1,ar4,ar2"); // every fake album has a Song 2
        Assert.Equal("Song 2 (Abbey Road) +2", vm.Artists[0].Note);

        vm.SearchArtists = false;
        vm.SearchAlbums = false;
        vm.SearchTracks = false; // the last one stays on
        Assert.True(vm.SearchTracks);
        Assert.Contains("\"FilterScope\": \"Tracks\"", _h.SettingsText);
    }

    [Fact]
    public async Task NothingFound_SaysSo()
    {
        var vm = await _h.SignedInAsync();

        await SearchAsync(vm, "zzz", "");

        Assert.Equal("No matches.", vm.ArtistPaneNote);
    }

    [Fact]
    public async Task InAView_OnlyTheViewIsSearched()
    {
        _h.Server.SetMarks("ec3", favourite: true);
        var vm = await _h.SignedInAsync();
        vm.View = LibraryView.Favourites;
        await Wait.UntilAsync(() => ArtistIds(vm) == "ar4", "Favourites to list Elvis Costello");

        await SearchAsync(vm, "letters", "ar4");
        await SearchAsync(vm, "model", ""); // This Year's Model isn't a favourite
        Assert.Equal("No matches.", vm.ArtistPaneNote);
    }

    [Fact]
    public async Task SearchingFromAPlayedView_GoesToTheLibrary()
    {
        var vm = await _h.SignedInAsync();
        vm.View = LibraryView.RecentlyPlayed;

        await SearchAsync(vm, "abbey", "ar1");

        Assert.Equal(LibraryView.Library, vm.View);
    }
}
