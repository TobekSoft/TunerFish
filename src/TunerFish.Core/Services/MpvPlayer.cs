using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace TunerFish.Services;

/// <summary>
/// Raw bindings to the libmpv client API (only the calls we use).
/// On Linux Mint / Ubuntu: sudo apt install libmpv2
/// </summary>
internal static class MpvNative
{
    public const string Lib = "mpv";
    private const string LibC = "libc";

    private static readonly object Gate = new();
    private static bool _resolverSet;

    private static readonly string[] Candidates =
        OperatingSystem.IsWindows() ? new[] { "libmpv-2.dll", "mpv-2.dll", "mpv-1.dll" } :
        OperatingSystem.IsMacOS() ? new[] { "libmpv.2.dylib", "libmpv.dylib", "/opt/homebrew/lib/libmpv.dylib", "/usr/local/lib/libmpv.dylib" } :
        new[] { "libmpv.so.2", "libmpv.so.1", "libmpv.so" };

    public static void EnsureResolver()
    {
        lock (Gate)
        {
            if (_resolverSet) return;
            NativeLibrary.SetDllImportResolver(typeof(MpvNative).Assembly, Resolve);
            _resolverSet = true;
        }
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name == Lib)
        {
            foreach (var candidate in Candidates)
                if (NativeLibrary.TryLoad(candidate, assembly, searchPath, out var handle))
                    return handle;
        }
        else if (name == LibC && OperatingSystem.IsLinux())
        {
            if (NativeLibrary.TryLoad("libc.so.6", out var handle)) return handle;
        }
        return IntPtr.Zero;
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr mpv_create();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_initialize(IntPtr ctx);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_terminate_destroy(IntPtr ctx);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_set_option_string(IntPtr ctx, byte[] name, byte[] value);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_set_property_string(IntPtr ctx, byte[] name, byte[] value);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr mpv_get_property_string(IntPtr ctx, byte[] name);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_command(IntPtr ctx, IntPtr[] args);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_free(IntPtr data);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr mpv_wait_event(IntPtr ctx, double timeout);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_wakeup(IntPtr ctx);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr mpv_error_string(int error);

