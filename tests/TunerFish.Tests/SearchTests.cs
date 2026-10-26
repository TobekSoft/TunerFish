using TunerFish.Models;
using TunerFish.Services;

namespace TunerFish.Tests;

/// <summary>Mirrors the Android app's SearchTest case for case (the server's search stands in for the phone's).</summary>
public sealed class SearchTests
{
    private sealed record Artist(string Id, string Name) : IListedArtist
    {
        public Marks Marks => default;
        public string SortKey => Name.ToLowerInvariant();
    }

    private static readonly Artist Floyd = new("floyd", "Pink Floyd"), Faith = new("faith", "Faith Hill"), Prodigy = new("prodigy", "The Prodigy");
    private static readonly FilteredArtists<Artist> Library = FilteredArtists<Artist>.All(new[] { Faith, Floyd, Prodigy });

    private static readonly Dictionary<string, string> ArtistNames = new() { ["floyd"] = "Pink Floyd", ["faith"] = "Faith Hill", ["prodigy"] = "The Prodigy" };

    private static BaseItem Album(string id, string title, string artistId, bool hidden = false) => new()
    {
        Id = id, Name = title, AlbumArtists = new() { new NameIdPair { Id = artistId, Name = ArtistNames[artistId] } },
        UserData = hidden ? new UserItemData { Likes = false } : null,
    };

    private static readonly BaseItem[] Albums =
    {
        Album("dsotm", "The Dark Side of the Moon", "floyd"), Album("wall", "The Wall", "floyd"),
        Album("fat", "The Fat of the Land", "prodigy"), Album("breathe-album", "Breathe", "faith"),
    };

    private static BaseItem Track(string title, string albumId)
    {
        var album = Albums.Single(a => a.Id == albumId);
        return new BaseItem { Id = title + albumId, Name = title, Album = album.Name, AlbumId = albumId, AlbumArtists = album.AlbumArtists };
    }

    private static readonly BaseItem[] Tracks = { Track("Breathe", "dsotm"), Track("Time", "dsotm"), Track("Breathe", "fat"), Track("Wish You Were Here", "wall") };

    private static readonly IReadOnlySet<SearchScope> All = new HashSet<SearchScope> { SearchScope.Artists, SearchScope.Albums, SearchScope.Tracks };

    private static IReadOnlySet<SearchScope> Only(params SearchScope[] scope) => scope.ToHashSet();

    /// <summary>What the server's search would answer, as far as the scope goes.</summary>
    private static List<SearchHit> Hits(string term, IReadOnlySet<SearchScope> scope, IEnumerable<BaseItem>? albums = null)
    {
        if (!Search.LooksBeyondArtists(term, scope)) return new();
        var a = scope.Contains(SearchScope.Albums) ? (albums ?? Albums).Where(x => Search.Matches(x.Name, term)) : Array.Empty<BaseItem>();
        var t = scope.Contains(SearchScope.Tracks) ? Tracks.Where(x => Search.Matches(x.Name, term)) : Array.Empty<BaseItem>();
        return Search.Hits(a, t);
    }

    private static FilteredArtists<Artist> Find(string term, IReadOnlySet<SearchScope>? scope = null, FilteredArtists<Artist>? list = null) =>
        Search.Narrow(list ?? Library, term, scope ?? All, Hits(term, scope ?? All), showHidden: false);

    private static IEnumerable<string> Ids(FilteredArtists<Artist> r) => r.Artists.Select(a => a.Id);
    private static string[]? AlbumsOf(FilteredArtists<Artist> r, string artistId) => r.AlbumsOf(artistId)?.Order().ToArray();

    [Fact]
    public void AnAlbumTitle_FindsItsArtist_WithOnlyThatAlbum()
    {
        var r = Find("dark side");
        Assert.Equal(new[] { "floyd" }, Ids(r));
        Assert.Equal(new[] { "dsotm" }, AlbumsOf(r, "floyd"));
        Assert.Equal(new MatchNote("The Dark Side of the Moon"), r.NoteOf("floyd"));
    }

    [Fact]
    public void ATrackTitle_FindsEveryArtistWithIt_AndTheAlbumItsOn()
    {
        var r = Find("breathe");
        Assert.Equal(new[] { "faith", "floyd", "prodigy" }, Ids(r));
        Assert.Equal(new[] { "dsotm" }, AlbumsOf(r, "floyd"));
        Assert.Equal(new MatchNote("Breathe (The Dark Side of the Moon)"), r.NoteOf("floyd"));
        Assert.Equal(new[] { "breathe-album" }, AlbumsOf(r, "faith")); // the album title, before its tracks
    }

    [Fact]
    public void AnArtistName_KeepsAllItsAlbums_WithNoNote()
    {
        var r = Find("floyd");
        Assert.Equal(new[] { "floyd" }, Ids(r));
        Assert.Null(r.AlbumsOf("floyd"));
        Assert.Null(r.NoteOf("floyd"));
    }

