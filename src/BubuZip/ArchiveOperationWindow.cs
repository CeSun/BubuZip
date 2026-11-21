using BubuZip.Core;
using EclipseUI;

namespace BubuZip;

public partial class ArchiveOperationWindow
{
    public ArchiveOperationSpec Spec { get; set; } = null!;
    public IDesktopServices Desktop { get; set; } = null!;

    private readonly BackgroundOperations _operations = new();
    private readonly object _stateGate = new();
    private ArchiveOperationSpec? _operation;
    private Task? _execution;
    private ArchiveOperationProgress? _reportedProgress;
    private ArchiveOperationOutcome? _outcome;
    private ArchiveOperationProgress? _visibleProgress;
    private ArchiveOperationOutcome? _visibleOutcome;
    private bool _started, _completed, _cancelRequested;
    private string _folderError = "";

    public ArchiveOperationOutcome Result
    {
        get
        {
            lock (_stateGate)
                return _outcome ?? new ArchiveOperationOutcome(false, false, null);
        }
    }

    public bool TaskIsRunning => _started && !_completed && !IsDisposed;

    private string OperationTitle => _operation?.Title ?? Spec?.Title ?? "正在处理";
    private string Heading => _visibleOutcome switch
    {
        { Succeeded: true } => "操作成功",
        { Cancelled: true } => "操作已取消",
        { } => "操作失败",
        _ => OperationTitle,
    };
    private string Description => _visibleOutcome switch
    {
        { Succeeded: true } => _operation?.SuccessMessage ?? "操作完成。",
        { Cancelled: true } => "操作已取消。",
        { } => "操作未能完成。",
        _ => _operation?.Description ?? Spec?.Description ?? "",
    };
    private double ProgressValue => _visibleOutcome?.Succeeded == true ? 100 :
        _visibleProgress is { } progress ? Math.Clamp(progress.Percent, 0, 100) : 0;
    private bool ProgressIsIndeterminate => !_completed && _visibleProgress is null;
    private string ProgressText => _visibleOutcome?.Succeeded == true ? "100%" :
        _visibleOutcome?.Cancelled == true ? "" :
        _cancelRequested && !_completed ? "正在取消，请稍候…" :
        _visibleProgress is { } progress ? $"{progress.CompletedEntries} / {progress.TotalEntries}  ·  {progress.EntryPath}" :
        _completed ? "" : "正在准备…";
    private string ErrorText => _folderError.Length > 0 ? _folderError : _visibleOutcome?.Error ?? "";
    private bool CanOpenFolder => _visibleOutcome?.Succeeded == true && !string.IsNullOrWhiteSpace(_operation?.ResultFolder);
    private string OutputLocation => _visibleOutcome?.Succeeded == true && !string.IsNullOrWhiteSpace(_operation?.ResultPath)
        ? "输出位置：" + _operation.ResultPath : "";

    protected override void OnInitialized()
    {
        base.OnInitialized();
        ArgumentNullException.ThrowIfNull(Spec);
        ArgumentNullException.ThrowIfNull(Spec.Execute);
        ArgumentNullException.ThrowIfNull(Desktop);
        _operation = Spec;
        // Native hosts attach their UI context after initialization. Start on the first update.
        Invalidate();
    }

    protected override void OnUpdating()
    {
        base.OnUpdating();
        if (!_started && !_cancelRequested)
        {
            _started = true;
            var operation = _operation!;
            _execution = _operations.RunAsync(token => operation.Execute(new OperationProgress(this), token));
            _ = _execution.ContinueWith(static (completed, owner) =>
                ((ArchiveOperationWindow)owner!).RecordOutcome(completed), this,
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        lock (_stateGate)
        {
            _visibleProgress = _reportedProgress;
            _visibleOutcome = _outcome;
        }
        _completed = _visibleOutcome is not null;
    }

    private void ReportProgress(ArchiveOperationProgress progress)
    {
        lock (_stateGate)
        {
            if (_outcome is not null || IsDisposed) return;
            _reportedProgress = progress;
        }
        Invalidate();
    }

    private void RecordOutcome(Task completed)
    {
        ArchiveOperationOutcome outcome;
        try
        {
            completed.GetAwaiter().GetResult();
            // Successful completion wins even if cancellation arrived after the output was committed.
            outcome = new ArchiveOperationOutcome(true, false, null);
        }
        catch (OperationCanceledException) { outcome = new ArchiveOperationOutcome(false, true, null); }
        catch (Exception error) { outcome = new ArchiveOperationOutcome(false, false, FriendlyError(error)); }

        lock (_stateGate) _outcome ??= outcome;
        Invalidate();
    }

    private void CancelOrClose()
    {
        if (_completed)
        {
            ArchiveWindowClose.Request(this);
            return;
        }
        if (_cancelRequested) return;
        _cancelRequested = true;
        if (!_started)
        {
            lock (_stateGate) _outcome ??= new ArchiveOperationOutcome(false, true, null);
        }
        _operations.RequestStop();
        Invalidate();
    }

    private void OpenResultFolder()
    {
        if (!CanOpenFolder) return;
        try { Desktop.Open(_operation!.ResultFolder!); _folderError = ""; }
        catch (Exception error) { _folderError = "无法打开输出文件夹：" + FriendlyError(error); }
        Invalidate();
    }

    private static string FriendlyError(Exception error) => error is SharpCompress.Common.CryptographicException
        ? "密码不正确，或压缩包的加密数据已损坏。" : error.Message;

    protected override void OnDispose()
    {
        try
        {
            // This barrier and outcome collection never need another UI frame or a posted continuation.
            _operations.StopAsync().GetAwaiter().GetResult();
            if (_execution is not null) RecordOutcome(_execution);
            else
            {
                lock (_stateGate) _outcome ??= new ArchiveOperationOutcome(false, true, null);
            }
            _completed = true;
        }
        finally
        {
            _operations.Dispose();
            base.OnDispose();
        }
    }

    private sealed class OperationProgress(ArchiveOperationWindow owner) : IProgress<ArchiveOperationProgress>
    {
        public void Report(ArchiveOperationProgress value) => owner.ReportProgress(value);
    }
}