    // mpv refuses to start (or misparses numbers) unless LC_NUMERIC is "C".
    [DllImport(LibC, EntryPoint = "setlocale", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr setlocale(int category, string locale);

    public const int LC_NUMERIC = 1; // glibc
    public const int MPV_EVENT_SHUTDOWN = 1;
    public const int MPV_EVENT_END_FILE = 7;
    public const int MPV_EVENT_IDLE = 11;
    public const int MPV_END_FILE_REASON_ERROR = 4;
}

/// <summary>
/// Audio-only playback through libmpv. Gapless between tracks of the same format,
/// plays anything ffmpeg can decode, and streams straight from Jellyfin URLs.
/// All members are safe to call from the UI thread; state is read by polling.
/// </summary>
public sealed class MpvPlayer : IDisposable
{
    private IntPtr _handle;
    private readonly Thread _eventThread;
    private volatile bool _disposing;
    private readonly ManualResetEventSlim _idle = new();

    /// <param name="clientName">Name the sound mixer shows for our audio stream.</param>
    public MpvPlayer(bool silentForTests = false, string clientName = "TunerFish")
    {
        MpvNative.EnsureResolver();
        if (OperatingSystem.IsLinux())
        {
            try { MpvNative.setlocale(MpvNative.LC_NUMERIC, "C"); } catch { /* not fatal */ }
        }

        _handle = MpvNative.mpv_create();
        if (_handle == IntPtr.Zero) throw new InvalidOperationException("mpv_create failed.");

        SetOption("vo", "null");               // never open a video window
        SetOption("vid", "no");                // ignore embedded cover art streams
        SetOption("audio-display", "no");
        SetOption("ytdl", "no");
        SetOption("terminal", "no");
        SetOption("input-default-bindings", "no");
        SetOption("idle", "yes");              // stay alive when the playlist ends
        SetOption("keep-open", "no");
        SetOption("gapless-audio", "weak");    // gapless when consecutive tracks share a format
        SetOption("prefetch-playlist", "yes"); // start fetching the next track early
        SetOption("audio-client-name", clientName);
        if (silentForTests) SetOption("ao", "null");

        var rc = MpvNative.mpv_initialize(_handle);
        if (rc < 0) throw new InvalidOperationException("mpv_initialize failed: " + ErrorString(rc));

        _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "mpv events" };
        _eventThread.Start();

        // mpv finishes starting on its own thread. Until it's idle, a playlist-pos set now is lost: it starts
        // at the first entry instead (seen when the saved queue was put back straight after start-up).
        // idle-active reads yes before then, so wait for the event.
        _idle.Wait(TimeSpan.FromSeconds(2));
    }

    // mpv queues events for every client; drain them so the queue never overflows.
    private void EventLoop()
    {
        while (!_disposing)
        {
            var ev = MpvNative.mpv_wait_event(_handle, 1.0);
            if (ev == IntPtr.Zero) continue;
            var id = Marshal.ReadInt32(ev);
            if (id == MpvNative.MPV_EVENT_SHUTDOWN) break;
            if (id == MpvNative.MPV_EVENT_IDLE) _idle.Set();
            if (id == MpvNative.MPV_EVENT_END_FILE)
            {
                // mpv_event: event_id, error, reply_userdata (8 bytes), then data -> mpv_event_end_file { reason, error, ... }.
                var endFile = Marshal.ReadIntPtr(ev, 16);
                if (endFile != IntPtr.Zero && Marshal.ReadInt32(endFile) == MpvNative.MPV_END_FILE_REASON_ERROR)
                    PlaybackFailed?.Invoke(ErrorString(Marshal.ReadInt32(endFile, 4)));
            }
        }
    }

    /// <summary>A track couldn't be played (mpv's reason, e.g. "loading failed"). Raised on mpv's event thread.</summary>
    public event Action<string>? PlaybackFailed;

    private static byte[] U8(string s) => System.Text.Encoding.UTF8.GetBytes(s + "\0");

    private static string ErrorString(int rc) =>
        Marshal.PtrToStringUTF8(MpvNative.mpv_error_string(rc)) ?? rc.ToString(CultureInfo.InvariantCulture);

    private void SetOption(string name, string value) =>
        MpvNative.mpv_set_option_string(_handle, U8(name), U8(value));

    private int SetProperty(string name, string value) =>
        _handle == IntPtr.Zero ? -1 : MpvNative.mpv_set_property_string(_handle, U8(name), U8(value));

    public string? GetString(string name)
    {
        if (_handle == IntPtr.Zero) return null;
        var p = MpvNative.mpv_get_property_string(_handle, U8(name));
        if (p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUTF8(p); }
        finally { MpvNative.mpv_free(p); }
    }

    public double? GetDouble(string name) =>
        double.TryParse(GetString(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    public int? GetInt(string name) =>
        int.TryParse(GetString(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;

    public bool? GetFlag(string name) => GetString(name) switch
    {
        "yes" => true,
        "no" => false,
        _ => null
    };

    private int Command(params string[] args)
    {
        if (_handle == IntPtr.Zero) return -1;
        var ptrs = new IntPtr[args.Length + 1];
        try
        {
            for (var i = 0; i < args.Length; i++) ptrs[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
            ptrs[args.Length] = IntPtr.Zero;
            return MpvNative.mpv_command(_handle, ptrs);
        }
        finally
        {
            foreach (var p in ptrs)
                if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p);
        }
    }

    // ---- State ----
    public int PlaylistPosition => GetInt("playlist-pos") ?? -1;
    public double? TimePosition => GetDouble("time-pos");
    public double? Duration => GetDouble("duration");
    public bool IsPaused => GetFlag("pause") ?? false;
    public bool IsIdle => GetFlag("idle-active") ?? true;

    /// <summary>libmpv's own version string, e.g. "mpv 0.37.0"; null if it can't be read.</summary>
    public string? MpvVersion => GetString("mpv-version");

    // ---- Control ----

    /// <summary>
    /// Replaces the queue with these URLs and starts playing at startIndex (paused if asked).
    /// A negative startIndex just loads them, playing nothing.
    /// </summary>
    public void LoadPlaylist(IReadOnlyList<string> urls, int startIndex, bool paused = false)
    {
        if (urls.Count == 0) return;
        Command("stop");
        Command("playlist-clear");
        foreach (var url in urls) Command("loadfile", url, "append");
        if (startIndex < 0) return;
        SetProperty("pause", paused ? "yes" : "no");
        SetProperty("playlist-pos", Math.Min(startIndex, urls.Count - 1).ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Adds to the end of the playlist. Never starts playing, even when nothing is.</summary>
    public void Append(string url) => Command("loadfile", url, "append");

    /// <summary>Loads an entry paused, ready to play: shown as the current track, but silent.</summary>
    public void Cue(int index)
    {
        SetProperty("pause", "yes");
        SetProperty("playlist-pos", index.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Removes one entry. Removing the one that's playing moves on to the next.</summary>
    public void RemoveAt(int index) => Command("playlist-remove", index.ToString(CultureInfo.InvariantCulture));

    /// <summary>Moves an entry so it ends up at <paramref name="newIndex"/> (List.Move semantics).</summary>
    public void Move(int oldIndex, int newIndex)
    {
        if (oldIndex == newIndex) return;
        // mpv inserts before the entry currently at index2, so moving down needs one more.
        var target = newIndex > oldIndex ? newIndex + 1 : newIndex;
        Command("playlist-move", oldIndex.ToString(CultureInfo.InvariantCulture), target.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Stops and empties the playlist.</summary>
    public void Clear()
    {
        Command("stop");
        Command("playlist-clear");
    }

    /// <summary>Jumps to an entry and plays it.</summary>
    public void PlayIndex(int index)
    {
        SetProperty("pause", "no");
        SetProperty("playlist-pos", index.ToString(CultureInfo.InvariantCulture));
    }

    public int PlaylistCount => GetInt("playlist-count") ?? 0;
    public string? PlaylistFile(int index) => GetString($"playlist/{index}/filename");

    public void TogglePause() => Command("cycle", "pause");
    public void Next() => Command("playlist-next", "weak");
    public void Previous() => Command("playlist-prev", "weak");
    public void Stop() => Command("stop");

    public void Seek(double seconds) =>
        Command("seek", Math.Max(0, seconds).ToString("0.###", CultureInfo.InvariantCulture), "absolute");

    public void SetVolume(double volume0To100) =>
        SetProperty("volume", Math.Clamp(volume0To100, 0, 100).ToString("0.#", CultureInfo.InvariantCulture));

    /// <summary>
    /// Bass and treble (<see cref="Tone"/>), heard at once. mpv swaps the filter in place: no gap, and
    /// no click (checked on a 40 Hz tone, 2026-10-25).
    /// </summary>
    public void SetTone(int bassDb, int trebleDb) => SetProperty("af", Tone.Filter(bassDb, trebleDb));

    /// <summary>mpv's audio filters as it reports them (for tests).</summary>
    public string? AudioFilters => GetString("af");

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        _disposing = true;
        MpvNative.mpv_wakeup(_handle);
        _eventThread.Join(TimeSpan.FromSeconds(2));
        MpvNative.mpv_terminate_destroy(_handle);
        _handle = IntPtr.Zero;
    }
}
