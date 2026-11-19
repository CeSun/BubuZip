using System.Diagnostics;
using System.Text;
using EclipseArchive.Core;
using EclipseUI;
using EclipseUI.Elements;
using EclipseUI.Themes.Fluent;
using Xunit;
using Path = System.IO.Path;

// The headless UI belongs to the test thread. Pump its synchronization context while
// navigation runs so every UI continuation and assertion stays on that same thread.
#pragma warning disable xUnit1031

namespace EclipseArchive.Ui.Tests;

public sealed class MainWindowTests
{
    private static readonly string[] ToolbarLabels =
        ["＋ 添加", "↓ 提取", "测试", "复制", "移动", "删除", "信息", "打开…"];

    [Fact]
    public void MainWindowShowsTheArchiveToolbarMenusAndFileColumns()
    {
        using var fixture = new WindowFixture();
        fixture.WriteFile("说明.txt", "本地文件");
        fixture.OpenWindow();

        var toolbar = Assert.Single(fixture.Elements<WrapPanel>(), panel =>
            panel.Children.OfType<Button>().Any(button => button.Label == "＋ 添加"));
        Assert.Equal(ToolbarLabels, toolbar.Children.OfType<Button>().Select(button => button.Label));

        var menu = Assert.Single(fixture.Elements<Menu>(), candidate =>
            candidate.Children.OfType<MenuItem>().Any(item => item.Label == "文件"));
        Assert.Equal(new[] { "文件", "编辑", "查看", "收藏夹", "帮助" },
            menu.Children.OfType<MenuItem>().Select(item => item.Label));
        var fileMenu = Assert.Single(menu.Children.OfType<MenuItem>(), item => item.Label == "文件");
        var fileCommands = Assert.IsType<ContextMenu>(fileMenu.Submenu);
        Assert.Contains(fileCommands.Items.OfType<MenuItem>(), item => item.Label == "添加到压缩包…");
        Assert.Contains(fileCommands.Items.OfType<MenuItem>(), item => item.Label == "测试压缩包");

        var header = Assert.Single(fixture.Elements<Grid>(), grid =>
            grid.Children.OfType<Button>().Any(button => button.Label == "名称"));
        Assert.Equal(new[] { "名称", "大小", "压缩后", "类型", "修改时间", "属性" },
            header.Children.OfType<Button>().Select(button => button.Label));
        Assert.Equal(SelectionMode.Extended, fixture.Files.SelectionMode);
        Assert.Equal("说明.txt", Assert.Single(fixture.Files.ItemValues.OfType<BrowserItem>()).Name);
        Assert.Equal(fixture.DirectoryPath, fixture.Address.Text);
    }

    [Fact]
    public void CommandSelectAllUpdatesTheSelectionSummaryAndFileCommands()
    {
        using var fixture = new WindowFixture();
        fixture.WriteFile("第一份.txt", "一");
        fixture.WriteFile("第二份.txt", "二");
        fixture.OpenWindow();

        Assert.Empty(fixture.Files.SelectedItems);
        Assert.False(fixture.FindButton("复制").IsEffectiveEnabled);
        Assert.False(fixture.FindButton("移动").IsEffectiveEnabled);
        Assert.False(fixture.FindButton("删除").IsEffectiveEnabled);
        Assert.False(fixture.FindButton("信息").IsEffectiveEnabled);

        fixture.Files.RaiseEvent(new KeyEventArgs(Element.KeyDownEvent, Key.A, KeyModifiers.Meta));
        fixture.Host.UpdateLayout();

        Assert.Equal(2, fixture.Files.SelectedItems.Count);
        Assert.Equal(new[] { "第一份.txt", "第二份.txt" }.Order(StringComparer.Ordinal),
            fixture.Files.SelectedItems.OfType<BrowserItem>().Select(item => item.Name).Order(StringComparer.Ordinal));
        Assert.Contains(fixture.Texts(), text => text.StartsWith("已选择 2 项", StringComparison.Ordinal));
        Assert.True(fixture.FindButton("复制").IsEffectiveEnabled);
        Assert.True(fixture.FindButton("移动").IsEffectiveEnabled);
        Assert.True(fixture.FindButton("删除").IsEffectiveEnabled);
        Assert.True(fixture.FindButton("信息").IsEffectiveEnabled);
        Assert.False(fixture.FindButton("↓ 提取").IsEffectiveEnabled);

        fixture.Files.Selection.Clear();
        fixture.Host.UpdateLayout();

        Assert.Empty(fixture.Files.SelectedItems);
        Assert.Contains("本地文件", fixture.Texts());
        Assert.False(fixture.FindButton("复制").IsEffectiveEnabled);
        Assert.False(fixture.FindButton("信息").IsEffectiveEnabled);
    }

