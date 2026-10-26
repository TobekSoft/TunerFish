using System.Net;
using System.Text;
using System.Text.Json;
using TunerFish.Models;

namespace TunerFish.Services;

/// <summary>
/// Thin client for the handful of Jellyfin endpoints TunerFish needs.
/// The server does all the scanning and tag reading; we just ask it questions.
/// <para>
/// One server can have several addresses (say the home network and a NetBird address for a laptop
/// that's out). They're tried in order and the first that answers as the same server is used; the
/// token and user are the same whichever address is in use.
/// </para>
/// </summary>
public sealed class JellyfinClient
{
    private const string ClientVersion = "0.1.0";

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly string _deviceId;
    private readonly string _clientName;
    private readonly TimeSpan _probeTimeout;

    /// <summary>The server's addresses, most preferred first.</summary>
    public IReadOnlyList<string> Addresses { get; private set; } = Array.Empty<string>();
    /// <summary>The address in use now: one of <see cref="Addresses"/>.</summary>
    public string ServerUrl { get; private set; } = "";
    /// <summary>The server's own id, so a different server answering at one of the addresses is never sent the token.</summary>
    public string? ServerId { get; private set; }
    public string? Token { get; private set; }
    public string? UserId { get; private set; }

    /// <param name="clientName">How this app is listed under the server's devices and sessions.</param>
    /// <param name="probeTimeout">How long to wait for an address to answer before trying the next.</param>
    public JellyfinClient(string deviceId, HttpClient? http = null, string clientName = "TunerFish", TimeSpan? probeTimeout = null)
    {
        _deviceId = deviceId;
        _clientName = clientName;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _probeTimeout = probeTimeout ?? TimeSpan.FromSeconds(4);
    }

    public void Configure(IEnumerable<string> addresses, string? token, string? userId, string? serverId)
    {
        Addresses = addresses.Select(a => a.Trim().TrimEnd('/')).Where(a => a.Length > 0).Distinct().ToList();
        // Keep the address in use if it's still listed, so editing the list doesn't switch servers needlessly.
        if (!Addresses.Contains(ServerUrl)) ServerUrl = Addresses.FirstOrDefault() ?? "";
        Token = token;
        UserId = userId;
        ServerId = serverId;
    }

    /// <summary>For sign-ins saved before the server id was recorded.</summary>
    public void RememberServerId(string id) => ServerId = id;

    /// <summary>The id of the Jellyfin server answering at <paramref name="address"/>, or null if nothing answers there.</summary>
    public async Task<string?> ServerIdAtAsync(string address, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_probeTimeout);
        try
        {
            using var resp = await _http.GetAsync(address.TrimEnd('/') + "/System/Info/Public", cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            await using var s = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            var info = await JsonSerializer.DeserializeAsync<PublicSystemInfo>(s, Json, cts.Token).ConfigureAwait(false);
            return string.IsNullOrEmpty(info?.Id) ? null : info.Id;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null; // refused, timed out, not Jellyfin: not reachable
        }
    }

    /// <summary>Whether this server answers at the address (and is the same server, if its id is known).</summary>
    public async Task<bool> IsReachableAsync(string address, CancellationToken ct = default) =>
        await ServerIdAtAsync(address, ct).ConfigureAwait(false) is { } id && (ServerId is null || id == ServerId);

