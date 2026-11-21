using System.IO.Compression;
using System.Text;
using BubuZip.Core;
using ICSharpCode.SharpZipLib.Zip;
using Xunit;

namespace BubuZip.Core.Tests;

public sealed class ArchiveCapabilitiesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "BubuZip-capabilities-" + Guid.NewGuid().ToString("N"));
    private readonly SharpCompressArchiveEngine engine = new();
    public ArchiveCapabilitiesTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);

    [Theory]
    [InlineData(ZipEncryptionMethod.Aes256)]
    [InlineData(ZipEncryptionMethod.ZipCrypto)]
    public async Task EncryptedZipWorksWithBothReadersAndIndependentDecoder(ZipEncryptionMethod encryption)
    {
        var password = encryption == ZipEncryptionMethod.Aes256 ? "测试密码-abc" : "ascii-password";
        var content = Encoding.UTF8.GetBytes("中文内容 📦");
        using var input = new MemoryStream(content);
        using var empty = new MemoryStream();
        using var archive = new MemoryStream();
        await engine.CreateWithSettingsAsync(archive,
            [new("资料/中文.txt", input), new("资料/空.txt", empty), new("空目录", null)],
            ArchiveFormat.Zip, password, false, new(ZipEncryption: encryption));
        Assert.True(archive.CanWrite);
        archive.Position = 0;
        var entries = await engine.ListAsync(archive);
        Assert.True(Assert.Single(entries, e => e.EntryPath == "资料/中文.txt").IsEncrypted);
        Assert.True(Assert.Single(entries, e => e.EntryPath == "资料/空.txt").IsEncrypted);
        Assert.True(Assert.Single(entries, e => e.IsDirectory).IsDirectory);
        archive.Position = 0;
        using var single = new MemoryStream();
        await engine.ExtractEntryAsync(archive, "资料/中文.txt", single, password);
        Assert.Equal(content, single.ToArray());
        archive.Position = 0;
        byte[]? batch = null;
        await engine.ExtractEntriesAsync(archive, [new("资料/中文.txt", () => new CallbackMemoryStream(bytes => batch = bytes))], password);
        Assert.Equal(content, batch);
        archive.Position = 0;
        await Assert.ThrowsAnyAsync<Exception>(() => engine.ExtractEntryAsync(archive, "资料/中文.txt", Stream.Null, "wrong"));
        archive.Position = 0;
        using var zip = new ICSharpCode.SharpZipLib.Zip.ZipFile(archive) { IsStreamOwner = false, Password = password };
        using var decoded = zip.GetInputStream(zip.GetEntry("资料/中文.txt"));
        using var independent = new MemoryStream();
        await decoded.CopyToAsync(independent);
        Assert.Equal(content, independent.ToArray());
    }

    [Theory]
    [InlineData(ArchiveFormat.Zip)]
    [InlineData(ArchiveFormat.SevenZip)]
    public async Task CompressionLevelsRoundTrip(ArchiveFormat format)
    {
        var data = Encoding.UTF8.GetBytes(new string('x', 100_000));
        foreach (var level in new[] { ArchiveCompressionLevel.Fastest, ArchiveCompressionLevel.Normal, ArchiveCompressionLevel.Ultra })
        {
            using var input = new MemoryStream(data);
            using var archive = new MemoryStream();
            await engine.CreateWithSettingsAsync(archive, [new("file.txt", input)], format, null, false, new(level));
            archive.Position = 0;
            using var output = new MemoryStream();
            await engine.ExtractEntryAsync(archive, "file.txt", output);
            Assert.Equal(data, output.ToArray());
        }
    }

    [Fact]
    public async Task ZipStoreReallyStoresUncompressedData()
    {
        var data = Encoding.UTF8.GetBytes(new string('x', 50_000));
        using var input = new MemoryStream(data);
        using var archive = new MemoryStream();
        await engine.CreateWithSettingsAsync(archive, [new("file.txt", input)], ArchiveFormat.Zip, null, false, new(ArchiveCompressionLevel.Store));
        archive.Position = 0;
        using var zip = new ZipArchive(archive, ZipArchiveMode.Read, true);
        var entry = Assert.Single(zip.Entries);
        Assert.Equal(data.Length, entry.CompressedLength);

    }

    [Theory]
    [InlineData(ZipEncryptionMethod.Aes256)]
    [InlineData(ZipEncryptionMethod.ZipCrypto)]
    public async Task EncryptedStoreUsesStoredCompression(ZipEncryptionMethod encryption)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 10000)));
        using var archive = new MemoryStream();
        await engine.CreateWithSettingsAsync(archive, [new("file.txt", input)], ArchiveFormat.Zip,
            "password", false, new(ArchiveCompressionLevel.Store, encryption));
        archive.Position = 0;
        using var zip = new ICSharpCode.SharpZipLib.Zip.ZipFile(archive) { IsStreamOwner = false, Password = "password" };
        Assert.Equal(CompressionMethod.Stored, zip.GetEntry("file.txt").CompressionMethod);
        using var content = zip.GetInputStream(zip.GetEntry("file.txt"));
        using var output = new MemoryStream();
        await content.CopyToAsync(output);
        Assert.Equal(10000, output.Length);
    }

    [Fact]
    public async Task ZipCryptoRejectsNonAsciiPasswordsBeforeWriting()
    {
        using var input = new MemoryStream([1, 2, 3]);
        using var archive = new MemoryStream();
        await Assert.ThrowsAsync<ArgumentException>(() => engine.CreateWithSettingsAsync(archive, [new("file", input)],
            ArchiveFormat.Zip, "中文", false, new(ZipEncryption: ZipEncryptionMethod.ZipCrypto)));
        Assert.Equal(0, archive.Length);
    }

    [Theory]
    [InlineData(ExtractionPathMode.FullPaths, "资料/子目录/one.txt")]
    [InlineData(ExtractionPathMode.CurrentFolder, "子目录/one.txt")]
    [InlineData(ExtractionPathMode.NoPaths, "one.txt")]
    public async Task ExtractionPathModesMapSelectedEntries(ExtractionPathMode mode, string expected)
    {
        var path = Path.Combine(root, "input.zip");
        using (var zip = ZipFileCreate(path))
        {
            using var writer = new StreamWriter(zip.CreateEntry("资料/子目录/one.txt").Open());
            await writer.WriteAsync("test");
        }
        var output = Path.Combine(root, "output");
        await new ArchiveFileService(engine).ExtractAsync(path, output, entryPaths: ["资料"], settings: new(mode, "资料"));
        Assert.Equal("test", await File.ReadAllTextAsync(Path.Combine(output, expected.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Fact]
    public async Task FlatteningCollisionsAreRejectedWithoutCreatingOutput()
    {
        var path = Path.Combine(root, "input.zip");
        using (var zip = ZipFileCreate(path)) { zip.CreateEntry("a/same.txt"); zip.CreateEntry("b/same.txt"); }
        var output = Path.Combine(root, "output");
        await Assert.ThrowsAnyAsync<Exception>(() => new ArchiveFileService(engine).ExtractAsync(path, output, settings: new(ExtractionPathMode.NoPaths)));
        Assert.False(Directory.Exists(output));
        Assert.Single(Directory.GetFiles(root));
    }

    private static ZipArchive ZipFileCreate(string path) => System.IO.Compression.ZipFile.Open(path, ZipArchiveMode.Create);
    private sealed class CallbackMemoryStream(Action<byte[]> callback) : MemoryStream
    {
        protected override void Dispose(bool disposing) { if (disposing) callback(ToArray()); base.Dispose(disposing); }
        public override ValueTask DisposeAsync() { callback(ToArray()); return base.DisposeAsync(); }
    }
}
