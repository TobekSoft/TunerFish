using System.Text.RegularExpressions;

namespace TunerFish.Services;

/// <summary>What part of the app a log entry is about. Written as its name (a word).</summary>
public enum LogArea { App, Server, Playback, Plays, Marks, Crash }

/// <summary>
/// The app's log: what helps with a bug report (connections, errors, crashes), kept in files for the
/// user to read and pass on by hand. Never a secret in it. The desktop writes it (Serilog, daily files);
/// this is the part both apps share, a port of the Android app's <c>AppLog.kt</c> (tests in <c>AppLogTests</c>).
/// </summary>
public static partial class AppLog
{
    /// <summary>The longest message kept (a crash's stack trace is cut there).</summary>
    public const int MaxMessage = 8000;

    [GeneratedRegex("""(api_key=)[^&\s"']+""", RegexOptions.IgnoreCase)]
    private static partial Regex ApiKey();

    [GeneratedRegex("""(Token=")[^"]*(")""")]
    private static partial Regex Token();

    [GeneratedRegex(@"""(AccessToken)""\s*:\s*""[^""]*""")]
    private static partial Regex AccessToken();

    [GeneratedRegex(@"""(Pw)""\s*:\s*""[^""]*""")]
    private static partial Regex Password();

    /// <summary><paramref name="text"/> without the session token or a password, wherever they turn up (stream URLs, headers, JSON).</summary>
    public static string Redact(string text)
    {
        text = ApiKey().Replace(text, "$1…");
        text = Token().Replace(text, "$1…$2");
        text = AccessToken().Replace(text, "\"$1\":\"…\"");
        return Password().Replace(text, "\"$1\":\"…\"");
    }

    /// <summary>A message as it's written: no secrets, and not too long.</summary>
    public static string Clean(string message)
    {
        var text = Redact(message);
        return text.Length <= MaxMessage ? text : text[..MaxMessage];
    }

    /// <summary>A crash: the error and where it happened.</summary>
    public static string Crash(Exception error) => Clean(error.ToString());
}
