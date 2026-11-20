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
        public void Open(ArchiveShellRequest request)
        {
            Window = new MainWindow { Engine = Engine, FileService = new ArchiveFileService(Engine), Desktop = Desktop, InitialPath = Root };
            Window.RequestShellAction(request);
            Window.EnsureInitialized();
            Host = new HeadlessRoot(Window) { AvailableSize = new Size(1000, 640) };
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
        public Task CreateAsync(Stream destination, IReadOnlyList<ArchiveWriteEntry> entries, ArchiveFormat format = ArchiveFormat.Zip,
            string? password = null, bool encryptFileNames = false, CancellationToken cancellationToken = default) =>
            _engine.CreateAsync(destination, entries, format, password, encryptFileNames, cancellationToken);
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
        public List<string> OpenedPaths { get; } = [];
        public IReadOnlyList<string> ChooseInputs() { InputPickerCalls++; return []; }
        public string? ChooseArchive() => null;
        public string? ChooseFolder(string? initialDirectory = null) => null;
        public string? ChooseSavePath(string suggestedName, string? initialDirectory = null) => null;
        public bool Confirm(string title, string message, string confirmLabel) => false;
        public void ShowMessage(string title, string message) { }
        public void Reveal(string path) { }
        public void Open(string path) => OpenedPaths.Add(path);
        public void Trash(IReadOnlyList<string> paths) { }
    }
}
