using Xunit;

namespace BubuZip.Core.Tests;

public sealed class ArchivePathsTests
{
    [Fact]
    public void NullPathHasAClearArgumentError()
    {
        var error = Assert.Throws<ArgumentNullException>(() => ArchivePaths.ResolveUserPath(null!));
        Assert.Equal("path", error.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\r\n")]
    public void EmptyPathHasAClearArgumentError(string path)
    {
        var error = Assert.Throws<ArgumentException>(() => ArchivePaths.ResolveUserPath(path));
        Assert.Equal("path", error.ParamName);
    }

    [Theory]
    [InlineData("/tmp", "/private/tmp")]
    [InlineData("/var", "/private/var")]
    [InlineData("/etc", "/private/etc")]
    public void OnlyConfirmedMacSystemAliasesMapToTheirPrivateDirectories(string alias, string target)
    {
        var input = alias + "/BubuZip-path-probe/not-created.txt";
        if (OperatingSystem.IsMacOS() && IsConfirmedAlias(alias, target))
        {
            Assert.Equal(target, ArchivePaths.ResolveUserPath(alias));
            Assert.Equal(target + "/BubuZip-path-probe/not-created.txt", ArchivePaths.ResolveUserPath(input));
        }
        else
        {
            Assert.Equal(Path.GetFullPath(alias), ArchivePaths.ResolveUserPath(alias));
            Assert.Equal(Path.GetFullPath(input), ArchivePaths.ResolveUserPath(input));
        }
    }

    [Theory]
    [InlineData("/tmp-data/archive.zip")]
    [InlineData("/tmpx/archive.zip")]
    [InlineData("/various/archive.zip")]
    [InlineData("/var-data/archive.zip")]
    [InlineData("/etcetera/config")]
    [InlineData("/etc-user/config")]
    [InlineData("/private/tmp/archive.zip")]
    public void SystemAliasMatchingRespectsCompletePathSegments(string path) =>
        Assert.Equal(Path.GetFullPath(path), ArchivePaths.ResolveUserPath(path));

    [Fact]
    public void DotSegmentsAreNormalizedBeforeMatchingASystemAlias()
    {
        const string path = "/tmp/unused/../archive.zip";
        var expected = OperatingSystem.IsMacOS() && IsConfirmedAlias("/tmp", "/private/tmp")
            ? "/private/tmp/archive.zip"
            : Path.GetFullPath(path);
        Assert.Equal(expected, ArchivePaths.ResolveUserPath(path));
    }

    [Fact]
    public void RelativePathsBecomeFullPaths()
    {
        var input = Path.Combine("relative-folder", "..", "archive.zip");
        var fullPath = Path.GetFullPath(input);
        var resolved = ArchivePaths.ResolveUserPath(input);
        Assert.True(Path.IsPathFullyQualified(resolved));
        Assert.Equal(ArchivePaths.ResolveUserPath(fullPath), resolved);
    }

    [Fact]
    public void AnOrdinaryUserSymlinkIsNotResolvedOrHiddenBySystemAliasNormalization()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = CreateTemporaryDirectory();
        try
        {
            var real = Path.Combine(root, "actual");
            var link = Path.Combine(root, "user-link");
            Directory.CreateDirectory(real);
            File.WriteAllText(Path.Combine(real, "content.txt"), "User links must remain visible to the caller's link checks.");
            Directory.CreateSymbolicLink(link, real);
            var throughUserLink = Path.Combine(link, "content.txt");

            Assert.Equal(Path.GetFullPath(throughUserLink), ArchivePaths.ResolveUserPath(throughUserLink));
            Assert.NotEqual(Path.Combine(real, "content.txt"), ArchivePaths.ResolveUserPath(throughUserLink));
            Assert.NotNull(new DirectoryInfo(link).LinkTarget);

            if (OperatingSystem.IsMacOS() && root.StartsWith("/private/tmp/", StringComparison.Ordinal) &&
                IsConfirmedAlias("/tmp", "/private/tmp"))
            {
                var throughBothAliases = "/tmp/" + root["/private/tmp/".Length..] + "/user-link/content.txt";
                Assert.Equal(throughUserLink, ArchivePaths.ResolveUserPath(throughBothAliases));
                Assert.Contains("/user-link/", ArchivePaths.ResolveUserPath(throughBothAliases));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task FileServiceCanCreateAndExtractThroughTheConfirmedMacTmpAlias()
    {
        if (!OperatingSystem.IsMacOS() || !IsConfirmedAlias("/tmp", "/private/tmp")) return;
        var physicalRoot = CreateTemporaryDirectory();
        var aliasRoot = "/tmp/" + physicalRoot["/private/tmp/".Length..];
        try
        {
            var source = Path.Combine(aliasRoot, "中文原文.txt");
            var archive = Path.Combine(aliasRoot, "archive.zip");
            var extracted = Path.Combine(aliasRoot, "unpacked");
            const string text = "通过 macOS 系统别名正常处理文件 📦";
            await File.WriteAllTextAsync(source, text);
            var service = new ArchiveFileService(new SharpCompressArchiveEngine());

            await service.CreateAsync([source], archive, ArchiveFormat.Zip);
            await service.ExtractAsync(archive, extracted);

            Assert.Equal(text, await File.ReadAllTextAsync(Path.Combine(physicalRoot, "unpacked", "中文原文.txt")));
            Assert.Empty(Directory.EnumerateFileSystemEntries(physicalRoot, ".bubuzip-*"));
        }
        finally { Directory.Delete(physicalRoot, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedAliasSpellingsStillRejectAnArchiveDestinationInsideItsSource(bool aliasSource)
    {
        if (!OperatingSystem.IsMacOS() || !IsConfirmedAlias("/tmp", "/private/tmp")) return;
        var root = CreateTemporaryDirectory();
        var aliasRoot = "/tmp/" + root["/private/tmp/".Length..];
        try
        {
            var physicalSource = Path.Combine(root, "source");
            Directory.CreateDirectory(physicalSource);
            var sourceFile = Path.Combine(physicalSource, "keep.txt");
            await File.WriteAllTextAsync(sourceFile, "Existing source bytes must remain unchanged.");
            var originalArchive = Path.Combine(physicalSource, "keep.zip");
            await WriteZipAsync(originalArchive);
            var originalBytes = await File.ReadAllBytesAsync(originalArchive);
            var source = Path.Combine(aliasSource ? aliasRoot : root, "source");
            var destination = Path.Combine(aliasSource ? root : aliasRoot, "source", "self.zip");
            var service = new ArchiveFileService(new SharpCompressArchiveEngine());

            await Assert.ThrowsAsync<IOException>(() => service.CreateAsync([source], destination, ArchiveFormat.Zip));

            Assert.False(File.Exists(Path.Combine(physicalSource, "self.zip")));
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(originalArchive));
            Assert.Equal("Existing source bytes must remain unchanged.", await File.ReadAllTextAsync(sourceFile));
            Assert.Empty(Directory.EnumerateFileSystemEntries(root, ".bubuzip-*", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedAliasSpellingsStillRejectAddingAnArchivesParentToItself(bool aliasArchive)
    {
        if (!OperatingSystem.IsMacOS() || !IsConfirmedAlias("/tmp", "/private/tmp")) return;
        var root = CreateTemporaryDirectory();
        var aliasRoot = "/tmp/" + root["/private/tmp/".Length..];
        try
        {
            var physicalArchive = Path.Combine(root, "keep.zip");
            await WriteZipAsync(physicalArchive);
            var originalBytes = await File.ReadAllBytesAsync(physicalArchive);
            var engine = new SharpCompressArchiveEngine();
            var editor = new ArchiveEditService(engine, new ArchiveFileService(engine));
            var archive = Path.Combine(aliasArchive ? aliasRoot : root, "keep.zip");
            var parent = aliasArchive ? root : aliasRoot;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            await Assert.ThrowsAsync<IOException>(() => editor.AddAsync(archive, [parent], cancellationToken: timeout.Token));

            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(physicalArchive));
            Assert.Empty(Directory.EnumerateFileSystemEntries(root, ".bubuzip-*"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task WriteZipAsync(string path)
    {
        await using var output = File.Create(path);
        using var content = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("The original archive must remain byte-for-byte intact."));
        await new SharpCompressArchiveEngine().CreateAsync(output, [new ArchiveWriteEntry("keep.txt", content)]);
    }

    private static bool IsConfirmedAlias(string alias, string target)
    {
        var info = new DirectoryInfo(alias);
        return info.LinkTarget is not null &&
            info.ResolveLinkTarget(returnFinalTarget: true) is { } actual &&
            string.Equals(Path.GetFullPath(actual.FullName), target, StringComparison.Ordinal);
    }

    private static string CreateTemporaryDirectory()
    {
        var parent = OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath();
        var root = Path.Combine(parent, "BubuZip.Paths.Tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
