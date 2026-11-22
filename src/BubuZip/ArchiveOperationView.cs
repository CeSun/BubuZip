using BubuZip.Core;
using EclipseUI;
using System.Diagnostics;
using EclipseUI.Elements;

namespace BubuZip;

public partial class ArchiveOperationView
{
    [UiParameter] public ArchiveOperationSpec Spec { get; set; } = null!;
    [UiParameter] public IDesktopServices Desktop { get; set; } = null!;
    [UiParameter] public Action BackgroundRequested { get; set; } = () => { };
    [UiParameter] public bool CanBackground { get; set; }

    [UiParameter] public Action<ArchiveOperationOutcome> CloseRequested { get; set; } = _ => { };
    [UiParameter] public Action<string> TitleChanged { get; set; } = _ => { };

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
    private readonly ManualResetEventSlim _resume = new(true);
    private readonly Stopwatch _elapsed = new();
    private Timer? _ticker;
    private bool _paused;
    private CancellationToken _operationToken;

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
    public string Heading => HasConflict ? ConflictHeading : _visibleOutcome switch
    {
        { Succeeded: true } when _visibleSummary?.Skipped > 0 => "解压完成（有跳过）",
        { Succeeded: true } => "操作成功",
        { Cancelled: true } => "操作已取消",
        { } => "操作失败",
        _ => $"{(_visibleProgress is null ? "" : $"{ProgressValue:0}% ")}{OperationTitle} {_operation?.DisplayPath ?? _operation?.ResultPath ?? Description}",
    };
    private TextBlock EntryLabel = null!, OutputLabel = null!, ConflictPathLabel = null!;
    private string DisplayHeading => FitLine(Heading, 20);
    private string DisplayEntry => FitLine(CurrentEntry, 13, keepEnd: true);
    private string DisplayOutput => FitLine(OutputLocation, 12, keepEnd: true);
    private string FitLine(string text, double fontSize, bool keepEnd = false)
    {
        var width = Math.Max(0, (FinalRect.Width > 0 ? FinalRect.Width : 680) - 28);
        if (TextMeasurer.Current.Measure(text, fontSize).Width <= width) return text;
        string Shorten(int length) => keepEnd ? "…" + text[^length..] : text[..length] + "…";
        var low = 0;
        var high = text.Length;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (TextMeasurer.Current.Measure(Shorten(middle), fontSize).Width <= width) low = middle;
            else high = middle - 1;
        }
        if (low > 0 && (keepEnd ? char.IsLowSurrogate(text[text.Length - low]) : char.IsHighSurrogate(text[low - 1]))) low--;
        return Shorten(low);
    }
    private string Description => _visibleOutcome switch
    {
        { Succeeded: true } => ExtractionCompletionText,
        { Cancelled: true } => "操作已取消。",
        { } => "操作未能完成。",
        _ => _operation?.Description ?? Spec?.Description ?? "",
    };
    private double ProgressValue => _visibleOutcome?.Succeeded == true ? 100 :
        _visibleProgress is { } progress ? Math.Clamp(progress.Percent, 0, _completed ? 100 : 99) : 0;
    private bool ProgressIsIndeterminate => !_completed && _visibleProgress is null;
    private string ProgressText => _visibleOutcome?.Succeeded == true ? Description :
        _visibleOutcome is not null ? Description :
        _cancelRequested && !_completed ? "正在取消，请稍候…" :
        _paused ? "已暂停" : _visibleProgress is null ? "正在准备…" :
        _visibleProgress.Percent >= 100 ? "正在完成…" : _operation?.IsExtraction == true ? "正在提取" : "正在添加";
    private string CurrentEntry => string.IsNullOrEmpty(_visibleProgress?.EntryPath) ? (_completed ? "" : "正在准备文件列表…") : _visibleProgress.EntryPath;
    private TimeSpan Elapsed { get { lock (_stateGate) return _elapsed.Elapsed; } }
    private static string Duration(TimeSpan value) => $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}";
    private string ElapsedText => Duration(Elapsed);
    private double BytesPerSecond => _visibleProgress?.ProcessedBytes is > 0 && Elapsed.TotalSeconds >= 0.25
        ? _visibleProgress.ProcessedBytes.Value / Elapsed.TotalSeconds : 0;
    private string RemainingText => _visibleOutcome?.Succeeded == true ? "0:00:00" :
        _completed || _paused || _cancelRequested || BytesPerSecond <= 0 || _visibleProgress?.TotalBytes is not > 0 ? "—" :
        Duration(TimeSpan.FromSeconds(Math.Min(TimeSpan.MaxValue.TotalSeconds / 2,
            Math.Max(0, _visibleProgress.TotalBytes.Value - (_visibleProgress.ProcessedBytes ?? 0)) / BytesPerSecond)));
    private string SpeedText => _paused || BytesPerSecond <= 0 ? "—" : Size((long)BytesPerSecond) + "/s";
    private string FileCountText => _visibleProgress is { } progress ? $"{progress.CompletedEntries} / {progress.TotalEntries}" : "—";
    private string RatioText
    {
        get
        {
            var denominator = _operation?.IsExtraction == true ? _visibleProgress?.TotalBytes : _visibleProgress?.ProcessedBytes;
            return denominator is > 0 && _visibleProgress?.CompressedBytes is { } packed
                ? $"{100d * packed / denominator.Value:0} %" : "—";
        }
    }
    private static string Size(long? bytes)
    {
        if (bytes is null) return "—";
        double value = bytes.Value;
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var index = 0;
        while (value >= 1024 && index < units.Length - 1) { value /= 1024; index++; }
        return index == 0 ? $"{value:0} {units[index]}" : $"{value:0.00} {units[index]}";
    }
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
            lock (_stateGate) { if (!_paused) _elapsed.Start(); }
            _ticker = new Timer(_ => { if (!IsDisposed) Invalidate(); }, null, 250, 250);
            _execution = _operations.RunAsync(token =>
            {
                _operationToken = token;
                return operation.Execute(new OperationProgress(this), token);
            });
            _ = _execution.ContinueWith(static (completed, owner) =>
                ((ArchiveOperationView)owner!).RecordOutcome(completed), this,
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        lock (_stateGate)
        {
            _visibleProgress = _reportedProgress;
            _visibleOutcome = _outcome;
            if (!ReferenceEquals(_visibleConflict, _pendingConflict)) _applyConflictToAll = false;
            _visibleConflict = _pendingConflict;
            _visibleSummary = _extractionSummary;
        }
        _completed = _visibleOutcome is not null;
    }

    protected override void OnUpdated()
    {
        base.OnUpdated();
        TitleChanged?.Invoke(Heading);
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

        lock (_stateGate) { _outcome ??= outcome; _elapsed.Stop(); }
        _ticker?.Dispose();
        Invalidate();
    }

    private void CancelOrClose()
    {
        if (_completed)
        {
            CloseRequested?.Invoke(Result);
            return;
        }
        if (_cancelRequested) return;
        _cancelRequested = true;
        if (!_started)
        {
            lock (_stateGate) _outcome ??= new ArchiveOperationOutcome(false, true, null);
        }
        _operations.RequestStop();
        _resume.Set();
        Invalidate();
    }

    private void TogglePause()
    {
        if (_completed || _cancelRequested) return;
        _paused = !_paused;
        lock (_stateGate)
        {
            if (_paused) { _resume.Reset(); _elapsed.Stop(); }
            else { _elapsed.Start(); _resume.Set(); }
        }
        Invalidate();
    }

    private void MoveToBackground()
    {
        if (!_completed) BackgroundRequested?.Invoke();
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
            _ticker?.Dispose();
            _resume.Dispose();
            _operations.Dispose();
            base.OnDispose();
        }
    }

    private sealed class OperationProgress(ArchiveOperationView owner) : IProgress<ArchiveOperationProgress>, IArchiveOperationControl, IArchiveExtractionInteraction
    {
        public Task<ArchiveConflictDecision> ResolveConflictAsync(ArchiveExtractionConflict conflict, CancellationToken cancellationToken) =>
            owner.ResolveConflictAsync(conflict, cancellationToken);
        public void ReportExtractionSummary(ArchiveExtractionSummary summary) => owner.ReportExtractionSummary(summary);
        public void Checkpoint(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            owner._resume.Wait(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        public void Report(ArchiveOperationProgress value)
        {
            Checkpoint(owner._operationToken);
            owner.ReportProgress(value);
        }
    }
}
