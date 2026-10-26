using System.Net;
using TunerFish.Services;
using TunerFish.Tests.Support;

namespace TunerFish.Tests;

public sealed class JellyfinClientTests : IClassFixture<FakeJellyfinServer>
{
    private readonly FakeJellyfinServer _server;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public JellyfinClientTests(FakeJellyfinServer server) => _server = server;

    private async Task<JellyfinClient> SignedInAsync()
    {
        var client = new JellyfinClient("test-device");
        await client.LoginAsync(_server.Url, FakeJellyfinServer.UserName, FakeJellyfinServer.Password, Ct);
        return client;
    }

    [Fact]
    public async Task Login_StoresTokenAndUser()
    {
        var client = new JellyfinClient("test-device");

        var auth = await client.LoginAsync(_server.Url + "/", FakeJellyfinServer.UserName, FakeJellyfinServer.Password, Ct);

        Assert.Equal(FakeJellyfinServer.Token, auth.AccessToken);
        Assert.Equal(FakeJellyfinServer.Token, client.Token);
        Assert.Equal(FakeJellyfinServer.UserId, client.UserId);
        Assert.Equal(_server.Url, client.ServerUrl);
    }

    [Fact]
    public async Task AuthHeader_NamesTheClient()
    {
        var spy = new HeaderSpy();
        using var http = new HttpClient(spy);
        var client = new JellyfinClient("test-device", http, clientName: "Renamed App");

        await client.LoginAsync(_server.Url, FakeJellyfinServer.UserName, FakeJellyfinServer.Password, Ct);

        Assert.StartsWith("MediaBrowser Client=\"Renamed App\", ", spy.LastAuthorization);
    }

