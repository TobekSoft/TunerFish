using TunerFish.Models;
using TunerFish.Services;

namespace TunerFish.Tests;

/// <summary>Mirrors the Android app's ViewsTest case for case.</summary>
public sealed class LibraryViewsTests
{
    private sealed record Item(string Id, Marks Marks, string ArtistKey = "", string SortTitle = "") : IMarked;

    private static Item I(string id, Marks marks = default) => new(id, marks);
    private static IEnumerable<string> Ids(IEnumerable<Item> items) => items.Select(x => x.Id);

    private static readonly Marks Fav = new(Favourite: true);
    private static readonly Marks Liked = new(Likes: true);
    private static readonly Marks Hidden = new(Likes: false);
    private static readonly Marks FavHidden = new(Favourite: true, Likes: false);

    [Fact]
    public void MarkViews_KeepTheirMark_InTheGivenOrder()
    {
        var all = new[] { I("b", Liked), I("a", Fav), I("c"), I("d", Fav) };
        Assert.Equal(new[] { "a", "d" }, Ids(LibraryView.Favourites.Albums(all, showHidden: false)));
        Assert.Equal(new[] { "b" }, Ids(LibraryView.Liked.Albums(all, showHidden: false)));
    }

    [Fact]
    public void HiddenThings_ShowOnlyInHidden_OrWithShowHidden()
    {
        var all = new[] { I("a", Fav), I("b", FavHidden), I("c", Hidden) };
        Assert.Equal(new[] { "a" }, Ids(LibraryView.Favourites.Albums(all, showHidden: false)));
        Assert.Equal(new[] { "a", "b" }, Ids(LibraryView.Favourites.Albums(all, showHidden: true)));
        Assert.Equal(new[] { "b", "c" }, Ids(LibraryView.Hidden.Albums(all, showHidden: false)));
        Assert.Equal(new[] { "a" }, Ids(LibraryView.RecentlyPlayed.Albums(all, showHidden: false)));
    }

    private sealed record Artist(string Id, Marks Marks = default) : IListedArtist
    {
        public string Name { get; init; } = Id;
        public string SortKey => Name.ToLowerInvariant();
    }

    private sealed record Album(string Id, string? ArtistId, Marks Marks = default) : IArtistAlbum
    {
        public string ArtistName { get; init; } = ArtistId ?? "";
    }

    private static readonly Artist[] Library = { new("abba"), new("beatles", Fav), new("cure"), new("doors", Hidden) };
    private static readonly Album[] NoAlbums = Array.Empty<Album>();

    private static FilteredArtists<Artist> Filter(LibraryView view, IEnumerable<Album> albums, bool showHidden) =>
        view.Filter(Library, albums, showHidden, (id, _) => new Artist(id));

    private static IEnumerable<string> Ids(FilteredArtists<Artist> v) => v.Artists.Select(a => a.Id);

    /// <summary>The albums the artist shows, in order of id, or null for all of them.</summary>
    private static string[]? AlbumsOf(FilteredArtists<Artist> v, string artistId) => v.AlbumsOf(artistId)?.Order().ToArray();

    [Fact]
    public void AMarkedArtist_IsListedWithAllItsAlbums()
    {
        var v = Filter(LibraryView.Favourites, NoAlbums, showHidden: false);
        Assert.Equal(new[] { "beatles" }, Ids(v));
        Assert.Null(v.AlbumsOf("beatles"));
    }

    [Fact]
    public void AnArtistWithMarkedAlbums_IsListedWithOnlyThose()
    {
        var albums = new[] { new Album("arrival", "abba", Fav), new Album("waterloo", "abba"), new Album("gold", "abba", Fav) };
        var v = Filter(LibraryView.Favourites, albums, showHidden: false);
        Assert.Equal(new[] { "abba", "beatles" }, Ids(v)); // filing order
        Assert.Equal(new[] { "arrival", "gold" }, AlbumsOf(v, "abba"));
    }

    [Fact]
    public void AMarkedArtistWithAMarkedAlbum_StillShowsAllItsAlbums()
    {
        var v = Filter(LibraryView.Favourites, new[] { new Album("help", "beatles", Fav) }, showHidden: false);
        Assert.Equal(new[] { "beatles" }, Ids(v));
        Assert.Null(v.AlbumsOf("beatles"));
    }

    [Fact]
    public void HiddenArtists_HideTheirMarkedAlbums_UnlessShowHidden()
    {
        var albums = new[] { new Album("la-woman", "doors", Fav), new Album("bad", "cure", FavHidden) };
        Assert.Equal(new[] { "beatles" }, Ids(Filter(LibraryView.Favourites, albums, showHidden: false)));
        var shown = Filter(LibraryView.Favourites, albums, showHidden: true);
        Assert.Equal(new[] { "beatles", "cure", "doors" }, Ids(shown));
        Assert.Equal(new[] { "bad" }, AlbumsOf(shown, "cure"));
    }

    [Fact]
    public void Hidden_ListsHiddenArtistsWhole_AndArtistsWithHiddenAlbums()
    {
        var albums = new[] { new Album("junk", "cure", Hidden), new Album("pornography", "cure") };
        var v = Filter(LibraryView.Hidden, albums, showHidden: false);
        Assert.Equal(new[] { "cure", "doors" }, Ids(v));
        Assert.Equal(new[] { "junk" }, AlbumsOf(v, "cure"));
        Assert.Null(v.AlbumsOf("doors"));
    }

