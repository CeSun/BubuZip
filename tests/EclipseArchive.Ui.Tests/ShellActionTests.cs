using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using EclipseArchive.Core;
using EclipseUI;
using EclipseUI.Elements;
using EclipseUI.Themes.Fluent;
using Xunit;
using Path = System.IO.Path;

// Headless UI tests intentionally pump the owning thread while archive operations run.
#pragma warning disable xUnit1031

namespace EclipseArchive.Ui.Tests;

public sealed class ShellActionTests
{
    [Fact]
    public void InitialFinderRequestPreflightSelectsTheSmallOperationWindowAndCreatesTheRequestedZip()
    {
        using var fixture = new ShellFixture();
        var source = fixture.WriteFile("访达 中文.txt", "preflight payload");
        var document = fixture.WriteFile("operation.eclipsearchive-request", "request fixture");
        var decoded = 0;

        fixture.Open(new(ArchiveShellAction.Open, [document]), path =>
        {
            if (path != document) return null;
            decoded++;
            return new(ArchiveShellAction.CompressZip, [source]);
        }, prepareInitialRequests: true);
        fixture.WaitForIdle();

        Assert.Equal(1, decoded);
        Assert.True(fixture.Window.OperationOnly);
        Assert.Equal(560, fixture.Window.Width);
        Assert.Equal(320, fixture.Window.Height);
        Assert.DoesNotContain(fixture.Elements<ArchivePane>(), pane => pane.IsEffectivelyVisible);
        Assert.DoesNotContain(fixture.Elements<ArchiveWelcomeIcon>(), icon => icon.IsEffectivelyVisible);
        Assert.DoesNotContain("轻松创建和打开压缩包", fixture.VisibleTexts());
        Assert.DoesNotContain(fixture.VisibleButtons(), button => button.Label is "新建压缩" or "打开压缩");
        Assert.Contains("操作成功", fixture.VisibleTexts());
        using var zip = ZipFile.OpenRead(Path.Combine(fixture.Root, "访达 中文.zip"));
        var entry = Assert.Single(zip.Entries);
        Assert.Equal("访达 中文.txt", entry.FullName);
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        Assert.Equal("preflight payload", reader.ReadToEnd());
        Assert.Empty(fixture.Desktop.OpenedPaths);
    }

    [Fact]
    public void OperationOnlyExtractionWritesTheOutputWithoutAutomaticallyOpeningItsFolder()
    {
        using var fixture = new ShellFixture();
        var archive = fixture.WriteZip("bundle.zip", ("目录/inside.txt", "extracted payload"));

        fixture.Open(new(ArchiveShellAction.ExtractFolder, [archive]), prepareInitialRequests: true);
        fixture.WaitForIdle();

        Assert.True(fixture.Window.OperationOnly);
        Assert.Equal("extracted payload", File.ReadAllText(Path.Combine(fixture.Root, "bundle", "目录", "inside.txt")));
        Assert.Empty(fixture.Desktop.OpenedPaths);
        Assert.Equal("提取完成。", fixture.Window.StatusMessage);
        Assert.Equal(100, Assert.Single(fixture.Elements<ProgressBar>()).Value);
        Assert.Contains(fixture.VisibleButtons(), button => button.Label == "打开文件夹");
        Assert.DoesNotContain(fixture.Elements<ArchivePane>(), pane => pane.IsEffectivelyVisible);
    }

    [Fact]
    public void OperationOnlySuccessConfirmationKeepsTheTreeAliveUntilRealPointerEventsHaveReturned()
    {
        using var fixture = new ShellFixture();
        var source = fixture.WriteFile("关闭进度.txt", "saved payload");
        fixture.Open(new(ArchiveShellAction.CompressZip, [source]), prepareInitialRequests: true);
        fixture.WaitForIdle();
        Assert.True(fixture.Window.OperationOnly);
        Assert.Equal("压缩包创建完成。", fixture.Window.StatusMessage);
        var destination = Path.Combine(fixture.Root, "关闭进度.zip");
        Assert.True(File.Exists(destination));
        var button = Assert.Single(fixture.VisibleButtons(), button => button.Label == "确认");
        var localOrigin = button.PointFromRoot(Point.Zero);
        var point = new Point(button.FinalRect.Width / 2 - localOrigin.X, button.FinalRect.Height / 2 - localOrigin.Y);
        Assert.Contains(fixture.Host.Input.HitTest(point), button.Descendants().Prepend(button));

        fixture.Host.PointerPress(point);
        fixture.Host.PointerRelease(point);

        Assert.False(fixture.Window.IsDisposed);
        fixture.Host.PointerMove(new Point(1, 1));
        Assert.False(fixture.Window.IsDisposed);
        Assert.True(fixture.Host.DrainPostedWork() > 0);
        Assert.True(fixture.Window.IsDisposed);
        Assert.Equal("压缩包创建完成。", fixture.Window.StatusMessage);
        using var zip = ZipFile.OpenRead(destination);
        using var reader = new StreamReader(Assert.Single(zip.Entries).Open(), Encoding.UTF8);
        Assert.Equal("saved payload", reader.ReadToEnd());
    }

