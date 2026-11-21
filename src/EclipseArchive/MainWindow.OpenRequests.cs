using System.Collections.Concurrent;

namespace EclipseArchive;

public partial class MainWindow
{
    public Func<string, ArchiveShellRequest?>? ShellRequestDecoder { get; set; }

    private sealed record OpenRequest(ArchiveShellRequest Request, bool NewWindow);
    private readonly ConcurrentQueue<OpenRequest> _openRequests = new();
    private volatile bool _acceptingInitialRequests = true;
    private int _initialArchiveQueued;
    private bool _openingRequestedArchive;

    // The macOS host calls this while its native window is still hidden. Only
    // decode/reroute here; operation work begins under the host's UI context.
    public void PrepareInitialRequests()
    {
        var pending = new List<OpenRequest>();
        while (_openRequests.TryDequeue(out var item))
        {
            var request = item.Request;
            try
            {
                if (request.Action == ArchiveShellAction.Open && ShellRequestDecoder?.Invoke(request.Paths[0]) is { } decoded)
                    request = decoded;
                if (request.Action != ArchiveShellAction.Open) OperationOnly = true;
                pending.Add(item with { Request = request });
            }
            catch (Exception error)
            {
                OperationOnly = true;
                ReportError(error);
            }
        }
        foreach (var item in pending)
        {
            if (item.Request.Action is ArchiveShellAction.Compress or ArchiveShellAction.CompressSevenZip or ArchiveShellAction.CompressZip)
                _openRequests.Enqueue(item);
            else
                foreach (var path in item.Request.Paths)
                    _openRequests.Enqueue(item with { Request = new(item.Request.Action, [path]) });
        }
    }

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
            _openRequests.Enqueue(new(request with { Paths = request.Paths.ToArray() }, false));
        else
            foreach (var path in request.Paths)
            {
                var newWindow = request.Action == ArchiveShellAction.Open &&
                    (Interlocked.Exchange(ref _initialArchiveQueued, 1) != 0 || !_acceptingInitialRequests);
                _openRequests.Enqueue(new(new(request.Action, [path]), newWindow));
            }
        Invalidate();
    }

    private void OpenNextRequestedArchive()
    {
        if (_disposed || TaskIsRunning || _panel != DialogKind.None || _error.Length > 0 ||
            !_openRequests.TryDequeue(out var request)) return;
        OpenRequestedArchive(request);
    }

    private async void OpenRequestedArchive(OpenRequest request)
    {
        _openingRequestedArchive = true;
        try { await HandleShellRequestAsync(request.Request, request.NewWindow); }
        catch (Exception error) { ReportError(error); }
        finally
        {
            _openingRequestedArchive = false;
            if (!_disposed) Invalidate();
        }
    }
}
