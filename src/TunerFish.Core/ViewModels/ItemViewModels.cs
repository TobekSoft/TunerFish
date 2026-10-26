using System.Globalization;
using System.Windows.Input;
using TunerFish.Models;
using TunerFish.Services;

namespace TunerFish.ViewModels;

/// <summary>
/// An artist or album row: the user's marks on it (heart, thumbs up, hidden) and the commands that
/// change them, which the main view model sets. Tracks are never marked.
/// </summary>
public abstract class MarkedVm : ObservableObject, IMarked
{
    private Marks _marks;
    public Marks Marks
    {
        get => _marks;
        set
        {
            if (!SetProperty(ref _marks, value)) return;
            OnPropertyChanged(nameof(IsFavourite));
            OnPropertyChanged(nameof(IsLiked));
            OnPropertyChanged(nameof(IsHidden));
            OnPropertyChanged(nameof(HasMarks));
            OnPropertyChanged(nameof(HideLabel));
        }
    }

    public bool IsFavourite => Marks.Favourite;
    public bool IsLiked => Marks.Liked;
    public bool IsHidden => Marks.Hidden;
    public bool HasMarks => Marks != Marks.None;

    /// <summary>"artist" or "album", for the menu's "Hide artist" / "Unhide album".</summary>
    protected abstract string Kind { get; }
    public string HideLabel => IsHidden ? $"Unhide {Kind}" : $"Hide {Kind}";

    public ICommand? ToggleFavouriteCommand { get; set; }
    public ICommand? ToggleLikeCommand { get; set; }
    public ICommand? ToggleHiddenCommand { get; set; }

    protected MarkedVm(BaseItem item) => _marks = Marks.From(item);
}

public sealed class ArtistVm : MarkedVm, IListedArtist
{
    public string Id { get; }
    public string Name { get; }

    /// <summary>What this artist files under: Jellyfin's sort name.</summary>
    public string SortKey { get; }

    /// <summary>Its group on the A–Z jump bar: "A" to "Z", or "#" for symbols and digits.</summary>
    public string Group { get; }

    protected override string Kind => "artist";

    private string? _note;
    /// <summary>What a search matched, for an artist listed for some of its albums or tracks: "Breathe (The Dark Side of the Moon) +2".</summary>
    public string? Note { get => _note; set => SetProperty(ref _note, value); }

    public ArtistVm(BaseItem item) : base(item)
    {
        Id = item.Id;
        Name = item.Name;
        SortKey = SortOrder.KeyFor(item);
        Group = SortOrder.GroupOf(SortKey);
    }

    public override string ToString() => Name;
}

/// <summary>One entry on the A–Z jump bar. Always shown; greyed out when no listed artist is in it.</summary>
public sealed class JumpGroupVm : ObservableObject
{
    /// <summary>Also the label: "#" or A to Z.</summary>
    public string Group { get; }
    public ICommand JumpCommand { get; }

    private bool _hasArtists;
    public bool HasArtists { get => _hasArtists; set => SetProperty(ref _hasArtists, value); }

    public JumpGroupVm(string group, Action<string> jump)
    {
        Group = group;
        JumpCommand = new RelayCommand(() => jump(group));
    }
}

public sealed class AlbumVm : MarkedVm, IArtistAlbum
{
    public string Id { get; }
    public string Title { get; }
    /// <summary>What this album files under when sorted by title: Jellyfin's sort name.</summary>
    public string SortTitle { get; }
    /// <summary>Right-click → "Play" / "Add to queue". Set by the main view model.</summary>
    public System.Windows.Input.ICommand? PlayCommand { get; set; }
    public System.Windows.Input.ICommand? QueueCommand { get; set; }
    public int? Year { get; }
    public string YearText => Year?.ToString(CultureInfo.InvariantCulture) ?? "";
    public string? ImageTag { get; }
    /// <summary>Its album artist, for albums listed from across the library (the views).</summary>
    public string? ArtistId { get; }
    public string ArtistName { get; }

    private bool _showArtist;
    /// <summary>Whether the tile's second line names the artist (in a view) or just the year.</summary>
    public bool ShowArtist { get => _showArtist; set { if (SetProperty(ref _showArtist, value)) OnPropertyChanged(nameof(Caption)); } }

    public string Caption => !ShowArtist ? YearText : Year is null ? ArtistName : $"{ArtistName} · {YearText}";

    private object? _cover;
    /// <summary>The decoded cover (an Avalonia Bitmap at runtime), or null.</summary>
    public object? Cover { get => _cover; set => SetProperty(ref _cover, value); }

