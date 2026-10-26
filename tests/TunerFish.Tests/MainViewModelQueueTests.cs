using TunerFish.Tests.Support;
using TunerFish.ViewModels;

namespace TunerFish.Tests;

/// <summary>The play queue: adding, removing, reordering, clearing, and greying out what's been played.</summary>
[Collection(AudioEngineCollection.Name)]
[Trait("Category", "Integration")]
public sealed class MainViewModelQueueTests : IDisposable
{
    private readonly VmHarness _h;

    public MainViewModelQueueTests(FakeJellyfinServer server) => _h = new VmHarness(server);

    public void Dispose() => _h.Dispose();

    private static string QueueIds(MainViewModel vm) => string.Join(",", vm.Queue.Select(e => e.Track.Id));

    private static Task WaitForTrack(MainViewModel vm, string? trackId, TimeSpan? timeout = null) =>
        Wait.UntilAsync(() => vm.CurrentTrack?.Id == trackId, trackId is null ? "nothing to be playing" : $"track {trackId} to be playing",
                        timeout, pump: vm.Tick, state: () => VmHarness.Describe(vm) + $", Queue={QueueIds(vm)}");

    /// <summary>Jumps to near the end of the current track so the queue moves on by itself.</summary>
    private static void SkipToEnd(MainViewModel vm)
    {
        vm.BeginSeek();
        vm.Position = 2.6;
        vm.EndSeek();
    }

    [Fact]
    public async Task PlayAlbum_ReplacesQueue_AndMarksCurrent()
    {
        LibMpv.SkipIfUnavailable();
        var vm = await _h.OnRevolverAsync();
        await vm.AddAlbumToQueueAsync(vm.Albums.First(a => a.Id == "al1"));
        Assert.Equal("al1-t1,al1-t2,al1-t3", QueueIds(vm));

        await vm.PlayFromTrackAsync(vm.Tracks[1]);

        await WaitForTrack(vm, "t2");
        Assert.Equal("t1,t2,t3", QueueIds(vm));
        Assert.True(vm.Queue[1].IsCurrent);
        Assert.Equal("▶", vm.Queue[1].NowGlyph);
        Assert.False(vm.Queue[0].IsPlayed); // skipped over by starting later, not played
        Assert.Equal("3 tracks · 0:09", vm.QueueSummaryText);
    }

    [Fact]
    public async Task AddAlbum_IntoAnEmptyQueue_LoadsItPaused_AndNeverStartsPlaying()
    {
        LibMpv.SkipIfUnavailable();
        var vm = await _h.OnRevolverAsync();
        var reportsBefore = _h.Server.Reports.Count; // the server is shared: only look at this test's

        vm.QueueSelectedAlbumCommand.Execute(null);
        await WaitForTrack(vm, "t1");
        await Wait.UntilAsync(() => vm.DurationText == "0:03", "the first track to load", pump: vm.Tick);
        Assert.True(vm.IsPaused);
        Assert.Equal("0:00", vm.PositionText);
        Assert.Equal("The Beatles — Revolver", vm.NowPlayingSubtitle);
        Assert.DoesNotContain(_h.Server.Reports.Skip(reportsBefore), r => r.Path == "/Sessions/Playing"); // not played, so not reported

        vm.TogglePauseCommand.Execute(null); // one press of play starts it
        await Wait.UntilAsync(() => !vm.IsPaused, "play to start", pump: vm.Tick);
        Assert.Equal("t1", vm.CurrentTrack?.Id);
    }

    [Fact]
    public async Task AddAlbum_ToAQueueThatsPlaying_DoesntInterrupt_ThenCarriesOnAcrossAlbums()
    {
        LibMpv.SkipIfUnavailable();
        var vm = await _h.OnRevolverAsync();

        vm.PlaySelectedAlbumCommand.Execute(null);
        await WaitForTrack(vm, "t1");
        vm.Albums.First(a => a.Id == "al1").QueueCommand!.Execute(null);
        await Wait.UntilAsync(() => vm.Queue.Count == 6, "Abbey Road to be queued");

        Assert.Equal("t1,t2,t3,al1-t1,al1-t2,al1-t3", QueueIds(vm));
        Assert.Equal("t1", vm.CurrentTrack?.Id); // queueing more didn't interrupt
        Assert.False(vm.IsPaused);
        Assert.Equal("The Beatles — Revolver", vm.NowPlayingSubtitle);

        vm.PlayQueueItem(vm.Queue[2]);
        await WaitForTrack(vm, "t3");
        SkipToEnd(vm);
        await WaitForTrack(vm, "al1-t1");
        Assert.Equal("The Beatles — Abbey Road", vm.NowPlayingSubtitle);
    }

