using System.IO.Compression;
using System.Text;
using Xunit;

namespace EclipseArchive.Core.Tests;

public sealed class ExtractHereTests : IDisposable
{
    private readonly string _root = Path.Combine(ArchivePaths.ResolveUserPath(Path.GetTempPath()), "EclipseArchive.ExtractHere-" + Guid.NewGuid().ToString("N"));
    private readonly ArchiveFileService _service = new(new SharpCompressArchiveEngine());

    public ExtractHereTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task ExtractsUnicodeFilesAndEmptyDirectoriesBesideTheArchiveAndKeepsUnrelatedFiles()
    {
        var archive = WriteZip(("资料/你好.txt", "中文 📦"), ("资料/空目录/", null), ("空文件.txt", ""));
        var untouched = Path.Combine(_root, "保留.txt");
        await File.WriteAllTextAsync(untouched, "original");

        await _service.ExtractHereAsync(archive, _root);

        Assert.Equal("中文 📦", await File.ReadAllTextAsync(Path.Combine(_root, "资料", "你好.txt")));
        Assert.True(Directory.Exists(Path.Combine(_root, "资料", "空目录")));
        Assert.Empty(await File.ReadAllBytesAsync(Path.Combine(_root, "空文件.txt")));
        Assert.Equal("original", await File.ReadAllTextAsync(untouched));
        Assert.True(File.Exists(archive));
        AssertNoStaging();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFileOrDirectoryConflictPreventsEveryTopLevelItemFromBeingCommitted(bool directoryConflict)
    {
        var conflict = Path.Combine(_root, directoryConflict ? "资料" : "same.txt");
        if (directoryConflict)
        {
            Directory.CreateDirectory(conflict);
            await File.WriteAllTextAsync(Path.Combine(conflict, "original.txt"), "keep directory");
        }
        else await File.WriteAllTextAsync(conflict, "keep file");
        var archive = WriteZip(("would-have-been-new.txt", "new"),
            (directoryConflict ? "资料/inside.txt" : "same.txt", "must not overwrite"));

        await Assert.ThrowsAsync<IOException>(() => _service.ExtractHereAsync(archive, _root));

        Assert.False(File.Exists(Path.Combine(_root, "would-have-been-new.txt")));
        if (directoryConflict)
        {
            Assert.Equal("keep directory", await File.ReadAllTextAsync(Path.Combine(conflict, "original.txt")));
            Assert.False(File.Exists(Path.Combine(conflict, "inside.txt")));
        }
        else Assert.Equal("keep file", await File.ReadAllTextAsync(conflict));
        AssertNoStaging();
    }

    [Fact]
    public async Task RetrySettingsCanExtractHereWithoutArchiveDirectories()
    {
        var archive = WriteZip(("资料/文件.txt", "content"));
        await _service.ExtractHereAsync(archive, _root, settings: new(ExtractionPathMode.NoPaths));
        Assert.Equal("content", await File.ReadAllTextAsync(Path.Combine(_root, "文件.txt")));
        Assert.False(Directory.Exists(Path.Combine(_root, "资料")));
        AssertNoStaging();
    }

    [Theory]
    [InlineData("../escaped.txt")]
    [InlineData("nested/../../escaped.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("C:\\absolute.txt")]
    public async Task UnsafePathsAreRejectedAndLeaveNoExtractedItems(string name)
    {
        var archive = WriteZip(("ordinary.txt", "safe"), (name, "escape"));

        await Assert.ThrowsAsync<InvalidDataException>(() => _service.ExtractHereAsync(archive, _root));

        Assert.Equal(new[] { archive }, Directory.EnumerateFileSystemEntries(_root));
        AssertNoStaging();
    }

    [Fact]
    public async Task ExistingDestinationSymlinksAreRejectedWithoutChangingTheirTargets()
    {
        if (OperatingSystem.IsWindows()) return;
        var original = Path.Combine(_root, "original.txt");
        await File.WriteAllTextAsync(original, "untouched");
        var link = Path.Combine(_root, "linked.txt");
        File.CreateSymbolicLink(link, original);
        var archive = WriteZip(("new.txt", "new"), ("linked.txt", "replacement"));

        await Assert.ThrowsAsync<IOException>(() => _service.ExtractHereAsync(archive, _root));

        Assert.Equal("untouched", await File.ReadAllTextAsync(original));
        Assert.NotNull(new FileInfo(link).LinkTarget);
        Assert.False(File.Exists(Path.Combine(_root, "new.txt")));
        AssertNoStaging();
    }

    [Fact]
    public async Task SymbolicLinkArchiveEntriesAreRejectedBeforeAnyOutputIsCommitted()
    {
        var archive = WriteZip(("placeholder.txt", "data"));
        var service = new ArchiveFileService(new SymlinkEntryEngine());

        await Assert.ThrowsAsync<NotSupportedException>(() => service.ExtractHereAsync(archive, _root));

        Assert.Equal(new[] { archive }, Directory.EnumerateFileSystemEntries(_root));
        AssertNoStaging();
    }

    [Fact]
    public async Task CancellationDuringExtractionRemovesAllStagingAndDoesNotPublishFiles()
    {
        var archive = WriteZip(("first.txt", "first"), ("second.txt", new string('x', 128 * 1024)));
        using var cancellation = new CancellationTokenSource();
        var progress = new ImmediateProgress(report =>
        {
            if (report.CompletedEntries > 0) cancellation.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.ExtractHereAsync(archive, _root,
            progress: progress, cancellationToken: cancellation.Token));

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(new[] { archive }, Directory.EnumerateFileSystemEntries(_root));
        AssertNoStaging();
    }

    private string WriteZip(params (string Name, string? Content)[] entries)
    {
        var path = Path.Combine(_root, "archive.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var entry in entries)
        {
            var item = zip.CreateEntry(entry.Name);
            if (entry.Content is null) continue;
            using var writer = new StreamWriter(item.Open(), new UTF8Encoding(false));
            writer.Write(entry.Content);
        }
        return path;
    }

    private void AssertNoStaging() => Assert.Empty(Directory.EnumerateFileSystemEntries(_root, ".eclipsearchive-*", SearchOption.AllDirectories));
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class ImmediateProgress(Action<ArchiveOperationProgress> report) : IProgress<ArchiveOperationProgress>
    {
        public void Report(ArchiveOperationProgress value) => report(value);
    }

    private sealed class SymlinkEntryEngine : IArchiveEngine
    {
        public Task<IReadOnlyList<ArchiveEntryInfo>> ListAsync(Stream source, string? password = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ArchiveEntryInfo>>([new("link", 0, false, false, LinkTarget: "../outside", IsSymbolicLink: true)]);
        public Task CreateAsync(Stream destination, IReadOnlyList<ArchiveWriteEntry> entries, ArchiveFormat format = ArchiveFormat.Zip,
            string? password = null, bool encryptFileNames = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ExtractEntryAsync(Stream source, string entryPath, Stream destination, string? password = null,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("A symbolic-link entry must never be extracted.");
    }
}