    protected override string Kind => "album";

    public AlbumVm(BaseItem item) : base(item)
    {
        Id = item.Id;
        ArtistId = item.AlbumArtists?.FirstOrDefault()?.Id;
        ArtistName = item.AlbumArtist ?? item.AlbumArtists?.FirstOrDefault()?.Name ?? "";
        Title = item.Name;
        SortTitle = SortOrder.KeyFor(item);
        Year = item.ProductionYear ?? item.PremiereDate?.Year;
        ImageTag = item.ImageTags != null && item.ImageTags.TryGetValue("Primary", out var tag) ? tag : null;
    }

    public override string ToString() => Title;
}

public sealed class TrackVm : ObservableObject
{
    public string Id { get; init; } = "";
    public string Disc { get; init; } = "";
    public string Track { get; init; } = "";
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Length { get; init; } = "";
    public double? Seconds { get; init; }
    public string Year { get; init; } = "";
    public string Genre { get; init; } = "";
    public string Format { get; init; } = "";
    public string Bitrate { get; init; } = "";
    public string SampleRate { get; init; } = "";
    public string BitDepth { get; init; } = "";
    public string Path { get; init; } = "";
    /// <summary>The user's play count and last play, from Jellyfin (as of when the album was opened).</summary>
    public string Plays { get; init; } = "";
    public string LastPlayed { get; init; } = "";

    private bool _isNowPlaying;
    public bool IsNowPlaying
    {
        get => _isNowPlaying;
        set { if (SetProperty(ref _isNowPlaying, value)) OnPropertyChanged(nameof(NowGlyph)); }
    }

    public string NowGlyph => IsNowPlaying ? "▶" : "";

    public static TrackVm From(BaseItem it)
    {
        var src = it.MediaSources?.FirstOrDefault();
        var audio = src?.MediaStreams?.FirstOrDefault(s => string.Equals(s.Type, "Audio", StringComparison.OrdinalIgnoreCase));
        var ci = CultureInfo.InvariantCulture;
        double? seconds = it.RunTimeTicks is long t ? t / 10_000_000.0 : null;

        return new TrackVm
        {
            Id = it.Id,
            Disc = it.ParentIndexNumber?.ToString(ci) ?? "",
            Track = it.IndexNumber?.ToString(ci) ?? "",
            Title = it.Name,
            Artist = it.Artists is { Count: > 0 } ? string.Join("; ", it.Artists) : it.AlbumArtist ?? "",
            Length = FormatTime(seconds),
            Seconds = seconds,
            Year = it.ProductionYear?.ToString(ci) ?? "",
            Genre = it.Genres is { Count: > 0 } ? string.Join("; ", it.Genres) : "",
            Format = src?.Container?.ToUpperInvariant() ?? "",
            Bitrate = src?.Bitrate is int b && b > 0 ? $"{b / 1000} kbps" : "",
            SampleRate = audio?.SampleRate is int sr && sr > 0 ? (sr / 1000.0).ToString("0.#", ci) + " kHz" : "",
            BitDepth = audio?.BitDepth is int bd && bd > 0 ? $"{bd}-bit" : "",
            Path = it.Path ?? "",
            Plays = it.UserData?.PlayCount is int n && n > 0 ? n.ToString(ci) : "",
            LastPlayed = it.UserData?.LastPlayedDate is DateTime d ? d.ToLocalTime().ToString("yyyy-MM-dd HH:mm", ci) : "",
        };
    }

    public static string FormatTime(double? seconds)
    {
        if (seconds is not double s || s < 0 || double.IsNaN(s)) return "";
        var ts = TimeSpan.FromSeconds(Math.Floor(s));
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}"
            : $"{ts.Minutes}:{ts.Seconds:00}";
    }
}

/// <summary>
/// One entry in the play queue. Each entry is its own object, so the same track can be queued
/// twice and each copy is removed, moved and marked played on its own.
/// </summary>
public sealed class QueueItemVm : ObservableObject
{
    public TrackVm Track { get; }
    /// <summary>The album it was queued from (title and cover for the now-playing bar).</summary>
    public AlbumVm? Album { get; }

    public string Title => Track.Title;
    public string Artist => Track.Artist;
    public string Length => Track.Length;
    public string Subtitle => Album is null ? Track.Artist : $"{Track.Artist} — {Album.Title}";

    private bool _isCurrent;
    public bool IsCurrent
    {
        get => _isCurrent;
        set { if (SetProperty(ref _isCurrent, value)) OnPropertyChanged(nameof(NowGlyph)); }
    }

