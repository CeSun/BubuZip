using BubuUI;

namespace BubuZip.Desktop;

internal sealed class FileTypeIconCache(Func<string, bool, IRasterImage?> readIcon)
{
    private readonly Dictionary<string, Task<IRasterImage?>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _worker = new(1);

    internal static string CacheKey(string name, bool directory)
        => directory ? "<folder>" : Path.GetExtension(name).ToLowerInvariant();

    public Task<IRasterImage?> GetIconAsync(string name, bool isDirectory)
    {
        var key = CacheKey(name, isDirectory);
        lock (_cache)
        {
            if (_cache.TryGetValue(key, out var cached)) return cached;
            if (_cache.Count >= 256) return Task.FromResult<IRasterImage?>(null);
            return _cache[key] = Task.Run(async () =>
            {
                await _worker.WaitAsync().ConfigureAwait(false);
                try { return readIcon(key, isDirectory); }
                catch { return null; }
                finally { _worker.Release(); }
            });
        }
    }
}
