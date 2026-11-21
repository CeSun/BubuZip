using System.Text;
using BubuZip.Core;
using Xunit;

namespace BubuZip.Ui.Tests;

public sealed class BrowserServiceTests
{
    private readonly IArchiveEngine _engine = new SharpCompressArchiveEngine();

    [Fact]
    public void DirectoryBrowsingShowsOnlyTheCurrentLevelAndPlacesFoldersFirst()
    {
        using var fixture = new BrowserFixture();
        var documentsPath = fixture.CreateDirectory("资料");
        var emptyPath = fixture.CreateDirectory("空目录");
        fixture.CreateDirectory("资料/子目录");
        var rootFilePath = fixture.WriteFile("说明.txt", "文件浏览器 📦");
        fixture.WriteFile("资料/你好，世界.txt", "中文内容");
        fixture.WriteFile("资料/子目录/深层.txt", "只在子目录显示");
        var browser = new BrowserService(_engine);

        var root = browser.BrowseDirectory(fixture.DirectoryPath);

        Assert.Equal(fixture.DirectoryPath, root.DirectoryPath);
        Assert.Null(root.ArchivePath);
        AssertNames(root, "资料", "空目录", "说明.txt");
        Assert.All(root.Items.Take(2), item => Assert.True(item.IsDirectory));
        Assert.False(root.Items[2].IsDirectory);
        var rootFile = Assert.Single(root.Items, item => item.Name == "说明.txt");
        Assert.Equal(rootFilePath, rootFile.Path);
        Assert.Equal((long)Encoding.UTF8.GetByteCount("文件浏览器 📦"), rootFile.Size);

        var documents = browser.BrowseDirectory(documentsPath);

        AssertNames(documents, "子目录", "你好，世界.txt");
        Assert.True(documents.Items[0].IsDirectory);
        Assert.False(documents.Items[1].IsDirectory);
        Assert.Equal(Path.Combine(documentsPath, "你好，世界.txt"), documents.Items[1].Path);
        Assert.Empty(browser.BrowseDirectory(emptyPath).Items);
    }

    [Fact]
    public void DirectoryBrowsingHidesDotFilesAndDotDirectoriesUnlessRequested()
    {
        using var fixture = new BrowserFixture();
        fixture.WriteFile("visible.txt", "可见文件");
        fixture.WriteFile(".DS_Store", "隐藏文件");
        fixture.CreateDirectory(".隐藏目录");
        fixture.WriteFile(".隐藏目录/secret.txt", "隐藏目录中的文件");
        var browser = new BrowserService(_engine);

        AssertNames(browser.BrowseDirectory(fixture.DirectoryPath), "visible.txt");

        var withHidden = browser.BrowseDirectory(fixture.DirectoryPath, showHidden: true);

        AssertNames(withHidden, ".隐藏目录", ".DS_Store", "visible.txt");
        Assert.True(withHidden.Items[0].IsDirectory);
        Assert.All(withHidden.Items.Skip(1), item => Assert.False(item.IsDirectory));
    }

    [Theory]
    [InlineData(ArchiveFormat.Zip)]
    [InlineData(ArchiveFormat.SevenZip)]
    public async Task ArchiveRootShowsOnlyImmediateChildrenAndPreservesEmptyDirectories(ArchiveFormat format)
    {
        using var fixture = new BrowserFixture();
        var archivePath = await fixture.CreateArchiveAsync(_engine, format,
        [
            new("docs/", null),
            new("docs/manual.txt", "手册"),
            new("资料/", null),
            new("资料/你好，世界.txt", "中文和 emoji 📦"),
            new("空目录/", null),
            new("说明.txt", "根文件"),
        ]);
        var browser = new BrowserService(_engine);

        var root = await browser.BrowseArchiveAsync(archivePath);

        Assert.Equal(archivePath, root.ArchivePath);
        Assert.Equal("", root.ArchivePrefix);
        AssertNames(root, "docs", "资料", "空目录", "说明.txt");
        Assert.All(root.Items.Take(3), item => Assert.True(item.IsDirectory));
        Assert.False(root.Items[3].IsDirectory);
        Assert.Equal("docs/", Assert.Single(root.Items, item => item.Name == "docs").Path);
        Assert.Equal("资料/", Assert.Single(root.Items, item => item.Name == "资料").Path);
        Assert.Equal("空目录/", Assert.Single(root.Items, item => item.Name == "空目录").Path);

        var empty = await browser.BrowseArchiveAsync(archivePath, "空目录/");

        Assert.Equal("空目录/", empty.ArchivePrefix);
        Assert.Empty(empty.Items);
    }