    [Fact]
    public void AnAlbumWhoseArtistIsntListed_IsFiledUnderItsArtistName()
    {
        var albums = new[] { new Album("x", "blondie", Liked) { ArtistName = "Blondie" } };
        var v = LibraryView.Liked.Filter(Library, albums, showHidden: false, (id, name) => new Artist(id) { Name = name });
        Assert.Equal(new[] { "Blondie" }, v.Artists.Select(a => a.Name));
        Assert.Equal(new[] { "x" }, AlbumsOf(v, "blondie"));
        Assert.True(v.Shows(new Album("x", "blondie")));
        Assert.False(v.Shows(new Album("y", "blondie")));
    }

    [Fact]
    public void OnlyTheViewsByMarks_Filter()
    {
        Assert.Empty(Filter(LibraryView.Library, NoAlbums, showHidden: true).Artists);
        Assert.Empty(Filter(LibraryView.MostPlayed, NoAlbums, showHidden: true).Artists);
    }

    [Fact]
    public void AnArtistsAlbums_AreTheViewsAlbums_OrAll()
    {
        var all = new[] { new Album("a", "x", Fav), new Album("b", "x"), new Album("c", "x", Hidden) };
        IEnumerable<string> Of(LibraryView view, IReadOnlySet<string>? only, bool showHidden) =>
            view.AlbumsOf(all, only, showHidden).Select(a => a.Id);
        Assert.Equal(new[] { "a" }, Of(LibraryView.Favourites, new HashSet<string> { "a" }, false));
        Assert.Equal(new[] { "a", "b" }, Of(LibraryView.Favourites, null, false));
        Assert.Equal(new[] { "a", "b", "c" }, Of(LibraryView.Favourites, null, true));
        Assert.Equal(new[] { "c" }, Of(LibraryView.Hidden, new HashSet<string> { "c" }, false));
        Assert.Equal(new[] { "a", "b", "c" }, Of(LibraryView.Hidden, null, false));
    }

    [Fact]
    public void AlbumsFromAcrossTheLibrary_AreFiledByArtistThenTitle()
    {
        var all = new[] { new Item("x", default, "beatles", "revolver"), new Item("y", default, "abba", "arrival"), new Item("z", default, "beatles", "help") };
        Assert.Equal(new[] { "y", "z", "x" }, Ids(LibraryViews.ByArtist(all, a => a.ArtistKey, a => a.SortTitle)));
    }

    private static PlayedTrack T(string album, int plays, int? day) =>
        new(album, plays, day is { } d ? new DateTime(2026, 10, d, 12, 0, 0, DateTimeKind.Utc) : null);

    [Fact]
    public void Recent_OrdersAlbumsByTheirLatestTrack()
    {
        var tracks = new[] { T("old", 9, 1), T("new", 1, 2), T("old", 1, 3), T("undated", 1, null) };
        Assert.Equal(new[] { "old", "new", "undated" }, PlayHistory.Recent(tracks));
        Assert.Equal(new[] { "old" }, PlayHistory.Recent(tracks, limit: 1));
    }

    [Fact]
    public void Most_IsPlaysThroughTheWholeAlbum_NotJustItsLength()
    {
        // long: 20 tracks heard once each = once through; short: 5 tracks heard 3 times = three times.
        // riff: one track of 10 played 20 times = twice through.
        var tracks = Enumerable.Repeat(T("long", 1, 1), 20).Concat(Enumerable.Repeat(T("short", 3, 2), 5)).Append(T("riff", 20, 3));
        var counts = new Dictionary<string, int> { ["long"] = 20, ["short"] = 5, ["riff"] = 10 };
        Assert.Equal(new[] { "short", "riff", "long" }, PlayHistory.Most(tracks, counts));
    }

    [Fact]
    public void Most_BreaksTiesByTheMostRecent_AndCopesWithoutTrackCounts()
    {
        var tracks = new[] { T("a", 2, 1), T("b", 2, 5) };
        Assert.Equal(new[] { "b", "a" }, PlayHistory.Most(tracks, new Dictionary<string, int>())); // counts unknown: tracks seen
    }

    [Fact]
    public void Candidates_AreTheAlbumsWithTheMostPlaysInAll()
    {
        var tracks = new[] { T("a", 1, 1), T("b", 5, 1), T("c", 2, 1), T("c", 2, 1) };
        Assert.Equal(new[] { "b", "c" }, PlayHistory.Candidates(tracks, limit: 2));
    }

    [Fact]
    public void APlayedTrack_ComesFromItsUserData()
    {
        var at = new DateTime(2026, 10, 24, 8, 32, 44, DateTimeKind.Utc);
        var item = new BaseItem { Id = "t", AlbumId = "al", UserData = new UserItemData { PlayCount = 4, LastPlayedDate = at } };
        Assert.Equal(new PlayedTrack("al", 4, at), PlayedTrack.From(item));
        item.AlbumId = null;
        Assert.Null(PlayedTrack.From(item));
    }
}
