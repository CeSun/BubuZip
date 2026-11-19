using System.Diagnostics;
using System.Text;
using EclipseArchive.Core;
using EclipseUI;
using EclipseUI.Elements;
using EclipseUI.Themes.Fluent;
using Xunit;
using Path = System.IO.Path;

// These tests deliberately stop pumping the UI context at task completion/close boundaries.
#pragma warning disable xUnit1031

namespace EclipseArchive.Ui.Tests;

public sealed class MainWindowLifecycleTests
{
    [Fact]
    public void ClosingDuringCreationJoinsEngineCleanupAndRemovesTemporaryOutputWithoutPumpingUi()
    {
        using var engine = new BlockingCreateEngine();
        using var fixture = new LifecycleFixture(engine);
        fixture.Desktop.Inputs = [fixture.WriteFile("正在处理.txt", "关闭窗口前必须清理的内容")];
        fixture.OpenWindow();
        fixture.Click("新建压缩");
        fixture.Click("选择文件…");
        fixture.Click("开始");

        Assert.True(engine.Entered.Wait(TimeSpan.FromSeconds(5)), "The archive writer did not start.");
        Assert.True(fixture.Window.TaskIsRunning);
        Assert.Single(Directory.EnumerateFileSystemEntries(fixture.DirectoryPath, ".eclipsearchive-*"));
        Assert.False(engine.Cleaned.IsSet);

        // No DrainPostedWork here: native window shutdown cannot rely on another UI frame.
        fixture.Window.Dispose();

        Assert.True(engine.Cleaned.IsSet);
        Assert.False(File.Exists(Path.Combine(fixture.DirectoryPath, "正在处理.zip")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.DirectoryPath, ".eclipsearchive-*"));
    }

    [Fact]
    public void CancellingAfterTheOutputWasCommittedStillReportsSuccess()
    {
        using var fixture = new LifecycleFixture();
        const string text = "已经提交的输出应当被报告为成功。";
        fixture.Desktop.Inputs = [fixture.WriteFile("提交.txt", text)];
        var archivePath = Path.Combine(fixture.DirectoryPath, "提交.zip");
        fixture.OpenWindow();
        fixture.Click("新建压缩");
        fixture.Click("选择文件…");
        fixture.Click("开始");

        // Observe the actual File.Move commit, while intentionally withholding the UI continuation.
        Assert.True(SpinWait.SpinUntil(() => File.Exists(archivePath), TimeSpan.FromSeconds(5)),
            "The archive was not committed.");
        Assert.True(fixture.Window.TaskIsRunning);
        Assert.Contains(fixture.Elements<ProgressBar>(), _ => true);

        fixture.Click("取消");
        fixture.WaitForIdle();

        Assert.Equal("压缩包创建完成。", fixture.Window.StatusMessage);
        Assert.True(File.Exists(archivePath));
        using var archive = File.OpenRead(archivePath);
        using var extracted = new MemoryStream();
        fixture.Engine.ExtractEntryAsync(archive, "提交.txt", extracted).GetAwaiter().GetResult();
        Assert.Equal(text, Encoding.UTF8.GetString(extracted.ToArray()));
        Assert.Empty(fixture.Elements<ProgressBar>());
        Assert.DoesNotContain("操作已取消", fixture.Texts());
    }

    [Fact]
    public void CancellingExtractionDoesNotOpenAnOutputFolder()
    {
        using var engine = new BlockingExtractEngine();
        using var fixture = new LifecycleFixture(engine);
        var archive = Path.Combine(fixture.DirectoryPath, "archive.zip");
        using (var output = File.Create(archive))
        using (var content = new MemoryStream(Encoding.UTF8.GetBytes("content")))
            new SharpCompressArchiveEngine().CreateAsync(output, [new ArchiveWriteEntry("file.txt", content)]).GetAwaiter().GetResult();
        fixture.Desktop.ArchiveChoice = archive;
        fixture.OpenWindow();
        fixture.Click("打开压缩"); fixture.WaitForIdle();
        fixture.Click("提取"); fixture.Click("开始");
        Assert.True(engine.Entered.Wait(TimeSpan.FromSeconds(5)), "The archive reader did not start.");
        fixture.Click("取消"); fixture.WaitForIdle();
        Assert.Empty(fixture.Desktop.OpenedPaths);
        Assert.False(Directory.Exists(Path.Combine(fixture.DirectoryPath, "archive")));
        Assert.Equal("操作已取消", fixture.Window.StatusMessage);
        Assert.Equal(archive, fixture.Window.CurrentLocation!.ArchivePath);
    }

