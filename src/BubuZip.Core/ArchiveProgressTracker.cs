using System.Diagnostics;

namespace BubuZip.Core;

internal sealed class ArchiveProgressTracker(IProgress<ArchiveOperationProgress>? sink, int totalEntries,
    long totalBytes, CancellationToken token)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _positions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _completed = new(StringComparer.Ordinal);
    private long _processed, _lastReport;
    private long? _compressed;
    private string _entry = "";

    public void Checkpoint()
    {
        token.ThrowIfCancellationRequested();
        (sink as IArchiveOperationControl)?.Checkpoint(token);
    }

    public void Advance(string entry, long position)
    {
        Checkpoint();
        lock (_gate)
        {
            var previous = _positions.GetValueOrDefault(entry);
            if (position > previous) { _processed += position - previous; _positions[entry] = position; }
            var changed = _entry != entry || (previous == 0 && position > 0);
            _entry = entry;
            Report(changed);
        }
    }

    public void Packed(long length)
    {
        Checkpoint();
        lock (_gate) { _compressed = length; Report(false); }
    }

    public void Complete(string entry)
    {
        Checkpoint();
        lock (_gate) { _completed.Add(entry); _entry = entry; Report(true); }
    }

    public void Report(bool force = true)
    {
        lock (_gate)
        {
            var now = Stopwatch.GetTimestamp();
            if (!force && _lastReport != 0 && Stopwatch.GetElapsedTime(_lastReport, now).TotalMilliseconds < 100) return;
            _lastReport = now;
            sink?.Report(new(_entry, _completed.Count, totalEntries)
            { ProcessedBytes = _processed, TotalBytes = totalBytes, CompressedBytes = _compressed });
        }
    }
}
