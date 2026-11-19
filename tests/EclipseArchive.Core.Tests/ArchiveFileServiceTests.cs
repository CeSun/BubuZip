using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using EclipseArchive.Core;
using Xunit;

namespace EclipseArchive.Core.Tests;

public sealed class ArchiveFileServiceTests : IDisposable
{
    private readonly string _root = CreateTemporaryDirectory();
    private readonly ArchiveFileService _service = new(new SharpCompressArchiveEngine());

    [Theory]
    [InlineData(ArchiveFormat.Zip)]
    [InlineData(ArchiveFormat.SevenZip)]
    public async Task FolderRoundTripPreservesUnicodeEmptyFilesAndEmptyDirectories(ArchiveFormat format)
    {
        var source = Path.Combine(_root, "资料");
        Directory.CreateDirectory(Path.Combine(source, "空目录"));
        await File.WriteAllTextAsync(Path.Combine(source, "你好.txt"), "中文内容 📦");
        await File.WriteAllBytesAsync(Path.Combine(source, "空文件.txt"), []);
        var archive = Path.Combine(_root, "bundle.archive");
        var output = Path.Combine(_root, "unpacked");
        var reports = new List<ArchiveOperationProgress>();

        await _service.CreateAsync([source], archive, format, progress: new ImmediateProgress(reports.Add));
        await _service.TestAsync(archive);
        await _service.ExtractAsync(archive, output);

        Assert.Equal("中文内容 📦", await File.ReadAllTextAsync(Path.Combine(output, "资料", "你好.txt")));
        Assert.Empty(await File.ReadAllBytesAsync(Path.Combine(output, "资料", "空文件.txt")));
        Assert.True(Directory.Exists(Path.Combine(output, "资料", "空目录")));
        Assert.Equal(100d, reports[^1].Percent);
        AssertNoTemporaryOutputs();
    }

    [Fact]
    public async Task SelectingVirtualDirectoryExtractsDescendantsAndExcludesSimilarPrefixes()
    {
        var archive = Path.Combine(_root, "virtual.zip");
        await WriteZip(archive,
            ("资料/one.txt", "first"),
            ("资料/子目录/two.txt", "second"),
            ("资料相邻/three.txt", "excluded"));
        var output = Path.Combine(_root, "selection");

        await _service.ExtractAsync(archive, output, entryPaths: ["资料"]);

        Assert.Equal("first", await File.ReadAllTextAsync(Path.Combine(output, "资料", "one.txt")));
        Assert.Equal("second", await File.ReadAllTextAsync(Path.Combine(output, "资料", "子目录", "two.txt")));
        Assert.False(Directory.Exists(Path.Combine(output, "资料相邻")));
    }

    [Fact]
    public async Task SelectingSingleFileDoesNotExtractItsSiblings()
    {
        var archive = Path.Combine(_root, "single.zip");
        await WriteZip(archive, ("dir/one.txt", "first"), ("dir/two.txt", "second"));
        var output = Path.Combine(_root, "selection");

        await _service.ExtractAsync(archive, output, entryPaths: ["dir/one.txt"]);

        Assert.True(File.Exists(Path.Combine(output, "dir", "one.txt")));
        Assert.False(File.Exists(Path.Combine(output, "dir", "two.txt")));
    }

