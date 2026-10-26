using TunerFish.Tests.Support;
using TunerFish.ViewModels;

namespace TunerFish.Tests;

/// <summary>The queue kept across restarts: a second view model over the same harness is the app started again.</summary>
[Collection(AudioEngineCollection.Name)]
[Trait("Category", "Integration")]
public sealed class MainViewModelSavedQueueTests : IDisposable
{
    private readonly VmHarness _h;

    public MainViewModelSavedQueueTests(FakeJellyfinServer server) => _h = new VmHarness(server);

    public void Dispose() => _h.Dispose();

    private static string QueueIds(MainViewModel vm) => string.Join(",", vm.Queue.Select(e => e.Track.Id));

    private static Task WaitFor(MainViewModel vm, Func<bool> condition, string what) =>
        Wait.UntilAsync(condition, what, pump: vm.Tick, state: () => VmHarness.Describe(vm) + $", Queue={QueueIds(vm)}");

    [Fact]
    public async Task TheQueue_ComesBack_PausedAtTheSameTrackAndTime()
    {
        LibMpv.SkipIfUnavailable();
        var first = await _h.OnRevolverAsync();
        await first.PlayFromTrackAsync(first.Tracks[1]);
        await first.AddAlbumToQueueAsync(first.Albums.First(a => a.Id == "al1"));
        await WaitFor(first, () => first.CurrentTrack?.Id == "t2" && first.DurationText == "0:03", "Revolver's second track to play");
        first.BeginSeek();
        first.Position = 1.5;
        first.EndSeek();
        first.TogglePauseCommand.Execute(null);
        await WaitFor(first, () => first.IsPaused && first.PositionText == "0:01", "a pause 1.5 s in");
        first.Dispose();

        var vm = await _h.SignedInAsync();

        Assert.Equal("t1,t2,t3,al1-t1,al1-t2,al1-t3", QueueIds(vm));
        Assert.Equal("6 tracks · 0:18", vm.QueueSummaryText);
        Assert.Equal("Remove album (3 tracks)", vm.Queue[0].RemoveAlbumLabel); // still two albums
        await WaitFor(vm, () => vm.CurrentTrack?.Id == "t2" && vm.PositionText == "0:01", "Revolver's second track, 1.5 s in");
        Assert.True(vm.IsPaused);
        Assert.Equal("The Beatles — Revolver", vm.NowPlayingSubtitle);
        Assert.Equal(_h.Server.Url + "/Audio/t2/stream", vm.PlaylistUrl(1)?.Split('?')[0]);
        await WaitFor(vm, () => vm.NowPlayingCover is not null, "Revolver's cover");

        vm.TogglePauseCommand.Execute(null);
        await WaitFor(vm, () => !vm.IsPaused && vm.CurrentTrack?.Id == "t2", "play to carry on from there");
    }

    [Fact]
    public async Task AQueueThatEnded_ComesBackAtTheStartOfItsLastTrack()
    {
        LibMpv.SkipIfUnavailable();
        var first = await _h.OnRevolverAsync();
        await first.PlayFromTrackAsync(first.Tracks[2]);
        await Wait.UntilAsync(() => first.CurrentTrack?.Id == "t3", "the last track to play", pump: first.Tick);
        await Wait.UntilAsync(() => first.CurrentTrack is null, "the queue to end", TimeSpan.FromSeconds(15), pump: first.Tick);
        first.Dispose();

        var vm = await _h.SignedInAsync();

        await WaitFor(vm, () => vm.CurrentTrack?.Id == "t3" && vm.DurationText == "0:03", "the last track, loaded");
        Assert.Equal("0:00", vm.PositionText);
        Assert.True(vm.IsPaused);
    }

    [Fact]
    public async Task ClearQueueOnExit_StartsEmpty_AndTheOldQueueIsGoneForGood()
    {
        LibMpv.SkipIfUnavailable();
        var first = await _h.OnRevolverAsync();
        await first.AddAlbumToQueueAsync(first.SelectedAlbum!);
        first.ClearQueueOnExit = true;
        Assert.Contains("\"ClearQueueOnExit\": true", _h.SettingsText);
        first.Dispose();

        var second = await _h.SignedInAsync();
        Assert.Empty(second.Queue);
        second.ClearQueueOnExit = false;
        second.Dispose();

        var vm = await _h.SignedInAsync();
        Assert.Empty(vm.Queue);
    }

    [Fact]
    public async Task SigningOut_ForgetsTheQueue()
    {
        LibMpv.SkipIfUnavailable();
        var first = await _h.OnRevolverAsync();
        await first.AddAlbumToQueueAsync(first.SelectedAlbum!);
        first.SignOutCommand.Execute(null);
        first.Dispose();

        var vm = await _h.SignedInAsync(); // signs in through the form: no saved session

        Assert.Empty(vm.Queue);
    }
}