    [Fact]
    public void AddCommandLetsTheUserChooseSourcesFormatDestinationAndPasswordThenCancel()
    {
        using var fixture = new WindowFixture();
        var first = fixture.WriteFile("第一份.txt", "一");
        var second = fixture.WriteFile("第二份.txt", "二");
        fixture.Desktop.Inputs = [first, second];
        var outputPath = Path.Combine(fixture.DirectoryPath, "自选压缩包.7z");
        fixture.Desktop.SavePath = outputPath;
        fixture.OpenWindow();

        fixture.Click("＋ 添加");

        Assert.Contains("添加到压缩包", fixture.Texts());
        Assert.Contains("请选择要添加的文件或文件夹", fixture.Texts());
        Assert.Contains("压缩包路径", fixture.Texts());
        Assert.False(fixture.FindButton("＋ 添加").IsEffectiveEnabled);
        var format = Assert.Single(fixture.Elements<ComboBox>());
        Assert.Equal(new[] { "ZIP", "7z" }, format.EffectiveItems.Cast<string>());
        Assert.Equal(0, format.SelectedIndex);
        Assert.Empty(fixture.Elements<PasswordBox>());
        Assert.Contains(fixture.Elements<TextBox>(), box =>
            box.Text == Path.Combine(fixture.DirectoryPath, "Archive.zip"));

        fixture.Click("选择文件…");

        Assert.Contains("已选择 2 个文件或文件夹", fixture.Texts());
        format.SelectedIndex = 1;
        fixture.Host.UpdateLayout();

        Assert.Contains("密码（无密码可留空）", fixture.Texts());
        var password = Assert.Single(fixture.Elements<PasswordBox>());
        password.Text = "本次使用的密码";
        var encryptNames = Assert.Single(fixture.Elements<CheckBox>(), box => box.Label == "加密文件名");
        encryptNames.Toggle();
        fixture.Host.UpdateLayout();

        fixture.Click("浏览…");

        Assert.EndsWith(".7z", Assert.Single(fixture.Desktop.SaveSuggestions));
        Assert.Contains(fixture.Elements<TextBox>(), box => box.Text == outputPath);
        fixture.Click("取消");

        Assert.DoesNotContain("添加到压缩包", fixture.Texts());
        Assert.Empty(fixture.Elements<ComboBox>());
        Assert.Empty(fixture.Elements<PasswordBox>());
        Assert.True(fixture.FindButton("＋ 添加").IsEffectiveEnabled);
        Assert.False(fixture.Window.TaskIsRunning);
        Assert.False(File.Exists(outputPath));
        Assert.Equal(fixture.DirectoryPath, fixture.Window.CurrentLocation!.DirectoryPath);

        fixture.Click("＋ 添加");
        var reopenedFormat = Assert.Single(fixture.Elements<ComboBox>());
        Assert.Equal(0, reopenedFormat.SelectedIndex);
        reopenedFormat.SelectedIndex = 1;
        fixture.Host.UpdateLayout();
        Assert.Equal("", Assert.Single(fixture.Elements<PasswordBox>()).Text);
        Assert.False(Assert.Single(fixture.Elements<CheckBox>(), box => box.Label == "加密文件名").Checked);
        fixture.Click("取消");
    }

    [Fact]
    public void PublicNavigationEntersALocalDirectoryAndReturnsToItsParent()
    {
        using var fixture = new WindowFixture();
        var childPath = fixture.CreateDirectory("资料");
        fixture.WriteFile("资料/你好，世界.txt", "中文内容");
        fixture.WriteFile("根文件.txt", "根目录");
        fixture.OpenWindow();

        fixture.Run(fixture.Window.NavigateToAsync(childPath));

        Assert.Equal(childPath, fixture.Window.CurrentLocation!.DirectoryPath);
        Assert.Null(fixture.Window.CurrentLocation.ArchivePath);
        Assert.Equal("你好，世界.txt", Assert.Single(fixture.Window.VisibleItems).Name);
        Assert.Equal(childPath, fixture.Address.Text);
        Assert.False(fixture.Window.TaskIsRunning);

        fixture.Run(fixture.Window.NavigateUpAsync());

        Assert.Equal(fixture.DirectoryPath, fixture.Window.CurrentLocation!.DirectoryPath);
        Assert.Equal(new[] { "资料", "根文件.txt" }.Order(StringComparer.Ordinal),
            fixture.Window.VisibleItems.Select(item => item.Name).Order(StringComparer.Ordinal));
        Assert.Equal(fixture.DirectoryPath, fixture.Address.Text);
    }

