using TunerFish.Models;

namespace TunerFish.Services;

/// <summary>What the filter box looks in, any combination. Stored as words, e.g. "Artists Albums".</summary>
public enum SearchScope { Artists, Albums, Tracks }

public static class SearchScopes
{
    public static readonly IReadOnlySet<SearchScope> Default = new HashSet<SearchScope> { SearchScope.Artists, SearchScope.Albums };

    /// <summary>An unknown word, or none at all, resets the setting to <see cref="Default"/>.</summary>
    public static IReadOnlySet<SearchScope> Parse(string? value)
    {
        var scope = new HashSet<SearchScope>();
        foreach (var word in (value ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var s = Enum.GetValues<SearchScope>().Cast<SearchScope?>().FirstOrDefault(x => x.ToString() == word);
            if (s is null) return Default;
            scope.Add(s.Value);
        }
        return scope.Count == 0 ? Default : scope;
    }

    public static string Format(IReadOnlySet<SearchScope> scope) =>
        string.Join(" ", Enum.GetValues<SearchScope>().Where(scope.Contains));
}

/// <summary>An album whose title matched, or a track on it (<see cref="Track"/>); filed under the album's artist.</summary>
public sealed record SearchHit(string ArtistId, string AlbumId, string Album, string? Track = null, bool Hidden = false)
{
    /// <summary>What the artist's row says matched: the album, or "Track (Album)".</summary>
    public string Label => Track is null ? Album : $"{Track} ({Album})";
}

/// <summary>The first match ("Breathe (The Dark Side of the Moon)") and how many <see cref="More"/> there are.</summary>
public sealed record MatchNote(string Text, int More = 0)
{
    public override string ToString() => More == 0 ? Text : $"{Text} +{More}";
}

/// <summary>
/// The filter box: artist names, and album and track titles across the whole collection (Jellyfin's
/// search). Album-first: a match is shown as its artist, with only the matching albums.
/// Port of the Android app's core Search.kt; its tests are mirrored in SearchTests.
/// </summary>
public static class Search
{
    /// <summary>Albums and tracks are looked for once the text is this long; artist names from the first letter.</summary>
    public const int MinLength = 2;

    /// <summary>How many albums, and how many tracks, one search asks the server for.</summary>
    public const int Limit = 300;

    /// <summary>Whether <paramref name="text"/> contains <paramref name="term"/>, ignoring case and accents.</summary>
    public static bool Matches(string text, string term) =>
        SortOrder.PlainLetters(text).Contains(SortOrder.PlainLetters(term.Trim()), StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="term"/> needs albums and tracks looked up on the server.</summary>
    public static bool LooksBeyondArtists(string term, IReadOnlySet<SearchScope> scope) =>
        term.Trim().Length >= MinLength && (scope.Contains(SearchScope.Albums) || scope.Contains(SearchScope.Tracks));

    /// <summary>Matches from the server's search results: <paramref name="albums"/> (MusicAlbum items) first, then <paramref name="tracks"/> (Audio).</summary>
    public static List<SearchHit> Hits(IEnumerable<BaseItem> albums, IEnumerable<BaseItem> tracks)
    {
        var hits = new List<SearchHit>();
        foreach (var a in albums)
            if (a.AlbumArtists?.FirstOrDefault()?.Id is { } artist)
                hits.Add(new SearchHit(artist, a.Id, a.Name, Hidden: Marks.From(a).Hidden));
        foreach (var t in tracks)
            if (t.AlbumArtists?.FirstOrDefault()?.Id is { } artist && t.AlbumId is { } album)
                hits.Add(new SearchHit(artist, album, t.Album ?? "", t.Name));
        return hits;
    }

    /// <summary>
    /// Narrows <paramref name="list"/> (the library, or a view by marks) to <paramref name="term"/>. An artist
    /// whose name matches keeps what <paramref name="list"/> shows of it; otherwise an artist with matching
    /// albums or tracks is kept with only those albums (of the ones <paramref name="list"/> shows), and a note
    /// of what matched. <paramref name="hits"/> may be from an earlier, shorter text: only those that still
    /// match count. Hidden albums' hits are left out unless <paramref name="showHidden"/>. Too little text to
    /// search titles, with artist names left out, leaves <paramref name="list"/> as it is.
    /// </summary>
    public static FilteredArtists<T> Narrow<T>(FilteredArtists<T> list, string term, IReadOnlySet<SearchScope> scope,
                                                IReadOnlyList<SearchHit> hits, bool showHidden) where T : IListedArtist
    {
        var t = term.Trim();
        // Nothing to go on yet: no text, or too little for titles with artist names left out.
        if (t.Length == 0 || (!scope.Contains(SearchScope.Artists) && !LooksBeyondArtists(t, scope))) return list;
        var found = !LooksBeyondArtists(t, scope)
            ? new Dictionary<string, List<SearchHit>>()
            : hits.Where(h => (showHidden || !h.Hidden) && Matches(h.Track ?? h.Album, t) &&
                              scope.Contains(h.Track is null ? SearchScope.Albums : SearchScope.Tracks))
                  .GroupBy(h => h.ArtistId).ToDictionary(g => g.Key, g => g.ToList());
        var artists = new List<T>();
        var albumIds = new Dictionary<string, HashSet<string>>();
        var notes = new Dictionary<string, MatchNote>();
        foreach (var a in list.Artists)
        {
            var shown = list.AlbumsOf(a.Id);
            if (scope.Contains(SearchScope.Artists) && a.Name.Contains(t, StringComparison.CurrentCultureIgnoreCase))
            {
                artists.Add(a);
                if (shown is not null) albumIds[a.Id] = shown.ToHashSet();
                continue;
            }
            var mine = found.GetValueOrDefault(a.Id)?.Where(h => shown is null || shown.Contains(h.AlbumId)).ToList();
            if (mine is not { Count: > 0 }) continue;
            artists.Add(a);
            albumIds[a.Id] = mine.Select(h => h.AlbumId).ToHashSet();
            notes[a.Id] = new MatchNote(mine[0].Label, mine.Count - 1);
        }
        return new FilteredArtists<T>(artists, albumIds, notes);
    }
}