    [Theory]
    [InlineData(ArchiveFormat.Zip)]
    [InlineData(ArchiveFormat.SevenZip)]
    public async Task ArchiveFolderBrowsingKeepsUnicodeNamesAndArchiveRelativePaths(ArchiveFormat format)
    {
        using var fixture = new BrowserFixture();
        const string content = "中文和 emoji 📦";
        var archivePath = await fixture.CreateArchiveAsync(_engine, format,
        [
            new("资料/", null),
            new("资料/子目录/", null),
            new("资料/子目录/深层.txt", "下一级"),
            new("资料/你好，世界.txt", content),
            new("根文件.txt", "上一级"),
        ]);
        var browser = new BrowserService(_engine);

        var documents = await browser.BrowseArchiveAsync(archivePath, "资料/");

        Assert.Equal("资料/", documents.ArchivePrefix);
        AssertNames(documents, "子目录", "你好，世界.txt");
        var childDirectory = documents.Items[0];
        Assert.True(childDirectory.IsDirectory);
        Assert.Equal("资料/子目录/", childDirectory.Path);
        var file = documents.Items[1];
        Assert.False(file.IsDirectory);
        Assert.Equal("资料/你好，世界.txt", file.Path);
        Assert.Equal((long)Encoding.UTF8.GetByteCount(content), file.Size);

        var nested = await browser.BrowseArchiveAsync(archivePath, childDirectory.Path);

        AssertNames(nested, "深层.txt");
        Assert.Equal("资料/子目录/深层.txt", Assert.Single(nested.Items).Path);
    }

    [Theory]
    [InlineData(ArchiveFormat.Zip)]
    [InlineData(ArchiveFormat.SevenZip)]
    public async Task ArchivePrefixMatchesWholeDirectoriesRatherThanSimilarNames(ArchiveFormat format)
    {
        using var fixture = new BrowserFixture();
        var archivePath = await fixture.CreateArchiveAsync(_engine, format,
        [
            new("docs/keep.txt", "应当显示"),
            new("docs/sub/nested.txt", "只显示所在目录"),
            new("docs2/other.txt", "不属于 docs"),
            new("docs-extra/another.txt", "也不属于 docs"),
            new("docs.txt", "根目录的文件"),
        ]);
        var browser = new BrowserService(_engine);

        var documents = await browser.BrowseArchiveAsync(archivePath, "docs/");

        AssertNames(documents, "sub", "keep.txt");
        Assert.True(documents.Items[0].IsDirectory);
        Assert.Equal("docs/sub/", documents.Items[0].Path);
        Assert.Equal("docs/keep.txt", documents.Items[1].Path);
        Assert.All(documents.Items, item => Assert.StartsWith("docs/", item.Path));
    }

    [Theory]
    [InlineData(ArchiveFormat.Zip)]
    [InlineData(ArchiveFormat.SevenZip)]
    public async Task ArchiveBrowsingSynthesizesMissingDirectoriesOnceAtEachLevel(ArchiveFormat format)
    {
        using var fixture = new BrowserFixture();
        var archivePath = await fixture.CreateArchiveAsync(_engine, format,
        [
            new("资料/子目录/第一份.txt", "一"),
            new("资料/子目录/第二份.txt", "二"),
            new("资料/同级.txt", "同级文件"),
        ]);
        var browser = new BrowserService(_engine);

        var root = await browser.BrowseArchiveAsync(archivePath);

        var documents = Assert.Single(root.Items);
        Assert.Equal("资料", documents.Name);
        Assert.Equal("资料/", documents.Path);
        Assert.True(documents.IsDirectory);

        var next = await browser.BrowseArchiveAsync(archivePath, documents.Path);

        AssertNames(next, "子目录", "同级.txt");
        Assert.True(next.Items[0].IsDirectory);
        Assert.Equal("资料/子目录/", next.Items[0].Path);

        var nested = await browser.BrowseArchiveAsync(archivePath, next.Items[0].Path);

        AssertNames(nested, "第一份.txt", "第二份.txt");
        Assert.All(nested.Items, item => Assert.False(item.IsDirectory));
        Assert.All(nested.Items, item => Assert.StartsWith("资料/子目录/", item.Path));
    }