    [Fact]
    public void PublicNavigationMovesThroughRealZipFoldersAndBackToTheFilesystem()
    {
        using var fixture = new WindowFixture();
        var archivePath = fixture.CreateZip(
            ("docs/指南.txt", "使用说明"),
            ("docs/deeper/细节.txt", "深层内容"),
            ("empty/", null),
            ("root.txt", "根目录内容"));
        fixture.OpenWindow();

        fixture.Run(fixture.Window.NavigateToAsync(archivePath));

        Assert.Equal(archivePath, fixture.Window.CurrentLocation!.ArchivePath);
        Assert.Equal("", fixture.Window.CurrentLocation.ArchivePrefix);
        Assert.Equal(archivePath + "!/", fixture.Address.Text);
        Assert.Equal(new[] { "docs", "empty", "root.txt" }.Order(StringComparer.Ordinal),
            fixture.Window.VisibleItems.Select(item => item.Name).Order(StringComparer.Ordinal));
        Assert.True(fixture.FindButton("↓ 提取").IsEffectiveEnabled);
        Assert.True(fixture.FindButton("测试").IsEffectiveEnabled);

        var docs = Assert.Single(fixture.Window.VisibleItems, item => item.Name == "docs");
        fixture.Run(fixture.Window.OpenItemAsync(docs));

        Assert.Equal("docs/", fixture.Window.CurrentLocation!.ArchivePrefix);
        Assert.Equal(new[] { "deeper", "指南.txt" }.Order(StringComparer.Ordinal),
            fixture.Window.VisibleItems.Select(item => item.Name).Order(StringComparer.Ordinal));
        fixture.Run(fixture.Window.NavigateToAsync(archivePath, "docs/deeper/"));

        Assert.Equal("docs/deeper/", fixture.Window.CurrentLocation!.ArchivePrefix);
        Assert.Equal("细节.txt", Assert.Single(fixture.Window.VisibleItems).Name);
        Assert.Equal(archivePath + "!/docs/deeper/", fixture.Address.Text);
        fixture.Files.SelectedIndex = 0;
        fixture.Host.UpdateLayout();
        Assert.True(fixture.FindButton("复制").IsEffectiveEnabled);
        Assert.False(fixture.FindButton("移动").IsEffectiveEnabled);
        Assert.True(fixture.FindButton("删除").IsEffectiveEnabled);

        fixture.Run(fixture.Window.NavigateUpAsync());
        Assert.Equal("docs/", fixture.Window.CurrentLocation!.ArchivePrefix);
        fixture.Run(fixture.Window.NavigateUpAsync());
        Assert.Equal("", fixture.Window.CurrentLocation!.ArchivePrefix);
        fixture.Run(fixture.Window.NavigateUpAsync());

        Assert.Null(fixture.Window.CurrentLocation!.ArchivePath);
        Assert.Equal(fixture.DirectoryPath, fixture.Window.CurrentLocation.DirectoryPath);
        Assert.Equal(fixture.DirectoryPath, fixture.Address.Text);
        Assert.Equal("导航测试.zip", Assert.Single(fixture.Window.VisibleItems).Name);
    }

