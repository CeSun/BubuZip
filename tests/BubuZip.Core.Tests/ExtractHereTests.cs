using System.IO.Compression;
using System.Text;
using Xunit;

namespace BubuZip.Core.Tests;

public sealed class ExtractHereTests : IDisposable
{
    private readonly string _root = Path.Combine(ArchivePaths.ResolveUserPath(Path.GetTempPath()), "BubuZip.ExtractHere-" + Guid.NewGuid().ToString("N"));
    private readonly ArchiveFileService _service = new(new SharpCompressArchiveEngine());

    public ExtractHereTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData("single.txt")]
    [InlineData("folder/one.txt")]
    [InlineData("folder/nested/one.txt")]
    public async Task SmartSingleRootDoesNotAddAWrapper(string entry)
    {
        var archive = WriteZip((entry, "data"));
        Assert.Equal(_root, await _service.ExtractSmartAsync(archive, _root));
        Assert.Equal("data", await File.ReadAllTextAsync(Path.Combine(_root, entry)));
        Assert.False(Directory.Exists(Path.Combine(_root, "archive")));
        AssertNoStaging();
    }

    [Fact]
    public async Task SmartSingleFolderWithSeveralChildrenAndExplicitDirectoryStaysHere()
    {
        var archive = WriteZip(("folder/", null), ("folder/a.txt", "a"), ("folder/b.txt", "b"));
        await _service.ExtractSmartAsync(archive, _root);
        Assert.Equal("a", await File.ReadAllTextAsync(Path.Combine(_root, "folder/a.txt")));
        Assert.Equal("b", await File.ReadAllTextAsync(Path.Combine(_root, "folder/b.txt")));
        Assert.False(Directory.Exists(Path.Combine(_root, "archive")));
        AssertNoStaging();
    }

    [Fact]
    public async Task SmartMultipleRootsUseArchiveNameAndMergeWithConflictChoices()
    {
        var archive = WriteZip(("a.txt", "a"), ("folder/b.txt", "b"));
        Directory.CreateDirectory(Path.Combine(_root, "archive"));
        await File.WriteAllTextAsync(Path.Combine(_root, "archive/a.txt"), "original");
        var interaction = new TestInteraction(_ => new(ArchiveConflictAction.KeepBoth));
        Assert.Equal(Path.Combine(_root, "archive"), await _service.ExtractSmartAsync(archive, _root, interaction: interaction));
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(_root, "archive/a.txt")));
        Assert.Equal("a", await File.ReadAllTextAsync(Path.Combine(_root, "archive/a (2).txt")));
        Assert.Equal("b", await File.ReadAllTextAsync(Path.Combine(_root, "archive/folder/b.txt")));
        Assert.Equal(new(0, 0, 1), interaction.Summary);
        AssertNoStaging();
    }

    [Fact]
    public async Task SmartWrapperConflictingWithFileCanKeepBoth()
    {
        var archive = WriteZip(("a.txt", "a"), ("b.txt", "b"));
        await File.WriteAllTextAsync(Path.Combine(_root, "archive"), "keep");
        var output = await _service.ExtractSmartAsync(archive, _root, interaction: new TestInteraction(conflict =>
        {
            Assert.False(conflict.CanReplace);
            Assert.True(conflict.IncomingIsDirectory);
            return new(ArchiveConflictAction.KeepBoth);
        }));
        Assert.Equal(Path.Combine(_root, "archive (2)"), output);
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(_root, "archive")));
        Assert.Equal("a", await File.ReadAllTextAsync(Path.Combine(_root, "archive (2)/a.txt")));
        AssertNoStaging();
    }

    [Fact]
    public async Task SmartEmptyArchiveDoesNotCreateAnEmptyWrapper()
    {
        var archive = WriteZip();
        await _service.ExtractSmartAsync(archive, _root);
        Assert.Equal(new[] { archive }, Directory.EnumerateFileSystemEntries(_root));
    }

    [Theory]
    [InlineData("package.tar.gz")]
    [InlineData("package.TAR.XZ")]
    [InlineData("package.tgz")]
    public async Task SmartCompoundArchiveNameUsesOneWrapper(string archiveName)
    {
        // The engine detects the content; the wrapper name follows the filename.
        var archive = Path.Combine(_root, archiveName);
        File.Move(WriteZip(("a.txt", "a"), ("b.txt", "b")), archive);
        var output = await _service.ExtractSmartAsync(archive, _root);
        Assert.Equal(Path.Combine(_root, "package"), output);
        Assert.Equal("a", await File.ReadAllTextAsync(Path.Combine(output, "a.txt")));
        AssertNoStaging();
    }

    [Theory]
    [InlineData(ArchiveConflictAction.Skip)]
    [InlineData(ArchiveConflictAction.KeepBoth)]
    [InlineData(ArchiveConflictAction.Cancel)]
    public async Task NamedFolderConflictingWithAFileHonorsTheDecision(ArchiveConflictAction action)
    {
        var archive = WriteZip(("folder/a.txt", "a"));
        var destination = Path.Combine(_root, "archive");
        await File.WriteAllTextAsync(destination, "original");
        var interaction = new TestInteraction(_ => new(action));
        if (action == ArchiveConflictAction.Cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                _service.ExtractToFolderAsync(archive, destination, interaction: interaction));
        else
        {
            var output = await _service.ExtractToFolderAsync(archive, destination, interaction: interaction);
            Assert.Equal(action == ArchiveConflictAction.Skip ? _root : destination + " (2)", output);
        }
        Assert.Equal("original", await File.ReadAllTextAsync(destination));
        Assert.Equal(action == ArchiveConflictAction.KeepBoth, File.Exists(Path.Combine(destination + " (2)", "folder/a.txt")));
        AssertNoStaging();
    }

    [Fact]
    public async Task SmartRejectsTraversalWithoutPublishingAnyFiles()
    {
        var archive = WriteZip(("safe.txt", "data"), ("../unsafe.txt", "data"));
        await Assert.ThrowsAsync<InvalidDataException>(() => _service.ExtractSmartAsync(archive, _root));
        Assert.Equal(new[] { archive }, Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public async Task SmartEncryptedArchiveCanRetryWithPassword()
    {
        var input = Path.Combine(_root, "source.txt");
        await File.WriteAllTextAsync(input, "secret");
        var archive = Path.Combine(_root, "encrypted.7z");
        await _service.CreateAsync([input], archive, ArchiveFormat.SevenZip, "password", encryptFileNames: true);
        var output = Directory.CreateDirectory(Path.Combine(_root, "output")).FullName;
        await Assert.ThrowsAnyAsync<Exception>(() => _service.ExtractSmartAsync(archive, output));
        Assert.Empty(Directory.EnumerateFileSystemEntries(output));
        await _service.ExtractSmartAsync(archive, output, "password");
        Assert.Equal("secret", await File.ReadAllTextAsync(Path.Combine(output, "source.txt")));
        AssertNoStaging();
    }

    [Theory]
    [InlineData(ArchiveConflictAction.Replace)]
    [InlineData(ArchiveConflictAction.Skip)]
    [InlineData(ArchiveConflictAction.KeepBoth)]
    [InlineData(ArchiveConflictAction.Cancel)]
    public async Task InteractiveConflictChoiceHasThePromisedEffect(ArchiveConflictAction action)
    {
        var target = Path.Combine(_root, "same.txt");
        await File.WriteAllTextAsync(target, "original");
        var archive = WriteZip(("same.txt", "incoming"), ("new.txt", "new"));
        var interaction = new TestInteraction(_ => new(action));
        if (action == ArchiveConflictAction.Cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.ExtractHereAsync(archive, _root, interaction: interaction));
        else await _service.ExtractHereAsync(archive, _root, interaction: interaction);
        Assert.Equal(action == ArchiveConflictAction.Replace ? "incoming" : "original", await File.ReadAllTextAsync(target));
        Assert.Equal(action != ArchiveConflictAction.Cancel, File.Exists(Path.Combine(_root, "new.txt")));
        Assert.Equal(action == ArchiveConflictAction.KeepBoth, File.Exists(Path.Combine(_root, "same (2).txt")));
        if (action == ArchiveConflictAction.KeepBoth) Assert.Equal("incoming", await File.ReadAllTextAsync(Path.Combine(_root, "same (2).txt")));
        var conflict = Assert.Single(interaction.Conflicts);
        Assert.Equal(target, conflict.DestinationPath);
        Assert.True(conflict.CanReplace);
        Assert.Equal(8, conflict.ExistingSize);
        Assert.Equal(8, conflict.IncomingSize);
        if (action == ArchiveConflictAction.Cancel) Assert.Null(interaction.Summary);
        else Assert.Equal(new(action == ArchiveConflictAction.Replace ? 1 : 0,
            action == ArchiveConflictAction.Skip ? 1 : 0, action == ArchiveConflictAction.KeepBoth ? 1 : 0), interaction.Summary);
        AssertNoStaging();
    }

    [Fact]
    public async Task ExistingDirectoriesMergeAndUnrelatedContentsAreKept()
    {
        Directory.CreateDirectory(Path.Combine(_root, "folder"));
        await File.WriteAllTextAsync(Path.Combine(_root, "folder", "keep.txt"), "keep");
        var archive = WriteZip(("folder/new.txt", "new"), ("folder/empty/", null));
        var interaction = new TestInteraction(_ => throw new InvalidOperationException("No file conflict expected"));
        await _service.ExtractHereAsync(archive, _root, interaction: interaction);
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(_root, "folder", "keep.txt")));
        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(_root, "folder", "new.txt")));
        Assert.True(Directory.Exists(Path.Combine(_root, "folder", "empty")));
        Assert.Empty(interaction.Conflicts);
        AssertNoStaging();
    }

    [Fact]
    public async Task KeepBothAvoidsNamesReservedByOtherIncomingEntries()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "same.txt"), "original");
        var archive = WriteZip(("same.txt", "renamed"), ("same (2).txt", "separate incoming file"));
        await _service.ExtractHereAsync(archive, _root, interaction: new TestInteraction(_ => new(ArchiveConflictAction.KeepBoth)));
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(_root, "same.txt")));
        Assert.Equal("separate incoming file", await File.ReadAllTextAsync(Path.Combine(_root, "same (2).txt")));
        Assert.Equal("renamed", await File.ReadAllTextAsync(Path.Combine(_root, "same (3).txt")));
        AssertNoStaging();
    }

    [Fact]
    public async Task ApplyToAllDoesNotReplaceADirectoryWhenTheConflictTypeChanges()
    {
        foreach (var name in new[] { "a.txt", "b.txt" }) await File.WriteAllTextAsync(Path.Combine(_root, name), "old");
        Directory.CreateDirectory(Path.Combine(_root, "c.txt"));
        await File.WriteAllTextAsync(Path.Combine(_root, "c.txt", "keep.txt"), "keep directory");
        var archive = WriteZip(("a.txt", "new a"), ("b.txt", "new b"), ("c.txt", "new c"));
        var interaction = new TestInteraction(conflict => conflict.CanReplace
            ? new(ArchiveConflictAction.Replace, true) : new(ArchiveConflictAction.KeepBoth));
        await _service.ExtractHereAsync(archive, _root, interaction: interaction);
        Assert.Equal(2, interaction.Conflicts.Count);
        Assert.False(interaction.Conflicts[1].CanReplace);
        Assert.Equal("new b", await File.ReadAllTextAsync(Path.Combine(_root, "b.txt")));
        Assert.Equal("keep directory", await File.ReadAllTextAsync(Path.Combine(_root, "c.txt", "keep.txt")));
        Assert.Equal("new c", await File.ReadAllTextAsync(Path.Combine(_root, "c (2).txt")));
        Assert.Equal(new(2, 0, 1), interaction.Summary);
        AssertNoStaging();
    }

    [Fact]
    public async Task CancellingALaterConflictKeepsTheEarlierReplacedFile()
    {
        foreach (var name in new[] { "a.txt", "b.txt" }) await File.WriteAllTextAsync(Path.Combine(_root, name), "old");
        var archive = WriteZip(("a.txt", "new"), ("b.txt", "new"));
        var interaction = new TestInteraction(conflict => new(conflict.EntryPath == "a.txt" ? ArchiveConflictAction.Replace : ArchiveConflictAction.Cancel));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.ExtractHereAsync(archive, _root, interaction: interaction));
        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(_root, "a.txt")));
        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(_root, "b.txt")));
        Assert.Equal(2, interaction.Conflicts.Count);
        AssertNoStaging();
    }

    [Fact]
    public async Task ConflictPromptsInterleaveWithExtraction()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "same.txt"), "original");
        var archive = WriteZip(("first.txt", "first"), ("same.txt", "incoming"), ("last.txt", "last"));
        var seen = new List<string>();
        var interaction = new TestInteraction(conflict =>
        {
            Assert.True(File.Exists(Path.Combine(_root, "first.txt")));
            Assert.False(File.Exists(Path.Combine(_root, "last.txt")));
            seen.Add(conflict.EntryPath);
            return new(ArchiveConflictAction.Skip);
        });
        await _service.ExtractHereAsync(archive, _root, interaction: interaction);
        Assert.Equal(["same.txt"], seen);
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(_root, "same.txt")));
        Assert.Equal("last", await File.ReadAllTextAsync(Path.Combine(_root, "last.txt")));
        AssertNoStaging();
    }

    [Fact]
    public async Task ExtractingASelectionIntoAnExistingFolderUsesTheSameConflictChoices()
    {
        var folder = Path.Combine(_root, "output");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "selected.txt"), "old");
        var archive = WriteZip(("selected.txt", "new"), ("excluded.txt", "excluded"));
        await _service.ExtractAsync(archive, folder, entryPaths: ["selected.txt"],
            interaction: new TestInteraction(_ => new(ArchiveConflictAction.Replace)));
        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(folder, "selected.txt")));
        Assert.False(File.Exists(Path.Combine(folder, "excluded.txt")));
        AssertNoStaging();
    }

    private sealed class TestInteraction(Func<ArchiveExtractionConflict, ArchiveConflictDecision> decide) : IArchiveExtractionInteraction
    {
        public List<ArchiveExtractionConflict> Conflicts { get; } = [];
        public ArchiveExtractionSummary? Summary { get; private set; }
        public Task<ArchiveConflictDecision> ResolveConflictAsync(ArchiveExtractionConflict conflict, CancellationToken cancellationToken)
        {
            Conflicts.Add(conflict);
            return Task.FromResult(decide(conflict));
        }
        public void ReportExtractionSummary(ArchiveExtractionSummary summary) => Summary = summary;
    }

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
    public async Task AFileOrDirectoryConflictKeepsEntriesExtractedBeforeIt(bool directoryConflict)
    {
        var conflict = Path.Combine(_root, directoryConflict ? "资料" : "same.txt");
        if (directoryConflict)
        {
            Directory.CreateDirectory(conflict);
            await File.WriteAllTextAsync(Path.Combine(conflict, "original.txt"), "keep directory");
        }
        else await File.WriteAllTextAsync(conflict, "keep file");
        var archive = WriteZip(("would-have-been-new.txt", "new"),
            (directoryConflict ? "资料/original.txt" : "same.txt", "must not overwrite"));

        await Assert.ThrowsAsync<IOException>(() => _service.ExtractHereAsync(archive, _root));

        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(_root, "would-have-been-new.txt")));
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
    public async Task CancellationDuringExtractionKeepsAlreadyPublishedFiles()
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
        Assert.True(File.Exists(Path.Combine(_root, "first.txt")));
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

    private void AssertNoStaging() => Assert.Empty(Directory.EnumerateFileSystemEntries(_root, ".bubuzip-*", SearchOption.AllDirectories));
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