    [Fact]
    public void TheScope_ChoosesWhatIsLookedIn()
    {
        Assert.Empty(Find("breathe", Only(SearchScope.Artists)).Artists);
        Assert.Equal(new[] { "faith" }, Ids(Find("breathe", Only(SearchScope.Albums))));
        Assert.Equal(new[] { "floyd", "prodigy" }, Ids(Find("breathe", Only(SearchScope.Tracks))));
        Assert.Empty(Find("floyd", Only(SearchScope.Albums, SearchScope.Tracks)).Artists);
    }

    [Fact]
    public void SeveralMatches_OnOneArtist_AreCountedInTheNote()
    {
        var r = Find("the", Only(SearchScope.Albums));
        Assert.Equal(new[] { "dsotm", "wall" }, AlbumsOf(r, "floyd"));
        Assert.Equal(new MatchNote("The Dark Side of the Moon", More: 1), r.NoteOf("floyd"));
        Assert.Equal("The Dark Side of the Moon +1", r.NoteOf("floyd")!.ToString());
    }

    [Fact]
    public void OneLetter_LooksOnlyAtArtistNames()
    {
        Assert.False(Search.LooksBeyondArtists("t", All));
        Assert.Equal(new[] { "faith", "floyd", "prodigy" }, Ids(Find("i")));
        Assert.Equal(new[] { "faith", "floyd", "prodigy" }, Ids(Find("z", Only(SearchScope.Tracks)))); // left as it is
    }

    [Fact]
    public void AccentsAndCase_DontMatter_ForTitles()
    {
        Assert.True(Search.Matches("Déjà Vu", "deja vu"));
        Assert.True(Search.Matches("Déjà Vu", "DÉJÀ"));
    }

    [Fact]
    public void HitsForAShorterText_AreNarrowedToTheLongerOne()
    {
        var earlier = Hits("th", All); // still arriving for "the dark"
        var r = Search.Narrow(Library, "the dark", All, earlier, showHidden: false);
        Assert.Equal(new[] { "floyd" }, Ids(r));
        Assert.Equal(new[] { "dsotm" }, AlbumsOf(r, "floyd"));
    }

    [Fact]
    public void HiddenAlbums_AreLeftOut_UnlessShowHidden()
    {
        var hits = Hits("dark", All, new[] { Album("dsotm", "The Dark Side of the Moon", "floyd", hidden: true) });
        Assert.Empty(Search.Narrow(Library, "dark", All, hits, showHidden: false).Artists);
        Assert.Equal(new[] { "floyd" }, Ids(Search.Narrow(Library, "dark", All, hits, showHidden: true)));
    }

    [Fact]
    public void InAView_OnlyTheViewsArtistsAndAlbums_AreSearched()
    {
        // Favourites: Pink Floyd for The Wall only, The Prodigy whole.
        var favourites = new FilteredArtists<Artist>(new[] { Floyd, Prodigy }, new() { ["floyd"] = new() { "wall" } });
        Assert.Empty(Find("dark side", list: favourites).Artists); // not a favourite album
        Assert.Equal(new[] { "wall" }, AlbumsOf(Find("wish", list: favourites), "floyd"));
        Assert.Equal(new[] { "fat" }, AlbumsOf(Find("breathe", list: favourites), "prodigy"));
        Assert.Equal(new[] { "wall" }, AlbumsOf(Find("pink", list: favourites), "floyd")); // a name match keeps the view's albums
    }

    [Fact]
    public void ServerResults_BecomeHits_UnderTheirAlbumArtist()
    {
        var album = Album("dsotm", "The Dark Side of the Moon", "floyd");
        var track = Track("Breathe", "dsotm");
        Assert.Equal(new[] { new SearchHit("floyd", "dsotm", "The Dark Side of the Moon"), new SearchHit("floyd", "dsotm", "The Dark Side of the Moon", "Breathe") },
                     Search.Hits(new[] { album }, new[] { track }));
        Assert.Empty(Search.Hits(new[] { new BaseItem { Id = "x", Name = "No artist" } }, Array.Empty<BaseItem>()));
    }

    [Fact]
    public void TheScope_IsStoredAsWords()
    {
        Assert.Equal("Artists Tracks", SearchScopes.Format(Only(SearchScope.Tracks, SearchScope.Artists)));
        Assert.Equal(new[] { SearchScope.Artists, SearchScope.Tracks }, SearchScopes.Parse("Artists Tracks").Order());
        Assert.Same(SearchScopes.Default, SearchScopes.Parse(null));
        Assert.Same(SearchScopes.Default, SearchScopes.Parse(""));
        Assert.Same(SearchScopes.Default, SearchScopes.Parse("Artists Genres")); // unknown word: reset
        Assert.Same(SearchScopes.Default, SearchScopes.Parse("artists 2"));      // only the words themselves
    }
}
