using System.Collections.Concurrent;

namespace EclipseArchive;

public partial class MainWindow
{
    private readonly ConcurrentQueue<ArchiveShellRequest> _openRequests = new();
    private bool _openingRequestedArchive;

    // OS callbacks may arrive before the window exists or from a native event callback.
    // Only enqueue here; navigation starts on the UI context during the next update.
    public void RequestOpenArchive(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        RequestShellAction(new(ArchiveShellAction.Open, [path]));
    }

    public void RequestShellAction(ArchiveShellRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.Action) || request.Paths.Count == 0 || request.Paths.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("请选择有效的文件或文件夹。", nameof(request));
        if (_disposed) return;
        // Compression combines the selection. Other actions operate on each archive in order.
        if (request.Action is ArchiveShellAction.Compress or ArchiveShellAction.CompressSevenZip or ArchiveShellAction.CompressZip)
            _openRequests.Enqueue(request with { Paths = request.Paths.ToArray() });
        else
            foreach (var path in request.Paths) _openRequests.Enqueue(new(request.Action, [path]));
        Invalidate();
    }

    private void OpenNextRequestedArchive()
    {
        if (_disposed || TaskIsRunning || _panel != DialogKind.None || _error.Length > 0 ||
            !_openRequests.TryDequeue(out var request)) return;
        OpenRequestedArchive(request);
    }

    private async void OpenRequestedArchive(ArchiveShellRequest request)
    {
        _openingRequestedArchive = true;
        try { await HandleShellRequestAsync(request); }
        catch (Exception error) { ReportError(error); }
        finally
        {
            _openingRequestedArchive = false;
            if (!_disposed) Invalidate();
        }
    }
}
