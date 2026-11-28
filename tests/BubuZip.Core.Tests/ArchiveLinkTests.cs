using System.IO.Compression;
using System.Text;
using Xunit;

namespace BubuZip.Core.Tests;

public sealed class ArchiveLinkTests : IDisposable
{
    private readonly string _root = ArchivePaths.ResolveUserPath(Path.Combine(Path.GetTempPath(), "bubuzip-links-" + Guid.NewGuid().ToString("N")));
    private readonly ArchiveFileService _service = new(new SharpCompressArchiveEngine());
    private const string Alias = "BubuZip.app/Contents/Resources/app/libSDL3.dylib";
    private const string Library = "BubuZip.app/Contents/Frameworks/libSDL3.0.dylib";
    private const string LinkText = "../../Frameworks/libSDL3.0.dylib";

    public ArchiveLinkTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData("new")]
    [InlineData("here")]
    [InlineData("smart")]
    [InlineData("folder")]
    public async Task MacBundleFileLinkExtractsAfterItsTargetAndPreservesLinkText(string mode)
    {
        var archive = WriteZip((Alias, LinkText, true), (Library, "native-library", false));
        var output = Path.Combine(_root, "output");
        if (mode != "new") Directory.CreateDirectory(output);
        switch (mode)
        {
            case "new": await _service.ExtractAsync(archive, output); break;
            case "here": await _service.ExtractHereAsync(archive, output); break;
            case "smart": await _service.ExtractSmartAsync(archive, output); break;
            case "folder":
                output = Path.Combine(output, "named");
                await _service.ExtractToFolderAsync(archive, output);
                break;
        }
        var link = Path.Combine(output, Alias);
        Assert.Equal("native-library", await File.ReadAllTextAsync(link));
        Assert.Equal(LinkText, new FileInfo(link).LinkTarget!.Replace('\\', '/'));
        Assert.Equal(Path.GetFullPath(Path.Combine(output, Library)), new FileInfo(link).ResolveLinkTarget(true)!.FullName);
        Assert.Empty(Directory.EnumerateFileSystemEntries(output, ".bubuzip-*", SearchOption.AllDirectories));
        await _service.TestAsync(archive);
    }

    [Fact]
    public async Task LinkChainsAndOriginalSpellingArePreserved()
    {
        var archive = WriteZip(("first", "./second", true), ("second", "dir/file", true), ("dir/file", "payload", false));
        var output = Path.Combine(_root, "output");
        await _service.ExtractAsync(archive, output);
        Assert.Equal("./second", new FileInfo(Path.Combine(output, "first")).LinkTarget!.Replace('\\', '/'));
        Assert.Equal("payload", await File.ReadAllTextAsync(Path.Combine(output, "first")));
        Assert.Equal("payload", await File.ReadAllTextAsync(Path.Combine(output, "second")));
    }

    [Theory]
    [InlineData(ExtractionPathMode.NoPaths, "", "libSDL3.dylib", "libSDL3.0.dylib")]
    [InlineData(ExtractionPathMode.CurrentFolder, "BubuZip.app/Contents", "Resources/app/libSDL3.dylib", LinkText)]
    public async Task PathModesRemapLinkTargets(ExtractionPathMode mode, string prefix, string alias, string target)
    {
        var archive = WriteZip((Alias, LinkText, true), (Library, "library", false));
        var output = Path.Combine(_root, "output");
        await _service.ExtractAsync(archive, output, settings: new(mode, prefix));
        Assert.Equal("library", await File.ReadAllTextAsync(Path.Combine(output, alias)));
        Assert.Equal(target, new FileInfo(Path.Combine(output, alias)).LinkTarget!.Replace('\\', '/'));
    }

    [Fact]
    public async Task SelectingALinkWithoutItsTargetFailsBeforeWriting()
    {
        var archive = WriteZip((Alias, LinkText, true), (Library, "library", false));
        var output = Path.Combine(_root, "output");
        await Assert.ThrowsAsync<InvalidDataException>(() => _service.ExtractAsync(archive, output, entryPaths: [Alias]));
        Assert.False(Directory.Exists(output));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("../../outside")]
    [InlineData("/outside")]
    [InlineData("C:/outside")]
    [InlineData("C:outside")]
    [InlineData("\\\\server\\share\\outside")]
    [InlineData("ordinary:stream")]
    [InlineData("ordinary\0")]
    [InlineData("ordinary\n")]
    [InlineData("missing")]
    [InlineData("missing/../ordinary")]
    [InlineData("ordinary/")]
    public async Task UnsafeOrMissingLinkTargetsFailBeforeWriting(string target)
    {
        var archive = WriteZip(("ordinary", "safe", false), ("link", target, true));
        var output = Path.Combine(_root, "output");
        await Assert.ThrowsAsync<InvalidDataException>(() => _service.ExtractAsync(archive, output));
        Assert.False(Directory.Exists(output));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CyclesIncludingSelfLinksFailBeforeWriting(bool self)
    {
        var archive = self ? WriteZip(("a", "a", true)) : WriteZip(("a", "b", true), ("b", "a", true));
        var output = Path.Combine(_root, "output");
        await Assert.ThrowsAsync<InvalidDataException>(() => _service.ExtractAsync(archive, output));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task CannotWriteThroughAnArchiveLink()
    {
        var archive = WriteZip(("pivot", "target", true), ("pivot/child", "bad", false), ("target", "safe", false));
        var output = Path.Combine(_root, "output");
        await Assert.ThrowsAsync<InvalidDataException>(() => _service.ExtractAsync(archive, output));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task CannotNormalizeThroughALinkEvenWhenTheLexicalTargetIsSafe()
    {
        var archive = WriteZip(("pivot", "target", true), ("alias", "pivot/../target", true), ("target", "safe", false));
        var output = Path.Combine(_root, "output");
        await Assert.ThrowsAsync<InvalidDataException>(() => _service.ExtractAsync(archive, output));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task DirectoryLinksRemainUnsupported()
    {
        var archive = WriteZip(("dir/", "", false), ("alias", "dir", true));
        var output = Path.Combine(_root, "output");
        await Assert.ThrowsAsync<NotSupportedException>(() => _service.ExtractAsync(archive, output));
        Assert.False(Directory.Exists(output));
    }

    [Theory]
    [InlineData(ArchiveConflictAction.KeepBoth)]
    [InlineData(ArchiveConflictAction.Replace)]
    public async Task LinksFollowTheActualTargetChosenByConflictResolution(ArchiveConflictAction action)
    {
        var archive = WriteZip(("alias", "file.txt", true), ("file.txt", "new", false));
        var output = Path.Combine(_root, "output");
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "file.txt"), "old");
        await _service.ExtractHereAsync(archive, output, interaction: new Interaction(_ => action));
        var target = action == ArchiveConflictAction.KeepBoth ? "file (2).txt" : "file.txt";
        Assert.Equal(target, new FileInfo(Path.Combine(output, "alias")).LinkTarget);
        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(output, "alias")));
        if (action == ArchiveConflictAction.KeepBoth)
            Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(output, "file.txt")));
    }

    [Fact]
    public async Task LinkConflictCanReplaceAnOrdinaryFileWithoutWritingThroughIt()
    {
        var archive = WriteZip(("alias", "target", true), ("target", "new", false));
        var output = Path.Combine(_root, "output");
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "alias"), "old");
        await _service.ExtractHereAsync(archive, output, interaction: new Interaction(_ => ArchiveConflictAction.Replace));
        Assert.Equal("target", new FileInfo(Path.Combine(output, "alias")).LinkTarget);
        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(output, "alias")));
    }

    [Fact]
    public async Task SkippedTargetsCannotBeReplacedByPreexistingContent()
    {
        var archive = WriteZip(("alias", "file", true), ("file", "new", false));
        var output = Path.Combine(_root, "output");
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "file"), "old");
        await Assert.ThrowsAsync<IOException>(() => _service.ExtractHereAsync(archive, output,
            interaction: new Interaction(_ => ArchiveConflictAction.Skip)));
        Assert.False(File.Exists(Path.Combine(output, "alias")));
        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(output, "file")));
    }

    [Fact]
    public async Task PreexistingDestinationLinkIsRejectedBeforeAnyWrites()
    {
        var archive = WriteZip(("ordinary", "new", false), ("alias", "target", true), ("target", "replacement", false));
        var output = Path.Combine(_root, "output");
        Directory.CreateDirectory(output);
        var outside = Path.Combine(_root, "outside");
        await File.WriteAllTextAsync(outside, "untouched");
        File.CreateSymbolicLink(Path.Combine(output, "target"), outside);
        await Assert.ThrowsAsync<IOException>(() => _service.ExtractHereAsync(archive, output));
        Assert.False(File.Exists(Path.Combine(output, "ordinary")));
        Assert.Equal("untouched", await File.ReadAllTextAsync(outside));
    }

    [Fact]
    public async Task ZipLinkPayloadCrcIsCheckedBeforeWriting()
    {
        const string target = "unique-target-for-crc";
        var archive = WriteZip(("alias", target, true), (target, "content", false));
        var bytes = File.ReadAllBytes(archive);
        var offset = bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(target));
        Assert.True(offset >= 0);
        bytes[offset] ^= 1;
        File.WriteAllBytes(archive, bytes);
        var output = Path.Combine(_root, "output");
        await Assert.ThrowsAnyAsync<Exception>(() => _service.ExtractAsync(archive, output));
        Assert.False(Directory.Exists(output));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(32769)]
    public async Task InvalidLinkPayloadSizeIsRejected(int size)
    {
        var archive = WriteZip(("alias", new string('x', size), true));
        var output = Path.Combine(_root, "output");
        await Assert.ThrowsAsync<InvalidDataException>(() => _service.ExtractAsync(archive, output));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task CancelAfterRegularFilesDoesNotCreateLinks()
    {
        var archive = WriteZip(("alias", "target", true), ("target", "content", false));
        var output = Path.Combine(_root, "output");
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.ExtractAsync(archive, output,
            progress: new ImmediateProgress(value => { if (value.CompletedEntries == 1) cancellation.Cancel(); }),
            cancellationToken: cancellation.Token));
        Assert.Equal("content", await File.ReadAllTextAsync(Path.Combine(output, "target")));
        Assert.Null(new FileInfo(Path.Combine(output, "alias")).LinkTarget);
    }

    [Fact]
    public async Task ProgressIncludesLinkCompletion()
    {
        var archive = WriteZip(("alias", "target", true), ("target", "content", false));
        var reports = new List<ArchiveOperationProgress>();
        await _service.ExtractAsync(archive, Path.Combine(_root, "output"), progress: new ImmediateProgress(reports.Add));
        Assert.Equal(2, reports[^1].CompletedEntries);
        Assert.Equal(100d, reports[^1].Percent);
        Assert.Equal(reports[^1].TotalBytes, reports[^1].ProcessedBytes);
    }

    [Fact]
    public async Task RemappedLinkDoesNotDependOnUnselectedIntermediateDirectories()
    {
        var archive = WriteZip(("alias", "dir/../target", true), ("target", "content", false), ("dir/", "", false));
        var output = Path.Combine(_root, "output");
        await _service.ExtractAsync(archive, output, entryPaths: ["alias", "target"]);
        Assert.Equal("target", new FileInfo(Path.Combine(output, "alias")).LinkTarget);
        Assert.Equal("content", await File.ReadAllTextAsync(Path.Combine(output, "alias")));
    }

    private sealed class ImmediateProgress(Action<ArchiveOperationProgress> report) : IProgress<ArchiveOperationProgress>
    {
        public void Report(ArchiveOperationProgress value) => report(value);
    }

    [Fact]
    public async Task ArchiveEditingStillRejectsLinksWithoutChangingTheOriginal()
    {
        var archive = WriteZip(("alias", "target", true), ("target", "content", false));
        var original = File.ReadAllBytes(archive);
        var editor = new ArchiveEditService(new SharpCompressArchiveEngine(), _service);
        await Assert.ThrowsAsync<NotSupportedException>(() => editor.DeleteAsync(archive, ["alias"]));
        Assert.Equal(original, File.ReadAllBytes(archive));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root, ".bubuzip-*"));
    }

    private string WriteZip(params (string Name, string Text, bool Link)[] entries)
    {
        var archive = Path.Combine(_root, "input.zip");
        using var zip = ZipFile.Open(archive, ZipArchiveMode.Create);
        foreach (var (name, text, link) in entries)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
            if (link) entry.ExternalAttributes = unchecked((int)(0xA1FFu << 16));
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(text);
        }
        return archive;
    }

    private sealed class Interaction(Func<ArchiveExtractionConflict, ArchiveConflictAction> decide) : IArchiveExtractionInteraction
    {
        public Task<ArchiveConflictDecision> ResolveConflictAsync(ArchiveExtractionConflict conflict, CancellationToken cancellationToken) =>
            Task.FromResult(new ArchiveConflictDecision(decide(conflict)));
        public void ReportExtractionSummary(ArchiveExtractionSummary summary) { }
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
