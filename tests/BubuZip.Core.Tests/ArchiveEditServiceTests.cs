using System.Text;
using System.Buffers.Binary;
using BubuZip.Core;
using Xunit;

namespace BubuZip.Core.Tests;

public sealed class ArchiveEditServiceTests : IDisposable
{
    private readonly string _root = CreateTemporaryDirectory();
    private readonly IArchiveEngine _engine = new SharpCompressArchiveEngine();
    private ArchiveFileService Files => new(_engine);
    private ArchiveEditService Editor => new(_engine, Files);

    [Theory]
    [InlineData(ArchiveFormat.Zip)]
    [InlineData(ArchiveFormat.SevenZip)]
    public async Task AddPreservesExistingContentAndMergesFolders(ArchiveFormat format)
    {
        var archive = await CreateArchive(format, ("docs/原有.txt", "original"), ("empty/", null));
        var sourceFolder = Path.Combine(_root, "docs");
        Directory.CreateDirectory(sourceFolder);
        await File.WriteAllTextAsync(Path.Combine(sourceFolder, "新增.txt"), "new 📦");

        await Editor.AddAsync(archive, [sourceFolder]);

        Assert.Equal("original", await ReadEntry(archive, "docs/原有.txt"));
        Assert.Equal("new 📦", await ReadEntry(archive, "docs/新增.txt"));
        Assert.Contains(await List(archive), entry => entry.EntryPath.TrimEnd('/') == "empty" && entry.IsDirectory);
        AssertNoWorkingDirectories();
    }

    [Fact]
    public async Task AddTargetsTheCurrentVirtualDirectoryOrCreatesANewOne()
    {
        var archive = await CreateArchive(ArchiveFormat.Zip, ("docs/original.txt", "old"));
        var source = await WriteFile("added.txt", "added");

        await Editor.AddAsync(archive, [source], archiveDirectory: "docs/new/");

        Assert.Equal("old", await ReadEntry(archive, "docs/original.txt"));
        Assert.Equal("added", await ReadEntry(archive, "docs/new/added.txt"));
        Assert.DoesNotContain(await List(archive), entry => entry.EntryPath == "added.txt");
        AssertNoWorkingDirectories();
    }

    [Fact]
    public async Task AddOnlyReplacesAnExistingFileWhenRequested()
    {
        var archive = await CreateArchive(ArchiveFormat.Zip, ("same.txt", "before"));
        var before = await File.ReadAllBytesAsync(archive);
        var source = await WriteFile("same.txt", "after");

        await Assert.ThrowsAsync<IOException>(() => Editor.AddAsync(archive, [source]));
        Assert.Equal(before, await File.ReadAllBytesAsync(archive));
        AssertNoWorkingDirectories();

        await Editor.AddAsync(archive, [source], replaceExisting: true);
        Assert.Equal("after", await ReadEntry(archive, "same.txt"));
        AssertNoWorkingDirectories();
    }

    [Theory]
    [InlineData(ArchiveFormat.Zip)]
    [InlineData(ArchiveFormat.SevenZip)]
    public async Task DeleteUnderstandsVirtualDirectoriesWithoutMatchingSimilarPrefixes(ArchiveFormat format)
    {
        var archive = await CreateArchive(format,
            ("docs/one.txt", "one"), ("docs/sub/two.txt", "two"), ("docs2/keep.txt", "keep"), ("root.txt", "root"));

        await Editor.DeleteAsync(archive, ["docs/", "docs/one.txt"]);

        var entries = await List(archive);
        Assert.DoesNotContain(entries, entry => entry.EntryPath.StartsWith("docs/", StringComparison.Ordinal));
        Assert.Equal("keep", await ReadEntry(archive, "docs2/keep.txt"));
        Assert.Equal("root", await ReadEntry(archive, "root.txt"));
        AssertNoWorkingDirectories();
    }

    [Theory]
    [InlineData(ArchiveFormat.Zip)]
    [InlineData(ArchiveFormat.SevenZip)]
    public async Task DeletingTheLastEntryCreatesAValidEmptyArchive(ArchiveFormat format)
    {
        var archive = await CreateArchive(format, ("last.txt", "last"));

        await Editor.DeleteAsync(archive, ["last.txt"]);

        Assert.Empty(await List(archive));
        await Files.TestAsync(archive);
        AssertNoWorkingDirectories();
    }

