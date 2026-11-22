using BubuZip.Core;
using Path = System.IO.Path;

namespace BubuZip;

public partial class MainWindow
{
    private string[] _copyEntries = [];
    private string _copyPrefix = "";
    private string? _pendingCopyWorkspace;

    private async void CopySelected()
    {
        if (!CanCopy || _panel != DialogKind.None || _pickerBusy || _snapshot?.ArchivePath is not { } archive) return;
        try
        {
            var rows = SelectedRows;
            _operationArchive = archive;
            _copyEntries = rows.Select(row => row.Path).ToArray();
            _copyPrefix = _snapshot.ArchivePrefix;
            _password = _archivePassword ?? "";
            _dialogError = "";
            if (_archivePassword is null && rows.Any(row => row.IsEncrypted))
            {
                _panel = DialogKind.CopyFiles;
                Invalidate();
                return;
            }
            await CopySelectionAsync(_archivePassword);
        }
        catch (Exception error) { if (!_disposed) ReportError(error); }
    }

    private async Task CopySelectionAsync(string? password)
    {
        if (Desktop is not IFileClipboardService clipboard || _copyEntries.Length == 0) return;
        var workspace = Directory.CreateTempSubdirectory("BubuZip-clipboard-").FullName;
        _pendingCopyWorkspace = workspace;
        var destination = Path.Combine(workspace, "files");
        var published = false;
        try
        {
            var progress = new Progress<ArchiveOperationProgress>(report =>
            {
                if (_disposed || !_busy) return;
                _progress = report.TotalEntries == 0 ? 0 : report.CompletedEntries * 100d / report.TotalEntries;
                _progressText = $"正在准备复制：{report.CompletedEntries} / {report.TotalEntries}  ·  {report.EntryPath}";
                Invalidate();
            });
            var ready = await RunOperationAsync(token => FileService.ExtractAsync(_operationArchive, destination,
                password, _copyEntries, progress, token, new(ExtractionPathMode.CurrentFolder, _copyPrefix)),
                "所选文件已准备好。", DialogKind.CopyFiles);
            if (!ready || _disposed) return;
            if (_cancelRequested)
            {
                StatusMessage = "操作已取消";
                Invalidate();
                return;
            }
            var paths = Directory.GetFileSystemEntries(destination);
            if (paths.Length == 0) throw new IOException("没有可复制的项目。");
            clipboard.CopyFiles(paths);
            // File-manager paste reads these paths later, including after this window closes.
            // Successful payloads remain in the OS temporary directory for normal temp cleanup.
            published = true;
            _pendingCopyWorkspace = null;
            if (password is not null) _archivePassword = password;
            StatusMessage = $"已复制 {paths.Length} 个项目，可在文件管理器中粘贴。";
            Invalidate();
        }
        catch (Exception error)
        {
            if (!_disposed)
            {
                _panel = DialogKind.CopyFiles;
                _dialogError = FriendlyError(error);
                Invalidate();
            }
        }
        finally
        {
            if (!published && Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
            if (_pendingCopyWorkspace == workspace) _pendingCopyWorkspace = null;
        }
    }

    private void DiscardPendingClipboardFiles()
    {
        var workspace = _pendingCopyWorkspace;
        _pendingCopyWorkspace = null;
        if (workspace is not null && Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
    }
}
