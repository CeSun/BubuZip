using System.Diagnostics;
using BubuZip.Core;
using EclipseUI;
using EclipseUI.Elements;
using EclipseUI.Themes.Fluent;
using Path = System.IO.Path;

namespace BubuZip;

public partial class MainWindow
{
    [UiParameter] public Action<IHostTheme> ApplyTheme { get; set; } = _ => { };
    [UiParameter] public IArchiveEngine Engine { get; set; } = null!;
    [UiParameter] public ArchiveFileService FileService { get; set; } = null!;
    [UiParameter] public ArchiveEditService? EditService { get; set; }
    [UiParameter] public IDesktopServices Desktop { get; set; } = null!;
    [UiParameter] public string InitialPath { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    [UiParameter] public bool OperationOnly { get; set; }
    public IArchiveOperationWindows? OperationWindows { get; set; }
    public IArchiveWindows? ArchiveWindows { get; set; }
    [UiParameter] public bool StartCreating { get; set; }

    private Grid RootGrid = null!;
    private ArchiveSurface DialogOverlay = null!;
    private ArchiveDialogBorder DialogFrame = null!;
    private Button DialogDismissButton = null!;
    private Element? _focusBeforeDialog;
    private DialogKind _presentedPanel;
    private bool _restoreDialogFocusPending;
    private ContextMenu MainMenu = null!;
    private ArchiveCommand MoreButton = null!;
    private ArchivePane RightPane = null!;
    private Button HomeCreateButton = null!;
    private ArchivePane? _activePane;
    private ArchivePane ActivePane => _activePane ?? RightPane;
    private TextBox AddressBox => ActivePane.Address;
    private ListBox Files => ActivePane?.Files!;
    private bool _showBrowser;
    private BrowserService _browser = null!;
    private BrowserSnapshot? _snapshot { get => ActivePane?.Snapshot; set => ActivePane.Snapshot = value; }
    private Stack<(BrowserSnapshot Snapshot, string? Password)> _history => ActivePane.History;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operationCancellation;
    private readonly BackgroundOperations _operations = new();
    private sealed record NestedArchive(BrowserSnapshot Parent, string? ParentPassword, string DisplayPath, string Name);
    private readonly Dictionary<string, NestedArchive> _nestedArchives = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private string? _nestedWorkspace, _rootArchivePath;
    private bool InNestedArchive => _snapshot?.ArchivePath is { } path && _nestedArchives.ContainsKey(path);
    private string OperationArchiveName => _nestedArchives.TryGetValue(_operationArchive, out var nested) ? nested.Name : Path.GetFileName(_operationArchive);
    private static readonly ArchiveFormat[] Formats = [ArchiveFormat.Zip, ArchiveFormat.SevenZip, ArchiveFormat.Tar, ArchiveFormat.TarGZip, ArchiveFormat.TarBZip2, ArchiveFormat.TarLZip, ArchiveFormat.TarZStandard, ArchiveFormat.GZip, ArchiveFormat.BZip2, ArchiveFormat.LZip, ArchiveFormat.ZStandard];
    private static readonly string[] FormatExtensions = [".zip", ".7z", ".tar", ".tar.gz", ".tar.bz2", ".tar.lz", ".tar.zst", ".gz", ".bz2", ".lz", ".zst"];
    private readonly string[] _formatNames = ["ZIP", "7z", "TAR", "TAR.GZ", "TAR.BZ2", "TAR.LZ", "TAR.ZST", "GZ（单文件）", "BZ2（单文件）", "LZ（单文件）", "ZST（单文件）"];
    private ArchiveFormat SelectedFormat => Formats[Math.Clamp(_formatIndex, 0, Formats.Length - 1)];
    private string SelectedExtension => FormatExtensions[Math.Clamp(_formatIndex, 0, FormatExtensions.Length - 1)];
    private bool SingleFileFormat => SelectedFormat is ArchiveFormat.GZip or ArchiveFormat.BZip2 or ArchiveFormat.LZip or ArchiveFormat.ZStandard;
    private bool HasCompressionLevel => SelectedFormat is ArchiveFormat.Zip or ArchiveFormat.SevenZip or ArchiveFormat.TarGZip or ArchiveFormat.GZip;
    private bool CanEncryptNewArchive => SelectedFormat is ArchiveFormat.Zip or ArchiveFormat.SevenZip;
    private readonly string[] _zipLevels = ["存储", "最快", "快速", "标准", "最大", "极限"];
    private readonly string[] _sevenZipLevels = ["最快", "快速", "标准", "最大", "极限"];
    private readonly string[] _zipEncryptionNames = ["AES-256", "ZipCrypto（兼容旧软件）"];
    private readonly string[] _extractPathNames = ["完整路径", "相对于当前目录", "不包含目录"];
    private static readonly ArchiveCompressionLevel[] Levels = [ArchiveCompressionLevel.Store, ArchiveCompressionLevel.Fastest, ArchiveCompressionLevel.Fast, ArchiveCompressionLevel.Normal, ArchiveCompressionLevel.Maximum, ArchiveCompressionLevel.Ultra];
    private string[] CompressionLevelNames => SelectedFormat == ArchiveFormat.SevenZip ? _sevenZipLevels : _zipLevels;
    private int _compressionLevelIndex = 3, _zipEncryptionIndex, _extractPathIndex = 1;
    private string _passwordConfirm = "";
    private bool _showCreatePassword;
    private string CompressionMethodName => SelectedFormat switch
    {
        ArchiveFormat.Zip => _compressionLevelIndex == 0 ? "存储" : "Deflate",
        ArchiveFormat.SevenZip => "LZMA2",
        ArchiveFormat.Tar => "存储",
        ArchiveFormat.TarGZip or ArchiveFormat.GZip => "Deflate",
        ArchiveFormat.TarBZip2 or ArchiveFormat.BZip2 => "BZip2",
        ArchiveFormat.TarLZip or ArchiveFormat.LZip => "LZMA",
        _ => "Zstandard",
    };
    private string[] _sources = [];
    private string[]? _extractEntries;
    private BrowserItem? _passwordEntry;
    private string[] _editEntries = [];
    private string _newName = "", _editDirectory = "";
    private bool _replaceExisting;
    private string? _archivePassword { get => ActivePane.Password; set => ActivePane.Password = value; }
    private string _operationArchive = "", _destination = "", _password = "", _error = "", _dialogError = "";
    private string _progressText = "", _lastClick = "", _resultPath = "", _resultFolder = "";
    private bool _operationSucceeded;
    private string _messageTitle = "", _messageBody = "";
    private string _sortColumn { get => ActivePane.SortColumn; set => ActivePane.SortColumn = value; }
    private long _lastClickTime;
    private bool _busy, _pickerBusy, _disposed, _encryptNames, _cancelRequested;
    private bool _sortDescending { get => ActivePane.SortDescending; set => ActivePane.SortDescending = value; }
    private int _formatIndex;
    private int FormatIndex
    {
        get => _formatIndex;
        set
        {
            if (value < 0 || value >= Formats.Length || value == _formatIndex) return;
            _formatIndex = value;
            if (_panel == DialogKind.Create && _destination.Length > 0)
            {
                _destination = _destinationIsAutomatic ? SuggestDestination() : WithArchiveExtension(_destination, SelectedExtension);
            }
            if (SelectedFormat != ArchiveFormat.SevenZip) _encryptNames = false;
            _compressionLevelIndex = SelectedFormat == ArchiveFormat.SevenZip ? 2 : 3;
            Invalidate();
        }
    }
    private double _progress = -1;
    private ThemeVariant _theme = ThemeVariant.System;
    private DialogKind _panel;
    private enum DialogKind { None, Create, Extract, Test, Unlock, EntryPassword, Progress, AddToArchive, DeleteEntries, Rename, Message, CopyFiles }

    public string StatusMessage { get => ActivePane?.Status ?? "就绪"; private set => ActivePane.Status = value; }
    public IReadOnlyList<BrowserItem> VisibleItems { get => ActivePane.Rows; private set => ActivePane.Rows = value; }
    public BrowserSnapshot? CurrentLocation => _snapshot;
    public bool TaskIsRunning => _busy || _pickerBusy || _openingRequestedArchive || _settingsWindowOpen;
    private BrowserItem[] SelectedRows => Files?.SelectedItems.OfType<BrowserItem>().ToArray() ?? [];
    private bool HasSelection => SelectedRows.Length > 0;
    private bool CanEditArchive => !_busy && !InNestedArchive && _snapshot?.ArchivePath is { } path &&
        (Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase) ||
         Path.GetExtension(path).Equals(".7z", StringComparison.OrdinalIgnoreCase));
    private bool CanAdd => _showBrowser && CanEditArchive;
    private bool CanExtractSelection => _showBrowser && !_busy && HasSelection;
    private bool CanCopy => CanExtractSelection && Desktop is IFileClipboardService;
    private bool CanDelete => CanEditArchive && HasSelection;
    private bool CanRename => HasSelection && SelectedRows.Length == 1 && CanEditArchive;
    private bool CanExtract => !_busy && (_snapshot?.ArchivePath is not null || SelectedRows.Any(row => row.IsArchive));
    private string SourceSummary => _sources.Length == 0 ? "请选择要添加的文件或文件夹" :
        _sources.Length == 1 ? Path.GetFileName(_sources[0]) : $"已选择 {_sources.Length} 个文件或文件夹";
    private string DialogTitle => _panel switch
    {
        DialogKind.Create => "添加到压缩包", DialogKind.Extract => "提取文件", DialogKind.Test => "测试压缩包",
        DialogKind.Unlock => "打开压缩包", DialogKind.EntryPassword => "打开加密文件", DialogKind.Progress => _operationSucceeded ? "操作成功" : "正在处理",
        DialogKind.AddToArchive => "添加到当前压缩包", DialogKind.DeleteEntries => "从压缩包删除", DialogKind.Rename => "重命名",
        DialogKind.Message => _messageTitle,
        DialogKind.CopyFiles => "复制到剪贴板",
        _ => OperationOnly ? "正在准备…" : "",
    };
    private string DialogDescription => _panel switch
    {
        DialogKind.Create => "将所选文件创建为新的压缩包。已有文件不会被覆盖。",
        DialogKind.Extract => OperationArchiveName + (_extractEntries is null ? " · 全部内容" : $" · 提取 {_extractEntries.Length} 个所选项目"),
        DialogKind.Test => OperationArchiveName + " · 读取所有文件，检查内容是否可以完整解压。",
        DialogKind.Unlock => OperationArchiveName + " · 此压缩包可能需要密码。",
        DialogKind.EntryPassword => (_passwordEntry?.Name ?? "") + " · 请输入文件密码。",
        DialogKind.Progress => _operationSucceeded ? "输出位置：" + _resultPath : _cancelRequested ? "正在取消，请稍候…" : "操作完成前请保持应用打开。",
        DialogKind.AddToArchive => Path.GetFileName(_operationArchive) + "!/" + _editDirectory,
        DialogKind.DeleteEntries => $"将从 {Path.GetFileName(_operationArchive)} 中删除所选 {_editEntries.Length} 个项目及其内容。",
        DialogKind.Rename => _editEntries.FirstOrDefault() ?? "",
        DialogKind.Message => "",
        DialogKind.CopyFiles => OperationArchiveName + $" · 复制 {_copyEntries.Length} 个项目，完成后可在文件管理器中粘贴。加密内容请输入密码。",
        _ => OperationOnly ? StatusMessage : "",
    };
    private bool IsArchiveEditDialog => Path.GetExtension(_operationArchive).Equals(".7z", StringComparison.OrdinalIgnoreCase) &&
        _panel is DialogKind.AddToArchive or DialogKind.DeleteEntries or DialogKind.Rename;
    private string DialogConfirmLabel => _panel switch
    {
        DialogKind.Unlock or DialogKind.EntryPassword => "打开", DialogKind.AddToArchive => "保存修改",
        DialogKind.DeleteEntries => "删除", DialogKind.Rename => "保存", _ => "开始",
    };