    [Fact]
    public void FirstTestCommandReadsASelectedZipWithoutRequiringAnOutputDestination()
    {
        using var fixture = new WindowFixture();
        fixture.CreateZip(("资料/你好.txt", "用于完整性测试的中文内容 📦"), ("空目录/", null));
        fixture.OpenWindow();
        fixture.SelectItem("导航测试.zip");

        fixture.Click("测试");

        Assert.Contains("测试压缩包", fixture.Texts());
        Assert.DoesNotContain("压缩包路径", fixture.Texts());
        Assert.DoesNotContain("输出文件夹（新建）", fixture.Texts());

        fixture.Click("开始");
        fixture.WaitForIdle();

        Assert.Equal("测试完成，所有文件均可读取。", fixture.Window.StatusMessage);
        Assert.Contains("测试完成，所有文件均可读取。", fixture.Texts());
        Assert.False(fixture.Window.TaskIsRunning);
        Assert.Empty(fixture.Elements<ProgressBar>());
        Assert.DoesNotContain("测试压缩包", fixture.Texts());
        Assert.Equal(fixture.DirectoryPath, fixture.Window.CurrentLocation!.DirectoryPath);
        Assert.Null(fixture.Window.CurrentLocation.ArchivePath);
    }

    [Fact]
    public void StartCommandsCreateARealZipThenExtractItsContentsToDisk()
    {
        using var fixture = new WindowFixture();
        const string content = "压缩后应当完整还原的中文、emoji 📦。\n第二行内容。";
        var sourcePath = fixture.WriteFile("中文原文.txt", content);
        var archivePath = Path.Combine(fixture.DirectoryPath, "往返归档.zip");
        fixture.Desktop.SavePath = archivePath;
        fixture.OpenWindow();
        fixture.SelectItem("中文原文.txt");

        fixture.Click("＋ 添加");

        Assert.Contains("中文原文.txt", fixture.Texts());
        Assert.Equal(0, Assert.Single(fixture.Elements<ComboBox>()).SelectedIndex);
        fixture.Click("浏览…");
        fixture.Click("开始");
        fixture.WaitForIdle();

        Assert.Equal("压缩包创建完成。", fixture.Window.StatusMessage);
        Assert.True(File.Exists(archivePath));
        Assert.Contains(fixture.Window.VisibleItems, item => item.Path == archivePath && item.IsArchive);
        var archive = Assert.Single(fixture.Window.VisibleItems, item => item.Path == archivePath);
        fixture.Run(fixture.Window.OpenItemAsync(archive));

        Assert.Equal(archivePath, fixture.Window.CurrentLocation!.ArchivePath);
        Assert.Equal("中文原文.txt", Assert.Single(fixture.Window.VisibleItems).Name);
        var extractedDirectory = Path.Combine(fixture.DirectoryPath, "往返归档");
        fixture.Click("↓ 提取");

        Assert.Contains("提取文件", fixture.Texts());
        Assert.Contains(fixture.Elements<TextBox>(), box => box.Text == extractedDirectory);
        fixture.Click("开始");
        fixture.WaitForIdle();

        Assert.Equal("提取完成。", fixture.Window.StatusMessage);
        Assert.Equal(content, File.ReadAllText(Path.Combine(extractedDirectory, "中文原文.txt")));
        Assert.Equal(content, File.ReadAllText(sourcePath));
        Assert.Equal(extractedDirectory, fixture.Window.CurrentLocation!.DirectoryPath);
        Assert.Null(fixture.Window.CurrentLocation.ArchivePath);
        Assert.Equal("中文原文.txt", Assert.Single(fixture.Window.VisibleItems).Name);
        Assert.Empty(fixture.Elements<ProgressBar>());
    }

    [Fact]
    public void SevenZipWorkflowEncryptsFilenamesAndOpensAfterEnteringThePassword()
    {
        using var fixture = new WindowFixture();
        fixture.WriteFile("机密.txt", "需要密码才能查看的内容");
        const string password = "Archive-密码-2026!";
        var archivePath = Path.Combine(fixture.DirectoryPath, "机密.7z");
        fixture.OpenWindow();
        fixture.SelectItem("机密.txt");

        fixture.Click("＋ 添加");
        Assert.Single(fixture.Elements<ComboBox>()).SelectedIndex = 1;
        fixture.Host.UpdateLayout();

        Assert.Contains(fixture.Elements<TextBox>(), box => box.Text == archivePath);
        Assert.Single(fixture.Elements<PasswordBox>()).Text = password;
        Assert.Single(fixture.Elements<CheckBox>(), box => box.Label == "加密文件名").Toggle();
        fixture.Host.UpdateLayout();
        fixture.Click("开始");
        fixture.WaitForIdle();

        Assert.Equal("压缩包创建完成。", fixture.Window.StatusMessage);
        Assert.True(File.Exists(archivePath));
        var archive = Assert.Single(fixture.Window.VisibleItems, item => item.Path == archivePath);
        fixture.Run(fixture.Window.OpenItemAsync(archive));

        Assert.Contains("打开压缩包", fixture.Texts());
        Assert.Null(fixture.Window.CurrentLocation!.ArchivePath);
        Assert.Single(fixture.Elements<PasswordBox>()).Text = password;
        fixture.Click("打开");
        fixture.WaitForIdle();

        Assert.Equal(archivePath, fixture.Window.CurrentLocation!.ArchivePath);
        var file = Assert.Single(fixture.Window.VisibleItems);
        Assert.Equal("机密.txt", file.Name);
        Assert.True(file.IsEncrypted);
        Assert.Empty(fixture.Elements<PasswordBox>());
        Assert.DoesNotContain("打开压缩包", fixture.Texts());
        Assert.False(fixture.Window.TaskIsRunning);
    }

