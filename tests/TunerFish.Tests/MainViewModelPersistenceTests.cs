using TunerFish.Services;
using TunerFish.Tests.Support;
using TunerFish.ViewModels;

namespace TunerFish.Tests;

[Collection(AudioEngineCollection.Name)]
public sealed class MainViewModelPersistenceTests : IDisposable
{
    private readonly VmHarness _h;

    public MainViewModelPersistenceTests(FakeJellyfinServer server) => _h = new VmHarness(server);

    public void Dispose() => _h.Dispose();

    [Fact]
    public void ACrash_IsLogged_AndTheNextStartSaysSoOnce()
    {
        _h.Create().NoteCrash(new InvalidOperationException("boom at ?api_key=secret"));

        Assert.Contains(_h.Logged, l => l.StartsWith("Crash: System.InvalidOperationException: boom at ?api_key=…", StringComparison.Ordinal));
        Assert.Contains("\"CrashToShare\": true", _h.SettingsText);

        var next = _h.Create();
        Assert.Contains("closed unexpectedly last time", next.Status);
        Assert.Contains("\"CrashToShare\": false", _h.SettingsText);
        Assert.DoesNotContain("unexpectedly", _h.Create().Status);
    }

    [Fact]
    public void Volume_IsClampedTo100()
    {
        var vm = _h.Create();

        vm.Volume = 140;

        Assert.Equal(100, vm.Volume);
    }

    [Fact]
    public void BassAndTreble_AreWholeDbWithinTwelve_AndFlatResetsBoth()
    {
        var vm = _h.Create();
        Assert.True(vm.IsToneFlat);

        vm.Bass = 3.4;
        vm.Treble = -20;

        Assert.Equal(3, vm.Bass);
        Assert.Equal(-12, vm.Treble);
        Assert.Equal("+3 dB", vm.BassLabel);
        Assert.Equal("−12 dB", vm.TrebleLabel);
        Assert.Equal("Bass +3 dB, treble −12 dB", vm.ToneTip);

        vm.FlatToneCommand.Execute(null);
        Assert.True(vm.IsToneFlat);
        Assert.Equal("Bass and treble", vm.ToneTip);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void BassAndTreble_AreSaved_AndHeardFromTheStart()
    {
        LibMpv.SkipIfUnavailable();
        var first = _h.Create();
        first.Bass = 4;
        first.Treble = -2;
        Assert.Contains("bass=g=4", first.AudioFilters);
        first.SaveSettings();
        first.Dispose();
        Assert.Contains("\"Bass\": 4", _h.SettingsText);

        var vm = _h.Create();

        Assert.Equal(4, vm.Bass);
        Assert.Equal(-2, vm.Treble);
        Assert.Contains("bass=g=4:f=100", vm.AudioFilters);
        Assert.Contains("treble=g=-2:f=10000", vm.AudioFilters);
    }

    [Fact]
    public void AlbumArtSize_IsClampedToSliderRange()
    {
        var vm = _h.Create();

        vm.AlbumArtSize = 20;
        Assert.Equal(MainViewModel.MinAlbumArtSize, vm.AlbumArtSize);

        vm.AlbumArtSize = 1000;
        Assert.Equal(MainViewModel.MaxAlbumArtSize, vm.AlbumArtSize);
    }

    [Fact]
    public void Settings_AreRestoredOnRestart()
    {
        var first = _h.Create();
        first.Volume = 35;
        first.SingleClickPlaysAlbum = true;
        first.AlbumSortIndex = 1;
        first.AlbumArtSize = 240;
        first.Theme = ThemeMode.Dark;
        first.SaveSettings();
        first.Dispose();

        var vm = _h.Create();

        Assert.Equal(ThemeMode.Dark, vm.Theme);

        Assert.Equal(35, vm.Volume);
        Assert.Equal(240, vm.AlbumArtSize);
        Assert.True(vm.SingleClickPlaysAlbum);
        Assert.Equal(1, vm.AlbumSortIndex);
    }

    [Fact]
    public void Theme_DefaultsToSystem_AndIsSavedAsAWord()
    {
        var first = _h.Create();
        Assert.Equal(ThemeMode.System, first.Theme);

        first.ThemeIndex = 0;
        first.Dispose();

        Assert.Contains("\"Theme\": \"Light\"", _h.SettingsText);
    }

    [Fact]
    public void UnknownThemeValue_ResetsOnlyThatSetting()
    {
        var first = _h.Create();
        first.Theme = ThemeMode.Light;
        first.Volume = 42;
        first.SaveSettings();
        first.Dispose();
        File.WriteAllText(_h.SettingsPath, _h.SettingsText.Replace("\"Light\"", "\"Sepia\""));

        var vm = _h.Create();

        Assert.Equal(ThemeMode.System, vm.Theme);
        Assert.Equal(42, vm.Volume);
    }

    [Fact]
    public void UnknownSortValue_ResetsOnlyThatSetting()
    {
        var first = _h.Create();
        first.AlbumSort = AlbumSortMode.Title;
        first.Volume = 42;
        first.SingleClickPlaysAlbum = true;
        first.SaveSettings();
        first.Dispose();
        File.WriteAllText(_h.SettingsPath, _h.SettingsText.Replace("\"Title\"", "\"SomethingFromTheFuture\""));

        var vm = _h.Create();

        Assert.Equal(AlbumSortMode.YearAscending, vm.AlbumSort);
        Assert.Equal(42, vm.Volume);
        Assert.True(vm.SingleClickPlaysAlbum);
    }

    [Fact]
    public void UnreadableSettings_AreReportedAtStartup()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_h.SettingsPath)!);
        File.WriteAllText(_h.SettingsPath, "{ this is not json");

        var vm = _h.Create();

        Assert.Contains("Settings file couldn't be read", vm.Status);
    }
}