    protected override void OnInitialized()
    {
        base.OnInitialized();
        _browser = new BrowserService(Engine);
        InitializePane(RightPane);
        _acceptingInitialRequests = false;
        if (StartCreating) PrepareCreate();
        foreach (var command in RootGrid.Descendants().OfType<ArchiveCommand>().Where(command => !command.ShowLabel).ToArray())
        {
            var text = new TextBlock { FontSize = 12, Padding = 7 }; text.AddRun(command.Label);
            RootGrid.Children.Add(new ToolTip { Target = command, Content = text });
        }
        RootGrid.AddHandler(Element.KeyDownEvent, HandleKey);
        if (!_openRequests.IsEmpty || _showSettingsRequested) Invalidate();
    }

    protected override void OnUpdating()
    {
        base.OnUpdating();
        ShowRequestedSettings();
        if (!_standaloneOperationClosing) OpenNextRequestedArchive();
        if (_presentedPanel == DialogKind.None && _panel != DialogKind.None)
            _focusBeforeDialog = FocusManager.GetFor(RootGrid)?.Focused;
    }

    protected override void OnUpdated()
    {
        base.OnUpdated();
        if (_standaloneOperationClosing)
        {
            _standaloneOperationClosing = false;
            if (!_openRequests.IsEmpty) Invalidate();
        }
        SynchronizeCreatePopup();
        _createPopupClosedForOperation?.TrySetResult(true);
        var previous = _presentedPanel;
        var changed = previous != _panel;
        _presentedPanel = _panel;
        if (_panel != DialogKind.None)
        {
            if (changed) DialogDismissButton.RequestFocus();
        }
        else
        {
            if (previous != DialogKind.None) _restoreDialogFocusPending = true;
            if (!_restoreDialogFocusPending || _busy) return;
            _restoreDialogFocusPending = false;
            var prior = _focusBeforeDialog;
            _focusBeforeDialog = null;
            if (prior?.RequestFocus() != true)
            {
                if (_showBrowser) Files.RequestFocus(); else HomeCreateButton.RequestFocus();
            }
        }
    }