    /// <summary>
    /// Checks every address at once and switches to the first in order that answers.
    /// Returns false (and stays put) if none does.
    /// </summary>
    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        var addresses = Addresses;
        var checks = addresses.Select(a => IsReachableAsync(a, ct)).ToList();
        for (var i = 0; i < checks.Count; i++)
        {
            if (!await checks[i].ConfigureAwait(false)) continue;
            if (addresses[i] != ServerUrl) OnConnect($"Using {addresses[i]}");
            ServerUrl = addresses[i];
            return true;
        }
        OnConnect($"No address answers ({string.Join(", ", addresses)})");
        return false;
    }

    /// <summary>Told when <see cref="ConnectAsync"/> changes address or finds none answering, for the app's log.</summary>
    public Action<string> OnConnect { get; set; } = _ => { };

    private string AuthHeader
    {
        get
        {
            var h = $"MediaBrowser Client=\"{_clientName}\", Device=\"{Environment.MachineName}\", " +
                    $"DeviceId=\"{_deviceId}\", Version=\"{ClientVersion}\"";
            return Token is null ? h : h + $", Token=\"{Token}\"";
        }
    }

    private HttpRequestMessage Request(HttpMethod method, string pathOrUrl)
    {
        var url = pathOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? pathOrUrl : ServerUrl + pathOrUrl;
        var req = new HttpRequestMessage(method, url);
        req.Headers.TryAddWithoutValidation("Authorization", AuthHeader);
        return req;
    }

    /// <summary>Signs in at one address. The caller decides which other addresses to keep (see <see cref="Configure"/>).</summary>
    public async Task<AuthResult> LoginAsync(string serverUrl, string userName, string password, CancellationToken ct)
    {
        Configure(new[] { serverUrl }, null, null, null);
        using var req = Request(HttpMethod.Post, "/Users/AuthenticateByName");
        var body = JsonSerializer.Serialize(new Dictionary<string, string> { ["Username"] = userName, ["Pw"] = password });
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var auth = await JsonSerializer.DeserializeAsync<AuthResult>(s, Json, ct).ConfigureAwait(false)
                   ?? throw new InvalidOperationException("Empty login response from server.");
        Configure(new[] { serverUrl }, auth.AccessToken, auth.User.Id, auth.ServerId);
        return auth;
    }

    public async Task LogoutAsync()
    {
        try
        {
            using var req = Request(HttpMethod.Post, "/Sessions/Logout");
            using var resp = await _http.SendAsync(req).ConfigureAwait(false);
        }
        catch
        {
            // Best effort; the local token is forgotten either way.
        }
    }

    /// <summary>A GET that, if the server can't be reached, looks for another address and tries once more.</summary>
    private Task<T> GetAsync<T>(string pathAndQuery, CancellationToken ct) =>
        WithRetryAsync(() => GetOnceAsync<T>(pathAndQuery, ct), ct);

    /// <summary>Runs <paramref name="call"/>; if the server can't be reached, looks for another address and runs it once more.</summary>
    private async Task<T> WithRetryAsync<T>(Func<Task<T>> call, CancellationToken ct)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (Exception ex) when (Addresses.Count > 1 && !ct.IsCancellationRequested &&
                                   ex is TaskCanceledException or HttpRequestException { StatusCode: null })
        {
            var before = ServerUrl;
            if (!await ConnectAsync(ct).ConfigureAwait(false) || ServerUrl == before) throw;
            return await call().ConfigureAwait(false);
        }
    }

    private async Task<T> GetOnceAsync<T>(string pathAndQuery, CancellationToken ct)
    {
        using var req = Request(HttpMethod.Get, pathAndQuery);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(s, Json, ct).ConfigureAwait(false)
               ?? throw new InvalidOperationException($"Empty response from {pathAndQuery}");
    }

    /// <summary>Every album artist in the library, sorted by Jellyfin's sort name, with the user's marks.</summary>
    public Task<ItemsResult> GetAlbumArtistsAsync(CancellationToken ct) =>
        GetAsync<ItemsResult>($"/Artists/AlbumArtists?userId={UserId}&sortBy=SortName&sortOrder=Ascending&fields=SortName&enableUserData=true", ct);

    /// <summary>Albums whose album artist is the given artist.</summary>
    public Task<ItemsResult> GetAlbumsAsync(string albumArtistId, CancellationToken ct) =>
        GetAsync<ItemsResult>($"/Items?userId={UserId}&includeItemTypes=MusicAlbum&recursive=true" +
                              $"&albumArtistIds={albumArtistId}&sortBy=SortName&fields=SortName&enableUserData=true", ct);

    /// <summary>Every album in the library with the user's marks, or only those passing a Jellyfin <paramref name="filter"/> (IsFavorite, Likes).</summary>
    public Task<ItemsResult> GetAllAlbumsAsync(string? filter, CancellationToken ct) =>
        GetAsync<ItemsResult>($"/Items?userId={UserId}&includeItemTypes=MusicAlbum&recursive=true&sortBy=SortName&fields=SortName" +
                              "&enableUserData=true&enableImageTypes=Primary&imageTypeLimit=1" + (filter is null ? "" : $"&filters={filter}"), ct);

    /// <summary>
    /// Jellyfin's own search: items of <paramref name="type"/> (MusicAlbum or Audio) whose title contains
    /// <paramref name="term"/>, with their album artists, at most <paramref name="limit"/> of them.
    /// </summary>
    public Task<ItemsResult> SearchAsync(string term, string type, int limit, CancellationToken ct) =>
        GetAsync<ItemsResult>($"/Items?userId={UserId}&searchTerm={Uri.EscapeDataString(term.Trim())}" +
                              $"&includeItemTypes={type}&recursive=true&limit={limit}&enableUserData=true&enableImages=false", ct);

    /// <summary>Albums by id, with their track counts, in no particular order.</summary>
    public async Task<List<BaseItem>> GetAlbumsByIdAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        var found = new List<BaseItem>();
        foreach (var chunk in ids.Chunk(50))
            found.AddRange((await GetAsync<ItemsResult>($"/Items?userId={UserId}&ids={string.Join(",", chunk)}" +
                                                        "&fields=SortName,ChildCount&enableUserData=true", ct).ConfigureAwait(false)).Items);
        return found;
    }

    /// <summary>The user's played tracks, most recent (<paramref name="sortBy"/> DatePlayed) or most played (PlayCount) first.</summary>
    public Task<ItemsResult> GetPlayedTracksAsync(string sortBy, int limit, CancellationToken ct) =>
        GetAsync<ItemsResult>($"/Items?userId={UserId}&includeItemTypes=Audio&recursive=true&filters=IsPlayed&sortBy={sortBy}" +
                              $"&sortOrder=Descending&limit={limit}&enableUserData=true&enableImages=false", ct);

    /// <summary>Tracks on an album, in disc / track order, with the user's play counts.</summary>
    public Task<ItemsResult> GetTracksAsync(string albumId, CancellationToken ct) =>
        GetAsync<ItemsResult>($"/Items?userId={UserId}&parentId={albumId}&includeItemTypes=Audio&recursive=true" +
                              "&sortBy=ParentIndexNumber,IndexNumber,SortName&fields=Genres,MediaSources,Path,SortName&enableUserData=true", ct);

    // Marks: the user's own heart and thumbs on an artist or album (see Marks). User data, never the library.

    /// <summary>Sets or clears the heart.</summary>
    public Task SetFavouriteAsync(string itemId, bool on, CancellationToken ct = default) =>
        UserCallAsync(on ? HttpMethod.Post : HttpMethod.Delete, $"/UserFavoriteItems/{itemId}?userId={UserId}", ct);

    /// <summary>Thumbs up (true), thumbs down (false), or neither (null).</summary>
    public Task SetLikesAsync(string itemId, bool? likes, CancellationToken ct = default) =>
        likes is { } l
            ? UserCallAsync(HttpMethod.Post, $"/UserItems/{itemId}/Rating?userId={UserId}&likes={(l ? "true" : "false")}", ct)
            : UserCallAsync(HttpMethod.Delete, $"/UserItems/{itemId}/Rating?userId={UserId}", ct);

    private Task UserCallAsync(HttpMethod method, string pathAndQuery, CancellationToken ct) =>
        WithRetryAsync(async () =>
        {
            using var req = Request(method, pathAndQuery);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            return true;
        }, ct);

    // Playback reports: Jellyfin keeps the user's play count, last played and played flag from these.
    // mpv plays the original file (static stream), so the method is DirectPlay.

    /// <summary>A track started playing. <paramref name="playSessionId"/> ties its reports together.</summary>
    public Task ReportStartAsync(string itemId, double positionSeconds, bool paused, string playSessionId, CancellationToken ct = default) =>
        PostJsonAsync("/Sessions/Playing", PlayState(itemId, positionSeconds, paused, playSessionId), ct);

    /// <summary>Where playback is now, and whether it's paused: sent on pause, resume, seek and now and then.</summary>
    public Task ReportProgressAsync(string itemId, double positionSeconds, bool paused, string playSessionId, CancellationToken ct = default) =>
        PostJsonAsync("/Sessions/Playing/Progress", PlayState(itemId, positionSeconds, paused, playSessionId), ct);

    /// <summary>The track stopped or playback moved on. The server decides whether it counts as played.</summary>
    public Task ReportStoppedAsync(string itemId, double positionSeconds, string playSessionId, CancellationToken ct = default) =>
        PostJsonAsync("/Sessions/Playing/Stopped", new Dictionary<string, object>
        {
            ["ItemId"] = itemId,
            ["MediaSourceId"] = itemId,
            ["PositionTicks"] = Ticks(positionSeconds),
            ["PlaySessionId"] = playSessionId,
        }, ct);

    private static Dictionary<string, object> PlayState(string itemId, double positionSeconds, bool paused, string playSessionId) => new()
    {
        ["ItemId"] = itemId,
        ["MediaSourceId"] = itemId,
        ["PositionTicks"] = Ticks(positionSeconds),
        ["IsPaused"] = paused,
        ["CanSeek"] = true,
        ["PlayMethod"] = "DirectPlay",
        ["PlaySessionId"] = playSessionId,
    };

    /// <summary>Jellyfin positions are in ticks of 100 ns.</summary>
    private static long Ticks(double seconds) => (long)Math.Round(seconds * TimeSpan.TicksPerSecond);

    private Task PostJsonAsync(string path, Dictionary<string, object> body, CancellationToken ct) =>
        WithRetryAsync(async () =>
        {
            using var req = Request(HttpMethod.Post, path);
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            return true;
        }, ct);

    /// <summary>Direct stream of the original file. mpv decodes whatever format it is.</summary>
    public string StreamUrl(string itemId) =>
        $"{ServerUrl}/Audio/{itemId}/stream?static=true&api_key={Uri.EscapeDataString(Token ?? "")}";

    public string ImageUrl(string itemId, string? tag, int maxHeight) =>
        $"{ServerUrl}/Items/{itemId}/Images/Primary?maxHeight={maxHeight}&quality=90" +
        (tag is null ? "" : $"&tag={Uri.EscapeDataString(tag)}");

    /// <summary>Downloads raw bytes; returns null on 404.</summary>
    public async Task<byte[]?> GetBytesAsync(string url, CancellationToken ct)
    {
        using var req = Request(HttpMethod.Get, url);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }
}