    [Fact]
    public void AddToTheCurrentArchiveFolderPreservesExistingContent()
    {
        using var fixture = new WindowFixture();
        var archive = fixture.CreateZip(("docs/original.txt", "old"), ("root.txt", "root"));
        var addition = fixture.WriteFile("新增.txt", "new 📦");
        fixture.Desktop.Inputs = [addition];
        fixture.OpenWindow();
        fixture.Run(fixture.Window.NavigateToAsync(archive, "docs/"));

        fixture.Click("＋ 添加");
        Assert.Contains("添加到当前压缩包", fixture.Texts());
        Assert.Empty(fixture.Elements<ComboBox>());
        fixture.Click("选择文件…");
        fixture.Click("保存修改");
        fixture.WaitForIdle();

        Assert.Equal("压缩包已更新。", fixture.Window.StatusMessage);
        Assert.Equal("docs/", fixture.Window.CurrentLocation!.ArchivePrefix);
        Assert.Contains(fixture.Window.VisibleItems, item => item.Path == "docs/新增.txt");
        Assert.Contains(fixture.Window.VisibleItems, item => item.Path == "docs/original.txt");
        Assert.Equal("new 📦", fixture.ReadArchiveEntry(archive, "docs/新增.txt"));
        Assert.Equal("old", fixture.ReadArchiveEntry(archive, "docs/original.txt"));
        Assert.Equal("root", fixture.ReadArchiveEntry(archive, "root.txt"));
        Assert.Equal(0, fixture.Desktop.TrashCalls);
    }

    [Fact]
    public void F2RenamesTheSelectedArchiveEntryAndRefreshesTheList()
    {
        using var fixture = new WindowFixture();
        var archive = fixture.CreateZip(("原名.txt", "preserved content"), ("keep.txt", "keep"));
        fixture.OpenWindow();
        fixture.Run(fixture.Window.NavigateToAsync(archive));
        fixture.SelectItem("原名.txt");

        fixture.Files.RaiseEvent(new KeyEventArgs(Element.KeyDownEvent, Key.F2));
        fixture.Host.UpdateLayout();
        Assert.Contains("重命名", fixture.Texts());
        var name = Assert.Single(fixture.Elements<TextBox>(), box => box.GetType() == typeof(TextBox) && box.Text == "原名.txt");
        name.Text = "新名.txt";
        fixture.Host.UpdateLayout();
        fixture.Click("保存");
        fixture.WaitForIdle();

        Assert.Equal("重命名完成。", fixture.Window.StatusMessage);
        Assert.Contains(fixture.Window.VisibleItems, item => item.Name == "新名.txt");
        Assert.DoesNotContain(fixture.Window.VisibleItems, item => item.Name == "原名.txt");
        Assert.Equal("preserved content", fixture.ReadArchiveEntry(archive, "新名.txt"));
        Assert.Equal("keep", fixture.ReadArchiveEntry(archive, "keep.txt"));
        Assert.Equal(0, fixture.Desktop.TrashCalls);
    }

