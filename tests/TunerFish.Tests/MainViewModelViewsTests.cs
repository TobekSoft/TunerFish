using TunerFish.Services;
using TunerFish.Tests.Support;

namespace TunerFish.Tests;

[Collection(AudioEngineCollection.Name)]
public sealed class MainViewModelViewsTests : IDisposable
{
    private readonly VmHarness _h;

    public MainViewModelViewsTests(FakeJellyfinServer server) => _h = new VmHarness(server);

    public void Dispose() => _h.Dispose();

    private static string ArtistIds(ViewModels.MainViewModel vm) => string.Join(",", vm.Artists.Select(a => a.Id));

    private static async Task ShowViewAsync(ViewModels.MainViewModel vm, LibraryView view, int expectedAlbums)
    {
        vm.View = view;
        await Wait.UntilAsync(() => vm.AlbumPaneNote is not null || vm.Albums.Count == expectedAlbums && vm.Albums.Count > 0,
                              $"{view} to load {expectedAlbums} albums", state: () => VmHarness.Describe(vm));
        Assert.Equal(expectedAlbums, vm.Albums.Count);
    }

    [Fact]
    public async Task Favourites_ListTheFavouriteArtists_AndThoseWithFavouriteAlbums_ThenTheAlbumsFiledByArtist()
    {
        _h.Server.SetMarks("ar4", favourite: true);
        _h.Server.SetMarks("ar7", favourite: true);
        foreach (var id in new[] { "ec3", "al2", "al1", "al9" }) _h.Server.SetMarks(id, favourite: true);
        var vm = await _h.SignedInAsync();

        await ShowViewAsync(vm, LibraryView.Favourites, 4);

        Assert.Equal("ar7,ar1,ar4,ar2", ArtistIds(vm)); // 10cc (#), Beatles, Elvis Costello, Now That's...
        // Beatles: Abbey Road, Revolver; Costello: Juliet Letters; Now That's What I Call Music 5.
        Assert.Equal("al1,al2,ec3,al9", VmHarness.AlbumIds(vm));
        Assert.Equal("Favourites", vm.AlbumsHeader);
        Assert.Equal("The Beatles · 1969", vm.Albums[0].Caption);
        Assert.True(vm.ShowingViewAlbums);
    }

    [Fact]
    public async Task PickingAMarkedArtistInAView_ShowsAllItsAlbums_AndYouCanGoBack()
    {
        _h.Server.SetMarks("ar1", favourite: true);
        _h.Server.SetMarks("al2", favourite: true);
        var vm = await _h.SignedInAsync();
        await ShowViewAsync(vm, LibraryView.Favourites, 1);

        await VmHarness.SelectArtistAsync(vm, "ar1", 3);
        Assert.Equal("The Beatles", vm.AlbumsHeader);
        Assert.Equal("1966", vm.Albums[0].Caption); // the artist's own albums show just the year
        Assert.False(vm.AlbumsLimited);

        vm.ShowViewAlbumsCommand.Execute(null);
        await Wait.UntilAsync(() => vm.Albums.Count == 1, "the view's albums again");
        Assert.Null(vm.SelectedArtist);
        Assert.Equal("Favourites", vm.AlbumsHeader);
    }

    [Fact]
    public async Task PickingAnArtistWithMarkedAlbums_ShowsOnlyThose_UnlessShowAll()
    {
        _h.Server.SetMarks("ec3", favourite: true);
        var vm = await _h.SignedInAsync();
        await ShowViewAsync(vm, LibraryView.Favourites, 1);
        Assert.Equal("ar4", ArtistIds(vm));

        vm.SelectedArtist = vm.Artists[0];
        await Wait.UntilAsync(() => vm.AlbumsLimited, "Elvis Costello's favourite albums", state: () => VmHarness.Describe(vm));
        Assert.Equal("ec3", VmHarness.AlbumIds(vm));
        Assert.Equal("Favourites: 1 of 4 albums", vm.AlbumsLimitText);
        Assert.Equal("Show all", vm.ShowAllAlbumsLabel);

        vm.ShowAllAlbums = true;
        Assert.Equal(4, vm.Albums.Count);
        Assert.Equal("All 4 albums", vm.AlbumsLimitText);
        Assert.Equal("Favourites only", vm.ShowAllAlbumsLabel);

        // A heart given while showing all counts at once.
        vm.Albums.Single(a => a.Id == "ec1").ToggleFavouriteCommand!.Execute(null);
        vm.ShowAllAlbums = false;
        Assert.Equal("Favourites: 2 of 4 albums", vm.AlbumsLimitText);
        await Wait.UntilAsync(() => _h.Server.MarksOf("ec1").Favourite, "the heart to reach the server");
    }

    [Fact]
    public async Task AHiddenArtist_HidesItsMarkedAlbums_UnlessShowHidden()
    {
        _h.Server.SetMarks("ar4", likes: false);
        _h.Server.SetMarks("ec3", favourite: true);
        _h.Server.SetMarks("al2", favourite: true);
        var vm = await _h.SignedInAsync(listedArtists: FakeJellyfinServer.ArtistCount - 1);

        await ShowViewAsync(vm, LibraryView.Favourites, 1);
        Assert.Equal("ar1", ArtistIds(vm));
        Assert.Equal("al2", VmHarness.AlbumIds(vm));

        vm.ShowHidden = true;
        Assert.Equal("ar1,ar4", ArtistIds(vm));
        Assert.Equal("al2,ec3", VmHarness.AlbumIds(vm));
    }