    private bool _isPlayed;
    /// <summary>Has been played (or skipped past) since it was queued. Shown greyed out.</summary>
    public bool IsPlayed { get => _isPlayed; set => SetProperty(ref _isPlayed, value); }

    public string NowGlyph => IsCurrent ? "▶" : "";

    /// <summary>The row's ✕ button and context menu. Set by the main view model.</summary>
    public System.Windows.Input.ICommand? RemoveCommand { get; set; }
    public System.Windows.Input.ICommand? RemoveAlbumCommand { get; set; }
    public System.Windows.Input.ICommand? PlayCommand { get; set; }

    private string? _removeAlbumLabel;
    /// <summary>"Remove album (n tracks)" for the run of its album's tracks around it; null if it has no album.</summary>
    public string? RemoveAlbumLabel { get => _removeAlbumLabel; set => SetProperty(ref _removeAlbumLabel, value); }

    public QueueItemVm(TrackVm track, AlbumVm? album)
    {
        Track = track;
        Album = album;
    }

    public override string ToString() => Title;
}

/// <summary>The columns the track grid can show. Keys are stored in settings; keep them stable.</summary>
public sealed record TrackColumnSpec(string Key, string Header, string MenuLabel, string BindingPath, double DefaultWidth, bool DefaultVisible);

public static class TrackColumns
{
    public static readonly IReadOnlyList<TrackColumnSpec> All = new[]
    {
        new TrackColumnSpec("now",        "",            "Now playing marker", nameof(TrackVm.NowGlyph),   32,  true),
        new TrackColumnSpec("disc",       "Disc",        "Disc",               nameof(TrackVm.Disc),       50,  false),
        new TrackColumnSpec("track",      "#",           "Track number",       nameof(TrackVm.Track),      44,  true),
        new TrackColumnSpec("title",      "Title",       "Title",              nameof(TrackVm.Title),      340, true),
        new TrackColumnSpec("artist",     "Artist",      "Artist",             nameof(TrackVm.Artist),     220, true),
        new TrackColumnSpec("length",     "Length",      "Length",             nameof(TrackVm.Length),     70,  true),
        new TrackColumnSpec("year",       "Year",        "Year",               nameof(TrackVm.Year),       60,  false),
        new TrackColumnSpec("genre",      "Genre",       "Genre",              nameof(TrackVm.Genre),      140, false),
        new TrackColumnSpec("format",     "Format",      "Format",             nameof(TrackVm.Format),     70,  false),
        new TrackColumnSpec("bitrate",    "Bitrate",     "Bitrate",            nameof(TrackVm.Bitrate),    90,  false),
        new TrackColumnSpec("samplerate", "Sample rate", "Sample rate",        nameof(TrackVm.SampleRate), 95,  false),
        new TrackColumnSpec("bitdepth",   "Bit depth",   "Bit depth",          nameof(TrackVm.BitDepth),   80,  false),
        new TrackColumnSpec("path",       "File",        "File path",          nameof(TrackVm.Path),       420, false),
        new TrackColumnSpec("plays",      "Plays",       "Play count",         nameof(TrackVm.Plays),      56,  true),
        new TrackColumnSpec("lastplayed", "Last played", "Last played",        nameof(TrackVm.LastPlayed), 130, false),
    };
}

/// <summary>One of the server's addresses, as the Options panel lists it.</summary>
public sealed class ServerAddressVm : ObservableObject
{
    public string Address { get; }
    public bool CanMoveUp { get; }
    public bool CanRemove { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand RemoveCommand { get; }

    public ServerAddressVm(string address, bool canMoveUp, bool canRemove, Action<string> moveUp, Action<string> remove)
    {
        Address = address;
        CanMoveUp = canMoveUp;
        CanRemove = canRemove;
        MoveUpCommand = new RelayCommand(() => moveUp(address));
        RemoveCommand = new RelayCommand(() => remove(address));
    }

    private bool? _reachable;
    private bool _inUse;

    /// <summary>Null while it's being checked.</summary>
    public bool? Reachable { get => _reachable; set { if (SetProperty(ref _reachable, value)) OnPropertyChanged(nameof(StatusText)); } }
    public bool InUse { get => _inUse; set { if (SetProperty(ref _inUse, value)) OnPropertyChanged(nameof(StatusText)); } }

    public string StatusText => Reachable switch
    {
        null => "Checking…",
        false => "Not reachable",
        true => InUse ? "Reachable · in use" : "Reachable",
    };
}
