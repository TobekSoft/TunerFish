using System.Text.Json;
using TunerFish.Services;
using TunerFish.Tests.Support;

namespace TunerFish.Tests;

/// <summary>The reporter turns the player's state, tick by tick, into Jellyfin's start/progress/stop reports.</summary>
public sealed class PlayReporterTests : IDisposable
{
    private readonly FakeJellyfinServer _server = new();
    private readonly JellyfinClient _client = new("test-device");
    private DateTime _now = new(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
    private readonly PlayReporter _reporter;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public PlayReporterTests() => _reporter = new PlayReporter(_client, () => _now);

    public void Dispose() => _server.Dispose();

    private Task SignInAsync() => _client.LoginAsync(_server.Url, FakeJellyfinServer.UserName, FakeJellyfinServer.Password, Ct);

    /// <summary>The reports so far, as "Kind item seconds[ paused]".</summary>
    private async Task<List<string>> ReportsAsync()
    {
        await _reporter.Idle;
        return _server.Reports.Select(r =>
        {
            var body = JsonDocument.Parse(r.Body).RootElement;
            var kind = r.Path switch { "/Sessions/Playing" => "Start", "/Sessions/Playing/Progress" => "Progress", _ => "Stop" };
            var seconds = body.GetProperty("PositionTicks").GetInt64() / TimeSpan.TicksPerSecond;
            var paused = body.TryGetProperty("IsPaused", out var p) && p.GetBoolean() ? " paused" : "";
            return $"{kind} {body.GetProperty("ItemId").GetString()} {seconds}{paused}";
        }).ToList();
    }

    /// <summary>One tick: the clock moves on half a second.</summary>
    private void Tick(string? track, double time, bool paused = false)
    {
        _now = _now.AddSeconds(0.5);
        _reporter.Update(track, time, paused);
    }

    [Fact]
    public async Task ATrackPlayedThrough_StartsThenStopsWhereItEnded_AndTheNextStarts()
    {
        await SignInAsync();
        Tick("t1", 0);
        Tick("t1", 0.5);
        Tick("t1", 179.8);   // (a long gap between ticks counts as a seek; fine)
        Tick("t2", 0.1);

        Assert.Equal(new[] { "Start t1 0", "Progress t1 179", "Stop t1 179", "Start t2 0" }, await ReportsAsync());
    }

    [Fact]
    public async Task PauseAndResume_AreReported()
    {
        await SignInAsync();
        Tick("t1", 0);
        Tick("t1", 5, paused: true);
        Tick("t1", 5, paused: true);
        Tick("t1", 5.5);

        Assert.Equal(new[] { "Start t1 0", "Progress t1 5 paused", "Progress t1 5" }, await ReportsAsync());
    }

    [Fact]
    public async Task APausedQueue_ReportsNothingUntilItPlays()
    {
        await SignInAsync();
        Tick("t1", 42, paused: true); // restored at startup, paused
        Tick("t1", 42, paused: true);
        Assert.Empty(await ReportsAsync());

        Tick("t1", 42.5);
        Assert.Equal(new[] { "Start t1 42" }, await ReportsAsync());
    }

    [Fact]
    public async Task WhilePlaying_ThePositionIsSentEveryTenSeconds()
    {
        await SignInAsync();
        for (var t = 0.0; t <= 12; t += 0.5) Tick("t1", t);

        Assert.Equal(new[] { "Start t1 0", "Progress t1 10" }, await ReportsAsync());
    }

    [Fact]
    public async Task TheEndOfTheQueue_AndClosing_StopTheTrack()
    {
        await SignInAsync();
        Tick("t1", 0);
        Tick("t1", 2.9);
        Tick(null, 0);          // the queue ran out
        Tick("t2", 0);
        Tick("t2", 1);
        await _reporter.CloseAsync();

        Assert.Equal(new[] { "Start t1 0", "Stop t1 2", "Start t2 0", "Stop t2 1" }, await ReportsAsync());
    }

    [Fact]
    public async Task SignedOut_NothingIsSent()
    {
        Tick("t1", 0);
        Tick("t2", 0);
        await _reporter.CloseAsync();

        Assert.Empty(_server.Reports);
    }

    [Fact]
    public async Task AReportTheServerDoesntTake_IsDropped_AndLogged()
    {
        var client = new JellyfinClient("test-device");
        client.Configure([FakeJellyfinServer.DeadUrl()], "tok", "u1", "srv1");
        var said = new List<string>();
        var reporter = new PlayReporter(client, () => _now, said.Add);

        reporter.Update("t1", 0, paused: false);
        await reporter.CloseAsync();

        Assert.Equal(2, said.Count);
        Assert.StartsWith("Server didn't take a play start report (track t1), dropped: ", said[0]);
        Assert.StartsWith("Server didn't take a play stop report (track t1)", said[1]);
    }
}