    [Fact]
    public async Task Unmarking_TakesItOutOfTheView_AtOnce()
    {
        foreach (var id in new[] { "al1", "al2" }) _h.Server.SetMarks(id, likes: true);
        var vm = await _h.SignedInAsync();
        await ShowViewAsync(vm, LibraryView.Liked, 2);

        vm.Albums.Single(a => a.Id == "al1").ToggleLikeCommand!.Execute(null);

        Assert.Equal("al2", VmHarness.AlbumIds(vm));
        await Wait.UntilAsync(() => _h.Server.MarksOf("al1").Likes is null, "the like to be cleared on the server");
    }

    [Fact]
    public async Task Hidden_ListsTheHiddenThings_EvenWithShowHiddenOff()
    {
        _h.Server.SetMarks("ar6", likes: false);
        _h.Server.SetMarks("ec2", likes: false);
        _h.Server.SetMarks("al1", likes: true); // Jellyfin's Dislikes would match this too
        var vm = await _h.SignedInAsync(listedArtists: FakeJellyfinServer.ArtistCount - 1);
        Assert.False(vm.ShowHidden);

        await ShowViewAsync(vm, LibraryView.Hidden, 1);

        Assert.Equal("ar6,ar4", ArtistIds(vm)); // Björk whole; Elvis Costello for My Aim Is True
        Assert.Equal("ec2", VmHarness.AlbumIds(vm));
    }

    [Fact]
    public async Task HiddenAlbums_StayOutOfTheOtherViews_UnlessShown()
    {
        _h.Server.SetMarks("al1", favourite: true);
        _h.Server.SetMarks("al2", favourite: true, likes: false);
        var vm = await _h.SignedInAsync();

        await ShowViewAsync(vm, LibraryView.Favourites, 1);
        Assert.Equal("al1", VmHarness.AlbumIds(vm));

        vm.ShowHidden = true;
        Assert.Equal("al1,al2", VmHarness.AlbumIds(vm));
    }

    [Fact]
    public async Task RecentlyPlayed_ListsAlbumsByTheirLatestPlay_AndNoArtists()
    {
        _h.Server.SetPlays("ec1-t1", 9, new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc));
        _h.Server.SetPlays("t2", 1, new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc));
        _h.Server.SetPlays("al1-t3", 1, new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc));
        var vm = await _h.SignedInAsync();

        await ShowViewAsync(vm, LibraryView.RecentlyPlayed, 3);

        Assert.Equal("al2,al1,ec1", VmHarness.AlbumIds(vm));
        Assert.Empty(vm.Artists);
        Assert.Equal("Recently played lists albums, not artists.", vm.ArtistPaneNote);
    }

    [Fact]
    public async Task MostPlayed_IsPlaysThroughTheWholeAlbum()
    {
        var day = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        _h.Server.SetPlays("ec1-t1", 5, day);                     // one track to death: 5 / 3
        foreach (var t in new[] { "t1", "t2", "t3" }) _h.Server.SetPlays(t, 2, day); // Revolver twice through: 6 / 3
        var vm = await _h.SignedInAsync();

        await ShowViewAsync(vm, LibraryView.MostPlayed, 2);

        Assert.Equal("al2,ec1", VmHarness.AlbumIds(vm));
    }

    [Fact]
    public async Task AnEmptyView_SaysWhy()
    {
        var vm = await _h.SignedInAsync();

        await ShowViewAsync(vm, LibraryView.Favourites, 0);

        Assert.StartsWith("No favourite albums.", vm.AlbumPaneNote);
        Assert.Equal("No favourites.", vm.ArtistPaneNote);
    }

    [Fact]
    public async Task BackToTheLibrary_ListsEveryArtist_OnTheLastOneViewed()
    {
        _h.Server.SetMarks("ar1", favourite: true);
        var vm = await _h.SignedInAsync();
        await VmHarness.SelectArtistAsync(vm, "ar4", 4);
        await ShowViewAsync(vm, LibraryView.Favourites, 0);
        Assert.Equal("ar1", ArtistIds(vm));

        vm.View = LibraryView.Library;

        Assert.Equal(FakeJellyfinServer.ArtistCount, vm.Artists.Count);
        Assert.Equal("ar4", vm.SelectedArtist?.Id);
        await Wait.UntilAsync(() => vm.Albums.Count == 4, "Elvis Costello's albums again");
        Assert.Null(vm.ArtistPaneNote);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AnAlbumFromAView_PlaysLikeAnyOther()
    {
        LibMpv.SkipIfUnavailable();
        _h.Server.SetMarks("al2", favourite: true);
        var vm = await _h.SignedInAsync();
        await ShowViewAsync(vm, LibraryView.Favourites, 1);
        await VmHarness.SelectAlbumAsync(vm, "al2");

        await vm.PlayFromTrackAsync(vm.Tracks[1]);

        await Wait.UntilAsync(() => vm.CurrentTrack?.Id == "t2", "Revolver's second track to play", state: () => VmHarness.Describe(vm));
        Assert.Equal(3, vm.Queue.Count);
    }
}