    [Theory]
    [InlineData(ArchiveFormat.Zip)]
    [InlineData(ArchiveFormat.SevenZip)]
    public async Task ArchivePrefixesUseForwardSlashesAndATrailingDirectorySeparator(ArchiveFormat format)
    {
        using var fixture = new BrowserFixture();
        var archivePath = await fixture.CreateArchiveAsync(_engine, format,
            [new("资料/子目录/文件.txt", "跨平台路径")]);
        var browser = new BrowserService(_engine);

        var snapshot = await browser.BrowseArchiveAsync(archivePath, "资料\\子目录");

        Assert.Equal("资料/子目录/", snapshot.ArchivePrefix);
        Assert.Equal("资料/子目录/文件.txt", Assert.Single(snapshot.Items).Path);
    }

    [Fact]
    public async Task BrowsingEncryptedSevenZipPassesThePasswordToTheEngine()
    {
        using var fixture = new BrowserFixture();
        const string password = "测试密码-Archive-2026!";
        var archivePath = await fixture.CreateArchiveAsync(_engine, ArchiveFormat.SevenZip,
            [new("机密.txt", "加密文件")], password, encryptFileNames: true);
        var browser = new BrowserService(_engine);

        var snapshot = await browser.BrowseArchiveAsync(archivePath, password: password);

        var file = Assert.Single(snapshot.Items);
        Assert.Equal("机密.txt", file.Name);
        Assert.Equal("机密.txt", file.Path);
        Assert.True(file.IsEncrypted);
    }

    [Theory]
    [InlineData(ArchiveFormat.Zip)]
    [InlineData(ArchiveFormat.SevenZip)]
    public async Task ArchiveBrowsingHonorsAnAlreadyCancelledRequest(ArchiveFormat format)
    {
        using var fixture = new BrowserFixture();
        var archivePath = await fixture.CreateArchiveAsync(_engine, format,
            [new("说明.txt", "用于验证取消")]);
        var browser = new BrowserService(_engine);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            browser.BrowseArchiveAsync(archivePath, cancellationToken: cancellation.Token));
    }

    private static void AssertNames(BrowserSnapshot snapshot, params string[] expected) =>
        Assert.Equal(expected.Order(StringComparer.Ordinal),
            snapshot.Items.Select(item => item.Name).Order(StringComparer.Ordinal));

    private sealed record FixtureEntry(string Path, string? Content);

    private sealed class BrowserFixture : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(),
            "BubuZip.Ui.Tests", Guid.NewGuid().ToString("N"));

        public BrowserFixture() => Directory.CreateDirectory(DirectoryPath);

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

        public async Task<string> CreateArchiveAsync(IArchiveEngine engine, ArchiveFormat format,
            IReadOnlyList<FixtureEntry> entries, string? password = null, bool encryptFileNames = false)
        {
            var extension = format == ArchiveFormat.SevenZip ? ".7z" : ".zip";
            var path = Path.Combine(DirectoryPath, "测试归档" + extension);
            var inputs = entries.Select(entry => new ArchiveWriteEntry(entry.Path,
                entry.Content is null ? null : new MemoryStream(Encoding.UTF8.GetBytes(entry.Content), writable: false)))
                .ToArray();
            try
            {
                await using var output = File.Create(path);
                await engine.CreateAsync(output, inputs, format, password, encryptFileNames);
            }
            finally
            {
                foreach (var entry in inputs) entry.Content?.Dispose();
            }
            return path;
        }

        public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);

        private string LocalPath(string relativePath) =>
            Path.Combine(DirectoryPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }
}
