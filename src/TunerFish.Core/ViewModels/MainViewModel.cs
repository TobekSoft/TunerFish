using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Net;
using System.Net.NetworkInformation;
using System.Windows.Input;
using TunerFish.Models;
using TunerFish.Services;

namespace TunerFish.ViewModels;

/// <summary>
/// All the app's behaviour lives here, independent of the UI toolkit.
/// The window calls <see cref="Tick"/> a few times a second to refresh playback state.
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly SettingsStore _store;
    private readonly JellyfinClient _jellyfin;
    private readonly CoverCache _covers;
    private readonly PlayReporter _reports;
    private readonly MpvPlayer? _player;
    private readonly QueueStore? _queueStore;
    private bool _queueDirty, _queueRestored;
    private readonly Func<string, int, object?> _decodeImage;
    private readonly Action<LogArea, string>? _log;

    private List<ArtistVm> _allArtists = new();
    private List<AlbumVm> _artistAlbums = new(); // all of the selected artist's, hidden ones too
    private List<AlbumVm> _viewAlbums = new();   // the view's albums, in the view's order
    private bool _viewLoaded;
    private FilteredArtists<ArtistVm> _viewList = FilteredArtists<ArtistVm>.Empty;  // the library's or the view's artists
    private FilteredArtists<ArtistVm> _shortlist = FilteredArtists<ArtistVm>.Empty; // those, narrowed by the filter box
    private readonly Dictionary<string, ArtistVm> _strangers = new(); // album artists not in the artist list
    private QueueItemVm? _currentEntry;
    private string? _tracksAlbumId;
    private bool _seeking;
    private CancellationTokenSource? _albumsCts, _tracksCts, _viewCts;
    private readonly SemaphoreSlim _sendingMarks = new(1, 1); // marks go to the server one at a time, in order

    public const int CoverSize = 300;

    /// <summary>Range of the album art size slider. The top is CoverSize, so covers are never enlarged.</summary>
    public const double MinAlbumArtSize = 100, MaxAlbumArtSize = CoverSize;

    private static readonly string PlaybackUnavailable = "Playback unavailable: libmpv not found. " + (
        OperatingSystem.IsWindows() ? "It belongs next to the app, as libmpv-2.dll." :
        OperatingSystem.IsMacOS() ? "With Homebrew: brew install mpv" :
        "On Mint/Ubuntu: sudo apt install libmpv2");

    public AppSettings Settings { get; }

    /// <summary>The app's display name (window title, sound mixer, Jellyfin's device list).</summary>
    public string AppName { get; }

    /// <summary>libmpv's version string, e.g. "mpv 0.37.0"; null when playback is unavailable.</summary>
    public string? PlayerVersion { get; }

    /// <param name="appName">Display name; the desktop app passes its AppInfo.Name. Data folders don't use it.</param>
    /// <param name="log">Writes to the app's log; every message has been through <see cref="AppLog.Clean"/>.</param>
    public MainViewModel(SettingsStore store, Func<string, int, object?> decodeImage,
                         bool silentAudio = false, string? coverCacheDir = null, HttpClient? http = null,
                         string appName = "TunerFish", string? databasePath = null, Action<LogArea, string>? log = null)
    {
        AppName = appName;
        _windowTitle = appName;
        _store = store;
        _decodeImage = decodeImage;
        _log = log;
        Settings = store.Load();
        JumpGroups = SortOrder.Groups.Select(g => new JumpGroupVm(g, JumpTo)).ToList();

        // Problems found while starting up. All of them are shown; one mustn't hide another.
        var warnings = new List<string>();
        if (store.LoadWarning is { } settingsWarning) warnings.Add(settingsWarning);
        var crashed = Settings.CrashToShare;
        if (crashed) warnings.Add($"{appName} closed unexpectedly last time. Menu → Open log folder shows what happened.");
        _jellyfin = new JellyfinClient(Settings.DeviceId, http, appName) { OnConnect = m => Log(LogArea.Server, m) };
        _covers = new CoverCache(_jellyfin, coverCacheDir);
        _reports = new PlayReporter(_jellyfin, log: m => Log(LogArea.Plays, m));

        _albumSort = Enum.TryParse<AlbumSortMode>(Settings.AlbumSort, out var sort) ? sort : AlbumSortMode.YearAscending;
        _singleClickPlaysAlbum = Settings.SingleClickPlaysAlbum;
        _showHidden = Settings.ShowHidden;
        _searchScope = SearchScopes.Parse(Settings.FilterScope);
        _clearQueueOnExit = Settings.ClearQueueOnExit;
        _theme = Enum.TryParse<ThemeMode>(Settings.Theme, out var theme) ? theme : ThemeMode.System;
        _volume = Math.Clamp(Settings.Volume, 0, 100);
        _bass = Tone.Clamp(Settings.Bass);
        _treble = Tone.Clamp(Settings.Treble);
        _albumArtSize = Math.Clamp(Settings.AlbumArtSize, MinAlbumArtSize, MaxAlbumArtSize);
        _serverUrl = SavedAddresses.FirstOrDefault() ?? "";
        _userName = Settings.UserName ?? "";

        try
        {
            _player = new MpvPlayer(silentAudio, appName);
            _player.SetVolume(_volume);
            _player.SetTone(_bass, _treble);
            PlayerVersion = _player.MpvVersion;
            _player.PlaybackFailed += reason => Log(LogArea.Playback, $"Couldn't play {_currentEntry?.Title ?? "a track"}: {reason}");
            Log(LogArea.Playback, $"Playing through {PlayerVersion ?? "libmpv"}");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            _player = null;
            warnings.Add(PlaybackUnavailable);
        }
        try
        {
            _queueStore = new QueueStore(databasePath);
        }
        catch (Exception ex)
        {
            warnings.Add($"The queue won't be kept for next time: {ex.Message}");
        }
        _status = string.Join(" · ", warnings);
        foreach (var w in warnings) Log(LogArea.App, w);
        if (crashed)
        {
            Settings.CrashToShare = false; // said once
            SaveSettings();
        }

        LoginCommand = new AsyncCommand(LoginAsync);
        SignOutCommand = new RelayCommand(SignOut);
        FlatToneCommand = new RelayCommand(() => { Bass = 0; Treble = 0; });
        TogglePauseCommand = new AsyncCommand(TogglePauseAsync);
        NextCommand = new RelayCommand(() => _player?.Next());
        PreviousCommand = new RelayCommand(Previous);
        PlaySelectedAlbumCommand = new AsyncCommand(() => SelectedAlbum is { } a ? PlayAlbumAsync(a) : Task.CompletedTask);
        QueueSelectedAlbumCommand = new AsyncCommand(() => SelectedAlbum is { } a ? AddAlbumToQueueAsync(a) : Task.CompletedTask);
        ClearQueueCommand = new RelayCommand(ClearQueue);
        AddServerAddressCommand = new RelayCommand(AddServerAddress);
        ShowViewAlbumsCommand = new RelayCommand(ShowViewAlbums);
        ToggleShowAllAlbumsCommand = new RelayCommand(() => ShowAllAlbums = !ShowAllAlbums);
        Queue.CollectionChanged += OnQueueChanged;

        // Wi-Fi changes, waking from suspend, NetBird coming up: check which address answers now (see Tick).
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
    }

    // ------------------------------------------------------------------ commands
    public ICommand LoginCommand { get; }
    public ICommand SignOutCommand { get; }
    public ICommand TogglePauseCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand PreviousCommand { get; }
    public ICommand PlaySelectedAlbumCommand { get; }
    public ICommand QueueSelectedAlbumCommand { get; }
    public ICommand ClearQueueCommand { get; }
    public ICommand AddServerAddressCommand { get; }
    public ICommand ShowViewAlbumsCommand { get; }
    public ICommand ToggleShowAllAlbumsCommand { get; }

    // ------------------------------------------------------------------ connection
    private bool _isLoggedIn;
    public bool IsLoggedIn { get => _isLoggedIn; private set => SetProperty(ref _isLoggedIn, value); }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }

    private string _status;
    public string Status { get => _status; set => SetProperty(ref _status, value); }

    private string _serverUrl;
    public string ServerUrl { get => _serverUrl; set => SetProperty(ref _serverUrl, value); }

    private string _userName;
    public string UserName { get => _userName; set => SetProperty(ref _userName, value); }

    private string _password = "";
    public string Password { get => _password; set => SetProperty(ref _password, value); }

    /// <summary>The saved addresses, most preferred first; settings files from before there could be several have just ServerUrl.</summary>
    private IReadOnlyList<string> SavedAddresses =>
        Settings.ServerUrls.Count > 0 ? Settings.ServerUrls
        : string.IsNullOrEmpty(Settings.ServerUrl) ? Array.Empty<string>() : new[] { Settings.ServerUrl };

    public async Task StartAsync()
    {
        var addresses = SavedAddresses;
        if (addresses.Count == 0 || string.IsNullOrEmpty(Settings.AccessToken) || string.IsNullOrEmpty(Settings.UserId))
            return;

        _jellyfin.Configure(addresses, Settings.AccessToken, Settings.UserId, Settings.ServerId);
        RefreshServerAddresses();
        IsLoggedIn = true;
        try
        {
            await FindServerAsync();
            RestoreQueue();
            await LoadArtistsAsync();
            Log(LogArea.Server, $"Signed in as {Settings.UserName} at {_jellyfin.ServerUrl}");
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            IsLoggedIn = false;
            Fail(LogArea.Server, "Your session has expired. Please sign in again.");
        }
        catch (Exception ex)
        {
            RestoreQueue(); // shown anyway; it plays once the server answers
            Fail(LogArea.Server, $"Couldn't reach {string.Join(" or ", addresses)}: {ex.Message}");
        }
    }

    private async Task LoginAsync()
    {
        if (string.IsNullOrWhiteSpace(ServerUrl) || string.IsNullOrWhiteSpace(UserName))
        {
            Status = "Enter the server address and your user name.";
            return;
        }

        IsBusy = true;
        Status = "Connecting…";
        try
        {
            var url = NormalizeUrl(ServerUrl);
            var previous = SavedAddresses;
            var auth = await _jellyfin.LoginAsync(url, UserName.Trim(), Password, CancellationToken.None);
            // Signing in again to the same server keeps its other addresses; a new server starts afresh.
            var sameServer = auth.ServerId != null && auth.ServerId == Settings.ServerId;
            var keep = sameServer ? previous : Array.Empty<string>();
            _jellyfin.Configure(keep.Prepend(url), auth.AccessToken, auth.User.Id, auth.ServerId);
            Settings.ServerId = auth.ServerId;
            Settings.UserName = UserName.Trim();
            Settings.UserId = auth.User.Id;
            Settings.AccessToken = auth.AccessToken;
            SaveAddresses(_jellyfin.Addresses); // also saves everything else
            RefreshServerAddresses();

            ServerUrl = url;
            Password = "";
            Status = "";
            IsLoggedIn = true;
            // Signed in again after the session expired: the queue saved then comes back. Another server's doesn't.
            if (!sameServer) _queueRestored = true;
            RestoreQueue();
            Log(LogArea.Server, $"Signed in as {Settings.UserName} at {url}");
            await LoadArtistsAsync();
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            Fail(LogArea.Server, "Wrong user name or password.");
        }
        catch (Exception ex)
        {
            Fail(LogArea.Server, $"Couldn't connect: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void SignOut()
    {
        Log(LogArea.Server, "Signed out");
        _ = StopReportsThenLogOutAsync(); // the last stop report needs the token the logout ends
        ClearQueue();

        Settings.AccessToken = null;
        Settings.UserId = null;
        SaveSettings();

        _viewCts?.Cancel();
        _searchCts?.Cancel();
        _hits = new List<SearchHit>();
        IsSearching = false;
        _view = LibraryView.Library;
        OnPropertyChanged(nameof(View));
        OnPropertyChanged(nameof(ViewIndex));
        OnPropertyChanged(nameof(InView));
        _viewAlbums = new List<AlbumVm>();
        _artistAlbums = new List<AlbumVm>();
        _allArtists = new List<ArtistVm>();
        _strangers.Clear();
        ApplyArtistFilter(); // empties the list and greys out the jump bar
        Albums = Array.Empty<AlbumVm>();
        Tracks = Array.Empty<TrackVm>();
        IsLoggedIn = false;
    }

    private async Task StopReportsThenLogOutAsync()
    {
        await _reports.CloseAsync();
        await _jellyfin.LogoutAsync();
    }

    // ------------------------------------------------------------------ server addresses
    // One server, several ways to reach it (home network, NetBird on the laptop away from home).
    // Same token and user on all of them; the first in order that answers is used.

    /// <summary>At startup: learn the server id if it was never saved, then use the best address that answers.</summary>
    private async Task FindServerAsync()
    {
        if (_jellyfin.ServerId is null && await _jellyfin.ServerIdAtAsync(_jellyfin.ServerUrl) is { } id)
        {
            _jellyfin.RememberServerId(id);
            Settings.ServerId = id;
            SaveSettings();
        }
        if (_jellyfin.Addresses.Count > 1) await _jellyfin.ConnectAsync();
    }

    public ObservableCollection<ServerAddressVm> ServerAddresses { get; } = new();

    private string _newServerAddress = "";
    public string NewServerAddress { get => _newServerAddress; set => SetProperty(ref _newServerAddress, value); }

    /// <summary>Adds the typed address after the existing ones.</summary>
    public void AddServerAddress()
    {
        var a = NormalizeUrl(NewServerAddress);
        NewServerAddress = "";
        if (a.Length == 0 || _jellyfin.Addresses.Contains(a)) return;
        ApplyAddresses(_jellyfin.Addresses.Append(a));
    }

    public void RemoveServerAddress(string address)
    {
        if (_jellyfin.Addresses.Count > 1) ApplyAddresses(_jellyfin.Addresses.Where(a => a != address));
    }

    public void MoveServerAddressUp(string address)
    {
        var list = _jellyfin.Addresses.ToList();
        var i = list.IndexOf(address);
        if (i <= 0) return;
        list.RemoveAt(i);
        list.Insert(i - 1, address);
        ApplyAddresses(list);
    }

    private void ApplyAddresses(IEnumerable<string> addresses)
    {
        _jellyfin.Configure(addresses, _jellyfin.Token, _jellyfin.UserId, _jellyfin.ServerId);
        SaveAddresses(_jellyfin.Addresses);
        RefreshServerAddresses();
        _ = CheckServerAddressesAsync();
    }

    private void SaveAddresses(IReadOnlyList<string> addresses)
    {
        Settings.ServerUrls = addresses.ToList();
        Settings.ServerUrl = addresses.FirstOrDefault();
        SaveSettings();
    }

    private void RefreshServerAddresses()
    {
        var list = _jellyfin.Addresses;
        ServerAddresses.Clear();
        for (var i = 0; i < list.Count; i++)
            ServerAddresses.Add(new ServerAddressVm(list[i], canMoveUp: i > 0, canRemove: list.Count > 1,
                                                    MoveServerAddressUp, RemoveServerAddress));
    }

    /// <summary>Marks each address reachable or not, and switches to the best one that answers.</summary>
    public async Task CheckServerAddressesAsync()
    {
        var rows = ServerAddresses.ToList();
        foreach (var row in rows) { row.Reachable = null; row.InUse = false; }
        await Task.WhenAll(rows.Select(async row => row.Reachable = await _jellyfin.IsReachableAsync(row.Address)));
        await _jellyfin.ConnectAsync();
        foreach (var row in rows) row.InUse = row.Address == _jellyfin.ServerUrl;
    }

    private volatile bool _networkChanged;
    private bool _reconnecting;

    private void OnNetworkAddressChanged(object? sender, EventArgs e) => _networkChanged = true;

    private async Task ReconnectAsync()
    {
        _reconnecting = true;
        try { await _jellyfin.ConnectAsync(); }
        catch { /* stays on the current address */ }
        finally { _reconnecting = false; }
    }

    public static string NormalizeUrl(string url)
    {
        url = url.Trim().TrimEnd('/');
        if (!url.Contains("://", StringComparison.Ordinal)) url = "http://" + url;
        return url;
    }

    // ------------------------------------------------------------------ artists
    private IReadOnlyList<ArtistVm> _artists = Array.Empty<ArtistVm>();
    public IReadOnlyList<ArtistVm> Artists { get => _artists; private set => SetProperty(ref _artists, value); }

    /// <summary>The A–Z jump bar: "#", then A to Z, always all 27.</summary>
    public IReadOnlyList<JumpGroupVm> JumpGroups { get; }

    /// <summary>Raised by the jump bar with the artist the view should scroll to the top of the list.</summary>
    public event Action<ArtistVm>? ArtistJumpRequested;

    /// <summary>The first listed artist in a jump-bar group (following the filter), or null if none.</summary>
    public ArtistVm? FirstArtistInGroup(string group) => Artists.FirstOrDefault(a => a.Group == group);

    /// <summary>Asks the view to scroll to a group's first artist. Doesn't select or filter anything.</summary>
    public void JumpTo(string group)
    {
        if (FirstArtistInGroup(group) is { } artist) ArtistJumpRequested?.Invoke(artist);
    }

    private string _artistFilter = "";
    /// <summary>
    /// The filter box: artist names as you type, and album and track titles (<see cref="SearchScope"/>) from
    /// Jellyfin's search once typing pauses. A played view goes to the Library to search it.
    /// </summary>
    public string ArtistFilter
    {
        get => _artistFilter;
        set
        {
            if (!SetProperty(ref _artistFilter, value)) return;
            if (value.Trim().Length > 0 && View is LibraryView.RecentlyPlayed or LibraryView.MostPlayed) View = LibraryView.Library;
            ApplyArtistFilter();
            RefreshAlbums();
            _ = SearchAsync();
        }
    }

    // ------------------------------------------------------------------ search
    // Album and track titles across the whole collection: Jellyfin's searchTerm, one request per type.
    // Album-first: the results are the artist list (Search.Narrow). Until the answer comes, the hits for
    // the earlier text narrow the list.

    private IReadOnlySet<SearchScope> _searchScope;
    private List<SearchHit> _hits = new();
    private CancellationTokenSource? _searchCts;

    /// <summary>How long typing must pause before titles are looked up.</summary>
    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(300);

    public bool SearchArtists { get => _searchScope.Contains(SearchScope.Artists); set => SetSearchScope(SearchScope.Artists, value); }
    public bool SearchAlbums { get => _searchScope.Contains(SearchScope.Albums); set => SetSearchScope(SearchScope.Albums, value); }
    public bool SearchTracks { get => _searchScope.Contains(SearchScope.Tracks); set => SetSearchScope(SearchScope.Tracks, value); }

    private bool _isSearching;
    /// <summary>True while album and track titles are being looked up.</summary>
    public bool IsSearching
    {
        get => _isSearching;
        private set { if (SetProperty(ref _isSearching, value)) OnPropertyChanged(nameof(ArtistPaneNote)); }
    }

    private void SetSearchScope(SearchScope s, bool on)
    {
        var scope = _searchScope.ToHashSet();
        if (on) scope.Add(s); else scope.Remove(s);
        // Something must be looked in: the last one stays on.
        if (scope.Count > 0 && !scope.SetEquals(_searchScope))
        {
            _searchScope = scope;
            Settings.FilterScope = SearchScopes.Format(scope);
            SaveSettings();
            ApplyArtistFilter();
            RefreshAlbums();
            _ = SearchAsync();
        }
        OnPropertyChanged(nameof(SearchArtists));
        OnPropertyChanged(nameof(SearchAlbums));
        OnPropertyChanged(nameof(SearchTracks));
    }

    private async Task SearchAsync()
    {
        _searchCts?.Cancel();
        var term = ArtistFilter.Trim();
        var scope = _searchScope;
        if (!Search.LooksBeyondArtists(term, scope))
        {
            _hits = new List<SearchHit>();
            IsSearching = false;
            return;
        }
        var cts = _searchCts = new CancellationTokenSource();
        var ct = cts.Token;
        IsSearching = true;
        try
        {
            await Task.Delay(SearchDelay, ct);
            var albums = scope.Contains(SearchScope.Albums) ? _jellyfin.SearchAsync(term, "MusicAlbum", Search.Limit, ct) : null;
            var tracks = scope.Contains(SearchScope.Tracks) ? _jellyfin.SearchAsync(term, "Audio", Search.Limit, ct) : null;
            var hits = Search.Hits(albums is null ? [] : (await albums).Items, tracks is null ? [] : (await tracks).Items);
            if (ct.IsCancellationRequested) return;
            _hits = hits;
            ApplyArtistFilter();
            RefreshAlbums();
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // The list stays as narrowed so far (artist names still filter).
            Fail(LogArea.Server, $"Couldn't search: {ex.Message}");
        }
        if (_searchCts == cts) IsSearching = false;
    }

    private ArtistVm? _selectedArtist;
    public ArtistVm? SelectedArtist
    {
        get => _selectedArtist;
        set
        {
            // Lists clear their selection when their items are replaced (e.g. while filtering).
            // Ignore that, so the albums you're looking at don't vanish under you.
            if (value is null) return;
            if (!SetProperty(ref _selectedArtist, value)) return;
            _showAllAlbums = false;
            OnPropertyChanged(nameof(ShowAllAlbums));
            OnPropertyChanged(nameof(ShowAllAlbumsLabel));
            OnPropertyChanged(nameof(ShowingViewAlbums));
            OnPropertyChanged(nameof(AlbumsHeader));
            Settings.LastArtistId = value.Id;
            _ = LoadAlbumsAsync(value);
        }
    }

    private async Task LoadArtistsAsync()
    {
        IsBusy = true;
        try
        {
            var result = await _jellyfin.GetAlbumArtistsAsync(CancellationToken.None);
            var cmp = SortOrder.Comparer;
            _allArtists = result.Items.Select(i => Wire(new ArtistVm(i)))
                .OrderBy(a => a.SortKey, cmp).ThenBy(a => a.Name, cmp).ToList();
            ApplyArtistFilter();

            var last = _allArtists.FirstOrDefault(a => a.Id == Settings.LastArtistId);
            if (last != null) SelectedArtist = last;
            Status = "";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>The filter box narrows the artist list (the library or a view by marks), not the played views.</summary>
    private bool Filtering => ArtistFilter.Trim().Length > 0 && (View == LibraryView.Library || View.ByMarks());

    private void ApplyArtistFilter()
    {
        // The view's albums plus the artist's, whose marks may just have changed.
        var viewIds = _viewAlbums.Select(a => a.Id).ToHashSet();
        _viewList = View == LibraryView.Library
            ? FilteredArtists<ArtistVm>.All(LibraryViews.Visible(_allArtists, ShowHidden))
            : View.Filter(_allArtists, _viewAlbums.Concat(_artistAlbums.Where(a => !viewIds.Contains(a.Id))), ShowHidden, Stranger);
        _shortlist = Search.Narrow(_viewList, ArtistFilter, _searchScope, _hits, ShowHidden || View == LibraryView.Hidden);
        Artists = _shortlist.Artists;
        foreach (var a in Artists) a.Note = _shortlist.NoteOf(a.Id)?.ToString();
        OnPropertyChanged(nameof(SelectedArtist)); // keep the highlight if it's still in the list
        OnPropertyChanged(nameof(ArtistPaneNote));

        var present = Artists.Select(a => a.Group).ToHashSet();
        foreach (var g in JumpGroups) g.HasArtists = present.Contains(g.Group);
    }

    /// <summary>An album artist that isn't in the artist list, filed under its name. The same row each time.</summary>
    private ArtistVm Stranger(string id, string name)
    {
        if (!_strangers.TryGetValue(id, out var artist))
            _strangers[id] = artist = Wire(new ArtistVm(new Models.BaseItem { Id = id, Name = name }));
        return artist;
    }

    // ------------------------------------------------------------------ albums
    private IReadOnlyList<AlbumVm> _albums = Array.Empty<AlbumVm>();
    public IReadOnlyList<AlbumVm> Albums
    {
        get => _albums;
        private set
        {
            if (!SetProperty(ref _albums, value)) return;
            OnPropertyChanged(nameof(AlbumCountText));
            OnPropertyChanged(nameof(AlbumPaneNote));
            OnPropertyChanged(nameof(AlbumsLimitText));
        }
    }

    private bool _albumsLimited;
    /// <summary>The artist was picked in a view by marks, or from a search, that shows only some of its albums.</summary>
    public bool AlbumsLimited { get => _albumsLimited; private set => SetProperty(ref _albumsLimited, value); }

    private int _albumsInAll;

    private bool _showAllAlbums;
    /// <summary>In a view by marks, all the picked artist's albums rather than the view's. Off again for the next artist.</summary>
    public bool ShowAllAlbums
    {
        get => _showAllAlbums;
        set
        {
            if (!SetProperty(ref _showAllAlbums, value)) return;
            OnPropertyChanged(nameof(ShowAllAlbumsLabel));
            RefreshAlbums();
        }
    }

    /// <summary>What the bar calls the albums it shows: "Matching" when the filter box limits them, or the view ("Favourites").</summary>
    private string AlbumsLabel => Filtering ? "Matching" : View.Label();

    /// <summary>"Favourites: 2 of 14 albums", or "All 14 albums" with Show all.</summary>
    public string AlbumsLimitText => ShowAllAlbums
        ? $"All {_albumsInAll} albums"
        : $"{AlbumsLabel}: {Albums.Count} of {_albumsInAll} album{(_albumsInAll == 1 ? "" : "s")}";

    public string ShowAllAlbumsLabel => ShowAllAlbums ? $"{AlbumsLabel} only" : "Show all";

    /// <summary>The album pane's title: the artist, or the view when it's showing the view's albums.</summary>
    public string AlbumsHeader => ShowingViewAlbums ? View.Label() : SelectedArtist?.Name ?? "";

    /// <summary>
    /// Shows the albums of the selected artist, or the visible ones of them (in a view by marks, the view's,
    /// unless Show all); in a view with no artist picked, the view's albums. Keeps the selected album
    /// highlighted if it's still listed.
    /// </summary>
    private void RefreshAlbums()
    {
        IReadOnlySet<string>? only = null;
        if (ShowingViewAlbums)
        {
            var albums = View.Albums(_viewAlbums, ShowHidden);
            Albums = View.ByMarks() ? albums.Where(_shortlist.Shows).ToList() : albums;
        }
        else
        {
            if (SelectedArtist is { } artist)
            {
                if (_shortlist.Lists(artist.Id)) only = _shortlist.AlbumsOf(artist.Id);
                // Filtered out: as the view shows it, or as the library does (the albums don't vanish as you type).
                else if (View.ByMarks()) only = _viewList.Lists(artist.Id) ? _viewList.AlbumsOf(artist.Id) : new HashSet<string>(); // its last mark just went
            }
            _albumsInAll = View.AlbumsOf(_artistAlbums, null, ShowHidden).Count;
            Albums = SortAlbums(View.AlbumsOf(_artistAlbums, ShowAllAlbums ? null : only, ShowHidden), AlbumSort);
        }
        AlbumsLimited = only != null;
        OnPropertyChanged(nameof(AlbumsLimitText));
        OnPropertyChanged(nameof(ShowAllAlbumsLabel));
        OnPropertyChanged(nameof(SelectedAlbum));
    }

    public string AlbumCountText => Albums.Count switch { 0 => "", 1 => "1 album", var n => $"{n} albums" };

    private AlbumVm? _selectedAlbum;
    public AlbumVm? SelectedAlbum
    {
        get => _selectedAlbum;
        set
        {
            if (value is null) return; // same reasoning as SelectedArtist
            if (!SetProperty(ref _selectedAlbum, value)) return;
            _ = LoadTracksAsync(value);
        }
    }

    private AlbumSortMode _albumSort;
    public AlbumSortMode AlbumSort
    {
        get => _albumSort;
        set
        {
            if (!SetProperty(ref _albumSort, value)) return;
            OnPropertyChanged(nameof(AlbumSortIndex));
            Settings.AlbumSort = value.ToString();
            SaveSettings();
            RefreshAlbums(); // re-highlights the same album in the new order
        }
    }

    /// <summary>For a ComboBox: 0 = Title, 1 = Year oldest first, 2 = Year newest first.</summary>
    public int AlbumSortIndex
    {
        get => (int)AlbumSort;
        set { if (Enum.IsDefined(typeof(AlbumSortMode), value)) AlbumSort = (AlbumSortMode)value; }
    }

    private bool _singleClickPlaysAlbum;
    public bool SingleClickPlaysAlbum
    {
        get => _singleClickPlaysAlbum;
        set
        {
            if (!SetProperty(ref _singleClickPlaysAlbum, value)) return;
            Settings.SingleClickPlaysAlbum = value;
            SaveSettings();
        }
    }

    private ThemeMode _theme;
    /// <summary>Light, dark or follow the desktop. The window applies it; Core only remembers it.</summary>
    public ThemeMode Theme
    {
        get => _theme;
        set
        {
            if (!SetProperty(ref _theme, value)) return;
            OnPropertyChanged(nameof(ThemeIndex));
            Settings.Theme = value.ToString();
            SaveSettings();
        }
    }

    /// <summary>For a ComboBox: 0 = Light, 1 = Dark, 2 = System.</summary>
    public int ThemeIndex
    {
        get => (int)Theme;
        set { if (Enum.IsDefined(typeof(ThemeMode), value)) Theme = (ThemeMode)value; }
    }

    private double _albumArtSize;
    /// <summary>Width and height of each album cover in the album list. Saved with the window layout on close.</summary>
    public double AlbumArtSize
    {
        get => _albumArtSize;
        set
        {
            if (!SetProperty(ref _albumArtSize, Math.Clamp(value, MinAlbumArtSize, MaxAlbumArtSize))) return;
            Settings.AlbumArtSize = _albumArtSize;
        }
    }

    public static IReadOnlyList<AlbumVm> SortAlbums(IEnumerable<AlbumVm> albums, AlbumSortMode mode)
    {
        var cmp = SortOrder.Comparer;
        return mode switch
        {
            AlbumSortMode.YearAscending => albums.OrderBy(a => a.Year ?? int.MaxValue).ThenBy(a => a.SortTitle, cmp).ThenBy(a => a.Title, cmp).ToList(),
            AlbumSortMode.YearDescending => albums.OrderByDescending(a => a.Year ?? int.MinValue).ThenBy(a => a.SortTitle, cmp).ThenBy(a => a.Title, cmp).ToList(),
            _ => albums.OrderBy(a => a.SortTitle, cmp).ThenBy(a => a.Title, cmp).ToList(),
        };
    }

    private async Task LoadAlbumsAsync(ArtistVm artist)
    {
        _albumsCts?.Cancel();
        var cts = _albumsCts = new CancellationTokenSource();
        try
        {
            var result = await _jellyfin.GetAlbumsAsync(artist.Id, cts.Token);
            if (cts.IsCancellationRequested) return;

            _artistAlbums = result.Items.Select(i => Wire(new AlbumVm(i))).ToList();
            _selectedAlbum = null;
            Tracks = Array.Empty<TrackVm>();
            _tracksAlbumId = null;
            RefreshAlbums();

            foreach (var album in _artistAlbums) _ = LoadCoverAsync(album);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Fail(LogArea.Server, $"Couldn't load albums: {ex.Message}"); }
    }

    private async Task LoadCoverAsync(AlbumVm album)
    {
        if (album.Cover != null) return;
        var file = await _covers.GetFileAsync(album.Id, album.ImageTag, CoverSize);
        if (file is null) return;
        try
        {
            album.Cover = await Task.Run(() => _decodeImage(file, CoverSize));
        }
        catch
        {
            // A broken image file shouldn't break browsing.
        }
    }

    // ------------------------------------------------------------------ views
    // The whole library, or a view of the user's own data (Favourites, Recently played, ...). A view by
    // marks shortens the artist list to the artists that are marked or have marked albums, and the album
    // pane lists the marked albums from across the library; picking an artist shows the albums the view
    // shows of it (all of them for a marked artist, or with Show all), and ShowViewAlbums goes back.
    // The played views list albums only. Not saved.

    public IReadOnlyList<string> ViewLabels { get; } = LibraryViews.All.Select(v => v.Label()).ToList();

    private LibraryView _view;
    public LibraryView View
    {
        get => _view;
        set
        {
            if (!SetProperty(ref _view, value)) return;
            OnPropertyChanged(nameof(ViewIndex));
            OnPropertyChanged(nameof(InView));
            _viewCts?.Cancel();
            _viewAlbums = new List<AlbumVm>();
            ApplyArtistFilter();
            if (value != LibraryView.Library)
            {
                ShowViewAlbums();
                return;
            }
            // Back to the library, on the artist you last looked at.
            _selectedArtist = null;
            OnPropertyChanged(nameof(SelectedArtist));
            OnPropertyChanged(nameof(ShowingViewAlbums));
            OnPropertyChanged(nameof(AlbumsHeader));
            if (_allArtists.FirstOrDefault(a => a.Id == Settings.LastArtistId) is { } last) SelectedArtist = last;
            else ShowNoAlbums();
        }
    }

    /// <summary>For a ComboBox, in the order of <see cref="ViewLabels"/>.</summary>
    public int ViewIndex
    {
        get => (int)View;
        set { if (Enum.IsDefined(typeof(LibraryView), value)) View = (LibraryView)value; }
    }

    public bool InView => View != LibraryView.Library;

    /// <summary>In a view with no artist picked: the album pane lists the view's albums.</summary>
    public bool ShowingViewAlbums => InView && SelectedArtist is null;

    /// <summary>Why the artist list is empty in a view, or null.</summary>
    public string? ArtistPaneNote => View switch
    {
        LibraryView.RecentlyPlayed or LibraryView.MostPlayed => $"{View.Label()} lists albums, not artists.",
        _ when Artists.Count > 0 => null,
        _ when Filtering => IsSearching ? "Searching…" : "No matches.",
        LibraryView.Library => null,
        _ when !_viewLoaded => null,
        LibraryView.Favourites => "No favourites.",
        LibraryView.Liked => "Nothing liked.",
        _ => "Nothing is hidden.",
    };

    /// <summary>Why the view's album list is empty, once it has loaded, or null.</summary>
    public string? AlbumPaneNote => !ShowingViewAlbums || !_viewLoaded || Albums.Count > 0 ? null : View switch
    {
        LibraryView.Favourites => "No favourite albums. Right-click an artist or album, or use the heart on an album, to add one.",
        LibraryView.Liked => "No liked albums. Right-click an artist or album, or use the thumbs up on an album, to like one.",
        LibraryView.Hidden => "Nothing is hidden.",
        _ => "Nothing played yet.",
    };

    /// <summary>Shows the view's albums again (after picking one of its artists), fetched afresh.</summary>
    public void ShowViewAlbums()
    {
        if (!InView) return;
        _selectedArtist = null;
        OnPropertyChanged(nameof(SelectedArtist));
        OnPropertyChanged(nameof(ShowingViewAlbums));
        OnPropertyChanged(nameof(AlbumsHeader));
        _albumsCts?.Cancel();
        ShowNoAlbums();
        _ = LoadViewAsync(View);
    }

    private void ShowNoAlbums()
    {
        _artistAlbums = new List<AlbumVm>();
        _selectedAlbum = null;
        Tracks = Array.Empty<TrackVm>();
        _tracksAlbumId = null;
        _viewLoaded = false;
        RefreshAlbums();
    }

    private async Task LoadViewAsync(LibraryView view)
    {
        _viewCts?.Cancel();
        var cts = _viewCts = new CancellationTokenSource();
        var ct = cts.Token;
        IsBusy = true;
        try
        {
            var albums = view switch
            {
                LibraryView.Favourites => FiledByArtist(ViewRows((await _jellyfin.GetAllAlbumsAsync("IsFavorite", ct)).Items)),
                LibraryView.Liked => FiledByArtist(ViewRows((await _jellyfin.GetAllAlbumsAsync("Likes", ct)).Items)),
                // Jellyfin's Dislikes filter matches everything not liked: pick the hidden ones out here.
                LibraryView.Hidden => FiledByArtist(ViewRows((await _jellyfin.GetAllAlbumsAsync(null, ct)).Items).Where(a => a.IsHidden)),
                LibraryView.RecentlyPlayed => await RecentlyPlayedAsync(ct),
                _ => await MostPlayedAsync(ct),
            };
            if (ct.IsCancellationRequested) return;
            _viewAlbums = albums;
            _viewLoaded = true;
            ApplyArtistFilter(); // the artists with marked albums join the list
            RefreshAlbums();
            foreach (var album in albums) _ = LoadCoverAsync(album);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!ct.IsCancellationRequested) Fail(LogArea.Server, $"Couldn't load {view.Label()}: {ex.Message}"); }
        finally
        {
            if (_viewCts == cts) IsBusy = false;
        }
    }

    private List<AlbumVm> ViewRows(IEnumerable<Models.BaseItem> items) =>
        items.Select(i => Wire(new AlbumVm(i) { ShowArtist = true })).ToList();

    private List<AlbumVm> FiledByArtist(IEnumerable<AlbumVm> albums)
    {
        var keys = _allArtists.ToDictionary(a => a.Id, a => a.SortKey);
        return LibraryViews.ByArtist(albums, a => a.ArtistId is { } id && keys.TryGetValue(id, out var k) ? k : a.ArtistName, a => a.SortTitle);
    }

    private async Task<List<PlayedTrack>> PlayedTracksAsync(string sortBy, CancellationToken ct) =>
        (await _jellyfin.GetPlayedTracksAsync(sortBy, PlayHistory.Tracks, ct)).Items.Select(PlayedTrack.From).OfType<PlayedTrack>().ToList();

    private async Task<List<AlbumVm>> RecentlyPlayedAsync(CancellationToken ct)
    {
        var ids = PlayHistory.Recent(await PlayedTracksAsync("DatePlayed", ct));
        return InOrder(ids, await _jellyfin.GetAlbumsByIdAsync(ids, ct));
    }

    private async Task<List<AlbumVm>> MostPlayedAsync(CancellationToken ct)
    {
        var tracks = await PlayedTracksAsync("PlayCount", ct);
        var found = await _jellyfin.GetAlbumsByIdAsync(PlayHistory.Candidates(tracks), ct);
        var ids = PlayHistory.Most(tracks, found.ToDictionary(a => a.Id, a => a.ChildCount ?? 0));
        return InOrder(ids, found);
    }

    /// <summary>The albums of <paramref name="ids"/>, in that order.</summary>
    private List<AlbumVm> InOrder(IReadOnlyList<string> ids, IEnumerable<Models.BaseItem> items)
    {
        var byId = ViewRows(items).ToDictionary(a => a.Id);
        return ids.Select(id => byId.GetValueOrDefault(id)).OfType<AlbumVm>().ToList();
    }

    // ------------------------------------------------------------------ marks
    // Heart, thumbs up and thumbs down (hide) on artists and albums: the user's own data in Jellyfin.
    // Shown at once and sent straight away; if the server can't be told, the change is undone.

    private bool _showHidden;
    /// <summary>List artists and albums marked hidden while browsing (the Hidden view always lists them).</summary>
    public bool ShowHidden
    {
        get => _showHidden;
        set
        {
            if (!SetProperty(ref _showHidden, value)) return;
            Settings.ShowHidden = value;
            SaveSettings();
            ApplyArtistFilter();
            RefreshAlbums();
        }
    }

    private ArtistVm Wire(ArtistVm artist)
    {
        WireMarks(artist);
        return artist;
    }

    private AlbumVm Wire(AlbumVm album)
    {
        album.PlayCommand = new AsyncCommand(() => PlayAlbumAsync(album));
        album.QueueCommand = new AsyncCommand(() => AddAlbumToQueueAsync(album));
        WireMarks(album);
        return album;
    }

    private void WireMarks(MarkedVm item)
    {
        item.ToggleFavouriteCommand = new AsyncCommand(() => SetMarksAsync(item, item.Marks.WithFavourite(!item.IsFavourite)));
        item.ToggleLikeCommand = new AsyncCommand(() => SetMarksAsync(item, item.Marks.WithLiked(!item.IsLiked)));
        item.ToggleHiddenCommand = new AsyncCommand(() => SetMarksAsync(item, item.Marks.WithHidden(!item.IsHidden)));
    }

    /// <summary>Every row standing for the item with this id (an album can be in a view and in its artist's list).</summary>
    private IEnumerable<MarkedVm> RowsFor(string id) =>
        _allArtists.Where(a => a.Id == id).Concat(_strangers.Values.Where(a => a.Id == id)).Cast<MarkedVm>()
            .Concat(_artistAlbums.Where(a => a.Id == id))
            .Concat(_viewAlbums.Where(a => a.Id == id))
            .Append(SelectedAlbum).OfType<MarkedVm>()
            .Distinct();

    private string IdOf(MarkedVm item) => item switch { ArtistVm a => a.Id, AlbumVm a => a.Id, _ => "" };

    /// <summary>Changes the marks on an artist or album: shown at once, then sent to the server.</summary>
    public async Task SetMarksAsync(MarkedVm item, Marks marks)
    {
        var old = item.Marks;
        if (marks == old) return;
        var id = IdOf(item);
        Show(id, marks);

        await _sendingMarks.WaitAsync();
        try
        {
            if (marks.Favourite != old.Favourite) await _jellyfin.SetFavouriteAsync(id, marks.Favourite);
            if (marks.Likes != old.Likes) await _jellyfin.SetLikesAsync(id, marks.Likes);
        }
        catch (Exception ex)
        {
            Show(id, old);
            Fail(LogArea.Marks, $"Couldn't save the change to {item}: {ex.Message}");
        }
        finally
        {
            _sendingMarks.Release();
        }
    }

    private void Show(string id, Marks marks)
    {
        foreach (var row in RowsFor(id)) row.Marks = marks;
        ApplyArtistFilter();
        RefreshAlbums();
    }

    // ------------------------------------------------------------------ tracks
    private IReadOnlyList<TrackVm> _tracks = Array.Empty<TrackVm>();
    public IReadOnlyList<TrackVm> Tracks { get => _tracks; private set => SetProperty(ref _tracks, value); }

    private async Task LoadTracksAsync(AlbumVm album)
    {
        _tracksCts?.Cancel();
        var cts = _tracksCts = new CancellationTokenSource();
        try
        {
            var tracks = await FetchTracksAsync(album, cts.Token);
            if (cts.IsCancellationRequested) return;
            foreach (var t in tracks) t.IsNowPlaying = CurrentTrack != null && t.Id == CurrentTrack.Id;
            _tracksAlbumId = album.Id;
            Tracks = tracks;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Fail(LogArea.Server, $"Couldn't load tracks: {ex.Message}"); }
    }

    private async Task<List<TrackVm>> FetchTracksAsync(AlbumVm album, CancellationToken ct)
    {
        var result = await _jellyfin.GetTracksAsync(album.Id, ct);
        return result.Items.Select(TrackVm.From).ToList();
    }

    // ------------------------------------------------------------------ queue
    /// <summary>What's playing and what's next, in order. The mpv playlist mirrors it entry for entry.</summary>
    public ObservableCollection<QueueItemVm> Queue { get; } = new();

    public bool HasQueue => Queue.Count > 0;

    public string QueueSummaryText
    {
        get
        {
            if (Queue.Count == 0) return "";
            var count = Queue.Count == 1 ? "1 track" : $"{Queue.Count} tracks";
            var seconds = Queue.Sum(e => e.Track.Seconds ?? 0);
            return seconds > 0 ? $"{count} · {TrackVm.FormatTime(seconds)}" : count;
        }
    }

    private void OnQueueChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasQueue));
        OnPropertyChanged(nameof(QueueSummaryText));
        _queueDirty = true;

        // Each row's "Remove album (n tracks)": the length of the run of its album it's in.
        for (var first = 0; first < Queue.Count;)
        {
            var (_, last) = AlbumRunAt(first);
            var n = last - first + 1;
            for (var i = first; i <= last; i++)
                Queue[i].RemoveAlbumLabel = Queue[i].Album is null ? null : n == 1 ? "Remove album (1 track)" : $"Remove album ({n} tracks)";
            first = last + 1;
        }
    }

    /// <summary>
    /// The queue positions of the album the entry at <paramref name="index"/> belongs to: the unbroken run of
    /// its album's tracks around it, matched by album id (the same album queued twice is two runs).
    /// Just <paramref name="index"/> if it has no album.
    /// </summary>
    public (int First, int Last) AlbumRunAt(int index)
    {
        var albumId = Queue[index].Album?.Id;
        if (albumId is null) return (index, index);
        var first = index;
        while (first > 0 && Queue[first - 1].Album?.Id == albumId) first--;
        var last = index;
        while (last < Queue.Count - 1 && Queue[last + 1].Album?.Id == albumId) last++;
        return (first, last);
    }

    // ------------------------------------------------------------------ saved queue
    // Kept across restarts like the Android app's: the entries in QueueStore, the place in settings
    // (QueueIndex, QueuePositionMs). Saved in Tick after a change and when the app closes.

    private bool _clearQueueOnExit;
    /// <summary>Start each launch with an empty queue instead of the saved one.</summary>
    public bool ClearQueueOnExit
    {
        get => _clearQueueOnExit;
        set
        {
            if (!SetProperty(ref _clearQueueOnExit, value)) return;
            Settings.ClearQueueOnExit = value;
            SaveSettings();
        }
    }

    /// <summary>
    /// Puts the saved queue back, paused at the saved track and time, ready for play. Once per run, on start;
    /// with Clear queue on exit, the saved one is thrown away instead.
    /// </summary>
    private void RestoreQueue()
    {
        if (_queueRestored || _queueStore is null) return;
        _queueRestored = true;
        try
        {
            if (ClearQueueOnExit || Queue.Count > 0)
            {
                if (ClearQueueOnExit) _queueStore.Save(Array.Empty<SavedQueueEntry>());
                return;
            }
            var saved = _queueStore.Load();
            if (saved.Count == 0) return;

            // One album row per album, as when it was queued, so covers load once and runs stay runs.
            var albums = saved.Where(e => e.AlbumId != null).DistinctBy(e => e.AlbumId).ToDictionary(e => e.AlbumId!, e =>
                new AlbumVm(new BaseItem
                {
                    Id = e.AlbumId!,
                    Name = e.AlbumTitle ?? "",
                    ImageTags = e.ImageTag is null ? null : new Dictionary<string, string> { ["Primary"] = e.ImageTag },
                }));
            foreach (var e in saved)
            {
                var track = new TrackVm { Id = e.TrackId, Title = e.Title, Artist = e.Artist, Seconds = e.Seconds, Length = TrackVm.FormatTime(e.Seconds) };
                Queue.Add(NewEntry(track, e.AlbumId is null ? null : albums[e.AlbumId]));
            }
            foreach (var album in albums.Values) _ = LoadCoverAsync(album);

            var index = Math.Clamp(Settings.QueueIndex, 0, Queue.Count - 1);
            var seconds = Math.Max(0, Settings.QueuePositionMs / 1000.0);
            _player?.LoadPlaylist(Queue.Select(q => _jellyfin.StreamUrl(q.Track.Id)).ToList(), index, paused: true);
            _playlistAddress = _jellyfin.ServerUrl;
            _pendingSeek = seconds > 1 ? seconds : null;
            _queueDirty = false;
            Tick();
        }
        catch (Exception ex)
        {
            Fail(LogArea.App, $"Couldn't put back the queue: {ex.Message}");
        }
    }

    /// <summary>
    /// Saves the queue if it changed, and always the place in it. At the end of the queue that's the start of
    /// the last track, so play has something to play.
    /// </summary>
    private void SaveQueue()
    {
        if (_queueStore is null || !_queueRestored && Queue.Count == 0) return; // never overwrite it before it's back
        if (_queueDirty)
        {
            _queueDirty = false;
            try
            {
                _queueStore.Save(Queue.Select(q => new SavedQueueEntry(q.Track.Id, q.Track.Title, q.Track.Artist, q.Track.Seconds,
                                                                       q.Album?.Id, q.Album?.Title, q.Album?.ImageTag)).ToList());
            }
            catch (Exception ex)
            {
                Fail(LogArea.App, $"Couldn't save the queue: {ex.Message}");
            }
        }

        var playing = _player is { IsIdle: false } ? _player.PlaylistPosition : -1;
        var index = playing >= 0 ? playing : _lastIndex;
        var seconds = playing >= 0 ? _player!.TimePosition ?? 0
                    : _lastDuration - _lastTime > 2 ? _lastTime : 0;
        if (_pendingSeek is { } resumeAt && playing >= 0 && seconds < 1) seconds = resumeAt; // restored, not yet sought
        Settings.QueueIndex = index >= 0 && index < Queue.Count ? index : 0;
        Settings.QueuePositionMs = Queue.Count == 0 ? 0 : (long)(seconds * 1000);
    }

    private QueueItemVm NewEntry(TrackVm track, AlbumVm? album)
    {
        var entry = new QueueItemVm(track, album);
        entry.RemoveCommand = new RelayCommand(() => RemoveFromQueue(entry));
        entry.RemoveAlbumCommand = new RelayCommand(() => RemoveAlbumFromQueue(entry));
        entry.PlayCommand = new RelayCommand(() => PlayQueueItem(entry));
        return entry;
    }

    private async Task<List<TrackVm>> TracksOfAsync(AlbumVm album) =>
        _tracksAlbumId == album.Id && Tracks.Count > 0
            ? Tracks.ToList()
            : await FetchTracksAsync(album, CancellationToken.None);

    /// <summary>
    /// Adds the whole album to the end of the queue. Never starts playing: into an empty queue, its first
    /// track loads paused, ready for play. Play album is the one that plays.
    /// </summary>
    public async Task AddAlbumToQueueAsync(AlbumVm album)
    {
        try
        {
            Enqueue(await TracksOfAsync(album), album);
        }
        catch (Exception ex)
        {
            Fail(LogArea.Playback, $"Couldn't queue {album.Title}: {ex.Message}");
        }
    }

    /// <summary>Adds one track from the track list to the end of the queue.</summary>
    public void AddTrackToQueue(TrackVm track) =>
        Enqueue(new[] { track }, Albums.FirstOrDefault(a => a.Id == _tracksAlbumId));

    private void Enqueue(IReadOnlyList<TrackVm> tracks, AlbumVm? album)
    {
        if (tracks.Count == 0) return;
        var wasEmpty = Queue.Count == 0;
        foreach (var track in tracks)
        {
            Queue.Add(NewEntry(track, album));
            _player?.Append(_jellyfin.StreamUrl(track.Id));
        }
        _playlistAddress ??= _jellyfin.ServerUrl;
        if (wasEmpty) _player?.Cue(0);
        Tick();
    }

    public void RemoveFromQueue(QueueItemVm entry)
    {
        var index = Queue.IndexOf(entry);
        if (index < 0) return;
        Queue.RemoveAt(index);
        _player?.RemoveAt(index); // if it was playing, mpv moves on to the next one
        Tick();
    }

    /// <summary>
    /// Removes the whole album the entry belongs to (see <see cref="AlbumRunAt"/>). If it's the album playing,
    /// playback moves on to whatever follows it.
    /// </summary>
    public void RemoveAlbumFromQueue(QueueItemVm entry)
    {
        var index = Queue.IndexOf(entry);
        if (index < 0) return;
        var (first, last) = AlbumRunAt(index);
        var run = Queue.Skip(first).Take(last - first + 1).ToList();
        // The playing one goes last, so mpv moves on once, straight past the rest of the album.
        var playing = _player?.PlaylistPosition is int pos && pos >= first && pos <= last ? Queue[pos] : null;
        foreach (var e in run.Where(e => e != playing).Reverse().Append(playing).OfType<QueueItemVm>())
        {
            var i = Queue.IndexOf(e);
            Queue.RemoveAt(i);
            _player?.RemoveAt(i);
        }
        Tick();
    }

    /// <summary>Moves an entry so it ends up at <paramref name="newIndex"/>. Playback isn't interrupted.</summary>
    public void MoveQueueItem(int oldIndex, int newIndex)
    {
        if (oldIndex == newIndex || oldIndex < 0 || newIndex < 0 || oldIndex >= Queue.Count || newIndex >= Queue.Count) return;
        Queue.Move(oldIndex, newIndex);
        _player?.Move(oldIndex, newIndex);
    }

    public void ClearQueue()
    {
        Queue.Clear();
        _player?.Clear();
        _playlistAddress = null;
        SetCurrent(null);
        Tick();
    }

    /// <summary>Double-clicking a queue entry plays it; the queue carries on from there.</summary>
    public void PlayQueueItem(QueueItemVm entry)
    {
        if (_player is null)
        {
            Status = PlaybackUnavailable;
            return;
        }
        var index = Queue.IndexOf(entry);
        if (index < 0) return;
        _player.PlayIndex(index);
        Tick();
    }

    // ------------------------------------------------------------------ playback
    /// <summary>Replaces the queue with the album and plays it.</summary>
    public async Task PlayAlbumAsync(AlbumVm album, string? startTrackId = null)
    {
        if (_player is null)
        {
            Status = PlaybackUnavailable;
            return;
        }

        try
        {
            var tracks = await TracksOfAsync(album);
            if (tracks.Count == 0) return;

            var start = startTrackId is null ? 0 : Math.Max(0, tracks.FindIndex(t => t.Id == startTrackId));
            SetCurrent(null);
            Queue.Clear();
            foreach (var t in tracks) Queue.Add(NewEntry(t, album));
            _player.LoadPlaylist(tracks.Select(t => _jellyfin.StreamUrl(t.Id)).ToList(), start);
            _playlistAddress = _jellyfin.ServerUrl;
            Tick();
        }
        catch (Exception ex)
        {
            Fail(LogArea.Playback, $"Couldn't play {album.Title}: {ex.Message}");
        }
    }

    /// <summary>Double-clicking a track plays the album it's on, starting from that track.</summary>
    public Task PlayFromTrackAsync(TrackVm track)
    {
        var album = Albums.FirstOrDefault(a => a.Id == _tracksAlbumId);
        return album is null ? Task.CompletedTask : PlayAlbumAsync(album, track.Id);
    }

    private async Task TogglePauseAsync()
    {
        if (_player is null) return;
        if (Queue.Count == 0 || _player.IsIdle)
        {
            // Carry on with whatever in the queue hasn't been played yet; otherwise play the album you're looking at.
            if (Queue.FirstOrDefault(e => !e.IsPlayed) is { } next) PlayQueueItem(next);
            else if (SelectedAlbum != null) await PlayAlbumAsync(SelectedAlbum);
            else if (Queue.Count > 0) PlayQueueItem(Queue[0]);
            return;
        }
        _player.TogglePause();
        Tick();
    }

    private void Previous()
    {
        if (_player is null) return;
        // Like most players: if you're more than a few seconds in, "previous" restarts the track.
        if ((_player.TimePosition ?? 0) > 3) _player.Seek(0);
        else _player.Previous();
    }

    // The address the mpv playlist's URLs point at, and where playback last was (for re-pointing).
    private string? _playlistAddress;
    private int _lastIndex = -1;
    private double _lastTime, _lastDuration;
    private double? _pendingSeek;

    /// <summary>
    /// mpv's playlist holds full stream URLs, so when the server is reached at a new address (laptop left
    /// home) the queue is reloaded entry for entry with the new address, carrying on from the same track and
    /// time, paused if it was paused. If the old stream already failed and mpv gave up, it goes back to the
    /// track that was playing, unless that had finished.
    /// </summary>
    private void RepointPlaylistIfServerMoved()
    {
        if (_player is null || _playlistAddress is null || Queue.Count == 0 || _jellyfin.ServerUrl == _playlistAddress) return;
        _playlistAddress = _jellyfin.ServerUrl;

        var idle = _player.IsIdle;
        var index = !idle ? _player.PlaylistPosition
                  : _lastDuration - _lastTime > 2 ? _lastIndex : -1;
        if (index >= Queue.Count) index = -1;
        var time = index < 0 ? 0 : !idle ? _player.TimePosition ?? _lastTime : _lastTime;

        _player.LoadPlaylist(Queue.Select(e => _jellyfin.StreamUrl(e.Track.Id)).ToList(), index, paused: !idle && _player.IsPaused);
        Log(LogArea.Playback, $"Server now at {_playlistAddress}: queue reloaded from there");
        _pendingSeek = index >= 0 && time > 1 ? time : null;
    }

    /// <summary>mpv's audio filters, i.e. bass and treble (for tests and diagnostics).</summary>
    public string? AudioFilters => _player?.AudioFilters;

    /// <summary>The URL mpv has for a queue entry (for tests and diagnostics).</summary>
    public string? PlaylistUrl(int index) => _player?.PlaylistFile(index);

    private TrackVm? _currentTrack;
    public TrackVm? CurrentTrack { get => _currentTrack; private set => SetProperty(ref _currentTrack, value); }

    private object? _nowPlayingCover;
    public object? NowPlayingCover { get => _nowPlayingCover; private set => SetProperty(ref _nowPlayingCover, value); }

    private string _nowPlayingTitle = "Nothing playing";
    public string NowPlayingTitle { get => _nowPlayingTitle; private set => SetProperty(ref _nowPlayingTitle, value); }

    private string _nowPlayingSubtitle = "";
    public string NowPlayingSubtitle { get => _nowPlayingSubtitle; private set => SetProperty(ref _nowPlayingSubtitle, value); }

    private string _windowTitle;
    public string WindowTitle { get => _windowTitle; private set => SetProperty(ref _windowTitle, value); }

    private bool _isPaused = true;
    public bool IsPaused
    {
        get => _isPaused;
        private set { if (SetProperty(ref _isPaused, value)) OnPropertyChanged(nameof(PlayPauseGlyph)); }
    }

    public string PlayPauseGlyph => IsPaused ? "▶" : "⏸";

    private double _duration = 1;
    public double Duration { get => _duration; private set => SetProperty(ref _duration, value); }

    private double _position;
    /// <summary>Bound two-way to the seek slider. Only applied to the player when the drag ends.</summary>
    public double Position { get => _position; set => SetProperty(ref _position, value); }

    private string _positionText = "0:00";
    public string PositionText { get => _positionText; private set => SetProperty(ref _positionText, value); }

    private string _durationText = "0:00";
    public string DurationText { get => _durationText; private set => SetProperty(ref _durationText, value); }

    private double _volume;
    public double Volume
    {
        get => _volume;
        set
        {
            if (!SetProperty(ref _volume, Math.Clamp(value, 0, 100))) return;
            _player?.SetVolume(_volume);
            Settings.Volume = _volume;
        }
    }

    // Bass and treble: heard at once, saved with the volume. Doubles for the sliders, always whole dB.

    private int _bass;
    public double Bass { get => _bass; set => SetTone(ref _bass, value, nameof(Bass)); }

    private int _treble;
    public double Treble { get => _treble; set => SetTone(ref _treble, value, nameof(Treble)); }

    public string BassLabel => Tone.Label(_bass);
    public string TrebleLabel => Tone.Label(_treble);
    public bool IsToneFlat => _bass == 0 && _treble == 0;
    public string ToneTip => IsToneFlat ? "Bass and treble" : $"Bass {BassLabel}, treble {TrebleLabel}";

    public RelayCommand FlatToneCommand { get; }

    private void SetTone(ref int field, double value, string name)
    {
        var db = Tone.Clamp((int)Math.Round(value));
        if (field == db) return;
        field = db;
        _player?.SetTone(_bass, _treble);
        Settings.Bass = _bass;
        Settings.Treble = _treble;
        foreach (var p in new[] { name, name + "Label", nameof(IsToneFlat), nameof(ToneTip) }) OnPropertyChanged(p);
    }

    public void BeginSeek() => _seeking = true;

    public void EndSeek()
    {
        if (!_seeking) return;
        _seeking = false;
        if (_player != null && CurrentTrack != null) _player.Seek(Position);
    }

    /// <summary>Called by the UI a few times a second.</summary>
    public void Tick()
    {
        if (_networkChanged && !_reconnecting && IsLoggedIn && _jellyfin.Addresses.Count > 1)
        {
            _networkChanged = false;
            _ = ReconnectAsync();
        }
        if (_queueDirty) SaveQueue();
        if (_player is null) return;
        RepointPlaylistIfServerMoved();

        var pos = _player.PlaylistPosition;
        var entry = pos >= 0 && pos < Queue.Count ? Queue[pos] : null;
        if (entry != _currentEntry) SetCurrent(entry);

        if (NowPlayingCover is null && _currentEntry?.Album?.Cover is { } cover)
            NowPlayingCover = cover;

        IsPaused = _player.IsIdle || _player.IsPaused;

        if (_seeking) return;
        var duration = CurrentTrack is null ? null : _player.Duration;
        var time = CurrentTrack is null ? null : _player.TimePosition;
        if (_pendingSeek is { } resumeAt && duration is > 0)
        {
            _pendingSeek = null;
            _player.Seek(resumeAt);
        }
        if (entry != null && time is { } t && duration is { } d)
        {
            _lastIndex = pos;
            _lastTime = t;
            _lastDuration = d;
        }
        Duration = duration is > 0 ? duration.Value : 1;          // set max before value
        Position = Math.Clamp(time ?? 0, 0, Duration);
        PositionText = TrackVm.FormatTime(time ?? 0);
        DurationText = duration is > 0 ? TrackVm.FormatTime(duration) : "0:00";

        _reports.Update(entry?.Track.Id, time ?? 0, IsPaused); // Jellyfin's play history
    }

    private void SetCurrent(QueueItemVm? entry)
    {
        if (_currentEntry != null && _currentEntry != entry)
        {
            _currentEntry.IsCurrent = false;
            _currentEntry.IsPlayed = true; // played, or skipped past
        }
        _currentEntry = entry;
        if (entry != null)
        {
            entry.IsCurrent = true;
            entry.IsPlayed = false; // playing it again (e.g. via Previous)
        }

        var track = entry?.Track;
        CurrentTrack = track;
        foreach (var t in Tracks) t.IsNowPlaying = track != null && t.Id == track.Id;

        if (entry is null)
        {
            NowPlayingTitle = "Nothing playing";
            NowPlayingSubtitle = "";
            NowPlayingCover = null;
            WindowTitle = AppName;
        }
        else
        {
            NowPlayingTitle = entry.Title;
            NowPlayingSubtitle = entry.Subtitle;
            NowPlayingCover = entry.Album?.Cover;
            WindowTitle = $"{entry.Title} — {entry.Artist} · {AppName}";
        }
    }

    // ------------------------------------------------------------------ persistence
    public void SaveSettings()
    {
        try { _store.Save(Settings); }
        catch (Exception ex) { Fail(LogArea.App, $"Couldn't save settings: {ex.Message}"); }
    }

    // ------------------------------------------------------------------ the app's log
    /// <summary>Adds to the app's log. Never fails: a log mustn't break the app.</summary>
    private void Log(LogArea area, string message)
    {
        try { _log?.Invoke(area, AppLog.Clean(message)); }
        catch { /* nowhere to say so */ }
    }

    /// <summary>Shows a problem in the status bar and logs it.</summary>
    private void Fail(LogArea area, string message)
    {
        Status = message;
        Log(area, message);
    }

    /// <summary>The app is going down: the error goes in the log, and the next start says so.</summary>
    public void NoteCrash(Exception error)
    {
        Log(LogArea.Crash, AppLog.Crash(error));
        Settings.CrashToShare = true;
        try { _store.Save(Settings); } catch { /* going down anyway */ }
    }

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return; // tests dispose a view model to restart it, then again when they finish
        _disposed = true;
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        _albumsCts?.Cancel();
        _tracksCts?.Cancel();
        _viewCts?.Cancel();
        _reports.CloseAsync().Wait(TimeSpan.FromSeconds(2)); // the last stop report, if the server answers quickly
        SaveQueue();
        SaveSettings();
        _player?.Dispose();
    }
}
