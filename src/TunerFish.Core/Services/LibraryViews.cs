using TunerFish.Models;

namespace TunerFish.Services;

/// <summary>
/// What the library panes show: the whole library, or a view of the user's own data. The views by
/// marks are filters: the artist list, shortened to the artists that are marked or have a marked
/// album (<see cref="LibraryViews.Filter"/>). The played views list albums only, from Jellyfin's play
/// history. Not saved: the app always opens on the Library.
/// Port of the Android app's core Views.kt; its tests are mirrored in LibraryViewsTests.
/// </summary>
public enum LibraryView { Library, Favourites, Liked, RecentlyPlayed, MostPlayed, Hidden }

/// <summary>An artist in the artist list.</summary>
public interface IListedArtist : IMarked
{
    string Id { get; }
    string Name { get; }
    /// <summary>What it files under: Jellyfin's sort name.</summary>
    string SortKey { get; }
}

/// <summary>An album, with the album artist it's filed under.</summary>
public interface IArtistAlbum : IMarked
{
    string Id { get; }
    string? ArtistId { get; }
    string ArtistName { get; }
}

/// <summary>
/// A shortened artist list (a view by marks, a search): its artists in filing order, and for each, the
/// albums it shows (all of them if absent) and why it's listed, if that needs saying.
/// </summary>
public sealed class FilteredArtists<T> where T : IListedArtist
{
    private readonly Dictionary<string, HashSet<string>> _albumIds;
    private readonly Dictionary<string, MatchNote> _notes;
    private readonly HashSet<string> _artistIds;

    public IReadOnlyList<T> Artists { get; }

    public FilteredArtists(IReadOnlyList<T> artists, Dictionary<string, HashSet<string>> albumIds,
                           Dictionary<string, MatchNote>? notes = null)
    {
        Artists = artists;
        _albumIds = albumIds;
        _notes = notes ?? new();
        _artistIds = artists.Select(a => a.Id).ToHashSet();
    }

    public static readonly FilteredArtists<T> Empty = new(Array.Empty<T>(), new());

    /// <summary>The whole list, every artist with all its albums.</summary>
    public static FilteredArtists<T> All(IReadOnlyList<T> artists) => new(artists, new());

    /// <summary>The ids of the albums the artist shows here, or null for all of them.</summary>
    public IReadOnlySet<string>? AlbumsOf(string artistId) => _albumIds.GetValueOrDefault(artistId);

    /// <summary>What matched, for an artist listed for some of its albums or tracks.</summary>
    public MatchNote? NoteOf(string artistId) => _notes.GetValueOrDefault(artistId);

    public bool Lists(string artistId) => _artistIds.Contains(artistId);

    /// <summary>Whether the album is shown here: its artist is listed, with this album among those it shows.</summary>
    public bool Shows(IArtistAlbum album) =>
        album.ArtistId is { } id && Lists(id) && (AlbumsOf(id) is not { } only || only.Contains(album.Id));
}

public static class LibraryViews
{
    /// <summary>In the order the chooser lists them.</summary>
    public static readonly IReadOnlyList<LibraryView> All = Enum.GetValues<LibraryView>();

    public static string Label(this LibraryView view) => view switch
    {
        LibraryView.RecentlyPlayed => "Recently played",
        LibraryView.MostPlayed => "Most played",
        _ => view.ToString(),
    };

    public static bool ByMarks(this LibraryView view) =>
        view is LibraryView.Favourites or LibraryView.Liked or LibraryView.Hidden;

    /// <summary>Whether something with these marks belongs in this view (the played views take whatever they're given).</summary>
    public static bool Includes(this LibraryView view, Marks marks) => view switch
    {
        LibraryView.Favourites => marks.Favourite,
        LibraryView.Liked => marks.Liked,
        LibraryView.Hidden => marks.Hidden,
        _ => true,
    };

    /// <summary>Everything, less the hidden things unless they're to be shown.</summary>
    public static List<T> Visible<T>(IEnumerable<T> all, bool showHidden) where T : IMarked =>
        all.Where(x => showHidden || !x.Marks.Hidden).ToList();

    /// <summary>Hidden things show only in Hidden, or with "Show hidden".</summary>
    private static bool Shows(this LibraryView view, Marks marks, bool showHidden) =>
        view == LibraryView.Hidden || showHidden || !marks.Hidden;

    /// <summary>The view's albums, in the order given.</summary>
    public static List<T> Albums<T>(this LibraryView view, IEnumerable<T> all, bool showHidden) where T : IMarked =>
        all.Where(x => view.Includes(x.Marks) && view.Shows(x.Marks, showHidden)).ToList();