    [Fact]
    public void OrdinaryArchivePreflightKeepsTheFullMainWindow()
    {
        using var fixture = new ShellFixture();
        var archive = fixture.WriteZip("ordinary.zip", ("inside.txt", "content"));

        fixture.Open(new(ArchiveShellAction.Open, [archive]), _ => null, prepareInitialRequests: true);
        fixture.WaitForIdle();

        Assert.False(fixture.Window.OperationOnly);
        Assert.Equal(1000, fixture.Window.Width);
        Assert.Equal(640, fixture.Window.Height);
        Assert.Equal(archive, fixture.Window.CurrentLocation!.ArchivePath);
        Assert.Single(fixture.Elements<ArchivePane>(), pane => pane.IsEffectivelyVisible);
    }

    [Fact]
    public void MainWindowCreationRunsTheCapturedOperationInASeparatePresenterWithoutLeavingAProgressOverlay()
    {
        using var fixture = new ShellFixture();
        var source = fixture.WriteFile("手动创建.txt", "manual payload");
        var presenter = new CapturingOperationWindows
        {
            OnShow = _ => Assert.Empty(fixture.Elements<ProgressBar>()),
        };
        fixture.Desktop.Inputs = [source];
        fixture.Open(operationWindows: presenter);
        fixture.Click("新建压缩");
        fixture.Click("选择文件…");
        fixture.WaitForIdle();

        fixture.Click("开始");
        fixture.WaitForIdle();

        var operation = Assert.Single(presenter.Operations);
        var destination = Path.Combine(fixture.Root, "手动创建.zip");
        Assert.Equal("正在压缩", operation.Title);
        Assert.Equal("手动创建.txt", operation.Description);
        Assert.Equal(destination, operation.ResultPath);
        Assert.Equal(fixture.Root, operation.ResultFolder);
        Assert.Equal("压缩包创建完成。", fixture.Window.StatusMessage);
        Assert.Empty(fixture.Elements<ProgressBar>());
        Assert.DoesNotContain(fixture.VisibleButtons(), button => button.Label is "开始" or "确认");
        using var zip = ZipFile.OpenRead(destination);
        using var reader = new StreamReader(Assert.Single(zip.Entries).Open(), Encoding.UTF8);
        Assert.Equal("manual payload", reader.ReadToEnd());
    }

    [Fact]
    public void AddingFilesToTheOpenArchiveAlsoUsesTheIndependentProgressWindow()
    {
        using var fixture = new ShellFixture();
        var archive = fixture.WriteZip("existing.zip", ("original.txt", "original"));
        var source = fixture.WriteFile("added.txt", "added payload");
        var presenter = new CapturingOperationWindows();
        fixture.Desktop.Inputs = [source];
        fixture.Open(new(ArchiveShellAction.Open, [archive]), operationWindows: presenter);
        fixture.WaitForIdle();
        fixture.Click("添加");
        fixture.Click("选择文件…");
        fixture.WaitForIdle();
        fixture.Click("保存修改");
        fixture.WaitForIdle();

        var operation = Assert.Single(presenter.Operations);
        Assert.Equal("正在压缩", operation.Title);
        Assert.Equal(archive, operation.ResultPath);
        Assert.Equal("压缩包已更新。", fixture.Window.StatusMessage);
        Assert.Empty(fixture.Elements<ProgressBar>());
        using var zip = ZipFile.OpenRead(archive);
        Assert.Equal(new[] { "added.txt", "original.txt" }, zip.Entries.Select(entry => entry.FullName).Order().ToArray());
    }

