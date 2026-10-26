using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace TunerFish.Tests.Support;

/// <summary>
/// A tiny stand-in for a Jellyfin server: canned JSON, generated WAV tones and a cover PNG.
/// Checks the session token like the real thing. Listens on a free port picked at startup.
/// Used as a class or collection fixture; it holds no per-test state, so sharing is safe.
/// </summary>
public sealed class FakeJellyfinServer : IDisposable
{
    public const string UserName = "me";
    public const string Password = "pw";
    public const string Token = "tok123";
    public const string UserId = "user1";
    /// <summary>The server's own id, the same whichever address it's reached at.</summary>
    public const string ServerId = "srv1";

    /// <summary>How many album artists the server has.</summary>
    public const int ArtistCount = 8;

    private readonly HttpListener _listener;
    private readonly ConcurrentQueue<string> _requests = new();
    private readonly ConcurrentQueue<(string Path, string Body)> _reports = new();
    private readonly string _serverId;

    // The user's own data (marks and plays), as Jellyfin keeps it. Tests in the shared collection run one at a
    // time and VmHarness clears it before each, so one test's marks never leak into another.
    private readonly ConcurrentDictionary<string, (bool Favourite, bool? Likes)> _marks = new();
    private readonly ConcurrentDictionary<string, (int Plays, DateTime? LastPlayed)> _plays = new();

    /// <summary>When set, every mark change (favourite, rating) fails with a server error.</summary>
    public bool FailMarkChanges { get; set; }

    public void ResetUserData()
    {
        _marks.Clear();
        _plays.Clear();
        FailMarkChanges = false;
    }

    public void SetMarks(string itemId, bool favourite = false, bool? likes = null) => _marks[itemId] = (favourite, likes);
    public (bool Favourite, bool? Likes) MarksOf(string itemId) => _marks.GetValueOrDefault(itemId);

    /// <summary>Records plays of a track ("t1".."t3" are Revolver's; "&lt;albumId&gt;-t1" another album's).</summary>
    public void SetPlays(string trackId, int plays, DateTime? lastPlayed) => _plays[trackId] = (plays, lastPlayed);

    private object UserData(string itemId)
    {
        var m = _marks.GetValueOrDefault(itemId);
        var p = _plays.GetValueOrDefault(itemId);
        return new { IsFavorite = m.Favourite, m.Likes, PlayCount = p.Plays, Played = p.Plays > 0, LastPlayedDate = p.LastPlayed };
    }

    private sealed record FakeAlbum(string Id, string Name, string? SortName, int? Year, string? ImageTag = null);

    private static readonly (string Id, string Name, string SortName)[] AlbumArtists =
    {
        ("ar1", "The Beatles", "beatles"),
        ("ar2", "Now That's What I Call Music 5", "now that's what i call music 5"),
        ("ar3", "The The", "the"),
        ("ar4", "Elvis Costello", "elvis costello"),
        ("ar5", "ABBA", "abba"),
        ("ar6", "Björk", "björk"),
        ("ar7", "10cc", "10cc"),
        ("ar8", "+++ (Crosses)", "+++ (crosses)"),
    };

    private static readonly Dictionary<string, FakeAlbum[]> AlbumsByArtist = new()
    {
        ["ar1"] = new FakeAlbum[]
        {
            new("al1", "Abbey Road", "abbey road", 1969),
            new("al2", "Revolver", "revolver", 1966, "tagA"),
            new("al3", "Unknown Year Demos", "unknown year demos", null),
        },
        // Sort names as Jellyfin makes them (articles dropped); My Aim Is True has none.
        ["ar4"] = new FakeAlbum[]
        {
            new("ec1", "This Year's Model", "this year's model", 1978),
            new("ec2", "My Aim Is True", null, 1977),
            new("ec3", "The Juliet Letters", "juliet letters", 1993),
            new("ec4", "A Case for Song", "case for song", 1990),
        },
        ["ar2"] = new FakeAlbum[] { new("al9", "Now That's What I Call Music 5", null, 1985) },
    };

    private object AlbumJson(string artistId, FakeAlbum a, bool withChildCount = false)
    {
        var artist = AlbumArtists.First(x => x.Id == artistId);
        return new
        {
            a.Id, a.Name, a.SortName, ProductionYear = a.Year,
            ImageTags = a.ImageTag is null ? null : new Dictionary<string, string> { ["Primary"] = a.ImageTag },
            AlbumArtist = artist.Name, AlbumArtists = new[] { new { artist.Id, artist.Name } },
            ChildCount = withChildCount ? 3 : (int?)null,
            UserData = UserData(a.Id),
        };
    }