    [Theory]
    [InlineData(ArchiveFormat.Zip)]
    [InlineData(ArchiveFormat.SevenZip)]
    public async Task RenameSupportsFilesVirtualFoldersAndCaseOnlyChanges(ArchiveFormat format)
    {
        var archive = await CreateArchive(format, ("docs/原名.txt", "content"), ("keep.txt", "keep"));

        await Editor.RenameAsync(archive, "docs/原名.txt", "新名.txt");
        await Editor.RenameAsync(archive, "docs/", "资料");
        await Editor.RenameAsync(archive, "keep.txt", "KEEP.txt");

        Assert.Equal("content", await ReadEntry(archive, "资料/新名.txt"));
        Assert.Equal("keep", await ReadEntry(archive, "KEEP.txt"));
        Assert.DoesNotContain(await List(archive), entry => entry.EntryPath.StartsWith("docs/", StringComparison.Ordinal));
        AssertNoWorkingDirectories();
    }

    [Fact]
    public async Task AConflictingRenamePreservesTheOriginalArchive()
    {
        var archive = await CreateArchive(ArchiveFormat.Zip, ("one.txt", "one"), ("two.txt", "two"));
        var before = await File.ReadAllBytesAsync(archive);

        await Assert.ThrowsAsync<IOException>(() => Editor.RenameAsync(archive, "one.txt", "two.txt"));

        Assert.Equal(before, await File.ReadAllBytesAsync(archive));
        AssertNoWorkingDirectories();
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("C:\\drive")]
    public async Task UnsafeMutationPathsPreserveTheOriginalArchive(string path)
    {
        var archive = await CreateArchive(ArchiveFormat.Zip, ("one.txt", "one"));
        var before = await File.ReadAllBytesAsync(archive);
        var source = await WriteFile("added.txt", "added");

        await Assert.ThrowsAsync<InvalidDataException>(() => Editor.DeleteAsync(archive, [path]));
        await Assert.ThrowsAsync<InvalidDataException>(() => Editor.RenameAsync(archive, "one.txt", path));
        await Assert.ThrowsAsync<InvalidDataException>(() => Editor.AddAsync(archive, [source], archiveDirectory: path));

        Assert.Equal(before, await File.ReadAllBytesAsync(archive));
        AssertNoWorkingDirectories();
    }

    [Fact]
    public async Task CancellingAnEditLeavesTheOriginalBytesAndRemovesExtractedContent()
    {
        var archive = await CreateArchive(ArchiveFormat.SevenZip, ("one.txt", "one"), ("two.txt", "two"));
        var source = await WriteFile("added.txt", "added");
        var before = await File.ReadAllBytesAsync(archive);
        using var cancellation = new CancellationTokenSource();
        var reports = new ImmediateProgress(report =>
        {
            if (report.CompletedEntries > 0) cancellation.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Editor.AddAsync(archive, [source], progress: reports, cancellationToken: cancellation.Token));

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(before, await File.ReadAllBytesAsync(archive));
        AssertNoWorkingDirectories();
    }

    [Fact]
    public async Task ExternalChangesDuringRebuildAreNotOverwritten()
    {
        var archive = await CreateArchive(ArchiveFormat.Zip, ("original.txt", "original"));
        var replacementArchive = await CreateArchive(ArchiveFormat.Zip, ("external.txt", "external"));
        var external = await File.ReadAllBytesAsync(replacementArchive);
        var addition = await WriteFile("added.txt", "added");
        var changed = false;
        var progress = new ImmediateProgress(_ =>
        {
            if (changed || !Directory.EnumerateFiles(_root, "updated.archive", SearchOption.AllDirectories).Any()) return;
            File.WriteAllBytes(archive, external);
            changed = true;
        });

        await Assert.ThrowsAsync<IOException>(() => Editor.AddAsync(archive, [addition], progress: progress));

        Assert.True(changed);
        Assert.Equal(external, await File.ReadAllBytesAsync(archive));
        Assert.Equal("external", await ReadEntry(archive, "external.txt"));
        AssertNoWorkingDirectories();
    }

    [Fact]
    public async Task EditingEncryptedSevenZipKeepsPasswordAndHidesTheHeader()
    {
        const string password = "archive-password-测试";
        var archive = await CreateArchive(ArchiveFormat.SevenZip, password, false,
            ("one.txt", "one"), ("two.txt", "two"));

        await Editor.RenameAsync(archive, "one.txt", "renamed.txt", password);
        await Editor.DeleteAsync(archive, ["two.txt"], password);

        await Assert.ThrowsAnyAsync<Exception>(() => List(archive));
        Assert.Equal("one", await ReadEntry(archive, "renamed.txt", password));
        var metadata = await List(archive, password);
        Assert.True(Assert.Single(metadata, entry => !entry.IsDirectory).IsEncrypted);
        AssertNoWorkingDirectories();
    }

    [Fact]
    public async Task MissingOrWrongPasswordDoesNotModifyAnEncryptedArchive()
    {
        const string password = "correct-password";
        var archive = await CreateArchive(ArchiveFormat.SevenZip, password, false, ("one.txt", "one"));
        var before = await File.ReadAllBytesAsync(archive);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Editor.DeleteAsync(archive, ["one.txt"]));
        await Assert.ThrowsAnyAsync<Exception>(() => Editor.DeleteAsync(archive, ["one.txt"], "wrong-password"));

        Assert.Equal(before, await File.ReadAllBytesAsync(archive));
        AssertNoWorkingDirectories();
    }