    [Theory]
    [InlineData("损坏.zip")]
    [InlineData("不支持.txt")]
    public void InvalidArchivesDisplayAnErrorWithoutAskingForAPassword(string fileName)
    {
        using var fixture = new LifecycleFixture();
        var path = fixture.WriteFile(fileName, "This is not a supported archive format.");
        fixture.Desktop.ArchiveChoice = path;
        fixture.OpenWindow();

        fixture.Click("打开压缩");
        fixture.WaitForIdle();

        Assert.Null(fixture.Window.CurrentLocation);
        Assert.Empty(fixture.Elements<PasswordBox>());
        Assert.DoesNotContain("打开压缩包", fixture.Texts());
        Assert.Contains(fixture.Elements<Button>(), button => button is not MenuItem && button.Label == "关闭");
        Assert.False(fixture.Window.TaskIsRunning);
    }

    [Fact]
    public void MissingPasswordForARealEncryptedHeaderProducesThePasswordPanelAndCanBeRetried()
    {
        using var fixture = new LifecycleFixture();
        const string password = "Lifecycle-中文密码!";
        var path = fixture.CreateEncryptedArchive(password);
        Exception? missingPassword;
        using (var archive = File.OpenRead(path))
            missingPassword = Record.Exception(() => { _ = fixture.Engine.ListAsync(archive).GetAwaiter().GetResult(); });
        Assert.IsAssignableFrom<SharpCompress.Common.CryptographicException>(missingPassword);
        fixture.Desktop.ArchiveChoice = path;
        fixture.OpenWindow();

        fixture.Click("打开压缩");
        fixture.WaitForIdle();

        Assert.Null(fixture.Window.CurrentLocation);
        Assert.Contains("打开压缩包", fixture.Texts());
        var passwordBox = Assert.Single(fixture.Elements<PasswordBox>());
        passwordBox.Text = password;
        fixture.Click("打开");
        fixture.WaitForIdle();

        Assert.Equal(path, fixture.Window.CurrentLocation!.ArchivePath);
        Assert.Equal("机密.txt", Assert.Single(fixture.Window.VisibleItems).Name);
        Assert.Empty(fixture.Elements<PasswordBox>());
        Assert.DoesNotContain("打开压缩包", fixture.Texts());
    }

    private sealed class LifecycleFixture : IDisposable
    {
        private HeadlessRoot.UiScope? _ui;
        public string DirectoryPath { get; } = CreateTemporaryDirectory();
        public IArchiveEngine Engine { get; }
        public FakeDesktopServices Desktop { get; } = new();
        public MainWindow Window { get; private set; } = null!;
        public HeadlessRoot Host { get; private set; } = null!;
        private ListBox Files { get; set; } = null!;

        public LifecycleFixture(IArchiveEngine? engine = null) => Engine = engine ?? new SharpCompressArchiveEngine();

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
            Files = Elements<ArchivePane>().Last().Files;
        }

        public IEnumerable<T> Elements<T>() where T : Element => Host.Content.Descendants().OfType<T>();
        public IEnumerable<string> Texts() => Elements<TextBlock>().Select(block => string.Concat(block.Runs.Select(run => run.Text)));

        public void Click(string label)
        {
            var button = Assert.Single(Elements<Button>(), button => button is not MenuItem && button.Label == label);
            Assert.True(button.IsEffectiveEnabled);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Host.UpdateLayout();
        }

        public void Select(string name)
        {
            Files.SelectedItem = Assert.Single(Window.VisibleItems, item => item.Name == name);
            Host.UpdateLayout();
        }

        public void WaitForIdle()
        {
            var elapsed = Stopwatch.StartNew();
            var spin = new SpinWait();
            while (true)
            {
                Host.DrainPostedWork();
                Host.UpdateLayout();
                if (!Window.TaskIsRunning && Host.SynchronizationContext?.PendingCount == 0) return;
                if (elapsed.Elapsed > TimeSpan.FromSeconds(10))
                    throw new TimeoutException($"UI operation did not finish. Status: {Window.StatusMessage}. Text: {string.Join(" | ", Texts())}");
                spin.SpinOnce();
            }
        }