    private sealed class HeaderSpy() : DelegatingHandler(new HttpClientHandler())
    {
        public string? LastAuthorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastAuthorization = request.Headers.TryGetValues("Authorization", out var v) ? string.Join(",", v) : null;
            return base.SendAsync(request, ct);
        }
    }

    [Fact]
    public async Task Search_AsksJellyfinForOneTypeOfItem_WithTheTextEncoded()
    {
        var client = await SignedInAsync();

        var albums = await client.SearchAsync(" abbey & co ", "MusicAlbum", Search.Limit, Ct);
        var road = await client.SearchAsync("abbey road", "MusicAlbum", Search.Limit, Ct);

        Assert.Empty(albums.Items);
        Assert.Contains("abbey & co", _server.SearchTerms); // trimmed, and the & reached the server as text
        Assert.Equal("ar1", road.Items.Single().AlbumArtists!.Single().Id);
    }

    [Fact]
    public async Task Login_WrongPassword_Throws401()
    {
        var client = new JellyfinClient("test-device");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.LoginAsync(_server.Url, FakeJellyfinServer.UserName, "nope", Ct));

        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.Null(client.Token);
    }

    [Fact]
    public async Task Requests_WithStaleToken_Throw401()
    {
        var client = new JellyfinClient("test-device");
        client.Configure([_server.Url], "stale", FakeJellyfinServer.UserId, FakeJellyfinServer.ServerId);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAlbumArtistsAsync(Ct));

        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
    }

    [Fact]
    public async Task Browse_ArtistsAlbumsAndTracks()
    {
        var client = await SignedInAsync();

        var artists = await client.GetAlbumArtistsAsync(Ct);
        var albums = await client.GetAlbumsAsync("ar1", Ct);
        var tracks = await client.GetTracksAsync("al2", Ct);

        Assert.Equal(FakeJellyfinServer.ArtistCount, artists.Items.Count);
        Assert.Equal(["Abbey Road", "Revolver", "Unknown Year Demos"], albums.Items.Select(a => a.Name));
        Assert.Equal("tagA", albums.Items[1].ImageTags!["Primary"]);
        Assert.Null(albums.Items[2].ProductionYear);
        Assert.Equal(["t1", "t2", "t3"], tracks.Items.Select(t => t.Id));
        Assert.Equal(96000, tracks.Items[0].MediaSources![0].MediaStreams![0].SampleRate);
    }

    [Fact]
    public async Task StreamUrl_CarriesTokenAndServesAudio()
    {
        var client = await SignedInAsync();

        var url = client.StreamUrl("t1");
        var bytes = await client.GetBytesAsync(url, Ct);

        Assert.Equal($"{_server.Url}/Audio/t1/stream?static=true&api_key={FakeJellyfinServer.Token}", url);
        Assert.NotNull(bytes);
        Assert.Equal("RIFF"u8.ToArray(), bytes[..4]);
    }

    [Fact]
    public async Task GetBytes_ReturnsNullOn404()
    {
        var client = await SignedInAsync();

        Assert.Null(await client.GetBytesAsync(client.ImageUrl("al1", "none", 300), Ct));
    }

    [Fact]
    public void ImageUrl_IncludesSizeAndEscapedTag()
    {
        var client = new JellyfinClient("test-device");
        client.Configure(["http://server:8096/"], null, null, null);

        Assert.Equal("http://server:8096/Items/al2/Images/Primary?maxHeight=300&quality=90&tag=a%2Fb",
                     client.ImageUrl("al2", "a/b", 300));
        Assert.Equal("http://server:8096/Items/al2/Images/Primary?maxHeight=300&quality=90",
                     client.ImageUrl("al2", null, 300));
    }

    // ---- One server, several addresses (home network, NetBird, ...) ----

    private static JellyfinClient Configured(params string[] addresses)
    {
        var client = new JellyfinClient("test-device", probeTimeout: TimeSpan.FromSeconds(2));
        client.Configure(addresses, FakeJellyfinServer.Token, FakeJellyfinServer.UserId, FakeJellyfinServer.ServerId);
        return client;
    }

    [Fact]
    public async Task Login_RecordsTheServerId()
    {
        var client = await SignedInAsync();

        Assert.Equal(FakeJellyfinServer.ServerId, client.ServerId);
        Assert.Equal(new[] { _server.Url }, client.Addresses);
    }

    [Fact]
    public async Task Connect_PrefersTheFirstAddressThatAnswers()
    {
        using var away = FakeJellyfinServer.WithId(FakeJellyfinServer.ServerId);
        var client = Configured(_server.Url, away.Url);

        Assert.True(await client.ConnectAsync(Ct));
        Assert.Equal(_server.Url, client.ServerUrl);
    }

    [Fact]
    public async Task Connect_FallsBackWhenTheFirstAddressIsDown()
    {
        var client = Configured(FakeJellyfinServer.DeadUrl(), _server.Url);

        Assert.True(await client.ConnectAsync(Ct));
        Assert.Equal(_server.Url, client.ServerUrl);
    }

    [Fact]
    public async Task Connect_SkipsADifferentServer_SoItNeverGetsTheToken()
    {
        using var stranger = FakeJellyfinServer.WithId("someone-else");
        var client = Configured(stranger.Url, _server.Url);

        Assert.True(await client.ConnectAsync(Ct));
        Assert.Equal(_server.Url, client.ServerUrl);
        Assert.False(await client.IsReachableAsync(stranger.Url, Ct));
        Assert.Equal(0, stranger.RequestCount("/Artists/AlbumArtists"));
    }

    [Fact]
    public async Task Connect_SaysWhenItChangesAddress_OrFindsNone()
    {
        var home = FakeJellyfinServer.DeadUrl();
        var client = Configured(home, _server.Url);
        var said = new List<string>();
        client.OnConnect = said.Add;

        await client.ConnectAsync(Ct);
        await client.ConnectAsync(Ct); // same address again: nothing to say
        Assert.Equal(new[] { $"Using {_server.Url}" }, said);

        client.Configure([home], client.Token, client.UserId, client.ServerId);
        Assert.False(await client.ConnectAsync(Ct));
        Assert.StartsWith("No address answers", said[^1]);
    }

    [Fact]
    public async Task Connect_FailsAndStaysPut_WhenNothingAnswers()
    {
        var home = FakeJellyfinServer.DeadUrl();
        var client = Configured(home, FakeJellyfinServer.DeadUrl());

        Assert.False(await client.ConnectAsync(Ct));
        Assert.Equal(home, client.ServerUrl);
    }

    [Fact]
    public async Task AFailedRequest_SwitchesAddressAndRetries()
    {
        var client = Configured(FakeJellyfinServer.DeadUrl(), _server.Url);

        var artists = await client.GetAlbumArtistsAsync(Ct); // no ConnectAsync first: the request finds the way

        Assert.Equal(FakeJellyfinServer.ArtistCount, artists.Items.Count);
        Assert.Equal(_server.Url, client.ServerUrl);
    }

    [Fact]
    public async Task ServerIdAt_ReadsThePublicInfo_OrNullWhenNothingAnswers()
    {
        var client = new JellyfinClient("test-device");

        Assert.Equal(FakeJellyfinServer.ServerId, await client.ServerIdAtAsync(_server.Url, Ct));
        Assert.Null(await client.ServerIdAtAsync(FakeJellyfinServer.DeadUrl(), Ct));
    }

    [Fact]
    public void Configure_TidiesTheList_AndKeepsTheAddressInUseIfStillListed()
    {
        var client = Configured("http://a:8096/", "http://a:8096", " ", "http://b:8096");
        Assert.Equal(new[] { "http://a:8096", "http://b:8096" }, client.Addresses);
        Assert.Equal("http://a:8096", client.ServerUrl);

        client.Configure(["http://b:8096", "http://a:8096"], client.Token, client.UserId, client.ServerId);
        Assert.Equal("http://a:8096", client.ServerUrl); // reordering doesn't switch by itself

        client.Configure(["http://b:8096"], client.Token, client.UserId, client.ServerId);
        Assert.Equal("http://b:8096", client.ServerUrl);
    }

    [Fact]
    public async Task PlayReports_SayWhichTrackWhereAndWhetherPaused()
    {
        var client = await SignedInAsync();

        await client.ReportStartAsync("rep-t1", 0, paused: false, "ps-client", Ct);
        await client.ReportProgressAsync("rep-t1", 12.5, paused: true, "ps-client", Ct);
        await client.ReportStoppedAsync("rep-t1", 200, "ps-client", Ct);

        var mine = _server.Reports.Where(r => r.Body.Contains("ps-client")).ToList();
        Assert.Equal(new[] { "/Sessions/Playing", "/Sessions/Playing/Progress", "/Sessions/Playing/Stopped" }, mine.Select(r => r.Path));
        Assert.Equal("""
                     {"ItemId":"rep-t1","MediaSourceId":"rep-t1","PositionTicks":0,"IsPaused":false,"CanSeek":true,"PlayMethod":"DirectPlay","PlaySessionId":"ps-client"}
                     """, mine[0].Body);
        Assert.Contains("\"PositionTicks\":125000000,\"IsPaused\":true", mine[1].Body);
        Assert.Equal("""{"ItemId":"rep-t1","MediaSourceId":"rep-t1","PositionTicks":2000000000,"PlaySessionId":"ps-client"}""", mine[2].Body);
    }

    [Fact]
    public async Task Marks_AreSetAndCleared_AsTheUsersOwnData()
    {
        var client = await SignedInAsync();

        await client.SetFavouriteAsync("al1", true, Ct);
        await client.SetLikesAsync("al1", false, Ct);
        Assert.Equal((true, (bool?)false), _server.MarksOf("al1"));

        var albums = await client.GetAlbumsAsync("ar1", Ct);
        var data = albums.Items.Single(a => a.Id == "al1").UserData!;
        Assert.True(data.IsFavorite);
        Assert.False(data.Likes);

        await client.SetFavouriteAsync("al1", false, Ct);
        await client.SetLikesAsync("al1", null, Ct);
        Assert.Equal((false, (bool?)null), _server.MarksOf("al1"));
    }

    [Fact]
    public async Task Tracks_CarryTheUsersPlayCounts()
    {
        var client = await SignedInAsync();
        var at = new DateTime(2026, 10, 24, 8, 32, 44, DateTimeKind.Utc);
        _server.SetPlays("ec1-t2", 4, at);

        var tracks = await client.GetTracksAsync("ec1", Ct);

        var data = tracks.Items.Single(t => t.Id == "ec1-t2").UserData!;
        Assert.Equal(4, data.PlayCount);
        Assert.Equal(at, data.LastPlayedDate?.ToUniversalTime());
        Assert.Equal(0, tracks.Items.Single(t => t.Id == "ec1-t1").UserData!.PlayCount);
    }
}
