using Path = System.IO.Path;

namespace BubuZip;

public partial class MainWindow
{
    private string? _lastSourceDirectory;
    private string _sourceNotice = "";
    private bool _destinationIsAutomatic = true;
    private string Destination
    {
        get => _destination;
        set
        {
            if (_destination == value) return;
            _destination = value;
            _destinationIsAutomatic = false;
            Invalidate();
        }
    }
    private string SourceSelectionError => _panel == DialogKind.Create && SingleFileFormat && _sources.Length > 0
        && (_sources.Length != 1 || Directory.Exists(_sources[0]))
        ? "此格式只能压缩单个文件。请移除多余项目，或改用 ZIP、7z、TAR 组合格式。" : "";
    private bool CanConfirmDialog => _panel is not (DialogKind.Create or DialogKind.AddToArchive)
        || (_sources.Length > 0 && SourceSelectionError.Length == 0);

    private void ChooseCreateFiles() => ChooseCreateSources(folder: false);
    private void ChooseCreateFolder() => ChooseCreateSources(folder: true);

    private async void ChooseCreateSources(bool folder)
    {
        if (_pickerBusy || _busy || _panel is not (DialogKind.Create or DialogKind.AddToArchive)) return;
        if (folder && _panel == DialogKind.Create && SingleFileFormat) return;
        try
        {
            var initial = Directory.Exists(_lastSourceDirectory) ? _lastSourceDirectory : InitialPath;
            var paths = await ShowPickerAsync<IReadOnlyList<string>>(() => folder
                ? Desktop.ChooseFolder(initial) is { } selected ? [selected] : []
                : Desktop.ChooseFiles(initial));
            if (_disposed || paths.Count == 0) return;
            _sourceNotice = "";
            _lastSourceDirectory = Path.GetDirectoryName(Path.GetFullPath(paths[0]));
            if (!folder && paths.Any(Directory.Exists)) throw new IOException("请使用“添加文件夹…”选择文件夹。");
            var merged = ArchiveSourceSelection.Merge(_sources, paths);
            var simplified = merged.Length < _sources.Length + paths.Count;
            UpdateSources(merged);
            if (simplified) _sourceNotice = "已合并重复项目及所选文件夹包含的子项。";
        }
        catch (Exception error) { if (!_disposed) { _dialogError = FriendlyError(error); Invalidate(); } }
    }

    private void RemoveCreateSource(string path)
    {
        if (_busy || _pickerBusy) return;
        UpdateSources(_sources.Where(source => source != path).ToArray());
    }

    private void UpdateSources(string[] sources)
    {
        _sources = sources;
        _sourceNotice = "";
        _dialogError = "";
        if (_panel == DialogKind.Create && _destinationIsAutomatic) _destination = SuggestDestination();
        Invalidate();
    }

    private void ResetAutomaticDestination()
    {
        if (_busy || _pickerBusy || _panel != DialogKind.Create) return;
        _destinationIsAutomatic = true;
        _destination = SuggestDestination();
        Invalidate();
    }

    private string SuggestDestination()
    {
        if (_sources.Length == 0)
            return UniqueDestination(Directory.Exists(InitialPath) ? InitialPath : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Archive", SelectedExtension);
        var first = Path.GetFullPath(_sources[0]);
        var folder = Path.GetDirectoryName(first) ?? InitialPath;
        var name = _sources.Length == 1
            ? Directory.Exists(first) ? Path.GetFileName(Path.TrimEndingDirectorySeparator(first))
                : SingleFileFormat ? Path.GetFileName(first) : Path.GetFileNameWithoutExtension(first)
            : "Archive";
        return UniqueDestination(folder, name, SelectedExtension);
    }
}
