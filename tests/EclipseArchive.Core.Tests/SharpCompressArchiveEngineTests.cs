using System.IO.Compression;
using System.Text;
using EclipseArchive.Core;
using Xunit;

namespace EclipseArchive.Core.Tests;

public sealed class SharpCompressArchiveEngineTests
{
    private const string FileName = "资料/你好，世界.txt";
    private const string EmptyFileName = "空文件.txt";
    private const string EmptyDirectoryName = "空目录/";
    private const string Password = "测试密码-Archive-2026!";
    private static readonly byte[] FileContent = Encoding.UTF8.GetBytes("中文内容、emoji 📦、换行\n以及第二行。");
    private readonly IArchiveEngine _engine = new SharpCompressArchiveEngine();

    [Theory]
    [InlineData(ArchiveFormat.Zip)]
    [InlineData(ArchiveFormat.SevenZip)]
    public async Task ArchiveRoundTripPreservesUnicodeEmptyFilesAndEmptyDirectories(ArchiveFormat format)
    {
        using var content = new MemoryStream(FileContent);
        using var emptyContent = new MemoryStream();
        using var archive = new MemoryStream();

        await _engine.CreateAsync(archive,
        [
            new ArchiveWriteEntry(FileName, content),
            new ArchiveWriteEntry(EmptyFileName, emptyContent),
            new ArchiveWriteEntry(EmptyDirectoryName, null),
        ], format);

        archive.Position = 0;
        var entries = await _engine.ListAsync(archive);

        var file = Assert.Single(entries, entry => entry.EntryPath == FileName);
        Assert.False(file.IsDirectory);
        Assert.Equal((long)FileContent.Length, file.Size);
        var emptyFile = Assert.Single(entries, entry => entry.EntryPath == EmptyFileName);
        Assert.False(emptyFile.IsDirectory);
        Assert.Equal(0L, emptyFile.Size);
        var emptyDirectory = Assert.Single(entries,
            entry => entry.EntryPath.TrimEnd('/') == EmptyDirectoryName.TrimEnd('/'));
        Assert.True(emptyDirectory.IsDirectory);

        using var extractedContent = new MemoryStream();
        archive.Position = 0;
        await _engine.ExtractEntryAsync(archive, FileName, extractedContent);
        Assert.Equal(FileContent, extractedContent.ToArray());

        using var extractedEmptyContent = new MemoryStream();
        archive.Position = 0;
        await _engine.ExtractEntryAsync(archive, EmptyFileName, extractedEmptyContent);
        Assert.Empty(extractedEmptyContent.ToArray());
    }