    [Fact]
    public async Task PlayedTracks_AreMarked_AndUnmarkedWhenPlayedAgain()
    {
        LibMpv.SkipIfUnavailable();
        var vm = await _h.OnRevolverAsync();
        vm.PlaySelectedAlbumCommand.Execute(null);
        await WaitForTrack(vm, "t1");
        Assert.All(vm.Queue, e => Assert.False(e.IsPlayed));

        SkipToEnd(vm);
        await WaitForTrack(vm, "t2");
        Assert.True(vm.Queue[0].IsPlayed);
        Assert.False(vm.Queue[0].IsCurrent);
        Assert.False(vm.Queue[1].IsPlayed);
        Assert.False(vm.Queue[2].IsPlayed);

        vm.Queue[0].PlayCommand!.Execute(null);
        await WaitForTrack(vm, "t1");
        Assert.False(vm.Queue[0].IsPlayed);
        Assert.True(vm.Queue[1].IsPlayed);
    }

    [Fact]
    public async Task QueueEnd_MarksLastPlayed_AndPlayResumesFromFirstUnplayed()
    {
        LibMpv.SkipIfUnavailable();
        var vm = await _h.OnRevolverAsync();
        await vm.PlayFromTrackAsync(vm.Tracks[2]);
        await WaitForTrack(vm, "t3");

        await WaitForTrack(vm, null, TimeSpan.FromSeconds(15));
        Assert.True(vm.Queue[2].IsPlayed);
        Assert.Equal(3, vm.Queue.Count); // played tracks stay in the queue

        vm.TogglePauseCommand.Execute(null);
        await WaitForTrack(vm, "t1");
    }

    [Fact]
    public async Task Move_ChangesWhatPlaysNext_WithoutInterrupting()
    {
        LibMpv.SkipIfUnavailable();
        var vm = await _h.OnRevolverAsync();
        vm.PlaySelectedAlbumCommand.Execute(null);
        await WaitForTrack(vm, "t1");

        vm.MoveQueueItem(2, 1);
        Assert.Equal("t1,t3,t2", QueueIds(vm));
        vm.Tick();
        Assert.Equal("t1", vm.CurrentTrack?.Id);

        SkipToEnd(vm);
        await WaitForTrack(vm, "t3");
    }

    [Fact]
    public async Task Move_TheCurrentTrack_KeepsItCurrent()
    {
        LibMpv.SkipIfUnavailable();
        var vm = await _h.OnRevolverAsync();
        vm.PlaySelectedAlbumCommand.Execute(null);
        await WaitForTrack(vm, "t1");
        var playing = vm.Queue[0];

        vm.MoveQueueItem(0, 2);
        vm.Tick();

        Assert.Equal("t2,t3,t1", QueueIds(vm));
        Assert.Same(playing, vm.Queue[2]);
        Assert.True(playing.IsCurrent);
        Assert.False(vm.Queue[0].IsPlayed);
        SkipToEnd(vm);
        await WaitForTrack(vm, null, TimeSpan.FromSeconds(15)); // it was last, so the queue ends
    }

    [Fact]
    public async Task Remove_Upcoming_SkipsIt_AndRemoveCurrent_MovesOn()
    {
        LibMpv.SkipIfUnavailable();
        var vm = await _h.OnRevolverAsync();
        vm.PlaySelectedAlbumCommand.Execute(null);
        await WaitForTrack(vm, "t1");

        vm.Queue[1].RemoveCommand!.Execute(null);
        Assert.Equal("t1,t3", QueueIds(vm));
        Assert.Equal("t1", vm.CurrentTrack?.Id);

        vm.RemoveFromQueue(vm.Queue[0]);
        Assert.Equal("t3", QueueIds(vm));
        await WaitForTrack(vm, "t3");
        Assert.True(vm.Queue[0].IsCurrent);
    }

