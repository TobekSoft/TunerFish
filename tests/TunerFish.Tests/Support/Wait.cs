using System.Diagnostics;

namespace TunerFish.Tests.Support;

public static class Wait
{
    /// <summary>Long enough that a busy machine doesn't cause false failures.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Polls until <paramref name="condition"/> holds, calling <paramref name="pump"/> (e.g. the view
    /// model's Tick) before each check. On timeout fails the test, naming what it waited for and
    /// including <paramref name="state"/> so you can see where things got stuck.
    /// </summary>
    public static async Task UntilAsync(Func<bool> condition, string what, TimeSpan? timeout = null,
                                        Action? pump = null, Func<string>? state = null)
    {
        var limit = timeout ?? DefaultTimeout;
        var ct = TestContext.Current.CancellationToken;
        var sw = Stopwatch.StartNew();
        while (true)
        {
            pump?.Invoke();
            if (condition()) return;
            if (sw.Elapsed >= limit) break;
            await Task.Delay(50, ct);
        }
        Assert.Fail($"Timed out after {limit.TotalSeconds:0.#} s waiting for: {what}" +
                    (state is null ? "" : $"\n  state: {state()}"));
    }
}
