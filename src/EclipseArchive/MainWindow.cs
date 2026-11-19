using System.Diagnostics;
using EclipseArchive.Core;
using EclipseUI;
using EclipseUI.Elements;
using EclipseUI.Themes.Fluent;
using Path = System.IO.Path;

namespace EclipseArchive;

public partial class MainWindow
{
    [UiParameter] public Action<IHostTheme> ApplyTheme { get; set; } = _ => { };
    [UiParameter] public IArchiveEngine Engine { get; set; } = null!;
    [UiParameter] public ArchiveFileService FileService { get; set; } = null!;
    [UiParameter] public ArchiveEditService? EditService { get; set; }
    [UiParameter] public IDesktopServices Desktop { get; set; } = null!;
    [UiParameter] public string InitialPath { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private Grid RootGrid = null!;
    private Menu MainMenu = null!;
    private TextBox AddressBox = null!;
    private ListBox Files = null!;
    private BrowserService _browser = null!;
    private BrowserSnapshot? _snapshot;
    private readonly Stack<(BrowserSnapshot Snapshot, string? Password)> _history = new();
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operationCancellation;
    private readonly BackgroundOperations _operations = new();
    private readonly string[] _formatNames = ["ZIP", "7z"];
    private string[] _sources = [];
    private string[]? _extractEntries;
    private string[] _editEntries = [];
    private string _newName = "", _editDirectory = "";
    private bool _replaceExisting;
    private string? _archivePassword;
    private string _operationArchive = "", _destination = "", _password = "", _error = "", _dialogError = "";
    private string _progressText = "", _sortColumn = "name", _lastClick = "";
    private long _lastClickTime;
    private bool _busy, _disposed, _showHidden, _sortDescending, _encryptNames, _cancelRequested;
    private int _formatIndex;
    private int FormatIndex
    {
        get => _formatIndex;
        set
        {
            _formatIndex = value;
            if (_panel == DialogKind.Create && _destination.Length > 0)
                _destination = Path.ChangeExtension(_destination, value == 1 ? ".7z" : ".zip");
            if (value == 0) { _password = ""; _encryptNames = false; }
            Invalidate();
        }
    }
    private double _progress = -1;
    private ThemeVariant _theme = ThemeVariant.System;
    private DialogKind _panel;
    private enum DialogKind { None, Create, Extract, Test, Unlock, Progress, AddToArchive, DeleteEntries, Rename }

    public string StatusMessage { get; private set; } = "就绪";
    public IReadOnlyList<BrowserItem> VisibleItems { get; private set; } = [];
    public BrowserSnapshot? CurrentLocation => _snapshot;
    public bool TaskIsRunning => _busy;
    private BrowserItem[] SelectedRows => Files?.SelectedItems.OfType<BrowserItem>().ToArray() ?? [];
    private bool HasSelection => SelectedRows.Length > 0;
    private bool CanManageFiles => !_busy && _snapshot?.ArchivePath is null && HasSelection;
    private bool CanEditArchive => !_busy && _snapshot?.ArchivePath is { } path &&
        (Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase) ||
         Path.GetExtension(path).Equals(".7z", StringComparison.OrdinalIgnoreCase));
    private bool CanAdd => !_busy && (_snapshot?.ArchivePath is null || CanEditArchive);
    private bool CanCopy => !_busy && HasSelection;
    private bool CanDelete => CanManageFiles || CanEditArchive && HasSelection;
    private bool CanRename => HasSelection && SelectedRows.Length == 1 && (CanManageFiles || CanEditArchive);
    private bool CanExtract => !_busy && (_snapshot?.ArchivePath is not null || SelectedRows.Any(row => row.IsArchive));
    private string SelectionSummary => HasSelection
        ? $"已选择 {SelectedRows.Length} 项 · {BrowserItem.FormatSize(SelectedRows.Where(row => !row.IsDirectory).Sum(row => row.Size))}"
        : _snapshot?.ArchivePath is not null ? CanEditArchive ? "归档浏览 · 可修改" : "归档浏览 · 只读" : "本地文件";
    private string SourceSummary => _sources.Length == 0 ? "请选择要添加的文件或文件夹" :
        _sources.Length == 1 ? Path.GetFileName(_sources[0]) : $"已选择 {_sources.Length} 个文件或文件夹";
    private string DialogTitle => _panel switch
    {
        DialogKind.Create => "添加到压缩包", DialogKind.Extract => "提取文件", DialogKind.Test => "测试压缩包",
        DialogKind.Unlock => "打开压缩包", DialogKind.Progress => "正在处理",
        DialogKind.AddToArchive => "添加到当前压缩包", DialogKind.DeleteEntries => "从压缩包删除", DialogKind.Rename => "重命名",
        _ => "",
    };
    private string DialogDescription => _panel switch
    {
        DialogKind.Create => "将所选文件创建为新的压缩包。已有文件不会被覆盖。",
        DialogKind.Extract => Path.GetFileName(_operationArchive) + (_extractEntries is null ? " · 全部内容" : $" · 提取 {_extractEntries.Length} 个所选项目"),
        DialogKind.Test => Path.GetFileName(_operationArchive) + " · 读取所有文件，检查内容是否可以完整解压。",
        DialogKind.Unlock => Path.GetFileName(_operationArchive) + " · 此压缩包可能需要密码。",
        DialogKind.Progress => _cancelRequested ? "正在取消，请稍候…" : "操作完成前请保持应用打开。",
        DialogKind.AddToArchive => Path.GetFileName(_operationArchive) + "!/" + _editDirectory,
        DialogKind.DeleteEntries => $"将从 {Path.GetFileName(_operationArchive)} 中删除所选 {_editEntries.Length} 个项目及其内容。",
        DialogKind.Rename => _editEntries.FirstOrDefault() ?? "",
        _ => "",
    };
    private bool IsArchiveEditDialog => Path.GetExtension(_operationArchive).Equals(".7z", StringComparison.OrdinalIgnoreCase) &&
        _panel is DialogKind.AddToArchive or DialogKind.DeleteEntries or DialogKind.Rename;
    private string DialogConfirmLabel => _panel switch
    {
        DialogKind.Unlock => "打开", DialogKind.AddToArchive => "保存修改",
        DialogKind.DeleteEntries => "删除", DialogKind.Rename => "保存", _ => "开始",
    };

