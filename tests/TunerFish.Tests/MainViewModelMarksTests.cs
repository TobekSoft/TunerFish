using TunerFish.Services;
using TunerFish.Tests.Support;

namespace TunerFish.Tests;

[Collection(AudioEngineCollection.Name)]
public sealed class MainViewModelMarksTests : IDisposable
{
    private readonly VmHarness _h;

    public MainViewModelMarksTests(FakeJellyfinServer server) => _h = new VmHarness(server);

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task MarksFromTheServer_AreShownOnArtistsAndAlbums()
    {
        _h.Server.SetMarks("ar5", favourite: true);
        _h.Server.SetMarks("al1", likes: true);
        var vm = await _h.SignedInAsync();
        await VmHarness.SelectArtistAsync(vm, "ar1", 3);

        Assert.True(vm.Artists.Single(a => a.Id == "ar5").IsFavourite);
        var abbeyRoad = vm.Albums.Single(a => a.Id == "al1");
        Assert.True(abbeyRoad.IsLiked);
        Assert.False(abbeyRoad.IsFavourite);
        Assert.False(vm.Albums.Single(a => a.Id == "al2").HasMarks);
    }

    [Fact]
    public async Task TheHeartAndThumbs_AreSentToTheServer_AndShownAtOnce()
    {
        var vm = await _h.OnRevolverAsync();
        var revolver = vm.SelectedAlbum!;

        revolver.ToggleFavouriteCommand!.Execute(null);
        Assert.True(revolver.IsFavourite);
        await Wait.UntilAsync(() => _h.Server.MarksOf("al2").Favourite, "the heart to reach the server");

        revolver.ToggleLikeCommand!.Execute(null);
        await Wait.UntilAsync(() => _h.Server.MarksOf("al2").Likes == true, "the like to reach the server");

        revolver.ToggleLikeCommand!.Execute(null);
        await Wait.UntilAsync(() => _h.Server.MarksOf("al2").Likes is null, "the like to be cleared");
        Assert.True(_h.Server.MarksOf("al2").Favourite); // the heart stays
    }

    [Fact]
    public async Task HidingAnArtist_LeavesItOutOfTheList_UnlessHiddenOnesAreShown()
    {
        var vm = await _h.SignedInAsync();
        var abba = vm.Artists.Single(a => a.Id == "ar5");

        await vm.SetMarksAsync(abba, abba.Marks.WithHidden(true));

        Assert.Equal((false, (bool?)false), _h.Server.MarksOf("ar5"));
        Assert.DoesNotContain(vm.Artists, a => a.Id == "ar5");
        Assert.False(vm.JumpGroups.Single(g => g.Group == "A").HasArtists); // ABBA was the only A

        vm.ShowHidden = true;
        Assert.Contains(vm.Artists, a => a.Id == "ar5");
        Assert.Contains("\"ShowHidden\": true", _h.SettingsText);
    }

    [Fact]
    public async Task HidingAnAlbum_LeavesItOutOfTheArtistsAlbums()
    {
        _h.Server.SetMarks("al3", likes: false);
        var vm = await _h.SignedInAsync();
        await VmHarness.SelectArtistAsync(vm, "ar1", 2);
        Assert.Equal("al2,al1", VmHarness.AlbumIds(vm));
        Assert.Equal("2 albums", vm.AlbumCountText);

        vm.ShowHidden = true;
        Assert.Equal("al2,al1,al3", VmHarness.AlbumIds(vm));

        vm.ShowHidden = false;
        var abbeyRoad = vm.Albums.Single(a => a.Id == "al1");
        abbeyRoad.ToggleHiddenCommand!.Execute(null);
        Assert.Equal("al2", VmHarness.AlbumIds(vm));
        await Wait.UntilAsync(() => _h.Server.MarksOf("al1").Likes == false, "the hide to reach the server");
    }

    [Fact]
    public async Task HidingALikedAlbum_ClearsTheLike()
    {
        _h.Server.SetMarks("al2", favourite: true, likes: true);
        var vm = await _h.OnRevolverAsync();

        await vm.SetMarksAsync(vm.SelectedAlbum!, vm.SelectedAlbum!.Marks.WithHidden(true));

        Assert.Equal((true, (bool?)false), _h.Server.MarksOf("al2"));
        Assert.False(vm.SelectedAlbum!.IsLiked);
    }

    [Fact]
    public async Task AChangeTheServerRefuses_IsUndone_AndSaysSo()
    {
        var vm = await _h.SignedInAsync();
        var abba = vm.Artists.Single(a => a.Id == "ar5");
        _h.Server.FailMarkChanges = true;

        await vm.SetMarksAsync(abba, abba.Marks.WithFavourite(true));

        Assert.False(abba.IsFavourite);
        Assert.Contains("Couldn't save the change to ABBA", vm.Status);
        Assert.Contains(_h.Logged, l => l.StartsWith("Marks: Couldn't save the change to ABBA", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Tracks_ShowTheirPlayCounts()
    {
        _h.Server.SetPlays("t2", 7, new DateTime(2026, 10, 24, 8, 30, 0, DateTimeKind.Utc));
        var vm = await _h.OnRevolverAsync();

        Assert.Equal("", vm.Tracks[0].Plays);
        Assert.Equal("7", vm.Tracks[1].Plays);
        Assert.StartsWith("2026-10-2", vm.Tracks[1].LastPlayed); // local time
        Assert.Contains(ViewModels.TrackColumns.All, c => c.Key == "plays");
    }
}