    [Theory]
    [InlineData("../escaped.txt")]
    [InlineData("nested/../../escaped.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("C:\\absolute.txt")]
    public async Task UnsafeEntryPathsAreRejectedBeforeCreatingAnOutput(string path)
    {
        var archive = Path.Combine(_root, "unsafe.zip");
        await WriteZip(archive, (path, "must not escape"));
        var output = Path.Combine(_root, "unpacked");

        await Assert.ThrowsAsync<InvalidDataException>(() => _service.ExtractAsync(archive, output));

        Assert.False(Directory.Exists(output));
        Assert.False(File.Exists(Path.Combine(_root, "escaped.txt")));
        AssertNoTemporaryOutputs();
    }

    [Theory]
    [InlineData("Dir/one.txt", "dir/two.txt")]
    [InlineData("one.txt", "ONE.txt")]
    [InlineData("dir", "dir/child.txt")]
    public async Task ConflictingFilesystemPathsAreRejected(string first, string second)
    {
        var archive = Path.Combine(_root, "conflict.zip");
        await WriteZip(archive, (first, "one"), (second, "two"));
        var output = Path.Combine(_root, "unpacked");

        await Assert.ThrowsAsync<InvalidDataException>(() => _service.ExtractAsync(archive, output));

        Assert.False(Directory.Exists(output));
        AssertNoTemporaryOutputs();
    }

    [Fact]
    public async Task ExistingOutputsArePreserved()
    {
        var source = Path.Combine(_root, "source.txt");
        var archive = Path.Combine(_root, "existing.zip");
        await File.WriteAllTextAsync(source, "source");
        await WriteZip(archive, ("original.txt", "original archive content"));
        var originalArchive = await File.ReadAllBytesAsync(archive);

        await Assert.ThrowsAsync<IOException>(() => _service.CreateAsync([source], archive, ArchiveFormat.Zip));
        Assert.Equal(originalArchive, await File.ReadAllBytesAsync(archive));

        var output = Path.Combine(_root, "existing-folder");
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "keep.txt"), "keep");
        await Assert.ThrowsAsync<IOException>(() => _service.ExtractAsync(archive, output));
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(output, "keep.txt")));
        Assert.Single(Directory.EnumerateFileSystemEntries(output));
        AssertNoTemporaryOutputs();
    }

    [Fact]
    public async Task DestinationInsideSourceFolderIsRejected()
    {
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        var archive = Path.Combine(source, "self.zip");

        await Assert.ThrowsAsync<IOException>(() => _service.CreateAsync([source], archive, ArchiveFormat.Zip));

        Assert.Empty(Directory.EnumerateFileSystemEntries(source));
        AssertNoTemporaryOutputs();
    }

    [Fact]
    public async Task FailedCreationDoesNotLeaveFinalOrTemporaryOutput()
    {
        var source = Path.Combine(_root, "source.txt");
        await File.WriteAllTextAsync(source, "source");
        var archive = Path.Combine(_root, "unsupported.zip");

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            _service.CreateAsync([source], archive, ArchiveFormat.Zip, password: "unsupported"));

        Assert.False(File.Exists(archive));
        AssertNoTemporaryOutputs();
    }

    [Theory]
    [InlineData(ArchiveFormat.Zip)]
    [InlineData(ArchiveFormat.SevenZip)]
    public async Task CancelledCreationDoesNotCommitPartialArchive(ArchiveFormat format)
    {
        var source = Path.Combine(_root, "source.txt");
        await File.WriteAllTextAsync(source, new string('x', 128 * 1024));
        var archive = Path.Combine(_root, "cancelled.archive");
        using var cancellation = new CancellationTokenSource();
        var progress = new ImmediateProgress(report =>
        {
            if (report.CompletedEntries > 0) cancellation.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _service.CreateAsync([source], archive, format,
                progress: progress, cancellationToken: cancellation.Token));

        Assert.True(cancellation.IsCancellationRequested);
        Assert.False(File.Exists(archive));
        AssertNoTemporaryOutputs();
    }

    [Theory]
    [InlineData(ArchiveFormat.Zip)]
    [InlineData(ArchiveFormat.SevenZip)]
    public async Task CancelledExtractionDoesNotCommitPartiallyExtractedDirectory(ArchiveFormat format)
    {
        var source = Path.Combine(_root, "source.txt");
        await File.WriteAllTextAsync(source, "source content");
        var archive = Path.Combine(_root, "archive.data");
        await _service.CreateAsync([source], archive, format);
        var output = Path.Combine(_root, "cancelled-output");
        using var cancellation = new CancellationTokenSource();
        var progress = new ImmediateProgress(report =>
        {
            if (report.CompletedEntries > 0) cancellation.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _service.ExtractAsync(archive, output, progress: progress, cancellationToken: cancellation.Token));

        Assert.True(cancellation.IsCancellationRequested);
        Assert.False(Directory.Exists(output));
        AssertNoTemporaryOutputs();
    }

    [Fact]
    public async Task EncryptedSevenZipTestAndExtractionRequireCorrectPassword()
    {
        var source = Path.Combine(_root, "秘密.txt");
        await File.WriteAllTextAsync(source, "private content");
        var archive = Path.Combine(_root, "encrypted.7z");
        await _service.CreateAsync([source], archive, ArchiveFormat.SevenZip, password: "correct-password");
        var output = Path.Combine(_root, "wrong-password-output");

        await Assert.ThrowsAnyAsync<Exception>(() => _service.ExtractAsync(archive, output, password: "wrong-password"));
        Assert.False(Directory.Exists(output));
        AssertNoTemporaryOutputs();
        await Assert.ThrowsAnyAsync<Exception>(() => _service.TestAsync(archive, password: "wrong-password"));
        await _service.TestAsync(archive, password: "correct-password");
        await _service.ExtractAsync(archive, output, password: "correct-password");
        Assert.Equal("private content", await File.ReadAllTextAsync(Path.Combine(output, "秘密.txt")));
    }

    [Fact]
    public async Task SourceSymbolicLinksAreRejected()
    {
        if (OperatingSystem.IsWindows()) return;
        var real = Path.Combine(_root, "real.txt");
        await File.WriteAllTextAsync(real, "real content");
        var link = Path.Combine(_root, "link.txt");
        File.CreateSymbolicLink(link, real);
        var archive = Path.Combine(_root, "links.zip");

        await Assert.ThrowsAsync<IOException>(() => _service.CreateAsync([link], archive, ArchiveFormat.Zip));

        Assert.False(File.Exists(archive));
        AssertNoTemporaryOutputs();
    }

    [Fact]
    public async Task DestinationSymbolicLinkAncestorsAreRejected()
    {
        if (OperatingSystem.IsWindows()) return;
        var archive = Path.Combine(_root, "archive.zip");
        await WriteZip(archive, ("inside.txt", "content"));
        var real = Path.Combine(_root, "real-directory");
        Directory.CreateDirectory(real);
        var alias = Path.Combine(_root, "alias-directory");
        Directory.CreateSymbolicLink(alias, real);

        await Assert.ThrowsAsync<IOException>(() =>
            _service.ExtractAsync(archive, Path.Combine(alias, "output")));

        Assert.Empty(Directory.EnumerateFileSystemEntries(real));
        AssertNoTemporaryOutputs();
    }

    [Fact]
    public async Task ArchiveLinksAreRejectedBeforeCreatingAnOutput()
    {
        var archive = Path.Combine(_root, "link.zip");
        await using (var file = File.Create(archive))
        {
            using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true);
            var link = zip.CreateEntry("link");
            link.ExternalAttributes = unchecked((int)(0xA1FFu << 16));
            await using var content = link.Open();
            await content.WriteAsync(Encoding.UTF8.GetBytes("../outside"));
        }
        var output = Path.Combine(_root, "unpacked");

        await Assert.ThrowsAsync<NotSupportedException>(() => _service.ExtractAsync(archive, output));

        Assert.False(Directory.Exists(output));
        AssertNoTemporaryOutputs();
    }

    [Fact]
    public async Task ArchiveTestRejectsStoredZipPayloadWithIncorrectCrc()
    {
        var archive = Path.Combine(_root, "crc.zip");
        var payload = Encoding.ASCII.GetBytes("KNOWN-STORED-ZIP-CONTENT-FOR-CRC-VALIDATION");
        await using (var file = File.Create(archive))
        {
            using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true);
            var entry = zip.CreateEntry("payload.txt", CompressionLevel.NoCompression);
            await using var content = entry.Open();
            await content.WriteAsync(payload);
        }

        await _service.TestAsync(archive);

        var bytes = await File.ReadAllBytesAsync(archive);
        Assert.Equal(0x04034B50u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4)));
        Assert.Equal((ushort)0, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8, 2)));
        var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(26, 2));
        var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(28, 2));
        var payloadOffset = 30 + nameLength + extraLength;
        Assert.Equal(payload, bytes.AsSpan(payloadOffset, payload.Length).ToArray());
        // Change only stored data: both the ZIP structure and its recorded CRC remain intact.
        bytes[payloadOffset + payload.Length / 2] ^= 0x40;
        await File.WriteAllBytesAsync(archive, bytes);

        await Assert.ThrowsAnyAsync<Exception>(() => _service.TestAsync(archive));
        AssertNoTemporaryOutputs();
    }

    [Fact]
    public async Task OrdinaryUnixFileAndDirectoryModesAreNotTreatedAsLinks()
    {
        var archive = Path.Combine(_root, "unix-modes.zip");
        await using (var file = File.Create(archive))
        {
            using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true);
            var directory = zip.CreateEntry("folder/");
            directory.ExternalAttributes = unchecked((int)(0x41EDu << 16)) | 0x10;
            var entry = zip.CreateEntry("folder/ordinary.txt");
            entry.ExternalAttributes = unchecked((int)(0x81A4u << 16));
            await using var content = entry.Open();
            await content.WriteAsync(Encoding.UTF8.GetBytes("ordinary file"));
        }
        var output = Path.Combine(_root, "unpacked");

        await _service.TestAsync(archive);
        await _service.ExtractAsync(archive, output);

        Assert.Equal("ordinary file", await File.ReadAllTextAsync(Path.Combine(output, "folder", "ordinary.txt")));
        AssertNoTemporaryOutputs();
    }

    private void AssertNoTemporaryOutputs() =>
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root, ".eclipsearchive-*"));

    private static async Task WriteZip(string path, params (string Path, string Content)[] entries)
    {
        await using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var (entryPath, text) in entries)
        {
            var entry = zip.CreateEntry(entryPath);
            await using var stream = entry.Open();
            await stream.WriteAsync(Encoding.UTF8.GetBytes(text));
        }
    }

    private static string CreateTemporaryDirectory()
    {
        // /var and /tmp are symlink aliases on macOS; use the real temp-directory path.
        var temporary = Path.GetFullPath(Path.GetTempPath());
        var current = Path.GetPathRoot(temporary)!;
        foreach (var part in temporary[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true) is { } target)
                current = target.FullName;
        }
        var directory = Path.Combine(current, "EclipseArchive-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class ImmediateProgress(Action<ArchiveOperationProgress> report) : IProgress<ArchiveOperationProgress>
    {
        public void Report(ArchiveOperationProgress value) => report(value);
    }
}
