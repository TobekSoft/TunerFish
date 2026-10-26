using System.Collections.Concurrent;

namespace TunerFish.Services;

/// <summary>
/// Downloads album covers once and keeps them on disk (~/.cache/TunerFish/covers, <see cref="AppPaths"/>).
/// The cache key includes Jellyfin's image tag, so if you change a cover on the
/// server the new one is fetched automatically.
/// </summary>
public sealed class CoverCache
{
    private readonly JellyfinClient _jellyfin;
    private readonly string _dir;
    private readonly ConcurrentDictionary<string, Task<string?>> _inFlight = new();
    private readonly SemaphoreSlim _downloads = new(6);

    public CoverCache(JellyfinClient jellyfin, string? directory = null)
    {
        _jellyfin = jellyfin;
        _dir = directory ?? AppPaths.Current.CoverDir;
        Directory.CreateDirectory(_dir);
    }

    /// <summary>Path to a cached image file, or null if the item has no cover.</summary>
    public Task<string?> GetFileAsync(string itemId, string? imageTag, int size)
    {
        if (string.IsNullOrEmpty(imageTag)) return Task.FromResult<string?>(null);
        var key = $"{itemId}_{Sanitize(imageTag)}_{size}";
        return _inFlight.GetOrAdd(key, k => FetchAsync(itemId, imageTag, size, k));
    }

    private async Task<string?> FetchAsync(string itemId, string imageTag, int size, string key)
    {
        var file = Path.Combine(_dir, key + ".img");
        if (File.Exists(file)) return file;

        await _downloads.WaitAsync().ConfigureAwait(false);
        try
        {
            var bytes = await _jellyfin.GetBytesAsync(_jellyfin.ImageUrl(itemId, imageTag, size), CancellationToken.None)
                                       .ConfigureAwait(false);
            if (bytes is null || bytes.Length == 0) return null;
            var tmp = file + ".part";
            await File.WriteAllBytesAsync(tmp, bytes).ConfigureAwait(false);
            File.Move(tmp, file, overwrite: true);
            return file;
        }
        catch
        {
            _inFlight.TryRemove(key, out _); // allow a retry later
            return null;
        }
        finally
        {
            _downloads.Release();
        }
    }

    private static string Sanitize(string s) =>
        string.Concat(s.Where(c => char.IsLetterOrDigit(c) || c == '-'));
}
