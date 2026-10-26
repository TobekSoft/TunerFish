using System.Text.Json;

namespace TunerFish.Services;

public enum AlbumSortMode
{
    Title = 0,
    YearAscending = 1,
    YearDescending = 2,
}

/// <summary>Light or dark colours. System follows the desktop's setting.</summary>
public enum ThemeMode
{
    Light = 0,
    Dark = 1,
    System = 2,
}

public sealed class ColumnSetting
{
    public string Key { get; set; } = "";
    public bool Visible { get; set; }
    public double Width { get; set; }
    public int DisplayIndex { get; set; } = -1;
}

/// <summary>
/// Everything TunerFish remembers. Choices are stored as readable strings, never as
/// numbers, and an unknown or bad value only resets that one setting (see the
/// accessors in the view model), so an update can't silently wipe your layout.
/// </summary>
public sealed class AppSettings
{
    public int Version { get; set; } = 1;

    // Connection (the password is never stored, only the session token)
    public string? ServerUrl { get; set; }   // always the first of ServerUrls (files from before there could be several)
    /// <summary>The server's addresses, most preferred first (e.g. home network, then NetBird).</summary>
    public List<string> ServerUrls { get; set; } = new();
    /// <summary>Jellyfin's id for the server, so the token is only ever sent to that server.</summary>
    public string? ServerId { get; set; }
    public string? UserName { get; set; }
    public string? UserId { get; set; }
    public string? AccessToken { get; set; }
    public string DeviceId { get; set; } = Guid.NewGuid().ToString("N");

    // Browsing
    public string AlbumSort { get; set; } = nameof(AlbumSortMode.YearAscending);
    public bool SingleClickPlaysAlbum { get; set; }
    public string? LastArtistId { get; set; }
    /// <summary>Show artists and albums marked hidden (thumbs down) while browsing. Same key as the Android app.</summary>
    public bool ShowHidden { get; set; }
    /// <summary>What the filter box looks in (<see cref="SearchScopes"/>): words, e.g. "Artists Albums". Same key as the Android app.</summary>
    public string FilterScope { get; set; } = "Artists Albums";

    // Retired keys, never to be reused: IgnoreArticles, SortArticles, IgnoreArticlesInAlbumTitles
    // (filing now follows Jellyfin's sort names). Old files that still have them load fine.

    // Playback
    public double Volume { get; set; } = 80;
    /// <summary>Bass and treble in whole dB (<see cref="Tone"/>), 0 = flat. Same keys as the Android app.</summary>
    public int Bass { get; set; }
    public int Treble { get; set; }
    /// <summary>
    /// Start each launch with an empty queue instead of the saved one (QueueStore). The place in the saved
    /// queue: entry and milliseconds into it. Same keys as the Android app.
    /// </summary>
    public bool ClearQueueOnExit { get; set; }
    public int QueueIndex { get; set; }
    public long QueuePositionMs { get; set; }

    /// <summary>The app crashed last time: the next start says where the log is. The Android key.</summary>
    public bool CrashToShare { get; set; }

    // Appearance
    public string Theme { get; set; } = nameof(ThemeMode.System);

    // Layout
    public List<ColumnSetting> Columns { get; set; } = new();
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 800;
    public bool WindowMaximized { get; set; }
    public double ArtistPaneWidth { get; set; } = 260;
    public double TrackPaneHeight { get; set; } = 300;
    public double QueuePaneWidth { get; set; } = 320;
    public double AlbumArtSize { get; set; } = 170;
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public string FilePath { get; }

    /// <summary>Set when the settings file couldn't be read and was set aside.</summary>
    public string? LoadWarning { get; private set; }

    public SettingsStore(string? filePath = null)
    {
        FilePath = filePath ?? AppPaths.Current.SettingsFile;
    }

    public AppSettings Load()
    {
        if (!File.Exists(FilePath)) return new AppSettings();
        try
        {
            var text = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<AppSettings>(text, Json) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            // Never silently overwrite a file we couldn't read: keep it for inspection.
            var backup = FilePath + $".unreadable-{DateTime.Now:yyyyMMdd-HHmmss}";
            try { File.Copy(FilePath, backup, overwrite: true); } catch { /* ignore */ }
            LoadWarning = $"Settings file couldn't be read ({ex.Message}). A copy was kept at {backup}.";
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        var dir = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(dir);

        // Write to a temp file and swap it in, so a crash mid-write can't corrupt settings.
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
        if (!OperatingSystem.IsWindows())
        {
            // The file holds a session token: readable by you only.
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        File.Move(tmp, FilePath, overwrite: true);
    }
}