    [Fact]
    public void MainWindowPresenterFailureRestoresTheConfigurationAndCanBeRetried()
    {
        using var fixture = new ShellFixture();
        var source = fixture.WriteFile("重试.txt", "retry payload");
        var presenter = new CapturingOperationWindows();
        fixture.Desktop.Inputs = [source];
        fixture.Engine.NextCreationFailure = new IOException("输出暂时不可写，请重试。");
        fixture.Open(operationWindows: presenter);
        fixture.Click("新建压缩");
        fixture.Click("选择文件…");
        fixture.WaitForIdle();
        var destination = Path.Combine(fixture.Root, "重试.zip");

        fixture.Click("开始");
        fixture.WaitForIdle();

        Assert.Single(presenter.Operations);
        Assert.False(File.Exists(destination));
        Assert.Contains("添加到压缩包", fixture.VisibleTexts());
        Assert.Contains("输出暂时不可写，请重试。", fixture.VisibleTexts());
        Assert.Equal(destination, Assert.Single(fixture.Elements<TextBox>(), box => box is not PasswordBox && box.IsEffectivelyVisible).Text);
        Assert.Contains("重试.txt", fixture.VisibleTexts());
        Assert.Empty(fixture.Elements<ProgressBar>());
        Assert.Contains(fixture.VisibleButtons(), button => button.Label == "开始" && button.IsEffectiveEnabled);

        fixture.Click("开始");
        fixture.WaitForIdle();

        Assert.Equal(2, presenter.Operations.Count);
        Assert.Equal("压缩包创建完成。", fixture.Window.StatusMessage);
        Assert.Empty(fixture.Elements<ProgressBar>());
        using var zip = ZipFile.OpenRead(destination);
        Assert.Equal("重试.txt", Assert.Single(zip.Entries).FullName);
    }

