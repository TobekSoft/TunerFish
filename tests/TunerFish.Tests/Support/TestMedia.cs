using System.Text;

namespace TunerFish.Tests.Support;

/// <summary>Test audio and artwork, made in code so the tests need no ffmpeg or files on disk.</summary>
public static class TestMedia
{
    public const int SampleRate = 44100;
    public const double ToneSeconds = 3;

    // One tone per track id the fake server knows: t1, t2, t3.
    private static readonly Dictionary<string, byte[]> Tones = new()
    {
        ["t1"] = SineWav(440, ToneSeconds),
        ["t2"] = SineWav(550, ToneSeconds),
        ["t3"] = SineWav(660, ToneSeconds),
    };

    public static byte[] Tone(string trackId) => Tones[trackId];

    /// <summary>A mono 16-bit PCM WAV file holding a quiet sine wave.</summary>
    public static byte[] SineWav(double frequency, double seconds, int sampleRate = SampleRate)
    {
        var samples = (int)(sampleRate * seconds);
        var dataBytes = samples * 2;
        using var ms = new MemoryStream(44 + dataBytes);
        using var w = new BinaryWriter(ms, Encoding.ASCII);

        w.Write("RIFF"u8);
        w.Write(36 + dataBytes);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);                 // fmt chunk size
        w.Write((short)1);           // PCM
        w.Write((short)1);           // mono
        w.Write(sampleRate);
        w.Write(sampleRate * 2);     // byte rate
        w.Write((short)2);           // block align
        w.Write((short)16);          // bits per sample
        w.Write("data"u8);
        w.Write(dataBytes);
        for (var i = 0; i < samples; i++)
            w.Write((short)(Math.Sin(2 * Math.PI * frequency * i / sampleRate) * short.MaxValue * 0.25));

        w.Flush();
        return ms.ToArray();
    }

    /// <summary>A valid 4×4 orange PNG.</summary>
    public static readonly byte[] CoverPng =
    {
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x04, 0x08, 0x02, 0x00, 0x00, 0x00, 0x26, 0x93, 0x09,
        0x29, 0x00, 0x00, 0x00, 0x10, 0x49, 0x44, 0x41, 0x54, 0x78, 0xDA, 0x63, 0xF8, 0xDF, 0xC3, 0x00,
        0x47, 0x0C, 0xC4, 0x71, 0x00, 0x92, 0x22, 0x18, 0xB1, 0xA0, 0x9A, 0x11, 0xD4, 0x00, 0x00, 0x00,
        0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82,
    };
}