    /// <summary>
    /// A view by marks out of the library's <paramref name="artists"/> and the <paramref name="albums"/> that
    /// might be in it: an artist marked itself is listed with all its albums (a favourite artist = all their
    /// albums, even ones added later); an artist with marked albums is listed with only those. A hidden artist
    /// hides all its albums. An album whose artist isn't in <paramref name="artists"/> is filed under an artist
    /// made by <paramref name="stranger"/> from its artist id and name.
    /// </summary>
    public static FilteredArtists<TArtist> Filter<TArtist, TAlbum>(this LibraryView view, IEnumerable<TArtist> artists,
        IEnumerable<TAlbum> albums, bool showHidden, Func<string, string, TArtist> stranger)
        where TArtist : IListedArtist where TAlbum : IArtistAlbum
    {
        if (!view.ByMarks()) return FilteredArtists<TArtist>.Empty;
        var known = artists.ToDictionary(a => a.Id);
        var whole = known.Values.Where(a => view.Includes(a.Marks) && view.Shows(a.Marks, showHidden)).ToList();
        var wholeIds = whole.Select(a => a.Id).ToHashSet();
        var some = albums
            .Where(a => a.ArtistId is { } id && !wholeIds.Contains(id) && view.Includes(a.Marks) && view.Shows(a.Marks, showHidden))
            .GroupBy(a => a.ArtistId!)
            .Where(g => !known.TryGetValue(g.Key, out var artist) || view.Shows(artist.Marks, showHidden))
            .ToList();
        var owners = some.Select(g => known.TryGetValue(g.Key, out var artist) ? artist : stranger(g.Key, g.First().ArtistName));
        var cmp = SortOrder.Comparer;
        return new FilteredArtists<TArtist>(
            whole.Concat(owners).OrderBy(a => a.SortKey, cmp).ThenBy(a => a.Name, cmp).ToList(),
            some.ToDictionary(g => g.Key, g => g.Select(a => a.Id).ToHashSet()));
    }

    /// <summary>
    /// An artist's albums as a view shows them: of <paramref name="all"/> its albums, the ones in
    /// <paramref name="only"/>, or all of them if null (the artist itself is marked, or "Show all").
    /// Hidden albums are left out as in the library, except in Hidden.
    /// </summary>
    public static List<T> AlbumsOf<T>(this LibraryView view, IEnumerable<T> all, IReadOnlySet<string>? only, bool showHidden)
        where T : IArtistAlbum
    {
        var shown = only is null ? all : all.Where(a => only.Contains(a.Id));
        return view == LibraryView.Hidden ? shown.ToList() : Visible(shown, showHidden);
    }

    /// <summary>Albums from across the library as the views by marks list them: filed by artist (<paramref name="artistKey"/>), then title.</summary>
    public static List<T> ByArtist<T>(IEnumerable<T> albums, Func<T, string> artistKey, Func<T, string> sortTitle) =>
        albums.OrderBy(artistKey, SortOrder.Comparer).ThenBy(sortTitle, SortOrder.Comparer).ToList();
}

/// <summary>A played track, as far as the played views care: its album, how often and when it was last played.</summary>
public sealed record PlayedTrack(string AlbumId, int Plays, DateTime? LastPlayed)
{
    public static PlayedTrack? From(BaseItem item) =>
        item.AlbumId is { } album && item.UserData is { } d ? new PlayedTrack(album, d.PlayCount, d.LastPlayedDate) : null;
}

/// <summary>Albums out of the tracks' play history. Album-first: an album counts as played as a whole.</summary>
public static class PlayHistory
{
    /// <summary>How many albums a played view lists.</summary>
    public const int Albums = 100;

    /// <summary>How many played tracks are read to make a played view.</summary>
    public const int Tracks = 2000;

    /// <summary>Albums by their latest played track, newest first.</summary>
    public static List<string> Recent(IEnumerable<PlayedTrack> tracks, int limit = Albums) =>
        tracks.GroupBy(t => t.AlbumId)
            .Select(g => (Album: g.Key, Latest: g.Max(t => t.LastPlayed) ?? DateTime.MinValue))
            .OrderByDescending(x => x.Latest)
            .Take(limit).Select(x => x.Album).ToList();

    /// <summary>The albums worth looking at for <see cref="Most"/>: the most plays in all, before their track counts are known.</summary>
    public static List<string> Candidates(IEnumerable<PlayedTrack> tracks, int limit = Albums * 2) =>
        tracks.GroupBy(t => t.AlbumId)
            .Select(g => (Album: g.Key, Plays: g.Sum(t => t.Plays)))
            .OrderByDescending(x => x.Plays)
            .Take(limit).Select(x => x.Album).ToList();

    /// <summary>
    /// Albums by how many times they've been played through: all plays of their tracks over the number
    /// of tracks (<paramref name="trackCounts"/>), so a long album isn't ahead for its length, nor one track
    /// played to death ahead of an album heard whole. Ties go to the one played most recently.
    /// </summary>
    public static List<string> Most(IEnumerable<PlayedTrack> tracks, IReadOnlyDictionary<string, int> trackCounts, int limit = Albums) =>
        tracks.GroupBy(t => t.AlbumId)
            .Select(g =>
            {
                var count = Math.Max(trackCounts.GetValueOrDefault(g.Key), g.Count());
                return (Album: g.Key, Through: (double)g.Sum(t => t.Plays) / count, Latest: g.Max(t => t.LastPlayed) ?? DateTime.MinValue);
            })
            .OrderByDescending(x => x.Through).ThenByDescending(x => x.Latest)
            .Take(limit).Select(x => x.Album).ToList();
}