    [Fact]
    public async Task RemoveAlbum_TakesTheWholeRun_AndThePlayingAlbumMovesOnToTheNext()
    {
        LibMpv.SkipIfUnavailable();
        var vm = await _h.OnRevolverAsync();
        vm.PlaySelectedAlbumCommand.Execute(null);
        await WaitForTrack(vm, "t1");
        await vm.AddAlbumToQueueAsync(vm.Albums.First(a => a.Id == "al1"));
        Assert.Equal("Remove album (3 tracks)", vm.Queue[1].RemoveAlbumLabel);

        vm.Queue[1].RemoveAlbumCommand!.Execute(null); // a track of the album that's playing

        Assert.Equal("al1-t1,al1-t2,al1-t3", QueueIds(vm));
        await WaitForTrack(vm, "al1-t1");
        Assert.Equal(_h.Server.Url + "/Audio/al1-t1/stream", vm.PlaylistUrl(0)?.Split('?')[0]); // mpv's playlist matches
        Assert.Null(vm.PlaylistUrl(3));
    }

    [Fact]
    public async Task RemoveAlbum_TheSameAlbumQueuedTwice_IsTwoRuns()
    {
        LibMpv.SkipIfUnavailable();
        var vm = await _h.OnRevolverAsync();
        var revolver = vm.SelectedAlbum!;
        await vm.AddAlbumToQueueAsync(revolver);
        await vm.AddAlbumToQueueAsync(vm.Albums.First(a => a.Id == "al1"));
        await vm.AddAlbumToQueueAsync(revolver);
        vm.AddTrackToQueue(vm.Tracks[0]); // a fourth Revolver track, straight after the third copy's
        await WaitForTrack(vm, "t1");
        Assert.Equal("Remove album (4 tracks)", vm.Queue[6].RemoveAlbumLabel);

        vm.RemoveAlbumFromQueue(vm.Queue[7]);

        Assert.Equal("t1,t2,t3,al1-t1,al1-t2,al1-t3", QueueIds(vm));
        Assert.Equal("t1", vm.CurrentTrack?.Id); // the first copy, loaded paused, is untouched
        Assert.True(vm.Queue[0].IsCurrent);
        Assert.Equal("Remove album (3 tracks)", vm.Queue[0].RemoveAlbumLabel);
        Assert.Null(vm.PlaylistUrl(6));
    }

    [Fact]
    public async Task SameTrackTwice_EntriesAreIndependent()
    {
        LibMpv.SkipIfUnavailable();
        var vm = await _h.OnRevolverAsync();
        vm.AddTrackToQueue(vm.Tracks[0]);
        await WaitForTrack(vm, "t1");
        vm.AddTrackToQueue(vm.Tracks[0]);
        Assert.Equal("t1,t1", QueueIds(vm));

        vm.RemoveFromQueue(vm.Queue[1]);

        Assert.Single(vm.Queue);
        Assert.True(vm.Queue[0].IsCurrent);
        Assert.Equal("1 track · 0:03", vm.QueueSummaryText);
    }

    [Fact]
    public async Task Clear_StopsAndEmpties()
    {
        LibMpv.SkipIfUnavailable();
        var vm = await _h.OnRevolverAsync();
        vm.PlaySelectedAlbumCommand.Execute(null);
        await WaitForTrack(vm, "t1");
        Assert.True(vm.HasQueue);

        vm.ClearQueueCommand.Execute(null);

        Assert.Empty(vm.Queue);
        Assert.False(vm.HasQueue);
        Assert.Equal("", vm.QueueSummaryText);
        await WaitForTrack(vm, null);
        Assert.Equal("Nothing playing", vm.NowPlayingTitle);
        Assert.All(vm.Tracks, t => Assert.False(t.IsNowPlaying));
    }
}