    [Fact]
    public void MainWindowPresenterCancellationDoesNotReportSuccessOrPublishAnArchive()
    {
        using var fixture = new ShellFixture();
        var source = fixture.WriteFile("取消.txt", "cancelled payload");
        var presenter = new CapturingOperationWindows { CancelNext = true };
        fixture.Desktop.Inputs = [source];
        fixture.Open(operationWindows: presenter);
        fixture.Click("新建压缩");
        fixture.Click("选择文件…");
        fixture.WaitForIdle();

        fixture.Click("开始");
        fixture.WaitForIdle();

        Assert.Single(presenter.Operations);
        Assert.Equal("操作已取消", fixture.Window.StatusMessage);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "取消.zip")));
        Assert.DoesNotContain("压缩包创建完成。", fixture.VisibleTexts());
        Assert.DoesNotContain("操作成功", fixture.VisibleTexts());
        Assert.Empty(fixture.Elements<ProgressBar>());
        Assert.Empty(fixture.Desktop.OpenedPaths);
    }

    [Fact]
    public void FinderRequestDocumentRunsItsActionThroughTheNormalFileOpenQueue()
    {
        using var fixture = new ShellFixture();
        var source = fixture.WriteFile("菜单 中文.txt", "payload");
        var document = fixture.WriteFile("operation.eclipsearchive-request", "request fixture");
        var decoded = 0;
        fixture.Open(new(ArchiveShellAction.Open, [document]), path =>
        {
            if (path != document) return null;
            decoded++;
            return new(ArchiveShellAction.CompressZip, [source]);
        });
        fixture.WaitForIdle();

        Assert.Equal(1, decoded);
        using var zip = ZipFile.OpenRead(Path.Combine(fixture.Root, "菜单 中文.zip"));
        Assert.Equal("菜单 中文.txt", Assert.Single(zip.Entries).FullName);
        Assert.Equal("压缩包创建完成。", fixture.Window.StatusMessage);
        Assert.Equal(0, fixture.Desktop.InputPickerCalls);
    }

    [Fact]
    public void RunningWindowAcceptsAFinderRequestDocumentAfterOpeningAnArchive()
    {
        using var fixture = new ShellFixture();
        var archive = fixture.WriteZip("first.zip", ("first.txt", "first"));
        var source = fixture.WriteFile("second.txt", "second");
        var document = fixture.WriteFile("operation.eclipsearchive-request", "request fixture");
        fixture.Open(new(ArchiveShellAction.Open, [archive]), path => path == document
            ? new(ArchiveShellAction.CompressZip, [source]) : null);
        fixture.WaitForIdle();
        Assert.Equal(archive, fixture.Window.CurrentLocation!.ArchivePath);

        fixture.Window.RequestOpenArchive(document);
        fixture.WaitForIdle();

        Assert.True(File.Exists(Path.Combine(fixture.Root, "second.zip")));
        Assert.Equal("压缩包创建完成。", fixture.Window.StatusMessage);
    }

    [Fact]
    public void InvalidFinderRequestIsReportedWithoutStartingAnOperation()
    {
        using var fixture = new ShellFixture();
        var document = fixture.WriteFile("operation.eclipsearchive-request", "request fixture");
        fixture.Open(new(ArchiveShellAction.Open, [document]), _ => throw new InvalidDataException("访达请求无效。"));
        fixture.WaitForIdle();
        Assert.Contains("访达请求无效。", fixture.Texts());
        Assert.Null(fixture.Window.CurrentLocation);
        Assert.Empty(Directory.EnumerateFiles(fixture.Root, "*.zip"));
    }

    [Fact]
    public void AddToArchiveUsesSelectedFilesWithoutOpeningAFilePicker()
    {
        using var fixture = new ShellFixture();
        var first = fixture.WriteFile("文档 one.txt", "one");
        var second = fixture.WriteFile("文档 two.txt", "two");

        fixture.Open(new(ArchiveShellAction.Compress, [first, second]));
        fixture.WaitForIdle();

        Assert.Contains("添加到压缩包", fixture.Texts());
        Assert.Contains("已选择 2 个文件或文件夹", fixture.Texts());
        Assert.Equal(0, fixture.Desktop.InputPickerCalls);
        Assert.Equal(Path.Combine(fixture.Root, "Archive.zip"), Assert.Single(fixture.Elements<TextBox>(), box => box is not PasswordBox && box.IsEffectivelyVisible).Text);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "Archive.zip")));
    }

    [Theory]
    [InlineData(ArchiveShellAction.CompressZip, ".zip")]
    [InlineData(ArchiveShellAction.CompressSevenZip, ".7z")]
    public void QuickCompressionCreatesARealArchiveOfTheRequestedFormat(ArchiveShellAction action, string extension)
    {
        using var fixture = new ShellFixture();
        var source = fixture.WriteFile("中文 文件.txt", "payload");
        var destination = Path.Combine(fixture.Root, "中文 文件" + extension);

        fixture.Open(new(action, [source]));
        fixture.WaitForIdle();

        Assert.True(File.Exists(destination));
        var bytes = File.ReadAllBytes(destination);
        var signature = extension == ".zip" ? new byte[] { 0x50, 0x4b } : [0x37, 0x7a, 0xbc, 0xaf, 0x27, 0x1c];
        Assert.Equal(signature, bytes.Take(signature.Length));
        using var archive = File.OpenRead(destination);
        var entries = fixture.Engine.ListAsync(archive).GetAwaiter().GetResult();
        Assert.Equal("中文 文件.txt", Assert.Single(entries).EntryPath);
        Assert.Equal("压缩包创建完成。", fixture.Window.StatusMessage);
        Assert.Equal(0, fixture.Desktop.InputPickerCalls);
    }

    [Fact]
    public void ExtractCommandShowsADestinationDialogWithoutAutomaticallyWritingFiles()
    {
        using var fixture = new ShellFixture();
        var archive = fixture.WriteZip("bundle.zip", ("inside.txt", "content"));

        fixture.Open(new(ArchiveShellAction.Extract, [archive]));
        fixture.WaitForIdle();

        Assert.Contains("提取文件", fixture.Texts());
        Assert.Contains("输出文件夹（新建）", fixture.Texts());
        Assert.Equal(Path.Combine(fixture.Root, "bundle"), Assert.Single(fixture.Elements<TextBox>(), box => box is not PasswordBox && box.IsEffectivelyVisible).Text);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "bundle")));
        Assert.Equal(0, fixture.Desktop.InputPickerCalls);
    }

    [Fact]
    public void ExtractToFolderCreatesANewUniqueDirectoryAndPreservesTheExistingOne()
    {
        using var fixture = new ShellFixture();
        var archive = fixture.WriteZip("bundle.zip", ("inside.txt", "content"));
        Directory.CreateDirectory(Path.Combine(fixture.Root, "bundle"));
        fixture.WriteFile("bundle/keep.txt", "keep");

        fixture.Open(new(ArchiveShellAction.ExtractFolder, [archive]));
        fixture.WaitForIdle();

        Assert.Equal("keep", File.ReadAllText(Path.Combine(fixture.Root, "bundle", "keep.txt")));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "bundle", "inside.txt")));
        var output = Path.Combine(fixture.Root, "bundle 2");
        Assert.Equal("content", File.ReadAllText(Path.Combine(output, "inside.txt")));
        Assert.Contains(output, fixture.Desktop.OpenedPaths);
        Assert.Equal("提取完成。", fixture.Window.StatusMessage);
    }

    [Fact]
    public void ExtractHerePublishesBesideTheArchiveWithoutCreatingAWrapperDirectory()
    {
        using var fixture = new ShellFixture();
        var archive = fixture.WriteZip("bundle.zip", ("资料/inside.txt", "content"));

        fixture.Open(new(ArchiveShellAction.ExtractHere, [archive]));
        fixture.WaitForIdle();

        Assert.Equal("content", File.ReadAllText(Path.Combine(fixture.Root, "资料", "inside.txt")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "bundle")));
        Assert.Equal("提取完成。", fixture.Window.StatusMessage);
        Assert.Contains(fixture.Root, fixture.Desktop.OpenedPaths);
    }

    [Fact]
    public void ExtractHereConflictReportsAnErrorAndDoesNotCommitAnyOtherItems()
    {
        using var fixture = new ShellFixture();
        var archive = fixture.WriteZip("bundle.zip", ("new.txt", "new"), ("keep.txt", "replacement"));
        fixture.WriteFile("keep.txt", "original");

        fixture.Open(new(ArchiveShellAction.ExtractHere, [archive]));
        fixture.WaitForIdle();

        Assert.Equal("original", File.ReadAllText(Path.Combine(fixture.Root, "keep.txt")));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "new.txt")));
        Assert.Contains(fixture.Texts(), text => text.Contains("已存在同名项目", StringComparison.Ordinal));
        Assert.Empty(fixture.Desktop.OpenedPaths);
        Assert.DoesNotContain("操作成功", fixture.Texts());
    }

    [Fact]
    public void TestCommandReadsArchiveContentsAndDoesNotExtractThem()
    {
        using var fixture = new ShellFixture();
        var archive = fixture.WriteZip("bundle.zip", ("one.txt", "one"), ("two.txt", "two"));

        fixture.Open(new(ArchiveShellAction.Test, [archive]));
        fixture.WaitForIdle();

        Assert.Equal("测试完成，所有文件均可读取。", fixture.Window.StatusMessage);
        Assert.Equal(2, fixture.Engine.Extractions);
        Assert.Equal(new[] { archive }, Directory.EnumerateFileSystemEntries(fixture.Root));
        Assert.Empty(fixture.Desktop.OpenedPaths);
    }

    private sealed class ShellFixture : IDisposable
    {
        private HeadlessRoot.UiScope? _scope;
        public string Root { get; } = Path.Combine(ArchivePaths.ResolveUserPath(Path.GetTempPath()), "EclipseArchive.ShellActions-" + Guid.NewGuid().ToString("N"));
        public CountingEngine Engine { get; } = new();
        public FakeDesktopServices Desktop { get; } = new();
        public MainWindow Window { get; private set; } = null!;
        public HeadlessRoot Host { get; private set; } = null!;

        public ShellFixture() => Directory.CreateDirectory(Root);
        public void Open(ArchiveShellRequest? request = null, Func<string, ArchiveShellRequest?>? decoder = null,
            bool prepareInitialRequests = false, bool operationOnly = false, IArchiveOperationWindows? operationWindows = null)
        {
            Window = new MainWindow
            {
                Engine = Engine, FileService = new ArchiveFileService(Engine), Desktop = Desktop, InitialPath = Root,
                ShellRequestDecoder = decoder, OperationOnly = operationOnly, OperationWindows = operationWindows,
            };
            if (request is not null) Window.RequestShellAction(request);
            if (prepareInitialRequests) Window.PrepareInitialRequests();
            Window.EnsureInitialized();
            Host = new HeadlessRoot(Window) { AvailableSize = new Size(Window.Width, Window.Height) };
            Host.Content.SetThemeDefaults(FluentTheme.Light);
            _scope = Host.InstallSynchronizationContext();
            Host.UpdateLayout();
        }

        public string WriteFile(string name, string text)
        {
            var path = Path.Combine(Root, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text, new UTF8Encoding(false));
            return path;
        }

        public string WriteZip(string name, params (string Name, string Content)[] entries)
        {
            var path = Path.Combine(Root, name);
            using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
            foreach (var entry in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(entry.Name).Open(), new UTF8Encoding(false));
                writer.Write(entry.Content);
            }
            return path;
        }

        public IEnumerable<T> Elements<T>() where T : Element => Host.Content.Descendants().OfType<T>();
        public IEnumerable<string> Texts() => Elements<TextBlock>().Select(text => string.Concat(text.Runs.Select(run => run.Text)));
        public IEnumerable<string> VisibleTexts() => Elements<TextBlock>().Where(text => text.IsEffectivelyVisible)
            .Select(text => string.Concat(text.Runs.Select(run => run.Text)));
        public IEnumerable<Button> VisibleButtons() => Elements<Button>().Where(button => button is not MenuItem && button.IsEffectivelyVisible);
        public void Click(string label)
        {
            var button = Assert.Single(VisibleButtons(), button => button.Label == label);
            Assert.True(button.IsEffectiveEnabled);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Host.DrainPostedWork();
            if (!Window.IsDisposed) Host.UpdateLayout();
        }
        public void WaitForIdle()
        {
            var clock = Stopwatch.StartNew();
            do
            {
                Host.DrainPostedWork();
                Host.UpdateLayout();
                if (!Window.TaskIsRunning && Host.SynchronizationContext?.PendingCount == 0) return;
                Thread.Sleep(1);
            } while (clock.Elapsed < TimeSpan.FromSeconds(10));
            throw new TimeoutException("Shell action did not finish: " + string.Join(" | ", Texts()));
        }

        public void Dispose()
        {
            _scope?.Dispose();
            Host?.Dispose();
            Window?.Dispose();
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class CountingEngine : IArchiveEngine
    {
        private readonly SharpCompressArchiveEngine _engine = new();
        public int Extractions;
        public Exception? NextCreationFailure;
        public Task CreateAsync(Stream destination, IReadOnlyList<ArchiveWriteEntry> entries, ArchiveFormat format = ArchiveFormat.Zip,
            string? password = null, bool encryptFileNames = false, CancellationToken cancellationToken = default)
        {
            if (NextCreationFailure is { } error)
            {
                NextCreationFailure = null;
                return Task.FromException(error);
            }
            return _engine.CreateAsync(destination, entries, format, password, encryptFileNames, cancellationToken);
        }
        public Task<IReadOnlyList<ArchiveEntryInfo>> ListAsync(Stream source, string? password = null, CancellationToken cancellationToken = default) =>
            _engine.ListAsync(source, password, cancellationToken);
        public Task ExtractEntryAsync(Stream source, string entryPath, Stream destination, string? password = null, CancellationToken cancellationToken = default)
        {
            Extractions++;
            return _engine.ExtractEntryAsync(source, entryPath, destination, password, cancellationToken);
        }
    }

    private sealed class FakeDesktopServices : IDesktopServices
    {
        public int InputPickerCalls;
        public IReadOnlyList<string> Inputs { get; set; } = [];
        public List<string> OpenedPaths { get; } = [];
        public IReadOnlyList<string> ChooseInputs() { InputPickerCalls++; return Inputs; }
        public string? ChooseArchive() => null;
        public string? ChooseFolder(string? initialDirectory = null) => null;
        public string? ChooseSavePath(string suggestedName, string? initialDirectory = null) => null;
        public bool Confirm(string title, string message, string confirmLabel) => false;
        public void ShowMessage(string title, string message) { }
        public void Reveal(string path) { }
        public void Open(string path) => OpenedPaths.Add(path);
        public void Trash(IReadOnlyList<string> paths) { }
    }

    private sealed class CapturingOperationWindows : IArchiveOperationWindows
    {
        public List<ArchiveOperationSpec> Operations { get; } = [];
        public Action<ArchiveOperationSpec>? OnShow { get; init; }
        public bool CancelNext { get; set; }

        public ArchiveOperationOutcome Show(ArchiveOperationSpec operation)
        {
            Operations.Add(operation);
            OnShow?.Invoke(operation);
            using var cancellation = new CancellationTokenSource();
            if (CancelNext) { CancelNext = false; cancellation.Cancel(); }
            try
            {
                Task.Run(() => operation.Execute(new InlineProgress(), cancellation.Token)).GetAwaiter().GetResult();
                return new ArchiveOperationOutcome(true, false, null);
            }
            catch (OperationCanceledException) { return new ArchiveOperationOutcome(false, true, null); }
            catch (Exception error) { return new ArchiveOperationOutcome(false, false, error.Message); }
        }

        private sealed class InlineProgress : IProgress<ArchiveOperationProgress>
        {
            public void Report(ArchiveOperationProgress value) { }
        }
    }
}