    [Fact]
    public void ArchiveDeleteRequiresItsOwnConfirmationAndNeverUsesTheSystemTrash()
    {
        using var fixture = new WindowFixture();
        var archive = fixture.CreateZip(("remove.txt", "remove"), ("keep.txt", "keep"));
        fixture.OpenWindow();
        fixture.Run(fixture.Window.NavigateToAsync(archive));
        fixture.SelectItem("remove.txt");
        var before = File.ReadAllBytes(archive);

        fixture.Click("删除");

        Assert.Contains("从压缩包删除", fixture.Texts());
        Assert.Contains("remove.txt", fixture.Texts());
        Assert.Equal(before, File.ReadAllBytes(archive));
        Assert.Equal(0, fixture.Desktop.TrashCalls);
        fixture.ClickEnabled("删除");
        fixture.WaitForIdle();

        Assert.Equal("所选归档项目已删除。", fixture.Window.StatusMessage);
        Assert.Equal("keep.txt", Assert.Single(fixture.Window.VisibleItems).Name);
        Assert.Equal("keep", fixture.ReadArchiveEntry(archive, "keep.txt"));
        Assert.Equal(0, fixture.Desktop.TrashCalls);
    }

    [Fact]
    public void AddingAConflictingFileCanBeRetriedWithExplicitReplacement()
    {
        using var fixture = new WindowFixture();
        var archive = fixture.CreateZip(("same.txt", "before"), ("keep.txt", "keep"));
        var source = fixture.WriteFile("same.txt", "after");
        var before = File.ReadAllBytes(archive);
        fixture.Desktop.Inputs = [source];
        fixture.OpenWindow();
        fixture.Run(fixture.Window.NavigateToAsync(archive));
        fixture.Click("＋ 添加");
        fixture.Click("选择文件…");

        var replace = Assert.Single(fixture.Elements<CheckBox>(), box => box.Label == "替换压缩包内的同名文件");
        Assert.False(replace.Checked);
        fixture.Click("保存修改");
        fixture.WaitForIdle();

        Assert.Equal(before, File.ReadAllBytes(archive));
        Assert.Contains("添加到当前压缩包", fixture.Texts());
        Assert.Contains(fixture.Texts(), text => text.Contains("已存在同名文件", StringComparison.Ordinal));
        Assert.Equal("before", fixture.ReadArchiveEntry(archive, "same.txt"));
        replace = Assert.Single(fixture.Elements<CheckBox>(), box => box.Label == "替换压缩包内的同名文件");
        replace.Toggle();
        fixture.Host.UpdateLayout();
        fixture.Click("保存修改");
        fixture.WaitForIdle();

        Assert.Equal("压缩包已更新。", fixture.Window.StatusMessage);
        Assert.Equal("after", fixture.ReadArchiveEntry(archive, "same.txt"));
        Assert.Equal("keep", fixture.ReadArchiveEntry(archive, "keep.txt"));
        Assert.Empty(fixture.Elements<CheckBox>());
        Assert.Equal(0, fixture.Desktop.TrashCalls);
    }

    private sealed class WindowFixture : IDisposable
    {
        private HeadlessRoot.UiScope? _ui;
        public string DirectoryPath { get; } = CreateTemporaryDirectory();
        public IArchiveEngine Engine { get; } = new SharpCompressArchiveEngine();
        public FakeDesktopServices Desktop { get; } = new();
        public MainWindow Window { get; private set; } = null!;
        public HeadlessRoot Host { get; private set; } = null!;
        public ListBox Files { get; private set; } = null!;
        public TextBox Address { get; private set; } = null!;

        public void OpenWindow()
        {
            Window = new MainWindow
            {
                Engine = Engine,
                FileService = new ArchiveFileService(Engine),
                Desktop = Desktop,
                InitialPath = DirectoryPath,
            };
            Window.EnsureInitialized();
            Host = new HeadlessRoot(Window) { AvailableSize = new Size(1140, 740) };
            Host.Content.SetThemeDefaults(FluentTheme.Light);
            _ui = Host.InstallSynchronizationContext();
            Host.UpdateLayout();
            Files = Assert.Single(Elements<ListBox>());
            Address = Assert.Single(Elements<TextBox>(), box =>
                box.Placeholder == "输入文件夹或压缩包路径，按 Return 打开");
        }

        public IEnumerable<T> Elements<T>() where T : Element => Host.Content.Descendants().OfType<T>();

        public IEnumerable<string> Texts() => Elements<TextBlock>()
            .Select(block => string.Concat(block.Runs.Select(run => run.Text)));

        public Button FindButton(string label) => Assert.Single(Elements<Button>(),
            button => button.GetType() == typeof(Button) && button.Label == label);