    [Fact]
    public async Task UnsupportedArchiveSignaturesAndSourceLinksLeaveTheOriginalUntouched()
    {
        var unsupported = await WriteFile("other.rar", "not an editable archive");
        var unsupportedBytes = await File.ReadAllBytesAsync(unsupported);
        await Assert.ThrowsAsync<NotSupportedException>(() => Editor.DeleteAsync(unsupported, ["one.txt"]));
        Assert.Equal(unsupportedBytes, await File.ReadAllBytesAsync(unsupported));

        var archive = await CreateArchive(ArchiveFormat.Zip, ("one.txt", "one"));
        var before = await File.ReadAllBytesAsync(archive);
        var source = await WriteFile("source.txt", "source");
        var link = Path.Combine(_root, "alias.txt");
        try { File.CreateSymbolicLink(link, source); }
        catch (IOException error) when (OperatingSystem.IsWindows() && error.HResult == unchecked((int)0x80070522))
        {
            // Windows can require a privilege that is not granted to ordinary test users.
            AssertNoWorkingDirectories();
            return;
        }
        await Assert.ThrowsAsync<IOException>(() => Editor.AddAsync(archive, [link]));
        Assert.Equal(before, await File.ReadAllBytesAsync(archive));
        AssertNoWorkingDirectories();
    }

    [Fact]
    public async Task SourceFolderContainingTheArchiveIsRejectedWithoutRecursiveSelfInclusion()
    {
        var archive = await CreateArchive(ArchiveFormat.Zip, ("one.txt", "one"));
        var before = await File.ReadAllBytesAsync(archive);

        await Assert.ThrowsAsync<IOException>(() => Editor.AddAsync(archive, [_root]));

        Assert.Equal(before, await File.ReadAllBytesAsync(archive));
        AssertNoWorkingDirectories();
    }

    [Fact]
    public async Task OrdinaryUnix0644FilesAnd0755DirectoriesRemainEditable()
    {
        var archive = await CreateMetadataZip(fileMode: 0x81A4, directoryMode: 0x41ED);

        await Editor.RenameAsync(archive, "folder/file.txt", "renamed.txt");

        Assert.Equal("metadata fixture", await ReadEntry(archive, "folder/renamed.txt"));
        AssertNoWorkingDirectories();
    }

    [Theory]
    [InlineData(0x81ED, 0x41ED)] // executable regular file: 0755
    [InlineData(0x8180, 0x41ED)] // private regular file: 0600
    [InlineData(0x81A4, 0x41C0)] // private directory: 0700
    [InlineData(0x89ED, 0x41ED)] // setuid + executable regular file
    public async Task ZipEditingPreservesUnixPermissionsAndTheirCreatorSystem(int fileMode, int directoryMode)
    {
        var archive = await CreateMetadataZip(fileMode, directoryMode);
        var before = ReadZipMetadata(archive);

        await Editor.RenameAsync(archive, "folder/file.txt", "renamed.txt");

        var after = ReadZipMetadata(archive);
        Assert.Equal(before["folder/file.txt"], after["folder/renamed.txt"]);
        Assert.Equal(before["folder/"], after["folder/"]);
        Assert.Equal((byte)3, after["folder/renamed.txt"].Creator);
        Assert.Equal("metadata fixture", await ReadEntry(archive, "folder/renamed.txt"));
        AssertNoWorkingDirectories();
    }

    [Theory]
    [InlineData(0x1)]
    [InlineData(0x2)]
    [InlineData(0x4)]
    public async Task ZipEditingPreservesSignificantDosAttributes(int dosAttributes)
    {
        var archive = await CreateMetadataZip(0x81A4, 0x41ED, dosAttributes);
        var before = ReadZipMetadata(archive);

        await Editor.RenameAsync(archive, "folder/file.txt", "renamed.txt");

        var after = ReadZipMetadata(archive);
        Assert.Equal(before["folder/file.txt"], after["folder/renamed.txt"]);
        Assert.Equal("metadata fixture", await ReadEntry(archive, "folder/renamed.txt"));
        AssertNoWorkingDirectories();
    }

