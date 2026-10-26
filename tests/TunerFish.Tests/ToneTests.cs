using TunerFish.Services;

namespace TunerFish.Tests;

/// <summary>
/// The levels mirror the Android app's ToneTest. Its filter maths isn't ported: mpv runs ffmpeg's
/// shelves, the same RBJ filters, so what's checked here is that they're asked for correctly.
/// </summary>
public sealed class ToneTests
{
    [Fact]
    public void Levels_AreWholeDbWithinTwelve_AndLabelledWithASign()
    {
        Assert.Equal(12, Tone.Clamp(20));
        Assert.Equal(-12, Tone.Clamp(-13));
        Assert.Equal("+3 dB", Tone.Label(3));
        Assert.Equal("−4 dB", Tone.Label(-4));
        Assert.Equal("0 dB", Tone.Label(0));
    }

    [Fact]
    public void Flat_IsNoFilterAtAll()
    {
        Assert.Equal("", Tone.Filter(0, 0));
    }

    [Fact]
    public void TheFilter_IsALowShelfAt100Hz_AndAHighShelfAt10kHz_SlopeOne()
    {
        Assert.Equal("@tone:lavfi=[bass=g=6:f=100:t=s:w=1,treble=g=0:f=10000:t=s:w=1]", Tone.Filter(6, 0));
        Assert.Equal("@tone:lavfi=[bass=g=-12:f=100:t=s:w=1,treble=g=12:f=10000:t=s:w=1]", Tone.Filter(-30, 30));
    }
}
