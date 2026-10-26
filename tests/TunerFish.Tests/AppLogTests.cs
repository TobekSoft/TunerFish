using TunerFish.Services;

namespace TunerFish.Tests;

/// <summary>
/// Mirrors the Android app's AppLogTest case for case, as far as the desktop shares it: the shared text
/// and the stored areas are the phone's (it keeps the log in a table); here Serilog writes the files.
/// </summary>
public sealed class AppLogTests
{
    [Fact]
    public void TheToken_NeverGetsIn_FromAStreamUrl_AHeader_OrJson()
    {
        Assert.Equal("Source error: http://192.168.1.33:8096/Audio/abc/stream?static=true&api_key=…",
                     AppLog.Redact("Source error: http://192.168.1.33:8096/Audio/abc/stream?static=true&api_key=0123456789abcdef"));
        Assert.Equal("…?api_key=…&tag=x", AppLog.Redact("…?api_key=secret&tag=x"));
        Assert.Equal("MediaBrowser Client=\"TunerFish\", Token=\"…\"", AppLog.Redact("MediaBrowser Client=\"TunerFish\", Token=\"secret\""));
        Assert.Equal("""{"AccessToken":"…","User":{}}""", AppLog.Redact("""{"AccessToken": "secret","User":{}}"""));
        Assert.Equal("""{"Username":"me","Pw":"…"}""", AppLog.Redact("""{"Username":"me","Pw":"hunter2"}"""));
    }

    [Fact]
    public void OrdinaryText_IsLeftAlone()
    {
        const string text = "Can't reach http://100.64.0.5:8096: timeout";
        Assert.Equal(text, AppLog.Redact(text));
    }

    [Fact]
    public void LongMessages_AreCut()
    {
        Assert.Equal(AppLog.MaxMessage, AppLog.Clean(new string('x', AppLog.MaxMessage + 500)).Length);
    }

    [Fact]
    public void ACrash_KeepsTheErrorAndWhereItHappened_WithoutSecrets()
    {
        Exception error;
        try { throw new InvalidOperationException("bad url ?api_key=secret"); }
        catch (Exception e) { error = e; }

        var text = AppLog.Crash(error);

        Assert.StartsWith("System.InvalidOperationException: bad url ?api_key=…", text);
        Assert.Contains("AppLogTests", text); // the stack trace
        Assert.DoesNotContain("secret", text);
    }
}