    [Fact]
    public async Task SharpCompressDosCreatorSentinelDoesNotBecomeUnixReadOnlyPermissions()
    {
        var archive = await CreateArchive(ArchiveFormat.Zip, ("before.txt", "content"));
        var before = ReadZipMetadata(archive)["before.txt"];
        Assert.Equal((byte)0, before.Creator);
        Assert.Equal(0x81000000u, unchecked((uint)before.Attributes));

        await Editor.RenameAsync(archive, "before.txt", "after.txt");

        Assert.Equal(before, ReadZipMetadata(archive)["after.txt"]);
        AssertNoWorkingDirectories();
    }

    [Theory]
    [InlineData(0x81ED, 0x41ED)]
    [InlineData(0x8180, 0x41ED)]
    [InlineData(0x81A4, 0x41C0)]
    [InlineData(0x89ED, 0x41ED)]
    public async Task SevenZipRejectsPermissionMetadataTheWriterCannotPreserve(int fileMode, int directoryMode)
    {
        var archive = await CreateArchive(ArchiveFormat.SevenZip, ("folder/", null), ("folder/file.txt", "content"));
        var before = await File.ReadAllBytesAsync(archive);
        var engine = new AttributeMetadataEngine(_engine, fileMode, directoryMode);
        var editor = new ArchiveEditService(engine, new ArchiveFileService(engine));

        var error = await Assert.ThrowsAsync<NotSupportedException>(() => editor.RenameAsync(archive, "folder/file.txt", "renamed.txt"));

        Assert.Contains("Unix 文件权限", error.Message);
        Assert.Equal(before, await File.ReadAllBytesAsync(archive));
        AssertNoWorkingDirectories();
    }

