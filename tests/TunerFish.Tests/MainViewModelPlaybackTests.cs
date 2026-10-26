using TunerFish.Tests.Support;
using TunerFish.ViewModels;

namespace TunerFish.Tests;

/// <summary>Real playback through silent libmpv. Each test tone is 3 seconds long.</summary>
[Collection(AudioEngineCollection.Name)]
[Trait("Category", "Integration")]
public sealed class MainViewModelPlaybackTests : IDisposable
{
    private readonly VmHarness _h;

    public MainViewModelPlaybackTests(FakeJellyfinServer server) => _h = new VmHarness(server);

    public void Dispose() => _h.Dispose();

    private static Task WaitForTrack(MainViewModel vm, string? trackId, TimeSpan? timeout = null) =>
        Wait.UntilAsync(() => vm.CurrentTrack?.Id == trackId, trackId is null ? "nothing to be playing" : $"track {trackId} to be playing",
                        timeout, pump: vm.Tick, state: () => VmHarness.Describe(vm));

    private static Task WaitFor(MainViewModel vm, Func<bool> condition, string what) =>
        Wait.UntilAsync(condition, what, pump: vm.Tick, state: () => VmHarness.Describe(vm));

    [Fact]
    public async Task PlayAlbum_StartsTrackOne_AndShowsNowPlaying()
    {
        LibMpv.SkipIfUnavailable();
        var vm = await _h.OnRevolverAsync();
        await Wait.UntilAsync(() => vm.Albums.First(a => a.Id == "al2").Cover != null, "Revolver's cover");

        vm.PlaySelectedAlbumCommand.Execute(null);

        await WaitForTrack(vm, "t1");
        Assert.True(vm.Tracks[0].IsNowPlaying);
        Assert.Equal("▶", vm.Tracks[0].NowGlyph);
        Assert.False(vm.Tracks[1].IsNowPlaying);
        Assert.Equal("Song 1", vm.NowPlayingTitle);
        Assert.Equal("The Beatles — Revolver", vm.NowPlayingSubtitle);
        Assert.IsType<string>(vm.NowPlayingCover);
        Assert.StartsWith("Song 1", vm.WindowTitle);
        await WaitFor(vm, () => !vm.IsPaused && vm.PlayPauseGlyph == "⏸", "the player to show it's playing");
        await WaitFor(vm, () => vm.DurationText == "0:03", "duration text 0:03");
    }

    [Fact]
    public async Task Playing_IsReportedToJellyfin()
    {
        LibMpv.SkipIfUnavailable();
        var vm = await _h.OnRevolverAsync();
        int Starts() => _h.Server.Reports.Count(r => r.Path == "/Sessions/Playing" && r.Body.Contains("\"ItemId\":\"t1\""));
        var before = Starts();

        vm.PlaySelectedAlbumCommand.Execute(null);

        await WaitFor(vm, () => Starts() > before, "a start report for track 1");
    }

    [Fact]
    public async Task AppName_IsUsedInWindowTitle_AndPlayerVersionIsReported()
    {
        LibMpv.SkipIfUnavailable();
        var vm = _h.Create(appName: "Renamed App");
        Assert.Equal("Renamed App", vm.AppName);
        Assert.Equal("Renamed App", vm.WindowTitle);
        Assert.StartsWith("mpv ", vm.PlayerVersion);

        await vm.StartAsync();
        vm.ServerUrl = _h.Server.Url;
        vm.UserName = FakeJellyfinServer.UserName;
        vm.Password = FakeJellyfinServer.Password;
        vm.LoginCommand.Execute(null);
        await Wait.UntilAsync(() => vm.IsLoggedIn && vm.Artists.Count == FakeJellyfinServer.ArtistCount, "sign-in", state: () => VmHarness.Describe(vm));
        await VmHarness.SelectArtistAsync(vm, "ar1", 3);
        await VmHarness.SelectAlbumAsync(vm, "al2");
        vm.PlaySelectedAlbumCommand.Execute(null);

        await WaitForTrack(vm, "t1");
        Assert.EndsWith(" · Renamed App", vm.WindowTitle);
    }

    [Fact]
    public async Task TogglePause_Pauses_ThenResumes()
    {
        LibMpv.SkipIfUnavailable();
        var vm = await _h.OnRevolverAsync();
        vm.PlaySelectedAlbumCommand.Execute(null);
        await WaitFor(vm, () => vm.CurrentTrack?.Id == "t1" && !vm.IsPaused, "track t1 playing");

        vm.TogglePauseCommand.Execute(null);
        await WaitFor(vm, () => vm.IsPaused, "pause");

        vm.TogglePauseCommand.Execute(null);
        await WaitFor(vm, () => !vm.IsPaused, "resume");
    }

