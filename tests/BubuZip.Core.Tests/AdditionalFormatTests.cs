using System.IO.Compression;
using System.Text;
using BubuZip.Core;
using Xunit;

namespace BubuZip.Core.Tests;

public sealed class AdditionalFormatTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "BubuZip-formats-" + Guid.NewGuid().ToString("N"));
    private readonly SharpCompressArchiveEngine engine = new();
    public AdditionalFormatTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);

    [Theory]
    [InlineData(ArchiveFormat.Tar, ".tar")]
    [InlineData(ArchiveFormat.TarGZip, ".tar.gz")]
    [InlineData(ArchiveFormat.TarBZip2, ".tar.bz2")]
    [InlineData(ArchiveFormat.TarLZip, ".tar.lz")]
    [InlineData(ArchiveFormat.TarZStandard, ".tar.zst")]
    public async Task TarFormatsRoundTripFoldersUnicodeAndEmptyDirectories(ArchiveFormat format, string extension)
    {
        var directory = Path.Combine(root, "资料");
        Directory.CreateDirectory(Path.Combine(directory, "空目录"));
        await File.WriteAllTextAsync(Path.Combine(directory, "文字.txt"), "中文内容 📦");
        var archive = Path.Combine(root, "bundle" + extension);
        var output = Path.Combine(root, "output");
        var service = new ArchiveFileService(engine);
        await service.CreateAsync([directory], archive, format);
        await service.TestAsync(archive);
        await service.ExtractAsync(archive, output);
        Assert.Equal("中文内容 📦", await File.ReadAllTextAsync(Path.Combine(output, "资料", "文字.txt")));
        Assert.True(Directory.Exists(Path.Combine(output, "资料", "空目录")));
    }

    [Theory]
    [InlineData(ArchiveFormat.GZip, ".gz")]
    [InlineData(ArchiveFormat.BZip2, ".bz2")]
    [InlineData(ArchiveFormat.LZip, ".lz")]
    [InlineData(ArchiveFormat.ZStandard, ".zst")]
    public async Task SingleFileFormatsRoundTrip(ArchiveFormat format, string extension)
    {
        var original = Path.Combine(root, "sample.txt");
        await File.WriteAllTextAsync(original, "single file content");
        var path = Path.Combine(root, "sample.txt" + extension);
        var output = Path.Combine(root, "output");
        var service = new ArchiveFileService(engine);
        await service.CreateAsync([original], path, format);
        await service.TestAsync(path);
        await service.ExtractAsync(path, output);
        Assert.Equal("single file content", await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(output))));
    }

    [Theory]
    [InlineData(ArchiveFormat.GZip)]
    [InlineData(ArchiveFormat.BZip2)]
    [InlineData(ArchiveFormat.LZip)]
    [InlineData(ArchiveFormat.ZStandard)]
    public async Task SingleFileFormatsRejectMultipleFilesWithoutCommitting(ArchiveFormat format)
    {
        var a = Path.Combine(root, "a"); var b = Path.Combine(root, "b");
        await File.WriteAllTextAsync(a, "a"); await File.WriteAllTextAsync(b, "b");
        var output = Path.Combine(root, "output");
        await Assert.ThrowsAsync<ArgumentException>(() => new ArchiveFileService(engine).CreateAsync([a, b], output, format));
        Assert.False(File.Exists(output));
    }

    [Theory]
    [InlineData("external.tar.gz", "H4sICM02/2oC/3NhbXBsZS50YXIA7c0xCsJAFATQPYon0JVEPY/gisVCIPmCx/djJfYKkveaGaaZ69Qvbd5FW2IbjyjfUNNxHF+ZPjMd3nru+3oahrKp5QfuS5znvC/rdGu9TwUAAAAAAAAAAIB/8wRtkYj7ACgAAA==")]
    [InlineData("external.tar.bz2", "QlpoOTFBWSZTWUci5yoAAHB7gMmAAAJAAe+AIABnRJ5ACAggAFQ0o9Q0NNGjIZpPKCSieo0aNAAGh95EehB0yEIl3hWY22QQIcDiMJMJnCNHkOTfLaUXZXB6JQ5tNHC1jCswVWhEiID8XckU4UJBHIucqA==")]
    [InlineData("external.tar.xz", "/Td6WFoAAATm1rRGAgAhARYAAAB0L+Wj4Cf/AGddADMbyc2KqWBC3VDqX/k/JQCSij3M88/OQnuLyBImoV3Hdm9tdCl0PfqsMPSvItfhHLdmCNAaWKS2ng1ybetxBmfDcbYXlR4k0yymwHf4y1MqEVUsBOppi9hl6q0e5xaAry4ubWpgYwAAACAI/b6h5yjrAAGDAYBQAACQA/TnscRn+wIAAAAABFla")]
    public async Task ExternalCompressedTarCanBeListedTestedAndExtracted(string name, string base64)
    {
        var path = Path.Combine(root, name);
        await File.WriteAllBytesAsync(path, Convert.FromBase64String(base64));
        var output = Path.Combine(root, "out");
        var service = new ArchiveFileService(engine);
        await service.TestAsync(path);
        await service.ExtractAsync(path, output);
        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(output, "folder", "test.txt")));
        await using var source = File.OpenRead(path);
        using var extracted = new MemoryStream();
        await engine.ExtractEntryAsync(source, "folder/test.txt", extracted);
        Assert.Equal("hello", Encoding.UTF8.GetString(extracted.ToArray()));
    }

    [Theory]
    [InlineData("external.bz2", "QlpoOTFBWSZTWRkxZT0AAACBAAJEoAAhmmgzTQczi7kinChIDJiynoA=")]
    [InlineData("external.xz", "/Td6WFoAAATm1rRGAgAhARYAAAB0L+WjAQAEaGVsbG8AAAAAsTe52+XaHpsAAR0FuC2Arx+2830BAAAAAARZWg==")]
    public async Task ExternalSingleStreamsCanBeExtracted(string name, string base64)
    {
        var path = Path.Combine(root, name);
        await File.WriteAllBytesAsync(path, Convert.FromBase64String(base64));
        var output = Path.Combine(root, "out");
        await new ArchiveFileService(engine).ExtractAsync(path, output);
        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(output, "external")));
    }

    [Fact]
    public async Task TarGzipIsReadableWithDotNetTarReader()
    {
        var path = Path.Combine(root, "source.txt");
        await File.WriteAllTextAsync(path, "independent");
        var archive = Path.Combine(root, "output.tar.gz");
        await new ArchiveFileService(engine).CreateAsync([path], archive, ArchiveFormat.TarGZip);
        using var file = File.OpenRead(archive);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new System.Formats.Tar.TarReader(gzip);
        var entry = tar.GetNextEntry();
        Assert.Equal("source.txt", entry!.Name);
        using var reader = new StreamReader(entry.DataStream!);
        Assert.Equal("independent", await reader.ReadToEndAsync());
        Assert.Null(tar.GetNextEntry());
    }

    [Theory]
    [InlineData("hello.arc", "1a0248454c4c4f2e545854000000000500000000000000d2340500000068656c6c6f1a00", "HELLO.TXT")]
    [InlineData("hello.Z", "1f9d9068cab061f306", "hello")]
    public async Task ReaderOnlyFormatsExtractThroughFileService(string name, string hex, string expectedName)
    {
        var archive = Path.Combine(root, name);
        await File.WriteAllBytesAsync(archive, Convert.FromHexString(hex));
        var output = Path.Combine(root, "out");
        await new ArchiveFileService(engine).ExtractAsync(archive, output);
        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(output, expectedName)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompressedTarRejectsUnsafePathsAndLinksBeforeCreatingOutput(bool link)
    {
        var archive = Path.Combine(root, "unsafe.tar.gz");
        await using (var file = File.Create(archive))
        await using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
        using (var tar = new System.Formats.Tar.TarWriter(gzip, leaveOpen: true))
        {
            var entry = new System.Formats.Tar.PaxTarEntry(link ? System.Formats.Tar.TarEntryType.SymbolicLink : System.Formats.Tar.TarEntryType.RegularFile,
                link ? "link" : "../escaped.txt");
            if (link) entry.LinkName = "../outside";
            else entry.DataStream = new MemoryStream(Encoding.UTF8.GetBytes("unsafe"));
            tar.WriteEntry(entry);
        }
        var output = Path.Combine(root, "out");
        await Assert.ThrowsAnyAsync<Exception>(() => new ArchiveFileService(engine).ExtractAsync(archive, output));
        Assert.False(Directory.Exists(output));
        Assert.False(File.Exists(Path.Combine(root, "escaped.txt")));
    }

    [Fact]
    public async Task DamagedArcFailsCrcAndDoesNotCommitOutput()
    {
        var bytes = Convert.FromHexString("1a0248454c4c4f2e545854000000000500000000000000d2340500000068656c6c6f1a00");
        bytes[29] ^= 1;
        var archive = Path.Combine(root, "corrupt.arc");
        await File.WriteAllBytesAsync(archive, bytes);
        var output = Path.Combine(root, "out");
        var service = new ArchiveFileService(engine);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.TestAsync(archive));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ExtractAsync(archive, output));
        // The CRC failure happens after the output folder and file stream are opened; partial output stays.
        Assert.True(Directory.Exists(output));
    }

    [Fact]
    public async Task GzipRoundTripPreservesUnicodeFilename()
    {
        var input = Path.Combine(root, "文件.txt");
        await File.WriteAllTextAsync(input, "内容");
        var archive = Path.Combine(root, "文件.txt.gz");
        var service = new ArchiveFileService(engine);
        await service.CreateAsync([input], archive, ArchiveFormat.GZip);
        Assert.Equal(0, (await File.ReadAllBytesAsync(archive))[3] & 0x08);
        var output = Path.Combine(root, "out");
        await service.ExtractAsync(archive, output);
        Assert.Equal("内容", await File.ReadAllTextAsync(Path.Combine(output, "文件.txt")));
    }

    [Fact]
    public async Task DamagedTarGzipFailsIntegrityCheck()
    {
        var input = Path.Combine(root, "input.txt");
        await File.WriteAllTextAsync(input, "check trailer");
        var archive = Path.Combine(root, "broken.tar.gz");
        var service = new ArchiveFileService(engine);
        await service.CreateAsync([input], archive, ArchiveFormat.TarGZip);
        var bytes = await File.ReadAllBytesAsync(archive);
        bytes[^8] ^= 0x80;
        await File.WriteAllBytesAsync(archive, bytes);
        await Assert.ThrowsAnyAsync<Exception>(() => service.TestAsync(archive));
    }

    [Theory]
    [InlineData(ArchiveFormat.Tar)]
    [InlineData(ArchiveFormat.TarGZip)]
    [InlineData(ArchiveFormat.TarBZip2)]
    [InlineData(ArchiveFormat.TarLZip)]
    [InlineData(ArchiveFormat.TarZStandard)]
    [InlineData(ArchiveFormat.GZip)]
    [InlineData(ArchiveFormat.BZip2)]
    [InlineData(ArchiveFormat.LZip)]
    [InlineData(ArchiveFormat.ZStandard)]
    public async Task StreamApisPreserveCallerOwnership(ArchiveFormat format)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("stream content"));
        using var archive = new MemoryStream();
        await engine.CreateAsync(archive, [new("source.txt", input)], format);
        Assert.True(input.CanRead);
        Assert.True(archive.CanWrite);
        archive.Position = 0;
        var entry = Assert.Single(await engine.ListAsync(archive));
        Assert.True(archive.CanRead);
        archive.Position = 0;
        using var output = new MemoryStream();
        await engine.ExtractEntryAsync(archive, entry.EntryPath, output);
        Assert.True(archive.CanRead);
        Assert.True(output.CanWrite);
        Assert.Equal("stream content", Encoding.UTF8.GetString(output.ToArray()));
    }

    [Fact]
    public async Task DamagedXzFailsWithoutCommittingOutput()
    {
        var bytes = Convert.FromBase64String("/Td6WFoAAATm1rRGAgAhARYAAAB0L+WjAQAEaGVsbG8AAAAAsTe52+XaHpsAAR0FuC2Arx+2830BAAAAAARZWg==");
        bytes[^10] ^= 0x80;
        var path = Path.Combine(root, "broken.xz");
        await File.WriteAllBytesAsync(path, bytes);
        var output = Path.Combine(root, "out");
        await Assert.ThrowsAnyAsync<Exception>(() => new ArchiveFileService(engine).ExtractAsync(path, output));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task CancelledCompressedTarExtractionDoesNotCommitPartialOutput()
    {
        var directory = Path.Combine(root, "source");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "one.txt"), "one");
        await File.WriteAllTextAsync(Path.Combine(directory, "two.txt"), "two");
        var archive = Path.Combine(root, "archive.tar.gz");
        var service = new ArchiveFileService(engine);
        await service.CreateAsync([directory], archive, ArchiveFormat.TarGZip);
        using var cancellation = new CancellationTokenSource();
        var progress = new ImmediateProgress(report => { if (report.CompletedEntries >= 2) cancellation.Cancel(); });
        var output = Path.Combine(root, "output");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ExtractAsync(archive, output, progress: progress, cancellationToken: cancellation.Token));
        // Cancellation stops further writes but keeps what was already written to the target.
        Assert.True(Directory.Exists(output));
        Assert.Empty(Directory.EnumerateFileSystemEntries(root, ".bubuzip-*"));
    }

    private sealed class ImmediateProgress(Action<ArchiveOperationProgress> report) : IProgress<ArchiveOperationProgress>
    {
        public void Report(ArchiveOperationProgress value) => report(value);
    }

    [Theory]
    [InlineData(ArchiveFormat.GZip, ".gz")]
    [InlineData(ArchiveFormat.BZip2, ".bz2")]
    [InlineData(ArchiveFormat.LZip, ".lz")]
    [InlineData(ArchiveFormat.ZStandard, ".zst")]
    public async Task EmptySingleFileCanBeCreatedAndExtracted(ArchiveFormat format, string extension)
    {
        var input = Path.Combine(root, "empty.txt");
        await File.WriteAllBytesAsync(input, []);
        var archive = Path.Combine(root, "empty.txt" + extension);
        var service = new ArchiveFileService(engine);
        await service.CreateAsync([input], archive, format);
        var output = Path.Combine(root, "out");
        await service.ExtractAsync(archive, output);
        Assert.Empty(await File.ReadAllBytesAsync(Path.Combine(output, "empty.txt")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("streaming data")]
    public async Task GzipAcceptsNonSeekableInputsIncludingEmpty(string text)
    {
        using var source = new NonSeekableInput(Encoding.UTF8.GetBytes(text));
        using var archive = new MemoryStream();
        await engine.CreateAsync(archive, [new("original.txt", source)], ArchiveFormat.GZip);
        Assert.True(source.CanRead);
        archive.Position = 0;
        var entry = Assert.Single(await engine.ListAsync(archive));
        archive.Position = 0;
        using var output = new MemoryStream();
        await engine.ExtractEntryAsync(archive, entry.EntryPath, output);
        Assert.Equal(text, Encoding.UTF8.GetString(output.ToArray()));
    }

    private sealed class NonSeekableInput(byte[] bytes) : Stream
    {
        private readonly MemoryStream inner = new(bytes);
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => inner.ReadAsync(buffer, token);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }

    [Fact]
    public async Task GzipWithoutOriginalFilenameHasUsableName()
    {
        var path = Path.Combine(root, "example.txt.gz");
        await using (var file = File.Create(path))
        await using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
            await gzip.WriteAsync(Encoding.UTF8.GetBytes("gzip content"));
        var service = new ArchiveFileService(engine);
        var output = Path.Combine(root, "output");
        await service.ExtractAsync(path, output);
        Assert.Equal("gzip content", await File.ReadAllTextAsync(Path.Combine(output, "example.txt")));
    }
}