    protected override void OnInitialized()
    {
        base.OnInitialized();
        _browser = new BrowserService(Engine);
        foreach (var item in MainMenu.Children.OfType<MenuItem>())
            if (item.Submenu is ContextMenu submenu)
                item.AddHandler(Button.ClickEvent, _ => submenu.Placement = Placement.Bottom);
        Files.ItemTemplate = CreateRow;
        Files.Selection.Changed += Invalidate;
        Files.AddHandler(Element.KeyDownEvent, args =>
        {
            if (args is KeyEventArgs { Key: Key.Enter }) { args.Handled = true; OpenSelected(); }
            else if (args is KeyEventArgs { Key: Key.Backspace }) { args.Handled = true; GoUp(); }
        });
        AddressBox.AddHandler(Element.KeyDownEvent, args =>
        {
            if (args is KeyEventArgs { Key: Key.Enter }) { args.Handled = true; OpenAddress(); }
        });
        RootGrid.AddHandler(Element.KeyDownEvent, HandleKey);
        try { ApplySnapshot(_browser.BrowseDirectory(Directory.Exists(InitialPath) ? InitialPath : Path.GetDirectoryName(InitialPath)!)); }
        catch (Exception error) { _error = FriendlyError(error); ApplySnapshot(_browser.BrowseDirectory(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))); }
    }

    public async Task NavigateToAsync(string path, string prefix = "", string? password = null, bool remember = true)
    {
        if (_busy || _disposed) return;
        _busy = true; _error = ""; Invalidate();
        try
        {
            BrowserSnapshot? snapshot = null;
            var showHidden = _showHidden;
            await _operations.RunAsync(async token =>
            {
                snapshot = Directory.Exists(path)
                    ? _browser.BrowseDirectory(path, showHidden)
                    : await _browser.BrowseArchiveAsync(path, prefix, password, token).ConfigureAwait(false);
            }, _lifetime.Token);
            if (_disposed) return;
            if (remember && _snapshot is not null && snapshot!.DisplayPath != _snapshot.DisplayPath) _history.Push((_snapshot, _archivePassword));
            _archivePassword = snapshot!.ArchivePath is null ? null : password;
            ApplySnapshot(snapshot);
        }
        finally { _busy = false; Invalidate(); }
    }

    public async Task NavigateUpAsync()
    {
        if (_snapshot is null) return;
        if (_snapshot.ArchivePath is not null)
        {
            var current = _snapshot.ArchivePrefix.TrimEnd('/');
            if (current.Length == 0) await NavigateToAsync(_snapshot.DirectoryPath);
            else await NavigateToAsync(_snapshot.ArchivePath, current.Contains('/') ? current[..current.LastIndexOf('/')] : "", _archivePassword);
        }
        else if (Directory.GetParent(_snapshot.DirectoryPath) is { } parent) await NavigateToAsync(parent.FullName);
    }

    public async Task OpenItemAsync(BrowserItem item)
    {
        if (_snapshot is null || _busy) return;
        if (_snapshot.ArchivePath is not null)
        {
            if (item.IsDirectory) await NavigateToAsync(_snapshot.ArchivePath, item.Path, _archivePassword);
            else
            {
                var archive = _snapshot.ArchivePath;
                var password = _archivePassword;
                var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EclipseArchive", "Opened", Guid.NewGuid().ToString("N"));
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
                }, "文件已提取并打开");
                if (opened && !_disposed && File.Exists(target)) Desktop.Open(target);
                else if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }
        else if (item.IsDirectory) await NavigateToAsync(item.Path);
        else if (item.IsArchive) await TryOpenArchiveAsync(item.Path);
        else Desktop.Open(item.Path);
    }

    private void ApplySnapshot(BrowserSnapshot snapshot)
    {
        _snapshot = snapshot;
        AddressBox.Text = snapshot.DisplayPath;
        Title = snapshot.ArchivePath is null ? "EclipseArchive — " + Path.GetFileName(snapshot.DirectoryPath) : "EclipseArchive — " + Path.GetFileName(snapshot.ArchivePath);
        UpdateRows();
        StatusMessage = $"{snapshot.Items.Count(item => item.IsDirectory)} 个文件夹，{snapshot.Items.Count(item => !item.IsDirectory)} 个文件 · {BrowserItem.FormatSize(snapshot.Items.Where(item => !item.IsDirectory).Sum(item => item.Size))}";
        Invalidate();
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
        Invalidate();
    }

    private Element CreateRow(object? value)
    {
        var item = (BrowserItem)value!;
        var grid = new Grid { ColumnDefinitions = "*,105,105,115,170,72", Height = 28, ClipToBounds = true };
        var values = new[] { item.Name, item.SizeLabel, item.PackedSizeLabel, item.TypeLabel, item.ModifiedLabel, item.AttributesLabel };
        for (var index = 0; index < values.Length; index++)
        {
            var cell = new TextBlock { FontSize = 13, Padding = 5, ClipToBounds = true };
            cell.AddRun(values[index]); Grid.SetColumn(cell, index); grid.Children.Add(cell);
        }
        grid.AddHandler(Element.PointerPressedEvent, args =>
        {
            if (args is PointerEventArgs { Button: PointerButton.Right }) Files.SelectedItem = item;
        });
        grid.AddHandler(Element.PointerReleasedEvent, args =>
        {
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
        try { await NavigateToAsync(previous.Snapshot.ArchivePath ?? previous.Snapshot.DirectoryPath, previous.Snapshot.ArchivePrefix, previous.Password, false); }
        catch (Exception error) { _history.Push(previous); ReportError(error); }
    }
    private async void GoHome() => await NavigateSafelyAsync(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    private async void GoDesktop() => await NavigateSafelyAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop"));
    private async void GoDownloads() => await NavigateSafelyAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
    private async Task NavigateSafelyAsync(string path) { try { await NavigateToAsync(path); } catch (Exception error) { ReportError(error); } }
    private async void OpenAddress()
    {
        var address = AddressBox.Text.Trim();
        if (address.StartsWith('~')) address = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + address[1..];
        var marker = address.IndexOf("!/", StringComparison.Ordinal);
        try
        {
            if (marker >= 0) await NavigateToAsync(address[..marker], address[(marker + 2)..], _archivePassword);
            else if (File.Exists(address)) await TryOpenArchiveAsync(address);
            else await NavigateToAsync(address);
        }
        catch (Exception error) { ReportError(error); }
    }
    private async void ChooseAndOpen()
    {
        try { if (Desktop.ChooseArchive() is { } file) await TryOpenArchiveAsync(file); }
        catch (Exception error) { ReportError(error); }
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
        if (_snapshot is null) return;
        try { await NavigateToAsync(_snapshot.ArchivePath ?? _snapshot.DirectoryPath, _snapshot.ArchivePrefix, _archivePassword, false); }
        catch (Exception error) { ReportError(error); }
    }
    private void ToggleHidden() { _showHidden = !_showHidden; Refresh(); }
    private void SortBy(string column) { _sortDescending = _sortColumn == column && !_sortDescending; _sortColumn = column; UpdateRows(); }
    private void SelectAll() { if (VisibleItems.Count > 0) { Files.Selection.Select(0); Files.Selection.Select(VisibleItems.Count - 1, extend: true); } }
    private void RevealLocation() { try { Desktop.Reveal(_snapshot?.ArchivePath ?? _snapshot?.DirectoryPath ?? InitialPath); } catch (Exception error) { ReportError(error); } }
    private void DismissError() { _error = ""; Invalidate(); }

    private void PrepareCreate()
    {
        if (_busy) return;
        if (_snapshot?.ArchivePath is not null)
        {
            if (!CanEditArchive) return;
            _operationArchive = _snapshot.ArchivePath; _editDirectory = _snapshot.ArchivePrefix;
            _sources = []; _password = _archivePassword ?? ""; _replaceExisting = false; _dialogError = "";
            _panel = DialogKind.AddToArchive; Invalidate(); return;
        }
        _sources = _snapshot?.ArchivePath is null ? SelectedRows.Select(row => row.Path).ToArray() : [];
        _formatIndex = 0; _password = ""; _encryptNames = false; _dialogError = "";
        _destination = UniqueDestination(_snapshot?.DirectoryPath ?? InitialPath, _sources.Length == 1 ? Path.GetFileNameWithoutExtension(_sources[0]) : "Archive", ".zip");
        _panel = DialogKind.Create; Invalidate();
    }
    private void ChooseCreateInputs()
    {
        try { var paths = Desktop.ChooseInputs(); if (paths.Count > 0) { _sources = paths.ToArray(); Invalidate(); } }
        catch (Exception error) { _dialogError = FriendlyError(error); Invalidate(); }
    }
    private void PrepareExtract()
    {
        if (_busy) return;
        _operationArchive = _snapshot?.ArchivePath ?? SelectedRows.FirstOrDefault(row => row.IsArchive)?.Path ?? "";
        if (_operationArchive.Length == 0) return;
        _extractEntries = _snapshot?.ArchivePath is not null
            ? HasSelection ? SelectedRows.Select(row => row.Path).ToArray() : _snapshot.ArchivePrefix.Length > 0 ? [_snapshot.ArchivePrefix] : null
            : null;
        _password = _snapshot?.ArchivePath is not null ? _archivePassword ?? "" : "";
        _destination = UniqueDestination(Path.GetDirectoryName(_operationArchive)!, ArchiveBaseName(_operationArchive), "");
        _dialogError = ""; _panel = DialogKind.Extract; Invalidate();
    }
    private void PrepareTest()
    {
        if (_busy) return;
        _operationArchive = _snapshot?.ArchivePath ?? SelectedRows.FirstOrDefault(row => row.IsArchive)?.Path ?? "";
        if (_operationArchive.Length == 0) return;
        _password = _snapshot?.ArchivePath is not null ? _archivePassword ?? "" : "";
        _dialogError = ""; _panel = DialogKind.Test; Invalidate();
    }
    private void ChooseDestination()
    {
        try
        {
            if (_panel == DialogKind.Create)
            {
                var suggested = Path.ChangeExtension(Path.GetFileName(_destination), _formatIndex == 1 ? ".7z" : ".zip");
                if (Desktop.ChooseSavePath(suggested, Path.GetDirectoryName(_destination)) is { } path) _destination = path;
            }
            else if (Desktop.ChooseFolder(Path.GetDirectoryName(_destination)) is { } folder)
                _destination = UniqueDestination(folder, ArchiveBaseName(_operationArchive), "");
            Invalidate();
        }
        catch (Exception error) { _dialogError = FriendlyError(error); Invalidate(); }
    }
    private void CloseDialog() { if (!_busy) { _panel = DialogKind.None; _password = ""; _dialogError = ""; Invalidate(); } }
    private async void ConfirmDialog()
    {
        if (_busy || _panel is DialogKind.None or DialogKind.Progress) return;
        var mode = _panel;
        try
        {
            if (mode == DialogKind.Unlock)
            {
                await NavigateToAsync(_operationArchive, password: PasswordOrNull);
                _panel = DialogKind.None; _password = ""; Invalidate(); return;
            }
            if (mode is DialogKind.Create or DialogKind.AddToArchive && _sources.Length == 0)
                throw new InvalidOperationException("请先选择要压缩的文件或文件夹。");
            if (mode == DialogKind.Create && _formatIndex == 1 && _encryptNames && PasswordOrNull is null)
                throw new InvalidOperationException("加密文件名需要先设置密码。");
            if (mode is DialogKind.Create or DialogKind.Extract && string.IsNullOrWhiteSpace(_destination)) throw new InvalidOperationException("请选择输出路径。");
            var password = mode == DialogKind.Create && _formatIndex == 0 ? null : PasswordOrNull;
            var destination = mode is DialogKind.Create or DialogKind.Extract ? Path.GetFullPath(_destination) : "";
            if (mode == DialogKind.Create) destination = Path.ChangeExtension(destination, _formatIndex == 1 ? ".7z" : ".zip");
            var progress = new Progress<ArchiveOperationProgress>(report =>
            {
                if (_disposed || !_busy || _panel != DialogKind.Progress) return;
                _progress = report.TotalEntries == 0 ? 0 : report.CompletedEntries * 100d / report.TotalEntries;
                _progressText = $"{report.CompletedEntries} / {report.TotalEntries}  ·  {report.EntryPath}"; Invalidate();
            });
            var completed = await RunOperationAsync(async token =>
            {
                if (mode == DialogKind.Create) await FileService.CreateAsync(_sources, destination,
                    _formatIndex == 1 ? ArchiveFormat.SevenZip : ArchiveFormat.Zip, password,
                    _formatIndex == 1 && _encryptNames, progress, token);
                else if (mode == DialogKind.Extract) await FileService.ExtractAsync(_operationArchive, destination, password, _extractEntries, progress, token);
                else if (mode == DialogKind.Test) await FileService.TestAsync(_operationArchive, password, progress, token);
                else if (mode == DialogKind.AddToArchive) await ArchiveEditor.AddAsync(_operationArchive, _sources, _replaceExisting,
                    password, progress, token, archiveDirectory: _editDirectory);
                else if (mode == DialogKind.DeleteEntries) await ArchiveEditor.DeleteAsync(_operationArchive, _editEntries, password, progress, token);
                else if (mode == DialogKind.Rename) await RenameSelectedAsync(_newName, password, progress, token);
            }, mode switch
            {
                DialogKind.Test => "测试完成，所有文件均可读取。", DialogKind.Create => "压缩包创建完成。",
                DialogKind.Extract => "提取完成。", DialogKind.AddToArchive => "压缩包已更新。",
                DialogKind.DeleteEntries => "所选归档项目已删除。", _ => "重命名完成。",
            }, mode);
            if (completed && mode is DialogKind.Create or DialogKind.Extract)
            {
                var message = StatusMessage;
                await NavigateToAsync(mode == DialogKind.Create ? Path.GetDirectoryName(destination)! : destination);
                StatusMessage = message; Invalidate();
            }
            else if (completed && mode is DialogKind.AddToArchive or DialogKind.DeleteEntries or DialogKind.Rename)
            {
                var message = StatusMessage;
                if (_snapshot?.ArchivePath is { } archivePath)
                    await NavigateToAsync(archivePath, _snapshot.ArchivePrefix, password, remember: false);
                else if (_snapshot is not null) await NavigateToAsync(_snapshot.DirectoryPath, remember: false);
                StatusMessage = message; Invalidate();
            }
        }
        catch (Exception error) { _dialogError = FriendlyError(error); Invalidate(); }
    }
    private string? PasswordOrNull => string.IsNullOrEmpty(_password) ? null : _password;

    private async Task<bool> RunOperationAsync(Func<CancellationToken, Task> action, string success, DialogKind returnPanel = DialogKind.None)
    {
        if (_busy) return false;
        _busy = true; _panel = DialogKind.Progress; _progress = -1; _progressText = "正在准备…";
        _dialogError = ""; _error = ""; _cancelRequested = false;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operationCancellation = cancellation; Invalidate();
        try
        {
            await _operations.RunAsync(action, cancellation.Token);
            if (_disposed) return false;
            StatusMessage = success; _panel = DialogKind.None; _password = ""; return true;
        }
        catch (OperationCanceledException) { if (!_disposed) { StatusMessage = "操作已取消"; _panel = DialogKind.None; _password = ""; } return false; }
        catch (Exception error) { if (!_disposed) { _panel = returnPanel; _dialogError = FriendlyError(error); if (returnPanel == DialogKind.None) _error = _dialogError; } return false; }
        finally { _operationCancellation = null; _busy = false; Invalidate(); }
    }
    private void CancelOperation() { _cancelRequested = true; _operationCancellation?.Cancel(); Invalidate(); }

    private async void CopySelected()
    {
        if (_snapshot?.ArchivePath is not null) PrepareExtract();
        else await TransferSelectedAsync(false);
    }
    private async void MoveSelected() => await TransferSelectedAsync(true);
    private async Task TransferSelectedAsync(bool move)
    {
        if (!CanManageFiles) return;
        try
        {
            var rows = SelectedRows.Select(row => row with { Path = ArchivePaths.ResolveUserPath(row.Path) }).ToArray();
            if (Desktop.ChooseFolder(_snapshot?.DirectoryPath) is not { } selectedFolder) return;
            var folder = ArchivePaths.ResolveUserPath(selectedFolder);
            await RunOperationAsync(token => Task.Run(() =>
            {
                var targets = rows.Select(row => (row, target: Path.Combine(folder, row.Name))).ToArray();
                foreach (var (row, target) in targets)
                {
                    if (row.IsLink) throw new NotSupportedException("暂不支持复制或移动符号链接。");
                    RejectLinkedAncestors(row.Path);
                    RejectLinkedAncestors(folder);
                    if (File.Exists(target) || Directory.Exists(target)) throw new IOException($"目标已存在：{row.Name}");
                    if (row.IsDirectory && (string.Equals(Path.GetFullPath(folder), Path.GetFullPath(row.Path), StringComparison.OrdinalIgnoreCase) ||
                        Path.GetFullPath(folder).StartsWith(Path.GetFullPath(row.Path) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                        throw new IOException("目标文件夹不能位于源文件夹内。");
                }
                foreach (var (row, target) in targets)
                {
                    token.ThrowIfCancellationRequested();
                    if (move) { if (row.IsDirectory) Directory.Move(row.Path, target); else File.Move(row.Path, target); }
                    else
                    {
                        var staging = Path.Combine(folder, ".EclipseArchive-copy-" + Guid.NewGuid().ToString("N"));
                        try
                        {
                            CopyFileOrDirectory(row.Path, staging, token);
                            token.ThrowIfCancellationRequested();
                            if (row.IsDirectory) Directory.Move(staging, target); else File.Move(staging, target);
                        }
                        finally
                        {
                            if (Directory.Exists(staging)) Directory.Delete(staging, true);
                            else if (File.Exists(staging)) File.Delete(staging);
                        }
                    }
                }
            }, token), move ? "所选项目已移动。" : "所选项目已复制。");
            await RefreshPreservingStatusAsync();
        }
        catch (Exception error) { ReportError(error); }
    }
    private static void CopyFileOrDirectory(string source, string target, CancellationToken token)
    {
        var info = new FileInfo(source);
        if (info.LinkTarget is not null || (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new NotSupportedException("暂不支持复制符号链接。");
        token.ThrowIfCancellationRequested();
        if (!Directory.Exists(source))
        {
            using var input = File.OpenRead(source);
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
            var buffer = new byte[64 * 1024];
            int length;
            while ((length = input.Read(buffer)) > 0)
            {
                token.ThrowIfCancellationRequested(); output.Write(buffer, 0, length);
            }
            token.ThrowIfCancellationRequested();
            return;
        }
        Directory.CreateDirectory(target);
        foreach (var path in Directory.EnumerateFileSystemEntries(source)) CopyFileOrDirectory(path, Path.Combine(target, Path.GetFileName(path)), token);
    }
    private async void TrashSelected()
    {
        if (CanEditArchive && HasSelection)
        {
            _operationArchive = _snapshot!.ArchivePath!; _editEntries = SelectedRows.Select(row => row.Path).ToArray();
            _password = _archivePassword ?? ""; _dialogError = ""; _panel = DialogKind.DeleteEntries;
            Invalidate(); return;
        }
        if (!CanManageFiles) return;
        try
        {
            var rows = SelectedRows;
            if (!Desktop.Confirm("移到废纸篓", $"将选中的 {rows.Length} 个项目移到废纸篓？", "移到废纸篓")) return;
            try { Desktop.Trash(rows.Select(row => row.Path).ToArray()); StatusMessage = "所选项目已移到废纸篓。"; }
            catch (Exception error) { ReportError(error); }
            await RefreshPreservingStatusAsync();
        }
        catch (Exception error) { ReportError(error); }
    }

    private ArchiveEditService ArchiveEditor => EditService ??= new ArchiveEditService(Engine, FileService);

    private void PrepareRename()
    {
        if (!CanRename) return;
        var item = SelectedRows[0];
        _editEntries = [item.Path]; _newName = item.Name; _operationArchive = _snapshot?.ArchivePath ?? "";
        _password = _archivePassword ?? ""; _dialogError = ""; _panel = DialogKind.Rename; Invalidate();
    }

    private async Task RenameSelectedAsync(string newName, string? password, IProgress<ArchiveOperationProgress> progress, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(newName) || newName is "." or ".." || newName.IndexOfAny(['/', '\\', '\0']) >= 0)
            throw new ArgumentException("请输入不包含路径分隔符的名称。");
        if (_operationArchive.Length > 0)
            await ArchiveEditor.RenameAsync(_operationArchive, _editEntries[0], newName, password, progress, token).ConfigureAwait(false);
        else
        {
            var source = _editEntries[0];
            var destination = Path.Combine(Path.GetDirectoryName(source)!, newName);
            if (source == destination) return;
            RejectLinkedAncestors(source);
            if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("该名称已经存在。");
            token.ThrowIfCancellationRequested();
            if (Directory.Exists(source)) Directory.Move(source, destination); else File.Move(source, destination);
        }
    }

    private async Task RefreshPreservingStatusAsync()
    {
        if (_disposed || _snapshot is null) return;
        var status = StatusMessage;
        var error = _error;
        try { await NavigateToAsync(_snapshot.DirectoryPath, remember: false); }
        finally { StatusMessage = status; _error = error; Invalidate(); }
    }

    private static void RejectLinkedAncestors(string path)
    {
        path = ArchivePaths.ResolveUserPath(path);
        FileSystemInfo? info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        while (info is not null)
        {
            if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new NotSupportedException("暂不支持通过符号链接路径复制或移动，请选择文件的实际位置。");
            info = info is DirectoryInfo directory ? directory.Parent : ((FileInfo)info).Directory;
        }
    }
    private void ShowProperties()
    {
        var rows = SelectedRows; if (rows.Length == 0) return;
        Desktop.ShowMessage("属性", string.Join("\n\n", rows.Take(8).Select(row => $"{row.Name}\n类型：{row.TypeLabel}\n大小：{row.SizeLabel}\n修改时间：{row.ModifiedLabel}\n位置：{row.Path}")));
    }
    private void ShowAbout() => Desktop.ShowMessage("关于 EclipseArchive", "基于 EclipseUI 的 macOS 压缩解压工具。\n\n操作：双击或 Return 打开，⌘O 打开压缩包，⌘A 全选，⌘R 刷新，F2 重命名。\n\n支持 ZIP / 7z 创建、修改，以及归档浏览、提取和测试。");
    private void HandleKey(RoutedEventArgs args)
    {
        if (args is not KeyEventArgs key) return;
        if (key.Key == Key.Escape && _panel != DialogKind.None)
        {
            args.Handled = true;
            if (_busy) CancelOperation(); else CloseDialog();
            return;
        }
        if (_panel != DialogKind.None || _busy) return;
        if (key.Key == Key.F2) { args.Handled = true; PrepareRename(); return; }
        if (key.Modifiers.HasFlag(KeyModifiers.Meta) || key.Modifiers.HasFlag(KeyModifiers.Control))
        {
            if (key.Key == Key.O) { args.Handled = true; ChooseAndOpen(); }
            else if (key.Key == Key.R) { args.Handled = true; Refresh(); }
            else if (key.Key == Key.L) { args.Handled = true; AddressBox.RequestFocus(); AddressBox.Select(0, AddressBox.Text.Length); }
        }
    }
    private void CycleTheme()
    {
        _theme = _theme switch { ThemeVariant.System => ThemeVariant.Light, ThemeVariant.Light => ThemeVariant.Dark, _ => ThemeVariant.System };
        ApplyTheme(FluentTheme.For(_theme));
    }
    private static string ArchiveBaseName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }
    private static string UniqueDestination(string folder, string name, string extension)
    {
        if (string.IsNullOrWhiteSpace(name)) name = "Archive";
        var path = Path.Combine(folder, name + extension);
        for (var suffix = 2; File.Exists(path) || Directory.Exists(path); suffix++) path = Path.Combine(folder, $"{name} {suffix}{extension}");
        return path;
    }
    private static string FriendlyError(Exception error) => error is SharpCompress.Common.CryptographicException
        ? "密码不正确，或压缩包的加密数据已损坏。" : error.Message;
    private void ReportError(Exception error) { if (!_disposed) { _error = FriendlyError(error); Invalidate(); } }
    protected override void OnDispose()
    {
        _disposed = true; _operationCancellation?.Cancel(); _lifetime.Cancel();
        try { _operations.StopAsync().GetAwaiter().GetResult(); }
        finally { _operationCancellation?.Dispose(); _operations.Dispose(); _lifetime.Dispose(); base.OnDispose(); }
    }
}