        public void Click(string label)
        {
            FindButton(label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Host.UpdateLayout();
        }

        public void ClickEnabled(string label)
        {
            var button = Assert.Single(Elements<Button>(), candidate => candidate.GetType() == typeof(Button) &&
                candidate.Label == label && candidate.IsEffectiveEnabled);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Host.UpdateLayout();
        }

        public void SelectItem(string name)
        {
            Files.SelectedItem = Assert.Single(Window.VisibleItems, item => item.Name == name);
            Host.UpdateLayout();
        }

        public void Run(Task operation)
        {
            PumpUntil(() => operation.IsCompleted, "navigation");
            operation.GetAwaiter().GetResult();
            Host.DrainPostedWork();
            Host.UpdateLayout();
        }

        public void WaitForIdle() => PumpUntil(
            () => !Window.TaskIsRunning && Host.SynchronizationContext?.PendingCount == 0,
            "archive operation");

        private void PumpUntil(Func<bool> completed, string operation)
        {
            var clock = Stopwatch.StartNew();
            while (true)
            {
                Host.DrainPostedWork();
                Host.UpdateLayout();
                if (completed()) return;
                if (clock.Elapsed > TimeSpan.FromSeconds(10))
                    throw new TimeoutException($"Headless {operation} did not complete within ten seconds. " +
                        $"Status: {Window.StatusMessage}. Visible text: {string.Join(" | ", Texts())}");
                Thread.Sleep(1);
            }
        }

        public string CreateDirectory(string relativePath)
        {
            var path = LocalPath(relativePath);
            Directory.CreateDirectory(path);
            return path;
        }

        public string WriteFile(string relativePath, string content)
        {
            var path = LocalPath(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return path;
        }

        public string CreateZip(params (string Path, string? Content)[] entries)
        {
            var path = Path.Combine(DirectoryPath, "导航测试.zip");
            var inputs = entries.Select(entry => new ArchiveWriteEntry(entry.Path,
                entry.Content is null ? null : new MemoryStream(Encoding.UTF8.GetBytes(entry.Content), writable: false)))
                .ToArray();
            try
            {
                using var output = File.Create(path);
                Engine.CreateAsync(output, inputs).GetAwaiter().GetResult();
            }
            finally
            {
                foreach (var entry in inputs) entry.Content?.Dispose();
            }
            return path;
        }

        public string ReadArchiveEntry(string archivePath, string entryPath)
        {
            using var source = File.OpenRead(archivePath);
            using var output = new MemoryStream();
            Engine.ExtractEntryAsync(source, entryPath, output).GetAwaiter().GetResult();
            return Encoding.UTF8.GetString(output.ToArray());
        }

        public void Dispose()
        {
            _ui?.Dispose();
            Host?.Dispose();
            Window?.Dispose();
            Directory.Delete(DirectoryPath, recursive: true);
        }

        private string LocalPath(string relativePath) =>
            Path.Combine(DirectoryPath, relativePath.Replace('/', Path.DirectorySeparatorChar));

        private static string CreateTemporaryDirectory()
        {
            // macOS aliases /var and /tmp through symlinks. Resolve every ancestor so
            // real workflow tests satisfy ArchiveFileService's filesystem policy.
            var temporary = Path.GetFullPath(Path.GetTempPath());
            var current = Path.GetPathRoot(temporary)!;
            foreach (var part in temporary[current.Length..].Split(Path.DirectorySeparatorChar,
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                if (new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true) is { } target)
                    current = target.FullName;
            }
            var directory = Path.Combine(current, "EclipseArchive.MainWindow.Tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return directory;
        }
    }

    private sealed class FakeDesktopServices : IDesktopServices
    {
        public IReadOnlyList<string> Inputs { get; set; } = [];
        public string? SavePath { get; set; }
        public List<string> SaveSuggestions { get; } = [];
        public int TrashCalls { get; private set; }

        public IReadOnlyList<string> ChooseInputs() => Inputs;
        public string? ChooseArchive() => null;
        public string? ChooseFolder(string? initialDirectory = null) => null;
        public string? ChooseSavePath(string suggestedName, string? initialDirectory = null)
        {
            SaveSuggestions.Add(suggestedName);
            return SavePath;
        }
        public bool Confirm(string title, string message, string confirmLabel) => false;
        public void ShowMessage(string title, string message) { }
        public void Reveal(string path) { }
        public void Open(string path) { }
        public void Trash(IReadOnlyList<string> paths) => TrashCalls++;
    }
}