    [Fact]
    public async Task SeekSlider_NotOverwrittenWhileDragging_AndSeekNearEndAdvances()
    {
        LibMpv.SkipIfUnavailable();
        var vm = await _h.OnRevolverAsync();
        vm.PlaySelectedAlbumCommand.Execute(null);
        await WaitForTrack(vm, "t1");

        // The user drags the seek slider: Tick must not fight the drag.
        vm.BeginSeek();
        vm.Position = 2.6;
        vm.Tick();
        Assert.Equal(2.6, vm.Position, 3);

        vm.EndSeek();
        await WaitForTrack(vm, "t2");
        Assert.True(vm.Tracks[1].IsNowPlaying);
        Assert.False(vm.Tracks[0].IsNowPlaying);
    }

    [Fact]
    public async Task Previous_EarlyInTrack_GoesBackATrack()
    {
        LibMpv.SkipIfUnavailable();
        var vm = await _h.OnRevolverAsync();
        await vm.PlayFromTrackAsync(vm.Tracks[1]);
        await WaitForTrack(vm, "t2");

        vm.PreviousCommand.Execute(null);

        await WaitForTrack(vm, "t1");
    }

    [Fact]
    public async Task PlayFromTrack_PlaysAlbumStartingThere()
    {
        LibMpv.SkipIfUnavailable();
        var vm = await _h.OnRevolverAsync();

        await vm.PlayFromTrackAsync(vm.Tracks[2]);

        await WaitForTrack(vm, "t3");
    }

    [Fact]
    public async Task BrowsingElsewhere_KeepsPlaying_WithoutFalseNowPlayingMarker()
    {
        LibMpv.SkipIfUnavailable();
        var vm = await _h.OnRevolverAsync();
        vm.PlaySelectedAlbumCommand.Execute(null); // 9 s of audio: plenty of time to browse
        await WaitForTrack(vm, "t1");

        vm.SelectedArtist = vm.Artists.First(a => a.Id == "ar2");
        await WaitFor(vm, () => vm.Albums.Count == 1 && vm.Tracks.Count == 0, "the other artist's albums, with the track list cleared");
        Assert.StartsWith("t", vm.CurrentTrack?.Id);

        vm.SelectedAlbum = vm.Albums[0];
        await WaitFor(vm, () => vm.Tracks.Count == 3, "the other album's tracks");
        Assert.All(vm.Tracks, t => Assert.False(t.IsNowPlaying));
        Assert.NotNull(vm.CurrentTrack);
    }

    [Fact]
    public async Task AlbumEnd_ReturnsToIdle()
    {
        LibMpv.SkipIfUnavailable();
        var vm = await _h.OnRevolverAsync();
        await vm.PlayFromTrackAsync(vm.Tracks[2]);
        await WaitForTrack(vm, "t3");

        await WaitForTrack(vm, null, TimeSpan.FromSeconds(15));

        Assert.Equal("Nothing playing", vm.NowPlayingTitle);
        Assert.Equal("TunerFish", vm.WindowTitle);
        Assert.True(vm.IsPaused);
    }

    [Fact]
    public async Task PlayButton_WithNothingQueued_PlaysSelectedAlbum()
    {
        LibMpv.SkipIfUnavailable();
        var vm = await _h.SignedInAsync();
        await VmHarness.SelectArtistAsync(vm, "ar2", 1);
        await VmHarness.SelectAlbumAsync(vm, "al9");

        vm.TogglePauseCommand.Execute(null);

        await WaitForTrack(vm, "al9-t1");
    }

    [Fact]
    public async Task WhenTheServerMovesAddress_TheQueueFollows_AndCarriesOn()
    {
        LibMpv.SkipIfUnavailable();
        using var away = FakeJellyfinServer.WithId(FakeJellyfinServer.ServerId); // the same server, reached another way
        var vm = await _h.OnRevolverAsync();
        vm.NewServerAddress = away.Url;
        vm.AddServerAddress();
        await vm.PlayFromTrackAsync(vm.Tracks[1]);
        await WaitForTrack(vm, "t2");

        // Prefer the other address, as if the laptop had left home: the playlist is reloaded with it.
        vm.MoveServerAddressUp(away.Url);
        await vm.CheckServerAddressesAsync();
        await WaitFor(vm, () => vm.PlaylistUrl(0)?.StartsWith(away.Url) == true, "mpv's playlist to point at the new address");

        Assert.All(Enumerable.Range(0, 3), i => Assert.StartsWith(away.Url, vm.PlaylistUrl(i)));
        Assert.Equal(3, vm.Queue.Count);
        await WaitForTrack(vm, "t2"); // the same track carries on
        await WaitFor(vm, () => away.RequestCount("/Audio/t2/stream") > 0, "the track to stream from the new address");
    }
}