        public string WriteFile(string name, string text)
        {
            var path = Path.Combine(DirectoryPath, name);
            File.WriteAllText(path, text, new UTF8Encoding(false));
            return path;
        }

        public string CreateEncryptedArchive(string password)
        {
            var path = Path.Combine(DirectoryPath, "加密.7z");
            using var output = File.Create(path);
            using var content = new MemoryStream(Encoding.UTF8.GetBytes("隐藏文件名的内容"));
            Engine.CreateAsync(output, [new ArchiveWriteEntry("机密.txt", content)], ArchiveFormat.SevenZip,
                password, encryptFileNames: true).GetAwaiter().GetResult();
            return path;
        }

        public void Dispose()
        {
            try { Window?.Dispose(); }
            finally
            {
                _ui?.Dispose();
                Host?.Dispose();
                Directory.Delete(DirectoryPath, true);
            }
        }

        private static string CreateTemporaryDirectory()
        {
            var temporary = Path.GetFullPath(Path.GetTempPath());
            var current = Path.GetPathRoot(temporary)!;
            foreach (var part in temporary[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                if (new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true) is { } target)
                    current = target.FullName;
            }
            var result = Path.Combine(current, "EclipseArchive.Lifecycle.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(result);
            return result;
        }
    }

    private sealed class BlockingCreateEngine : IArchiveEngine, IDisposable
    {
        private readonly IArchiveEngine _inner = new SharpCompressArchiveEngine();
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Cleaned { get; } = new();

        public async Task CreateAsync(Stream destination, IReadOnlyList<ArchiveWriteEntry> entries,
            ArchiveFormat format = ArchiveFormat.Zip, string? password = null, bool encryptFileNames = false,
            CancellationToken cancellationToken = default)
        {
            await destination.WriteAsync(new byte[] { 1, 2, 3 }, cancellationToken).ConfigureAwait(false);
            Entered.Set();
            try { await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false); }
            finally { await Task.Yield(); Cleaned.Set(); }
        }

        public Task<IReadOnlyList<ArchiveEntryInfo>> ListAsync(Stream source, string? password = null,
            CancellationToken cancellationToken = default) => _inner.ListAsync(source, password, cancellationToken);
        public Task ExtractEntryAsync(Stream source, string entryPath, Stream destination, string? password = null,
            CancellationToken cancellationToken = default) => _inner.ExtractEntryAsync(source, entryPath, destination, password, cancellationToken);
        public void Dispose() { Entered.Dispose(); Cleaned.Dispose(); }
    }

    private sealed class BlockingExtractEngine : IArchiveEngine, IDisposable
    {
        private readonly IArchiveEngine _inner = new SharpCompressArchiveEngine();
        public ManualResetEventSlim Entered { get; } = new();
        public Task CreateAsync(Stream destination, IReadOnlyList<ArchiveWriteEntry> entries,
            ArchiveFormat format = ArchiveFormat.Zip, string? password = null, bool encryptFileNames = false,
            CancellationToken cancellationToken = default) =>
            _inner.CreateAsync(destination, entries, format, password, encryptFileNames, cancellationToken);
        public Task<IReadOnlyList<ArchiveEntryInfo>> ListAsync(Stream source, string? password = null,
            CancellationToken cancellationToken = default) => _inner.ListAsync(source, password, cancellationToken);
        public async Task ExtractEntryAsync(Stream source, string entryPath, Stream destination, string? password = null,
            CancellationToken cancellationToken = default)
        {
            Entered.Set();
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
        public void Dispose() => Entered.Dispose();
    }

    private sealed class FakeDesktopServices : IDesktopServices
    {
        public string? ArchiveChoice { get; set; }
        public List<string> OpenedPaths { get; } = [];
        public IReadOnlyList<string> Inputs { get; set; } = [];
        public IReadOnlyList<string> ChooseInputs() => Inputs;
        public string? ChooseArchive() => ArchiveChoice;
        public string? ChooseFolder(string? initialDirectory = null) => null;
        public string? ChooseSavePath(string suggestedName, string? initialDirectory = null) => null;
        public bool Confirm(string title, string message, string confirmLabel) => false;
        public void ShowMessage(string title, string message) { }
        public void Reveal(string path) { }
        public void Open(string path) => OpenedPaths.Add(path);
        public void Trash(IReadOnlyList<string> paths) { }
    }
}
