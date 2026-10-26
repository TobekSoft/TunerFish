using TunerFish.Services;
using TunerFish.Tests.Support;

namespace TunerFish.Tests;

/// <summary>The libmpv wrapper on its own, playing generated WAV files from a temp directory.</summary>
[Collection(AudioEngineCollection.Name)]
[Trait("Category", "Integration")]
public sealed class MpvPlayerTests : IDisposable
{
    private readonly TempDir _dir = new();
    private MpvPlayer? _player;

    public void Dispose()
    {
        _player?.Dispose();
        _dir.Dispose();
    }

    private (MpvPlayer Player, List<string> Files) Start(int startIndex = 0)
    {
        LibMpv.SkipIfUnavailable();
        var files = new[] { "t1", "t2", "t3" }.Select(id =>
        {
            var f = _dir.File(id + ".wav");
            File.WriteAllBytes(f, TestMedia.Tone(id));
            return f;
        }).ToList();
        _player = new MpvPlayer(silentForTests: true);
        _player.LoadPlaylist(files, startIndex);
        return (_player, files);
    }

    private static Task WaitFor(MpvPlayer p, Func<bool> condition, string what, TimeSpan? timeout = null) =>
        Wait.UntilAsync(condition, what, timeout,
            state: () => $"playlist-pos={p.PlaylistPosition}, time-pos={p.TimePosition}, duration={p.Duration}, paused={p.IsPaused}, idle={p.IsIdle}");

    [Fact]
    public async Task LoadPlaylist_PlaysFromStartIndex_AndReportsDuration()
    {
        var (p, _) = Start(startIndex: 1);

        await WaitFor(p, () => p.PlaylistPosition == 1 && !p.IsIdle && p.Duration is > 0, "track 2 to start");

        Assert.Equal(TestMedia.ToneSeconds, p.Duration!.Value, 1);
        Assert.False(p.IsPaused);
    }

    [Fact]
    public async Task AFileThatWontPlay_IsReported()
    {
        LibMpv.SkipIfUnavailable();
        var bad = _dir.File("bad.wav");
        File.WriteAllText(bad, "not audio");
        _player = new MpvPlayer(silentForTests: true);
        var failed = new System.Collections.Concurrent.ConcurrentQueue<string>();
        _player.PlaybackFailed += failed.Enqueue;

        _player.LoadPlaylist([bad], 0);

        await Wait.UntilAsync(() => !failed.IsEmpty, "mpv to report the failure");
        Assert.False(string.IsNullOrWhiteSpace(failed.Single()));
    }

    [Fact]
    public async Task TogglePause_PausesAndResumes()
    {
        var (p, _) = Start();
        await WaitFor(p, () => p.Duration is > 0, "playback to start");

        p.TogglePause();
        await WaitFor(p, () => p.IsPaused, "pause");

        p.TogglePause();
        await WaitFor(p, () => !p.IsPaused, "resume");
    }

    [Fact]
    public async Task Next_And_Previous_MoveThroughPlaylist()
    {
        var (p, _) = Start();
        await WaitFor(p, () => p.PlaylistPosition == 0, "track 1");

        p.Next();
        await WaitFor(p, () => p.PlaylistPosition == 1, "next → track 2");

        p.Previous();
        await WaitFor(p, () => p.PlaylistPosition == 0, "previous → track 1");
    }

    [Fact]
    public async Task PlaylistEnd_GoesIdle()
    {
        var (p, _) = Start(startIndex: 2);
        await WaitFor(p, () => p.PlaylistPosition == 2, "track 3");

        await WaitFor(p, () => p.IsIdle && p.PlaylistPosition == -1, "the end of the playlist", TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void MpvVersion_ReportsLibmpvsVersion()
    {
        LibMpv.SkipIfUnavailable();
        _player = new MpvPlayer(silentForTests: true);

        Assert.StartsWith("mpv ", _player.MpvVersion);
    }

    private static string Names(MpvPlayer p) =>
        string.Join(",", Enumerable.Range(0, p.PlaylistCount).Select(i => Path.GetFileNameWithoutExtension(p.PlaylistFile(i))));

    [Fact]
    public async Task Move_FollowsListMoveSemantics_BothWays()
    {
        var (p, _) = Start();
        await WaitFor(p, () => p.PlaylistPosition == 0, "track 1");

        p.Move(0, 2);
        Assert.Equal("t2,t3,t1", Names(p));
        p.Move(2, 0);
        Assert.Equal("t1,t2,t3", Names(p));
        p.Move(1, 2);
        Assert.Equal("t1,t3,t2", Names(p));
    }

    [Fact]
    public async Task RemoveAt_Current_MovesOnToNext()
    {
        var (p, files) = Start();
        await WaitFor(p, () => p.PlaylistPosition == 0 && p.Duration is > 0, "track 1");

        p.RemoveAt(0);

        Assert.Equal("t2,t3", Names(p));
        await WaitFor(p, () => p.PlaylistPosition == 0 && p.PlaylistFile(0) == files[1] && !p.IsIdle, "track 2 to play");
    }

    [Fact]
    public async Task Append_WhenIdle_DoesntPlay_CueLoadsPaused_AndClearEmpties()
    {
        var (p, files) = Start(startIndex: 2);
        await WaitFor(p, () => p.IsIdle && p.PlaylistPosition == -1, "the end of the playlist", TimeSpan.FromSeconds(15));

        p.Append(files[0]);
        await WaitFor(p, () => p.PlaylistCount == 4, "the track to be appended");
        Assert.True(p.IsIdle);
        Assert.Equal(-1, p.PlaylistPosition);

        p.Cue(3);
        await WaitFor(p, () => p.PlaylistPosition == 3 && p.Duration is > 0, "the appended track to load");
        Assert.True(p.IsPaused);

        p.Clear();
        await WaitFor(p, () => p.IsIdle && p.PlaylistCount == 0, "an empty, idle player");
    }

    [Fact]
    public async Task Seek_MovesPosition_AndVolumeIsClamped()
    {
        var (p, _) = Start();
        await WaitFor(p, () => p.Duration is > 0, "playback to start");
        p.TogglePause(); // hold still while we check the position
        await WaitFor(p, () => p.IsPaused, "pause");

        p.Seek(2);
        // A plain "absolute" seek isn't sample-exact (it lands on a demuxer packet), so allow some slack.
        await WaitFor(p, () => p.TimePosition is > 1.5 and < 2.5, "position near 2 s");

        p.SetVolume(140);
        Assert.Equal(100, p.GetDouble("volume"));
        p.SetVolume(-5);
        Assert.Equal(0, p.GetDouble("volume"));
    }

    [Fact]
    public async Task SetTone_MidTrack_KeepsPlaying_AndFlatTakesTheFilterOut()
    {
        var (p, _) = Start();
        await WaitFor(p, () => p.TimePosition is > 0.3, "playback to start");

        p.SetTone(6, -3);
        Assert.Contains("bass=g=6:f=100", p.AudioFilters);
        Assert.Contains("treble=g=-3:f=10000", p.AudioFilters);
        var at = p.TimePosition!.Value;
        await WaitFor(p, () => p.TimePosition > at + 0.3, "the same track to carry on");
        Assert.Equal(0, p.PlaylistPosition);

        p.SetTone(0, 0);
        Assert.Equal("", p.AudioFilters);
    }
}
