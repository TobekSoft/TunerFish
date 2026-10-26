using TunerFish.Tests.Support;

namespace TunerFish.Tests;

[Collection(AudioEngineCollection.Name)]
public sealed class MainViewModelBrowsingTests : IDisposable
{
    private readonly VmHarness _h;

    public MainViewModelBrowsingTests(FakeJellyfinServer server) => _h = new VmHarness(server);

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task ArtistFilter_NarrowsList_AndClearingRestoresIt()
    {
        var vm = await _h.SignedInAsync();

        vm.ArtistFilter = "now";
        var only = Assert.Single(vm.Artists);
        Assert.Equal("ar2", only.Id);

        vm.ArtistFilter = "";
        Assert.Equal(FakeJellyfinServer.ArtistCount, vm.Artists.Count);
    }

    [Fact]
    public async Task SelectingArtist_LoadsAlbums_OldestFirst_NoYearLast()
    {
        var vm = await _h.SignedInAsync();

        await VmHarness.SelectArtistAsync(vm, "ar1", 3);

        Assert.Equal("al2,al1,al3", VmHarness.AlbumIds(vm));
        Assert.Equal("3 albums", vm.AlbumCountText);
    }

    [Fact]
    public async Task Covers_AreDecoded_OnlyForAlbumsWithArt()
    {
        var vm = await _h.SignedInAsync();
        await VmHarness.SelectArtistAsync(vm, "ar1", 3);

        await Wait.UntilAsync(() => vm.Albums.First(a => a.Id == "al2").Cover is string, "Revolver's cover to be decoded");
        Assert.Null(vm.Albums.First(a => a.Id == "al1").Cover);
    }

    [Fact]
    public async Task AlbumSort_ReordersAlbums_AndIsSavedAsText()
    {
        var vm = await _h.SignedInAsync();
        await VmHarness.SelectArtistAsync(vm, "ar1", 3);

        vm.AlbumSortIndex = 2;
        Assert.Equal("al1,al2,al3", VmHarness.AlbumIds(vm)); // newest first, no-year last

        vm.AlbumSortIndex = 0;
        Assert.Equal("al1,al2,al3", VmHarness.AlbumIds(vm)); // by title
        Assert.Equal("Abbey Road", vm.Albums[0].Title);

        vm.AlbumSortIndex = 2;
        Assert.Contains("\"AlbumSort\": \"YearDescending\"", _h.SettingsText);
        vm.AlbumSortIndex = 1;
        Assert.Contains("\"AlbumSort\": \"YearAscending\"", _h.SettingsText);
    }

    [Fact]
    public async Task SelectingAlbum_LoadsTracks_WithFormattedFields()
    {
        var vm = await _h.OnRevolverAsync();

        var t = vm.Tracks[0];
        Assert.Equal("1", t.Track);
        Assert.Equal("0:03", t.Length);
        Assert.Equal("96 kHz", t.SampleRate);
        Assert.Equal("24-bit", t.BitDepth);
        Assert.Equal("900 kbps", t.Bitrate);
        Assert.Equal("FLAC", t.Format);
    }
}