    public async Task NavigateToAsync(string path, string prefix = "", string? password = null, bool remember = true, bool preserveHistory = false)
    {
        if (_busy || _disposed) return;
        var fullPath = Path.GetFullPath(path);
        if (Directory.Exists(fullPath)) throw new InvalidOperationException("只能浏览压缩包内的内容，不能浏览系统文件夹。");
        _busy = true; _error = ""; Invalidate();
        try
        {
            BrowserSnapshot? snapshot = null;
            await _operations.RunAsync(async token =>
            {
                snapshot = await _browser.BrowseArchiveAsync(fullPath, prefix, password, token).ConfigureAwait(false);
            }, _lifetime.Token);
            if (_disposed) return;
            var sameArchive = _snapshot?.ArchivePath is { } previousPath &&
                string.Equals(previousPath, snapshot!.ArchivePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            if (_nestedArchives.TryGetValue(fullPath, out var nested)) snapshot = snapshot! with { ArchiveDisplayPath = nested.DisplayPath };
            if (!sameArchive && !preserveHistory)
            {
                _history.Clear();
                ClearNestedArchives();
                _rootArchivePath = fullPath;
            }
            else if (remember && _snapshot is not null && snapshot!.DisplayPath != _snapshot.DisplayPath)
                _history.Push((_snapshot, _archivePassword));
            _archivePassword = password;
            _showBrowser = true;
            ApplySnapshot(snapshot!);
        }
        finally { _busy = false; Invalidate(); }
    }

    public async Task NavigateUpAsync()
    {
        if (_snapshot?.ArchivePath is not { } archive) return;
        var current = _snapshot.ArchivePrefix.TrimEnd('/');
        if (current.Length == 0)
        {
            if (_nestedArchives.TryGetValue(archive, out var nested))
                await NavigateToAsync(nested.Parent.ArchivePath!, nested.Parent.ArchivePrefix, nested.ParentPassword, preserveHistory: true);
            return;
        }
        await NavigateToAsync(archive, current.Contains('/') ? current[..current.LastIndexOf('/')] : "", _archivePassword);
    }

    public async Task OpenItemAsync(BrowserItem item)
    {
        if (_snapshot is null || _busy) return;
        if (_snapshot.ArchivePath is not null)
        {
            if (item.IsDirectory) await NavigateToAsync(_snapshot.ArchivePath, item.Path, _archivePassword);
            else if (item.IsLink) throw new NotSupportedException("暂不支持打开压缩包内的链接。");
            else if (item.IsEncrypted && _archivePassword is null)
            {
                _passwordEntry = item; _password = ""; _dialogError = "";
                _panel = DialogKind.EntryPassword; Invalidate();
            }
            else
            {
                if (item.IsEncrypted) { _passwordEntry = item; _password = _archivePassword ?? ""; }
                await OpenItemContentAsync(item, _archivePassword, item.IsEncrypted ? DialogKind.EntryPassword : DialogKind.None);
            }
        }
    }

    private Task OpenItemContentAsync(BrowserItem item, string? password, DialogKind returnPanel) =>
        item.IsArchive ? OpenNestedArchiveAsync(item, password, returnPanel) : OpenArchiveFileAsync(item, password, returnPanel);

    private async Task OpenNestedArchiveAsync(BrowserItem item, string? password, DialogKind returnPanel)
    {
        var parent = _snapshot!;
        var workspace = _nestedWorkspace ??= Directory.CreateTempSubdirectory("BubuZip-nested-").FullName;
        var folder = Path.Combine(workspace, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var safeName = item.Name is "." or ".." || item.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            ? "archive" + Path.GetExtension(item.Name) : item.Name;
        var target = Path.Combine(folder, safeName);
        var opened = await RunOperationAsync(async token =>
        {
            try
            {
                await using var source = File.OpenRead(parent.ArchivePath!);
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await Engine.ExtractEntryAsync(source, item.Path, output, password, token);
            }
            catch { if (File.Exists(target)) File.Delete(target); throw; }
        }, "压缩包已打开", returnPanel);
        if (!opened || _disposed) { if (File.Exists(target)) File.Delete(target); return; }
        if (item.IsEncrypted) _archivePassword = password;
        _nestedArchives[target] = new(parent, _archivePassword, parent.DisplayPath + item.Name, item.Name);
        try
        {
            await NavigateToAsync(target, preserveHistory: true);
        }
        catch (SharpCompress.Common.CryptographicException error)
        {
            _operationArchive = target; _password = ""; _panel = DialogKind.Unlock;
            _dialogError = FriendlyError(error); Invalidate();
        }
        catch
        {
            _nestedArchives.Remove(target);
            if (File.Exists(target)) File.Delete(target);
            throw;
        }
    }

    private void ClearNestedArchives()
    {
        _nestedArchives.Clear();
        var workspace = _nestedWorkspace;
        _nestedWorkspace = null;
        if (workspace is not null && Directory.Exists(workspace))
        {
            try { Directory.Delete(workspace, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private async Task OpenArchiveFileAsync(BrowserItem item, string? password, DialogKind returnPanel)
    {
        var archive = _snapshot!.ArchivePath!;
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BubuZip", "Opened", Guid.NewGuid().ToString("N"));
        var target = Path.Combine(folder, Path.GetFileName(item.Name));
        var opened = await RunOperationAsync(async token =>
        {
            Directory.CreateDirectory(folder);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            try
            {
                await using var source = File.OpenRead(archive);
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
                await Engine.ExtractEntryAsync(source, item.Path, output, password, token);
            }
            catch { Directory.Delete(folder, true); throw; }
        }, "文件已提取并打开", returnPanel);
        if (opened && !_disposed && File.Exists(target))
        {
            if (item.IsEncrypted) _archivePassword = password;
            Desktop.Open(target);
        }
        else if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }

    private void ApplySnapshot(BrowserSnapshot snapshot)
    {
        _snapshot = snapshot;
        ActivePane.HasParentArchive = snapshot.ArchivePath is { } path && _nestedArchives.ContainsKey(path);
        ActivePane.FileScroll.ScrollToHorizontalOffset(0);
        ActivePane.FileScroll.ScrollToVerticalOffset(0);
        AddressBox.Text = snapshot.DisplayPath;
        Title = "BubuZip — " + (snapshot.ArchivePath is { } archive && _nestedArchives.TryGetValue(archive, out var nested) ? nested.Name : Path.GetFileName(snapshot.ArchivePath));
        UpdateRows();
        StatusMessage = $"{snapshot.Items.Count(item => item.IsDirectory)} 个文件夹，{snapshot.Items.Count(item => !item.IsDirectory)} 个文件 · {BrowserItem.FormatSize(snapshot.Items.Where(item => !item.IsDirectory).Sum(item => item.Size))}" + (InNestedArchive ? " · 嵌套压缩包只读" : "");
        Invalidate();
        ActivePane.RefreshView();
    }

    private void UpdateRows()
    {
        if (_snapshot is null) return;
        object? Key(BrowserItem row) => _sortColumn switch
        {
            "size" => row.Size, "packed" => row.PackedSize, "type" => row.TypeLabel,
            "modified" => row.Modified, "attributes" => row.AttributesLabel, _ => row.Name,
        };
        var rows = _snapshot.Items.OrderByDescending(row => row.IsDirectory);
        VisibleItems = (_sortDescending ? rows.ThenByDescending(Key) : rows.ThenBy(Key)).ToArray();
        Files.ItemsSource = VisibleItems;
        ActivePane.RefreshView();
        Invalidate();
    }

    private Element CreateRow(object? value, ArchivePane pane)
    {
        var item = (BrowserItem)value!;
        var grid = new ArchiveFileRow { ColumnDefinitions = pane.FileColumnDefinitions, Height = 22, ClipToBounds = true };
        var name = new Grid { ColumnDefinitions = "22,*", ClipToBounds = true };
        name.Children.Add(new ArchiveGlyph { Symbol = item.IsDirectory ? ArchiveSymbol.Folder : item.IsArchive ? ArchiveSymbol.Archive : ArchiveSymbol.File, VerticalAlignment = VerticalAlignment.Center });
        var label = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, ClipToBounds = true };
        label.AddRun(item.Name); Grid.SetColumn(label, 1); name.Children.Add(label); grid.Children.Add(name);
        var values = new[] { "", item.IsDirectory ? "" : item.Size.ToString("N0").Replace(',', ' '), item.ModifiedLabel,
            item.PackedSize?.ToString("N0").Replace(',', ' ') ?? "", item.TypeLabel, item.AttributesLabel };
        for (var index = 1; index < values.Length; index++)
        {
            var cell = new TextBlock { FontSize = 12, Margin = 3, VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = index is 1 or 3 ? HorizontalAlignment.Right : HorizontalAlignment.Stretch, ClipToBounds = true };
            cell.AddRun(values[index]); Grid.SetColumn(cell, index); grid.Children.Add(cell);
        }
        grid.AddHandler(Element.PointerPressedEvent, args =>
        {
            ActivatePane(pane);
            if (args is PointerEventArgs { Button: PointerButton.Right }) Files.SelectedItem = item;
        });
        grid.AddHandler(Element.PointerReleasedEvent, args =>
        {
            ActivatePane(pane);
            if (args is not PointerEventArgs { Button: PointerButton.Left } pointer || pointer.Modifiers != KeyModifiers.None) return;
            var now = Stopwatch.GetTimestamp();
            if (_lastClick == item.Path && Stopwatch.GetElapsedTime(_lastClickTime, now).TotalMilliseconds < 450)
            {
                _lastClick = ""; args.Handled = true; OpenRow(item);
            }
            else { _lastClick = item.Path; _lastClickTime = now; }
        });
        return grid;
    }

    private async void OpenRow(BrowserItem item) { try { await OpenItemAsync(item); } catch (Exception error) { ReportError(error); } }
    private void OpenSelected() { if (SelectedRows.FirstOrDefault() is { } row) OpenRow(row); }
    private async void GoUp() { try { await NavigateUpAsync(); } catch (Exception error) { ReportError(error); } }
    private async void GoBack()
    {
        if (_busy || !_history.TryPop(out var previous)) return;
        try { await NavigateToAsync(previous.Snapshot.ArchivePath!, previous.Snapshot.ArchivePrefix, previous.Password, false, preserveHistory: true); }
        catch (Exception error) { _history.Push(previous); ReportError(error); }
    }
    private async void ChooseAndOpen()
    {
        if (_pickerBusy || _busy) return;
        try
        {
            var file = await ShowPickerAsync(Desktop.ChooseArchive);
            if (!_disposed && file is not null)
            {
                if (ArchiveWindows is not null) ArchiveWindows.Open(file);
                else await TryOpenArchiveAsync(file);
            }
        }
        catch (Exception error) { if (!_disposed) ReportError(error); }
    }

    private async Task<T> ShowPickerAsync<T>(Func<T> picker)
    {
        _pickerBusy = true; Invalidate();
        try
        {
            return Desktop is IAsyncDesktopDialogService asyncDialogs
                ? await asyncDialogs.ShowDialogAsync(picker)
                : picker();
        }
        finally
        {
            _pickerBusy = false;
            if (!_disposed) Invalidate();
        }
    }
    private async Task TryOpenArchiveAsync(string path)
    {
        try { await NavigateToAsync(path); }
        catch (SharpCompress.Common.CryptographicException error)
        {
            _operationArchive = path; _password = ""; _panel = DialogKind.Unlock;
            _dialogError = FriendlyError(error); Invalidate();
        }
    }
    private async void Refresh()
    {
        if (_snapshot?.ArchivePath is not { } archive) return;
        try { await NavigateToAsync(archive, _snapshot.ArchivePrefix, _archivePassword, false); }
        catch (Exception error) { ReportError(error); }
    }
    private void SortBy(string column) { _sortDescending = _sortColumn == column && !_sortDescending; _sortColumn = column; UpdateRows(); }
    private void SelectAll() { if (VisibleItems.Count > 0) { Files.Selection.Select(0); Files.Selection.Select(VisibleItems.Count - 1, extend: true); } }
    private void DismissError() { _error = ""; Invalidate(); }

    private void ChooseCreate()
    {
        if (_busy || _pickerBusy) return;
        try
        {
            if (ArchiveWindows is not null) ArchiveWindows.Create(InitialPath);
            else PrepareCreate();
        }
        catch (Exception error) { ReportError(error); }
    }

    private void PrepareCreate()
    {
        if (_busy) return;
        _sourceNotice = "";
        if (_snapshot?.ArchivePath is not null)
        {
            if (!CanEditArchive) return;
            _operationArchive = _snapshot.ArchivePath; _editDirectory = _snapshot.ArchivePrefix;
            _sources = []; _password = _archivePassword ?? ""; _replaceExisting = false; _dialogError = "";
            _panel = DialogKind.AddToArchive; Invalidate(); return;
        }
        _sources = [];
        _showCreatePassword = false;
        _formatIndex = 0; _compressionLevelIndex = 3; _zipEncryptionIndex = 0; _password = ""; _passwordConfirm = ""; _encryptNames = false; _dialogError = "";
        _destinationIsAutomatic = true;
        _destination = SuggestDestination();
        _panel = DialogKind.Create; Invalidate();
    }
    private static string WithArchiveExtension(string path, string extension)
    {
        if (path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) return path;
        foreach (var existing in FormatExtensions.OrderByDescending(value => value.Length))
            if (path.EndsWith(existing, StringComparison.OrdinalIgnoreCase)) return path[..^existing.Length] + extension;
        return Path.ChangeExtension(path, extension);
    }

    private void ClearCreateInputs()
    {
        if (_busy || _pickerBusy) return;
        UpdateSources([]);
    }
    private void PrepareExtract()
    {
        if (_busy) return;
        _shellExtractHere = _shellExtractSmart = _shellExtractFolder = false;
        _operationArchive = _snapshot?.ArchivePath ?? SelectedRows.FirstOrDefault(row => row.IsArchive)?.Path ?? "";
        if (_operationArchive.Length == 0) return;
        _extractEntries = _snapshot?.ArchivePath is not null
            ? HasSelection ? SelectedRows.Select(row => row.Path).ToArray() : _snapshot.ArchivePrefix.Length > 0 ? [_snapshot.ArchivePrefix] : null
            : null;
        _password = _snapshot?.ArchivePath is not null ? _archivePassword ?? "" : "";
        _destination = UniqueDestination(Path.GetDirectoryName(_rootArchivePath ?? _operationArchive)!, ArchiveBaseName(OperationArchiveName), "");
        _extractPathIndex = 1; _dialogError = ""; _panel = DialogKind.Extract; Invalidate();
    }
    private void PrepareTest()
    {
        if (_busy) return;
        _operationArchive = _snapshot?.ArchivePath ?? SelectedRows.FirstOrDefault(row => row.IsArchive)?.Path ?? "";
        if (_operationArchive.Length == 0) return;
        _password = _snapshot?.ArchivePath is not null ? _archivePassword ?? "" : "";
        _dialogError = ""; _panel = DialogKind.Test; Invalidate();
    }
    private async void ChooseDestination()
    {
        if (_pickerBusy || _busy) return;
        try
        {
            if (_panel == DialogKind.Create)
            {
                var suggested = WithArchiveExtension(Path.GetFileName(_destination), SelectedExtension);
                var directory = Path.GetDirectoryName(_destination);
                var path = await ShowPickerAsync(() => Desktop.ChooseSavePath(suggested, directory));
                if (_disposed) return;
                if (path is not null) { _destination = path; _destinationIsAutomatic = false; }
            }
            else
            {
                var directory = Path.GetDirectoryName(_destination);
                var folder = await ShowPickerAsync(() => Desktop.ChooseFolder(directory));
                if (_disposed) return;
                if (folder is not null)
                {
                    _destination = _shellExtractHere ? folder : Path.Combine(folder, ArchiveBaseName(OperationArchiveName));
                }
            }
            Invalidate();
        }
        catch (Exception error) { if (!_disposed) { _dialogError = FriendlyError(error); Invalidate(); } }
    }
    private void CloseDialog()
    {
        if (_busy || _pickerBusy || _settingsWindowOpen) return;
        if (OperationOnly) { ArchiveWindowClose.Request(this); return; }
        _panel = DialogKind.None; _operationSucceeded = false; _resultPath = ""; _resultFolder = "";
        _password = ""; _passwordConfirm = ""; _dialogError = ""; Invalidate();
    }
    private void DismissDialog() { if (_panel == DialogKind.Progress && _busy) CancelOperation(); else CloseDialog(); }
    private void OpenResultFolder()
    {
        if (_busy || !_operationSucceeded || _resultFolder.Length == 0) return;
        try { Desktop.Open(_resultFolder); _dialogError = ""; }
        catch (Exception error) { _dialogError = "无法打开输出文件夹：" + FriendlyError(error); }
        Invalidate();
    }
    private async void ConfirmDialog()
    {
        if (_busy || _pickerBusy || _panel is DialogKind.None or DialogKind.Progress) return;
        var mode = _panel;
        try
        {
            if (mode == DialogKind.CopyFiles)
            {
                await CopySelectionAsync(PasswordOrNull);
                return;
            }
            if (mode == DialogKind.EntryPassword)
            {
                if (PasswordOrNull is null) throw new InvalidOperationException("请输入密码。");
                await OpenItemContentAsync(_passwordEntry!, PasswordOrNull, DialogKind.EntryPassword);
                return;
            }
            if (mode == DialogKind.Unlock)
            {
                await NavigateToAsync(_operationArchive, password: PasswordOrNull, preserveHistory: _nestedArchives.ContainsKey(_operationArchive));
                _panel = DialogKind.None; _password = ""; Invalidate(); return;
            }
            if (mode is DialogKind.Create or DialogKind.AddToArchive && _sources.Length == 0)
                throw new InvalidOperationException("请先选择要压缩的文件或文件夹。");
            if (mode == DialogKind.Create && SourceSelectionError.Length > 0)
                throw new InvalidOperationException(SourceSelectionError);
            if (mode == DialogKind.Create && CanEncryptNewArchive && _password != _passwordConfirm)
                throw new InvalidOperationException("两次输入的密码不一致。");
            if (mode == DialogKind.Create && _formatIndex == 1 && _encryptNames && PasswordOrNull is null)
                throw new InvalidOperationException("加密文件名需要先设置密码。");
            if (mode is DialogKind.Create or DialogKind.Extract && string.IsNullOrWhiteSpace(_destination)) throw new InvalidOperationException("请选择输出路径。");
            var password = mode == DialogKind.Create && !CanEncryptNewArchive ? null : PasswordOrNull;
            var createSettings = new ArchiveCreateSettings(mode == DialogKind.Create && HasCompressionLevel && _compressionLevelIndex >= 0
                ? Levels[Math.Clamp(_compressionLevelIndex + (SelectedFormat == ArchiveFormat.SevenZip ? 1 : 0), 0, Levels.Length - 1)]
                : ArchiveCompressionLevel.Normal,
                _zipEncryptionIndex == 1 ? ZipEncryptionMethod.ZipCrypto : ZipEncryptionMethod.Aes256);
            var extractSettings = new ArchiveExtractSettings((ExtractionPathMode)_extractPathIndex, _snapshot?.ArchivePrefix ?? "");
            var destination = mode is DialogKind.Create or DialogKind.Extract ? Path.GetFullPath(_destination) : "";
            if (mode == DialogKind.Create) destination = WithArchiveExtension(destination, SelectedExtension);
            var actualDestination = destination;
            var progress = new Progress<ArchiveOperationProgress>(report =>
            {
                if (_disposed || !_busy || _panel != DialogKind.Progress) return;
                _progress = report.Percent;
                _progressText = $"{report.CompletedEntries} / {report.TotalEntries}  ·  {report.EntryPath}"; Invalidate();
            });
            async Task Execute(IProgress<ArchiveOperationProgress> operationProgress, CancellationToken token)
            {
                if (mode == DialogKind.Create) await FileService.CreateAsync(_sources, destination,
                    SelectedFormat, password,
                    _formatIndex == 1 && _encryptNames, operationProgress, token, createSettings);
                else if (mode == DialogKind.Extract)
                {
                    var interaction = operationProgress as IArchiveExtractionInteraction;
                    if (_shellExtractSmart) actualDestination = await FileService.ExtractSmartAsync(_operationArchive, destination, password, operationProgress, token, extractSettings, interaction);
                    else if (_shellExtractHere) actualDestination = await FileService.ExtractHereAsync(_operationArchive, destination, password, operationProgress, token, extractSettings, interaction);
                    else if (_shellExtractFolder) actualDestination = await FileService.ExtractToFolderAsync(_operationArchive, destination, password, operationProgress, token, extractSettings, interaction);
                    else await FileService.ExtractAsync(_operationArchive, destination, password, _extractEntries, operationProgress, token, extractSettings, interaction);
                }
                else if (mode == DialogKind.Test) await FileService.TestAsync(_operationArchive, password, operationProgress, token);
                else if (mode == DialogKind.AddToArchive) await ArchiveEditor.AddAsync(_operationArchive, _sources, _replaceExisting,
                    password, operationProgress, token, archiveDirectory: _editDirectory);
                else if (mode == DialogKind.DeleteEntries) await ArchiveEditor.DeleteAsync(_operationArchive, _editEntries, password, operationProgress, token);
                else if (mode == DialogKind.Rename) await RenameSelectedAsync(_newName, password, operationProgress, token);
            }
            var success = mode switch
            {
                DialogKind.Test => "测试完成，所有文件均可读取。", DialogKind.Create => "压缩包创建完成。",
                DialogKind.Extract => "提取完成。", DialogKind.AddToArchive => "压缩包已更新。",
                DialogKind.DeleteEntries => "所选归档项目已删除。", _ => "重命名完成。",
            };
            var resultFolder = mode == DialogKind.Create ? Path.GetDirectoryName(destination) : mode == DialogKind.Extract ? destination : null;
            var completed = OperationWindows is not null && mode is DialogKind.Create or DialogKind.Extract or DialogKind.AddToArchive
                ? await RunOperationWindowAsync(new(mode == DialogKind.Extract ? "正在解压" : "正在压缩",
                    mode == DialogKind.Create ? SourceSummary : OperationArchiveName, success,
                    async (reporter, token) =>
                    {
                        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
                        await _operations.RunAsync(inner => Execute(reporter, inner), linked.Token).ConfigureAwait(false);
                    }, mode == DialogKind.AddToArchive ? _operationArchive : destination,
                    mode == DialogKind.AddToArchive ? Path.GetDirectoryName(_operationArchive) : resultFolder,
                    mode == DialogKind.Create ? destination : _operationArchive, mode == DialogKind.Extract,
                    mode == DialogKind.Extract ? () => actualDestination : null), mode)
                : await RunOperationAsync(token => Execute(progress, token), success, mode,
                    mode is DialogKind.Create or DialogKind.Extract ? destination : null, resultFolder);
            if (completed && mode == DialogKind.Extract)
                _resultPath = _resultFolder = actualDestination;
            if (completed && mode == DialogKind.Extract && !_disposed && !OperationOnly && OperationWindows is null)
            {
                try { Desktop.Open(actualDestination); }
                catch (Exception error)
                {
                    _dialogError = $"解压已完成，但无法打开输出文件夹：{FriendlyError(error)}";
                    Invalidate();
                }
            }
            if (completed && mode is DialogKind.AddToArchive or DialogKind.DeleteEntries or DialogKind.Rename)
            {
                var message = StatusMessage;
                _history.Clear();
                ClearNestedArchives();
                if (_snapshot?.ArchivePath is { } archivePath)
                    await NavigateToAsync(archivePath, _snapshot.ArchivePrefix, password, remember: false);
                StatusMessage = message; Invalidate();
            }
        }
        catch (Exception error)
        {
            if (_panel == DialogKind.None) ReportError(error);
            else { _dialogError = FriendlyError(error); Invalidate(); }
        }
    }
    private string? PasswordOrNull => string.IsNullOrEmpty(_password) ? null : _password;

    private bool RunOperationWindow(ArchiveOperationSpec operation, DialogKind returnPanel)
    {
        _busy = true; _panel = DialogKind.None; _dialogError = ""; _error = "";
        _password = ""; _passwordConfirm = ""; Invalidate();
        try
        {
            var result = OperationWindows!.Show(operation);
            if (_disposed) return false;
            if (result.Succeeded)
            {
                StatusMessage = operation.SuccessMessage;
                return true;
            }
            if (result.Cancelled)
            {
                StatusMessage = "操作已取消";
                return false;
            }
            _panel = returnPanel;
            _dialogError = result.Error ?? "操作未完成，请重试。";
            return false;
        }
        catch (Exception error)
        {
            if (!_disposed) { _panel = returnPanel; _dialogError = FriendlyError(error); }
            return false;
        }
        finally { _busy = false; if (!_disposed) Invalidate(); }
    }

    private async Task<bool> RunOperationAsync(Func<CancellationToken, Task> action, string success, DialogKind returnPanel = DialogKind.None,
        string? resultPath = null, string? resultFolder = null)
    {
        if (_busy) return false;
        _busy = true; _panel = DialogKind.Progress; _progress = -1; _progressText = "正在准备…";
        _dialogError = ""; _error = ""; _cancelRequested = false; _operationSucceeded = false;
        _resultPath = ""; _resultFolder = "";
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operationCancellation = cancellation; Invalidate();
        try
        {
            await _operations.RunAsync(action, cancellation.Token);
            if (_disposed) return false;
            StatusMessage = success;
            if (resultPath is not null && resultFolder is not null)
            {
                _operationSucceeded = true; _resultPath = resultPath; _resultFolder = resultFolder;
                _progress = 100; _progressText = success; _cancelRequested = false;
            }
            else _panel = DialogKind.None;
            _password = ""; _passwordConfirm = ""; return true;
        }
        catch (OperationCanceledException) { if (!_disposed) { StatusMessage = "操作已取消"; _panel = DialogKind.None; _password = ""; _passwordConfirm = ""; } return false; }
        catch (Exception error) { if (!_disposed) { _panel = returnPanel; _dialogError = FriendlyError(error); if (returnPanel == DialogKind.None) _error = _dialogError; } return false; }
        finally { _operationCancellation = null; _busy = false; Invalidate(); }
    }
    private void CancelOperation() { _cancelRequested = true; _operationCancellation?.Cancel(); Invalidate(); }

    private void TrashSelected()
    {
        if (!CanDelete) return;
        _operationArchive = _snapshot!.ArchivePath!;
        _editEntries = SelectedRows.Select(row => row.Path).ToArray();
        _password = _archivePassword ?? "";
        _dialogError = ""; _panel = DialogKind.DeleteEntries; Invalidate();
    }

    private ArchiveEditService ArchiveEditor => EditService ??= new ArchiveEditService(Engine, FileService);

    private void PrepareRename()
    {
        if (!CanRename) return;
        var item = SelectedRows[0];
        _editEntries = [item.Path]; _newName = item.Name; _operationArchive = _snapshot?.ArchivePath ?? "";
        _password = _archivePassword ?? ""; _dialogError = ""; _panel = DialogKind.Rename; Invalidate();
    }

    private Task RenameSelectedAsync(string newName, string? password, IProgress<ArchiveOperationProgress> progress, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(newName) || newName is "." or ".." || newName.IndexOfAny(['/', '\\', '\0']) >= 0)
            throw new ArgumentException("请输入不包含路径分隔符的名称。");
        return ArchiveEditor.RenameAsync(_operationArchive, _editEntries[0], newName, password, progress, token);
    }

    private void ShowProperties()
    {
        var rows = SelectedRows; if (rows.Length == 0) return;
        ShowMessageDialog("属性", string.Join("\n\n", rows.Take(8).Select(row => $"{row.Name}\n类型：{row.TypeLabel}\n大小：{row.SizeLabel}\n修改时间：{row.ModifiedLabel}\n位置：{row.Path}")));
    }
    private void ShowAbout() => ShowMessageDialog("关于 BubuZip", "基于 EclipseUI 的桌面压缩解压工具。\n\n操作：双击或 Enter 打开，Ctrl/⌘+O 打开压缩包，Ctrl/⌘+A 全选，Ctrl/⌘+R 刷新，F2 重命名。\n\n支持 ZIP / 7z 创建、修改，以及归档浏览、提取和测试。");
    private void ShowMessageDialog(string title, string message)
    {
        _messageTitle = title; _messageBody = message; _dialogError = "";
        _panel = DialogKind.Message; Invalidate();
    }
    private void HandleKey(RoutedEventArgs args)
    {
        if (args is not KeyEventArgs key) return;
        if (key.Key == Key.Escape && _panel != DialogKind.None)
        {
            args.Handled = true;
            if (_busy) CancelOperation(); else CloseDialog();
            return;
        }
        if (_panel != DialogKind.None || _busy || _pickerBusy) return;
        if (key.Key == Key.F2) { args.Handled = true; PrepareRename(); return; }
        if (key.Modifiers.HasFlag(KeyModifiers.Meta) || key.Modifiers.HasFlag(KeyModifiers.Control))
        {
            if (key.Key == Key.O) { args.Handled = true; ChooseAndOpen(); }
            else if (key.Key == Key.A) { args.Handled = true; SelectAll(); }
            else if (key.Key == Key.R) { args.Handled = true; Refresh(); }
            else if (key.Key == Key.C && Files.IsFocusWithin && CanCopy) { args.Handled = true; CopySelected(); }
        }
    }
    private void CycleTheme()
    {
        SetSelectedTheme((Appearance?.Variant ?? _theme) switch { ThemeVariant.System => ThemeVariant.Light, ThemeVariant.Light => ThemeVariant.Dark, _ => ThemeVariant.System });
    }
    private static string ArchiveBaseName(string path) => ArchivePaths.GetArchiveBaseName(path);
    private static string UniqueDestination(string folder, string name, string extension)
    {
        if (string.IsNullOrWhiteSpace(name)) name = "Archive";
        var path = Path.Combine(folder, name + extension);
        for (var suffix = 2; File.Exists(path) || Directory.Exists(path); suffix++) path = Path.Combine(folder, $"{name} {suffix}{extension}");
        return path;
    }
    private static string FriendlyError(Exception error) => error is SharpCompress.Common.CryptographicException
        ? "密码不正确，或压缩包的加密数据已损坏。" : error.Message;
    private void ReportError(Exception error)
    {
        if (_disposed) return;
        _error = FriendlyError(error);
        if (OperationOnly) { _messageTitle = "无法执行操作"; _messageBody = _error; _panel = DialogKind.Message; }
        Invalidate();
    }
    protected override void OnDispose()
    {
        _disposed = true; _operationCancellation?.Cancel(); _lifetime.Cancel();
        _createPopupClosedForOperation?.TrySetResult(false);
        _standaloneOperationClosed?.TrySetResult(new(false, true, null));
        _standaloneOperationView?.Dispose();
        try { _operations.StopAsync().GetAwaiter().GetResult(); }
        finally { DiscardPendingClipboardFiles(); ClearNestedArchives(); _operationCancellation?.Dispose(); _operations.Dispose(); _lifetime.Dispose(); base.OnDispose(); }
    }
}