    [Fact]
    public async Task CreatedZipCanBeReadByDotNetZipArchive()
    {
        using var content = new MemoryStream(FileContent);
        using var archive = new MemoryStream();
        await _engine.CreateAsync(archive, [new ArchiveWriteEntry(FileName, content)]);

        archive.Position = 0;
        using var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
        var entry = Assert.Single(zip.Entries);
        Assert.Equal(FileName, entry.FullName);
        using var entryStream = entry.Open();
        using var extracted = new MemoryStream();
        await entryStream.CopyToAsync(extracted);
        Assert.Equal(FileContent, extracted.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PasswordProtectedSevenZipRoundTripWorks(bool encryptFileNames)
    {
        using var content = new MemoryStream(FileContent);
        using var archive = new MemoryStream();
        await _engine.CreateAsync(archive, [new ArchiveWriteEntry(FileName, content)],
            ArchiveFormat.SevenZip, Password, encryptFileNames);

        archive.Position = 0;
        var entries = await _engine.ListAsync(archive, Password);
        var entry = Assert.Single(entries);
        Assert.Equal(FileName, entry.EntryPath);
        Assert.True(entry.IsEncrypted);

        using var extracted = new MemoryStream();
        archive.Position = 0;
        await _engine.ExtractEntryAsync(archive, FileName, extracted, Password);
        Assert.Equal(FileContent, extracted.ToArray());
    }

    [Fact]
    public async Task SevenZipWithoutHeaderEncryptionAllowsListingWithoutPassword()
    {
        using var content = new MemoryStream(FileContent);
        using var archive = new MemoryStream();
        await _engine.CreateAsync(archive, [new ArchiveWriteEntry(FileName, content)],
            ArchiveFormat.SevenZip, Password);

        archive.Position = 0;
        var entries = await _engine.ListAsync(archive);
        Assert.Equal(FileName, Assert.Single(entries).EntryPath);
    }

    [Fact]
    public async Task SevenZipHeaderEncryptionPreventsListingWithoutPassword()
    {
        using var content = new MemoryStream(FileContent);
        using var archive = new MemoryStream();
        await _engine.CreateAsync(archive, [new ArchiveWriteEntry(FileName, content)],
            ArchiveFormat.SevenZip, Password, encryptFileNames: true);

        archive.Position = 0;
        await Assert.ThrowsAnyAsync<Exception>(() => _engine.ListAsync(archive));
        Assert.True(archive.CanRead);

        archive.Position = 0;
        await Assert.ThrowsAnyAsync<Exception>(() => _engine.ListAsync(archive, "wrong-password"));
        Assert.True(archive.CanRead);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WrongSevenZipPasswordFailsExtraction(bool encryptFileNames)
    {
        using var content = new MemoryStream(FileContent);
        using var archive = new MemoryStream();
        await _engine.CreateAsync(archive, [new ArchiveWriteEntry(FileName, content)],
            ArchiveFormat.SevenZip, Password, encryptFileNames);

        using var extracted = new MemoryStream();
        archive.Position = 0;
        await Assert.ThrowsAnyAsync<Exception>(() =>
            _engine.ExtractEntryAsync(archive, FileName, extracted, "wrong-password"));
        Assert.True(archive.CanRead);
        Assert.True(extracted.CanWrite);
    }

    [Theory]
    [InlineData(ArchiveFormat.Zip)]
    [InlineData(ArchiveFormat.SevenZip)]
    public async Task OperationsLeaveAllCallerOwnedStreamsOpen(ArchiveFormat format)
    {
        using var content = new MemoryStream(FileContent);
        using var archive = new MemoryStream();
        await _engine.CreateAsync(archive, [new ArchiveWriteEntry(FileName, content)], format);
        Assert.True(content.CanRead);
        Assert.True(archive.CanWrite);

        archive.Position = 0;
        await _engine.ListAsync(archive);
        Assert.True(archive.CanRead);

        using var extracted = new MemoryStream();
        archive.Position = 0;
        await _engine.ExtractEntryAsync(archive, FileName, extracted);
        Assert.True(archive.CanRead);
        Assert.True(extracted.CanWrite);
        Assert.Equal(FileContent, extracted.ToArray());
    }

    [Theory]
    [InlineData(ArchiveFormat.Zip)]
    [InlineData(ArchiveFormat.SevenZip)]
    public async Task PreCancelledCreationDoesNotWriteOrConsumeInput(ArchiveFormat format)
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var content = new MemoryStream(FileContent);
        using var archive = new MemoryStream();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _engine.CreateAsync(archive, [new ArchiveWriteEntry(FileName, content)], format,
                cancellationToken: cancelled.Token));

        Assert.Equal(0L, archive.Length);
        Assert.Equal(0L, content.Position);
        Assert.True(content.CanRead);
        Assert.True(archive.CanWrite);
    }

    [Theory]
    [InlineData(ArchiveFormat.Zip)]
    [InlineData(ArchiveFormat.SevenZip)]
    public async Task PreCancelledReadOperationsLeaveStreamsUntouched(ArchiveFormat format)
    {
        using var content = new MemoryStream(FileContent);
        using var archive = new MemoryStream();
        await _engine.CreateAsync(archive, [new ArchiveWriteEntry(FileName, content)], format);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        archive.Position = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _engine.ListAsync(archive, cancellationToken: cancelled.Token));
        Assert.Equal(0L, archive.Position);

        using var extracted = new MemoryStream();
        archive.Position = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _engine.ExtractEntryAsync(archive, FileName, extracted,
                cancellationToken: cancelled.Token));
        Assert.Equal(0L, archive.Position);
        Assert.Equal(0L, extracted.Length);
        Assert.True(archive.CanRead);
        Assert.True(extracted.CanWrite);
    }

    [Theory]
    [InlineData(ArchiveFormat.Zip)]
    [InlineData(ArchiveFormat.SevenZip)]
    public async Task CreationCanBeCancelledAfterReadingContent(ArchiveFormat format)
    {
        var bytes = new byte[512 * 1024];
        new Random(42).NextBytes(bytes);
        using var cancellation = new CancellationTokenSource();
        using var content = new CancelAfterIoStream(new MemoryStream(bytes, writable: false),
            cancellation, cancelOnRead: true);
        using var archive = new MemoryStream();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _engine.CreateAsync(archive, [new ArchiveWriteEntry(FileName, content)], format,
                cancellationToken: cancellation.Token));

        Assert.True(content.BytesProcessed > 0);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(content.CanRead);
        Assert.True(archive.CanWrite);
    }

    [Theory]
    [InlineData(ArchiveFormat.Zip)]
    [InlineData(ArchiveFormat.SevenZip)]
    public async Task ExtractionCanBeCancelledAfterWritingContent(ArchiveFormat format)
    {
        var bytes = new byte[512 * 1024];
        new Random(42).NextBytes(bytes);
        using var content = new MemoryStream(bytes);
        using var archive = new MemoryStream();
        await _engine.CreateAsync(archive, [new ArchiveWriteEntry(FileName, content)], format);
        using var cancellation = new CancellationTokenSource();
        using var extracted = new CancelAfterIoStream(new MemoryStream(),
            cancellation, cancelOnRead: false);

        archive.Position = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _engine.ExtractEntryAsync(archive, FileName, extracted,
                cancellationToken: cancellation.Token));

        Assert.True(extracted.BytesProcessed > 0);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(archive.CanRead);
        Assert.True(extracted.CanWrite);
    }

    [Fact]
    public async Task ZipHeaderEncryptionIsRejectedBeforeWriting()
    {
        using var content = new MemoryStream(FileContent);
        using var archive = new MemoryStream();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _engine.CreateAsync(archive, [new ArchiveWriteEntry(FileName, content)],
                ArchiveFormat.Zip, Password, encryptFileNames: true));
        Assert.Equal  (0L, archive.Length);
        Assert.Equal(0L, content.Position);
        Assert.True(content.CanRead);
        Assert.True(archive.CanWrite);
    }

    private sealed class CancelAfterIoStream(
        MemoryStream inner,
        CancellationTokenSource cancellation,
        bool cancelOnRead) : Stream
    {
        public long BytesProcessed { get; private set; }
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var count = inner.Read(buffer);
            if (cancelOnRead) CancelAfterProcessing(count);
            return count;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count,
            CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Write(byte[] buffer, int offset, int count) =>
            Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            inner.Write(buffer);
            if (!cancelOnRead) CancelAfterProcessing(buffer.Length);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count,
            CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        private void CancelAfterProcessing(int count)
        {
            if (count <= 0) return;
            BytesProcessed += count;
            cancellation.Cancel();
        }
    }
}
