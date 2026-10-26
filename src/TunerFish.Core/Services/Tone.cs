using System.Globalization;

namespace TunerFish.Services;

/// <summary>
/// Bass and treble, like the two knobs on an amplifier: a low shelf and a high shelf, each set in
/// whole dB from -<see cref="MaxDb"/> to +<see cref="MaxDb"/>, 0 = flat. Done in mpv, never the
/// system equaliser. Same levels and filters as the Android app's <c>Tone.kt</c>.
/// </summary>
public static class Tone
{
    public const int MaxDb = 12;
    /// <summary>Where each shelf is half-way to its full boost or cut.</summary>
    public const int BassHz = 100;
    public const int TrebleHz = 10_000;

    public static int Clamp(int db) => Math.Clamp(db, -MaxDb, MaxDb);

    public static string Label(int db) =>
        db > 0 ? $"+{db} dB" : db < 0 ? $"−{-db} dB" : "0 dB";

    /// <summary>
    /// mpv's <c>af</c> for these levels: ffmpeg's bass and treble shelves (RBJ cookbook, slope 1, the
    /// same filters Android computes itself), or nothing at all when flat.
    /// </summary>
    public static string Filter(int bassDb, int trebleDb)
    {
        bassDb = Clamp(bassDb);
        trebleDb = Clamp(trebleDb);
        if (bassDb == 0 && trebleDb == 0) return "";
        return string.Create(CultureInfo.InvariantCulture,
            $"@tone:lavfi=[bass=g={bassDb}:f={BassHz}:t=s:w=1,treble=g={trebleDb}:f={TrebleHz}:t=s:w=1]");
    }
}
