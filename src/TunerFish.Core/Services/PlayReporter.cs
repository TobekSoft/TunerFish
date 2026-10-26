namespace TunerFish.Services;

/// <summary>
/// Tells Jellyfin what's playing (start, progress, stop), so it keeps the user's play counts and last
/// played dates: the history the "played" views are built from. Fed the player's state on every
/// <see cref="ViewModels.MainViewModel.Tick"/>; works out the events from the changes.
/// <para>
/// Reports go out one at a time, in order. A report that fails is dropped: the desktop only plays
/// while it can reach the server, so unlike the phone it keeps no plays to send later.
/// </para>
/// </summary>
public sealed class PlayReporter
{
    /// <summary>How often a playing track's position is sent, besides pauses, resumes and seeks.</summary>
    public static readonly TimeSpan ProgressEvery = TimeSpan.FromSeconds(10);

    /// <summary>A jump bigger than this between two ticks is a seek.</summary>
    private const double SeekSeconds = 3;

    private readonly JellyfinClient _jellyfin;
    private readonly Func<DateTime> _now;
    private readonly Action<string> _log;
    private Task _sending = Task.CompletedTask;

    private string? _trackId, _session;
    private double _time;
    private bool _paused;
    private DateTime _lastSent;

    /// <param name="now">The clock, for tests.</param>
    /// <param name="log">Told of a report the server didn't take, for the app's log.</param>
    public PlayReporter(JellyfinClient jellyfin, Func<DateTime>? now = null, Action<string>? log = null)
    {
        _jellyfin = jellyfin;
        _now = now ?? (() => DateTime.UtcNow);
        _log = log ?? (_ => { });
    }

    /// <summary>Completes when every report so far has been sent (or failed).</summary>
    public Task Idle => _sending;

    /// <summary>
    /// The player now: the track playing (null if none), how far in, and whether paused. A track is
    /// started once it actually plays, so a queue restored paused reports nothing until play.
    /// </summary>
    public void Update(string? trackId, double timeSeconds, bool paused)
    {
        if (trackId != _trackId)
        {
            if (_session != null) Stop(_trackId!, _session, _time); // where the old track last was
            _trackId = trackId;
            _session = null;
        }
        else if (_session != null)
        {
            var seeked = Math.Abs(timeSeconds - _time) > SeekSeconds;
            if (paused != _paused || seeked || _now() - _lastSent >= ProgressEvery)
                Progress(trackId!, _session, timeSeconds, paused);
        }

        if (trackId != null && _session is null && !paused) Start(trackId, timeSeconds);
        _time = timeSeconds;
        _paused = paused;
    }

    /// <summary>The app is closing or signing out: the playing track stops where it is.</summary>
    public Task CloseAsync()
    {
        Update(null, 0, paused: true);
        return _sending;
    }

    private void Start(string trackId, double time)
    {
        var session = _session = Guid.NewGuid().ToString("N");
        _lastSent = _now();
        Send("start", trackId, () => _jellyfin.ReportStartAsync(trackId, time, paused: false, session));
    }

    private void Progress(string trackId, string session, double time, bool paused)
    {
        _lastSent = _now();
        Send("progress", trackId, () => _jellyfin.ReportProgressAsync(trackId, time, paused, session));
    }

    private void Stop(string trackId, string session, double time) =>
        Send("stop", trackId, () => _jellyfin.ReportStoppedAsync(trackId, time, session));

    private void Send(string kind, string trackId, Func<Task> report)
    {
        if (_jellyfin.Token is null) return; // signed out
        _sending = After(_sending, kind, trackId, report);
    }

    private async Task After(Task before, string kind, string trackId, Func<Task> report)
    {
        await before.ConfigureAwait(false);
        try
        {
            await report().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Best effort: Jellyfin's history misses this one.
            _log($"Server didn't take a play {kind} report (track {trackId}), dropped: {ex.Message}");
        }
    }
}