    private IEnumerable<(string ArtistId, FakeAlbum Album)> EveryAlbum() =>
        AlbumsByArtist.SelectMany(kv => kv.Value.Select(a => (kv.Key, a)));

    private static string AlbumOfTrack(string trackId) => trackId.Contains('-') ? trackId.Split('-')[0] : "al2";

    public int Port { get; }

    /// <summary>Base URL, already normalised the way the app stores it (no trailing slash).</summary>
    public string Url => $"http://127.0.0.1:{Port}";

    public FakeJellyfinServer() : this(ServerId) { }

    /// <summary>Another server: the same server at a second address (same id), or a stranger (different id).</summary>
    public static FakeJellyfinServer WithId(string serverId) => new(serverId);

    private FakeJellyfinServer(string serverId)
    {
        _serverId = serverId;
        // HttpListener can't bind port 0, so ask the OS for a free port and retry if we lose the race.
        for (var attempt = 1; ; attempt++)
        {
            var port = FreePort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                _listener = listener;
                Port = port;
                break;
            }
            catch (HttpListenerException) when (attempt < 10)
            {
                listener.Close();
            }
        }

        _ = Task.Run(AcceptLoop);
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try { return ((IPEndPoint)probe.LocalEndpoint).Port; }
        finally { probe.Stop(); }
    }

    /// <summary>An address where nothing answers (a free port nobody is listening on).</summary>
    public static string DeadUrl() => $"http://127.0.0.1:{FreePort()}";

    /// <summary>
    /// Playback reports received (/Sessions/Playing...), path and JSON body, oldest first. A shared
    /// fixture collects every test's: look for your own items or sessions.
    /// </summary>
    public IReadOnlyList<(string Path, string Body)> Reports => _reports.ToArray();

    /// <summary>Every search text the server was asked for, oldest first (shared fixture: every test's).</summary>
    public ConcurrentQueue<string> SearchTerms { get; } = new();

    /// <summary>How many requests have been made for this path (query string ignored).</summary>
    public int RequestCount(string path) => _requests.Count(p => p == path);

    private async Task AcceptLoop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { break; }
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url!.AbsolutePath;
            _requests.Enqueue(path);
            var (status, contentType, body) = await RespondAsync(ctx.Request, path);
            ctx.Response.StatusCode = status;
            if (body != null)
            {
                ctx.Response.ContentType = contentType;
                ctx.Response.ContentLength64 = body.Length;
                await ctx.Response.OutputStream.WriteAsync(body);
            }
            ctx.Response.Close();
        }
        catch
        {
            // Client went away mid-response (mpv does this when it stops a stream). Not our problem.
            try { ctx.Response.Abort(); } catch { /* ignore */ }
        }
    }

    private async Task<(int Status, string ContentType, byte[]? Body)> RespondAsync(HttpListenerRequest req, string path)
    {
        var q = req.QueryString;
        var authed = (req.Headers["Authorization"] ?? "").Contains($"Token=\"{Token}\"");

        if (path == "/Users/AuthenticateByName")
        {
            var d = JsonSerializer.Deserialize<Dictionary<string, string>>(await new StreamReader(req.InputStream).ReadToEndAsync())!;
            return d["Username"] == UserName && d["Pw"] == Password
                ? Json(new { AccessToken = Token, User = new { Id = UserId, Name = UserName }, ServerId = _serverId })
                : (401, "", null);
        }
        if (path == "/Sessions/Logout") return (204, "", null);
        if (path == "/System/Info/Public") return Json(new { Id = _serverId, ServerName = "Fake" });

        if (path.StartsWith("/Audio/") && path.EndsWith("/stream"))
        {
            // Track ids are "t1".."t3", or "<albumId>-t1" for albums other than al2.
            var id = path.Split('/')[2];
            id = id.Contains('-') ? id.Split('-')[1] : id;
            return q["api_key"] != Token ? (401, "", null) : (200, "audio/wav", TestMedia.Tone(id));
        }
        if (path == "/Items/al2/Images/Primary") return (200, "image/png", TestMedia.CoverPng);
        if (path.Contains("/Images/")) return (404, "", null);

        if (!authed) return (401, "", null);

        if (path.StartsWith("/UserFavoriteItems/") || (path.StartsWith("/UserItems/") && path.EndsWith("/Rating")))
        {
            if (q["userId"] != UserId) return (400, "", null);
            if (FailMarkChanges) return (500, "", null);
            var id = path.Split('/')[2];
            var m = _marks.GetValueOrDefault(id);
            var delete = req.HttpMethod == "DELETE";
            _marks[id] = path.StartsWith("/UserFavoriteItems/")
                ? (!delete, m.Likes)
                : (m.Favourite, delete ? null : q["likes"] == "true");
            return Json(UserData(id));
        }

        if (path.StartsWith("/Sessions/Playing"))
        {
            _reports.Enqueue((path, await new StreamReader(req.InputStream).ReadToEndAsync()));
            return (204, "", null);
        }

        if (path == "/Artists/AlbumArtists")
            return Json(new { Items = AlbumArtists.Select(a => new { a.Id, a.Name, a.SortName, UserData = UserData(a.Id) }) });

        if (path == "/Items" && q["ids"] is { } ids)
        {
            var wanted = ids.Split(',').ToHashSet();
            return Json(new { Items = EveryAlbum().Where(x => wanted.Contains(x.Album.Id)).Select(x => AlbumJson(x.ArtistId, x.Album, withChildCount: true)) });
        }

        // Jellyfin's search: titles containing the text. Every album's tracks are "Song 1".."Song 3".
        if (path == "/Items" && q["searchTerm"] is { } term)
        {
            SearchTerms.Enqueue(term);
            bool Has(string title) => title.Contains(term, StringComparison.OrdinalIgnoreCase);
            if (q["includeItemTypes"] == "MusicAlbum")
                return Json(new { Items = EveryAlbum().Where(x => Has(x.Album.Name)).Select(x => AlbumJson(x.ArtistId, x.Album)) });
            var artistOf = AlbumArtists.ToDictionary(a => a.Id);
            return Json(new { Items = EveryAlbum().SelectMany(x => Enumerable.Range(1, 3).Select(n => (x.ArtistId, x.Album, Id: (x.Album.Id == "al2" ? "" : x.Album.Id + "-") + $"t{n}", Name: $"Song {n}")))
                .Where(t => Has(t.Name))
                .Select(t => new { t.Id, t.Name, Album = t.Album.Name, AlbumId = t.Album.Id,
                                   AlbumArtists = new[] { new { artistOf[t.ArtistId].Id, artistOf[t.ArtistId].Name } } }) });
        }

        if (path == "/Items" && q["includeItemTypes"] == "MusicAlbum")
        {
            if (q["albumArtistIds"] is { } artistId)
                return Json(new { Items = AlbumsByArtist.GetValueOrDefault(artistId, Array.Empty<FakeAlbum>()).Select(a => AlbumJson(artistId, a)) });
            // The whole library, or what passes the filter. Like Jellyfin, Likes means thumbs up only.
            var all = EveryAlbum().Where(x => q["filters"] switch
            {
                "IsFavorite" => _marks.GetValueOrDefault(x.Album.Id).Favourite,
                "Likes" => _marks.GetValueOrDefault(x.Album.Id).Likes == true,
                _ => true,
            });
            return Json(new { Items = all.Select(x => AlbumJson(x.ArtistId, x.Album)) });
        }

        if (path == "/Items" && q["includeItemTypes"] == "Audio" && q["filters"] == "IsPlayed")
        {
            var played = _plays.Where(p => p.Value.Plays > 0);
            played = q["sortBy"] == "PlayCount" ? played.OrderByDescending(p => p.Value.Plays) : played.OrderByDescending(p => p.Value.LastPlayed);
            return Json(new { Items = played.Select(p => new { Id = p.Key, Name = p.Key, AlbumId = AlbumOfTrack(p.Key), UserData = UserData(p.Key) }) });
        }

        if (path == "/Items" && q["includeItemTypes"] == "Audio")
            return Json(new { Items = new[] { "t1", "t2", "t3" }.Select((id, i) => (Id: (q["parentId"] == "al2" ? "" : q["parentId"] + "-") + id, Short: id, Index: i)).Select(t => new {
                t.Id, Name = $"Song {t.Index + 1}", IndexNumber = t.Index + 1, ParentIndexNumber = 1, AlbumId = q["parentId"],
                RunTimeTicks = 30_000_000L, Artists = new[] { "The Beatles" }, Path = $"/music/{t.Short}.flac",
                MediaSources = new[] { new { Container = "flac", Bitrate = 900000, MediaStreams = new[] { new { Type = "Audio", SampleRate = 96000, BitDepth = 24 } } } },
                UserData = UserData(t.Id) }).ToArray() });

        return (404, "", null);
    }

    private static (int, string, byte[]?) Json(object value) =>
        (200, "application/json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)));

    public void Dispose()
    {
        try { _listener.Stop(); } catch { /* ignore */ }
        _listener.Close();
    }
}
