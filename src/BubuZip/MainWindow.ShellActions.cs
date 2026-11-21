using BubuZip.Core;
using Path = System.IO.Path;

namespace BubuZip;

public partial class MainWindow
{
    private bool _shellExtractHere;

    private async Task HandleShellRequestAsync(ArchiveShellRequest request, bool newWindow = false)
    {
        var paths = request.Paths.Select(ArchivePaths.ResolveUserPath).ToArray();
        if (request.Action == ArchiveShellAction.Open && ShellRequestDecoder?.Invoke(paths[0]) is { } command)
        {
            RequestShellAction(command);
            return;
        }
        foreach (var path in paths)
            if (!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException("所选文件或文件夹已不存在。", path);
        if (request.Action == ArchiveShellAction.Open)
        {
            if (newWindow && ArchiveWindows is not null) ArchiveWindows.Open(paths[0]);
            else await TryOpenArchiveAsync(paths[0]);
            return;
        }

        ReturnHome();
        if (request.Action is ArchiveShellAction.Compress or ArchiveShellAction.CompressSevenZip or ArchiveShellAction.CompressZip)
        {
            PrepareCreate();
            _sources = paths;
            _formatIndex = request.Action == ArchiveShellAction.CompressSevenZip ? 1 : 0;
            _compressionLevelIndex = _formatIndex == 1 ? 2 : 3;
            _destination = _suggestedDestination = SuggestDestination();
            Invalidate();
            if (request.Action != ArchiveShellAction.Compress) ConfirmDialog();
            return;
        }

        var archive = paths[0];
        if (Directory.Exists(archive)) throw new InvalidOperationException("请选择压缩包文件。");
        _operationArchive = archive;
        _rootArchivePath = archive;
        _extractEntries = null;
        _password = "";
        _dialogError = "";
        _extractPathIndex = 0;
        _shellExtractHere = request.Action == ArchiveShellAction.ExtractHere;
        _destination = _shellExtractHere ? Path.GetDirectoryName(archive)!
            : UniqueDestination(Path.GetDirectoryName(archive)!, ArchiveBaseName(archive), "");
        _panel = request.Action == ArchiveShellAction.Test ? DialogKind.Test : DialogKind.Extract;
        Invalidate();
        if (request.Action != ArchiveShellAction.Extract) ConfirmDialog();
    }
}
