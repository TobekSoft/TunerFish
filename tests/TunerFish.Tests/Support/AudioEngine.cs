using TunerFish.Services;

namespace TunerFish.Tests.Support;

/// <summary>
/// Everything that creates an MpvPlayer (directly or through MainViewModel) runs in this collection,
/// one test at a time: there's one audio engine and the playback checks are timing-sensitive.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AudioEngineCollection : ICollectionFixture<FakeJellyfinServer>
{
    public const string Name = "Audio engine";
}

public static class LibMpv
{
    private static readonly Lazy<string?> Problem = new(() =>
    {
        try
        {
            using var probe = new MpvPlayer(silentForTests: true);
            return null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            return ex.Message;
        }
    });

    /// <summary>Skips the current test, with the reason, if libmpv can't be loaded.</summary>
    public static void SkipIfUnavailable() =>
        Assert.SkipWhen(Problem.Value is not null,
            $"libmpv could not be loaded, so playback isn't tested ({Problem.Value}). On Mint/Ubuntu: sudo apt install libmpv2");
}
