using BubuZip.Core;

namespace BubuZip;

public partial class ArchiveOperationView
{
    private sealed record PendingConflict(ArchiveExtractionConflict Conflict,
        TaskCompletionSource<ArchiveConflictDecision> Completion);
    private PendingConflict? _pendingConflict, _visibleConflict;
    private ArchiveExtractionSummary? _extractionSummary, _visibleSummary;
    private bool _applyConflictToAll;
    private bool HasConflict => _visibleConflict is not null && !_completed;
    private bool CanChooseConflict => HasConflict && !_visibleConflict!.Completion.Task.IsCompleted && !_cancelRequested;
    private bool CanReplaceConflict => CanChooseConflict && _visibleConflict!.Conflict.CanReplace;
    private string ConflictHeading => _visibleConflict?.Conflict.CanReplace == true ? "发现同名文件" : "文件与文件夹重名";
    private string ConflictPath => _visibleConflict?.Conflict.DestinationPath ?? "";
    private string DisplayConflictPath => FitLine(ConflictPath, 13, keepEnd: true);
    private string ConflictExistingTitle => _visibleConflict?.Conflict.ExistingIsDirectory == true ? "目标位置的文件夹" : "目标位置的文件";
    private string ConflictIncomingTitle => _visibleConflict?.Conflict.IncomingIsDirectory == true ? "压缩包中的文件夹" : "压缩包中的文件";
    private string ConflictExistingDetails => _visibleConflict is { Conflict: var conflict }
        ? $"{(conflict.ExistingIsDirectory ? "文件夹" : Size(conflict.ExistingSize))} · {conflict.ExistingModified.ToLocalTime():yyyy-MM-dd HH:mm:ss}" : "";
    private string ConflictIncomingDetails => _visibleConflict is { Conflict: var conflict }
        ? $"{(conflict.IncomingIsDirectory ? "文件夹" : Size(conflict.IncomingSize))} · {conflict.IncomingModified.ToLocalTime():yyyy-MM-dd HH:mm:ss}" : "";
    private string ConflictExplanation => _visibleConflict?.Conflict.CanReplace == true
        ? "替换：覆盖现有文件。跳过：保留现有文件。保留两者：为解压文件加上序号。"
        : "文件与文件夹不能直接替换。可以跳过此项，或保留两者并自动改名。";

    private async Task<ArchiveConflictDecision> ResolveConflictAsync(ArchiveExtractionConflict conflict, CancellationToken token)
    {
        var pending = new PendingConflict(conflict, new(TaskCreationOptions.RunContinuationsAsynchronously));
        lock (_stateGate) { _pendingConflict = pending; _elapsed.Stop(); }
        Invalidate();
        try { return await pending.Completion.Task.WaitAsync(token).ConfigureAwait(false); }
        finally
        {
            lock (_stateGate)
            {
                if (ReferenceEquals(_pendingConflict, pending)) _pendingConflict = null;
                if (!_paused && !_cancelRequested && _outcome is null) _elapsed.Start();
            }
            Invalidate();
        }
    }

    private void ChooseConflict(ArchiveConflictAction action)
    {
        if (!CanChooseConflict || action == ArchiveConflictAction.Replace && !CanReplaceConflict) return;
        _visibleConflict!.Completion.TrySetResult(new(action, _applyConflictToAll));
        Invalidate();
    }

    private void ReportExtractionSummary(ArchiveExtractionSummary summary)
    {
        lock (_stateGate) _extractionSummary = summary;
        Invalidate();
    }

    private string ExtractionCompletionText => _visibleSummary is { } summary && summary != new ArchiveExtractionSummary(0, 0, 0)
        ? $"解压完成：替换 {summary.Replaced} 项，跳过 {summary.Skipped} 项，另存 {summary.Renamed} 项。"
        : _operation?.SuccessMessage ?? "操作完成。";
}