    [Fact]
    public async Task ZipAdditionKeepsTheExecutablePermissionsOfTheNewSourceFile()
    {
        if (OperatingSystem.IsWindows()) return;
        var archive = await CreateArchive(ArchiveFormat.Zip, ("original.txt", "original"));
        var source = await WriteFile("script.sh", "#!/bin/sh\necho example\n");
        File.SetUnixFileMode(source, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        await Editor.AddAsync(archive, [source]);

        var metadata = ReadZipMetadata(archive)["script.sh"];
        Assert.Equal((byte)3, metadata.Creator);
        Assert.Equal(0x81EDu, unchecked((uint)metadata.Attributes) >> 16);
        AssertNoWorkingDirectories();
    }

    [Theory]
    [InlineData("需要保留的归档注释", "")]
    [InlineData("", "需要保留的文件注释")]
    public async Task ZipCommentsBlockEditingWithoutChangingBytes(string archiveComment, string entryComment)
    {
        var archive = await CreateMetadataZip(0x81A4, 0x41ED, archiveComment: archiveComment, entryComment: entryComment);
        var before = await File.ReadAllBytesAsync(archive);

        var error = await Assert.ThrowsAsync<NotSupportedException>(() => Editor.RenameAsync(archive, "folder/file.txt", "renamed.txt"));

        Assert.Contains("注释", error.Message);
        Assert.Equal(before, await File.ReadAllBytesAsync(archive));
        AssertNoWorkingDirectories();
    }

    private async Task<string> CreateMetadataZip(int fileMode, int directoryMode, int dosAttributes = 0,
        string archiveComment = "", string entryComment = "")
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        await using var output = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using (var archive = new System.IO.Compression.ZipArchive(output, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            archive.Comment = archiveComment;
            var directory = archive.CreateEntry("folder/");
            directory.ExternalAttributes = unchecked((int)((uint)directoryMode << 16)) | 0x10;
            var entry = archive.CreateEntry("folder/file.txt");
            entry.ExternalAttributes = unchecked((int)((uint)fileMode << 16)) | dosAttributes;
            entry.Comment = entryComment;
            await using var content = entry.Open();
            await content.WriteAsync(Encoding.UTF8.GetBytes("metadata fixture"));
        }
        // ZipArchive records the host OS as creator; force Unix so this fixture tests
        // preservation of Unix creator metadata on every host, including Windows.
        await output.FlushAsync();
        output.Position = 0;
        var bytes = new byte[checked((int)output.Length)];
        await output.ReadExactlyAsync(bytes);
        var eocd = bytes.Length - 22 - Encoding.UTF8.GetByteCount(archiveComment);
        var position = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(eocd + 16)));
        for (var index = 0; index < 2; index++)
        {
            Assert.Equal(0x02014B50u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position)));
            bytes[position + 5] = 3;
            position += 46 + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(position + 28)) +
                BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(position + 30)) +
                BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(position + 32));
        }
        output.Position = 0;
        await output.WriteAsync(bytes);
        return path;
    }

    private static Dictionary<string, (int Attributes, byte Creator)> ReadZipMetadata(string path)
    {
        using var input = File.OpenRead(path);
        using var archive = new System.IO.Compression.ZipArchive(input, System.IO.Compression.ZipArchiveMode.Read);
        var bytes = File.ReadAllBytes(path);
        // These deliberately small, comment-free fixtures use the ordinary EOCD record.
        var position = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - 22 + 16)));
        var result = new Dictionary<string, (int Attributes, byte Creator)>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            Assert.Equal(0x02014B50u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position)));
            result.Add(entry.FullName, (entry.ExternalAttributes, bytes[position + 5]));
            position += 46 + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(position + 28)) +
                BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(position + 30)) +
                BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(position + 32));
        }
        return result;
    }

    private Task<string> CreateArchive(ArchiveFormat format, params (string Name, string? Content)[] entries) =>
        CreateArchive(format, null, false, entries);

    private async Task<string> CreateArchive(ArchiveFormat format, string? password, bool encryptNames,
        params (string Name, string? Content)[] entries)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + (format == ArchiveFormat.Zip ? ".zip" : ".7z"));
        var inputs = entries.Select(entry => new ArchiveWriteEntry(entry.Name,
            entry.Content is null ? null : new MemoryStream(Encoding.UTF8.GetBytes(entry.Content)))).ToArray();
        try
        {
            await using var output = File.Create(path);
            await _engine.CreateAsync(output, inputs, format, password, encryptNames);
        }
        finally { foreach (var entry in inputs) entry.Content?.Dispose(); }
        return path;
    }

    private async Task<IReadOnlyList<ArchiveEntryInfo>> List(string archive, string? password = null)
    {
        await using var source = File.OpenRead(archive);
        return await _engine.ListAsync(source, password);
    }

    private async Task<string> ReadEntry(string archive, string path, string? password = null)
    {
        await using var source = File.OpenRead(archive);
        using var output = new MemoryStream();
        await _engine.ExtractEntryAsync(source, path, output, password);
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private async Task<string> WriteFile(string name, string text)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, text);
        return path;
    }

    private void AssertNoWorkingDirectories() => Assert.Empty(Directory.EnumerateFileSystemEntries(_root, ".bubuzip-*"));

    private static string CreateTemporaryDirectory()
    {
        var temporary = Path.GetFullPath(Path.GetTempPath());
        var current = Path.GetPathRoot(temporary)!;
        foreach (var part in temporary[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (new DirectoryInfo(current).ResolveLinkTarget(true) is { } target) current = target.FullName;
        }
        var root = Path.Combine(current, "BubuZip-edit-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    private sealed class ImmediateProgress(Action<ArchiveOperationProgress> report) : IProgress<ArchiveOperationProgress>
    {
        public void Report(ArchiveOperationProgress value) => report(value);
    }

    private sealed class AttributeMetadataEngine(IArchiveEngine inner, int fileMode, int directoryMode) : IArchiveEngine
    {
        public Task CreateAsync(Stream destination, IReadOnlyList<ArchiveWriteEntry> entries, ArchiveFormat format = ArchiveFormat.Zip,
            string? password = null, bool encryptFileNames = false, CancellationToken cancellationToken = default) =>
            inner.CreateAsync(destination, entries, format, password, encryptFileNames, cancellationToken);

        public async Task<IReadOnlyList<ArchiveEntryInfo>> ListAsync(Stream source, string? password = null, CancellationToken cancellationToken = default) =>
            (await inner.ListAsync(source, password, cancellationToken)).Select(entry => entry with
            {
                Attributes = unchecked((int)((uint)(entry.IsDirectory ? directoryMode : fileMode) << 16)) | 0x8000 |
                    (entry.IsDirectory ? 0x10 : 0),
            }).ToArray();

        public Task ExtractEntryAsync(Stream source, string entryPath, Stream destination, string? password = null,
            CancellationToken cancellationToken = default) => inner.ExtractEntryAsync(source, entryPath, destination, password, cancellationToken);
    }
}
