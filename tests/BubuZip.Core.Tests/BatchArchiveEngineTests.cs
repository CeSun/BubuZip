using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using SharpCompress.Common;
using SharpCompress.Compressors.LZMA;
using SharpCompress.Writers.SevenZip;
using Xunit;

namespace BubuZip.Core.Tests;

public sealed class BatchArchiveEngineTests
{
    private readonly SharpCompressArchiveEngine _engine = new();

    [Theory]
    [InlineData(ArchiveFormat.Zip)]
    [InlineData(ArchiveFormat.SevenZip)]
    public async Task BatchExtractionKeepsOneDestinationOpenAndPreservesSelectedContent(ArchiveFormat format)
    {
        var contents = new Dictionary<string, byte[]>
        {
            ["资料/第一份.txt"] = Encoding.UTF8.GetBytes("第一份内容 📦"),
            ["资料/第二份.txt"] = Encoding.UTF8.GetBytes("第二份内容"),
            ["空文件.txt"] = [],
            ["未选择.txt"] = Encoding.UTF8.GetBytes("不应创建输出"),
        };
        using var source = await CreateArchive(contents, format);
        var outputs = new Dictionary<string, TrackedDestination>();
        var completed = new List<string>();
        var active = 0;
        var maximumActive = 0;
        var targets = contents.Keys.Take(3).Reverse().Select(name => new ArchiveExtractionTarget(name, () =>
        {
            maximumActive = Math.Max(maximumActive, ++active);
            var output = new TrackedDestination(() => active--);
            outputs.Add(name, output);
            return output;
        })).ToArray();

        await _engine.ExtractEntriesAsync(source, targets, entryCompleted: completed.Add);

        Assert.Equal(1, maximumActive);
        Assert.Equal(0, active);
        Assert.Equal(3, outputs.Count);
        Assert.Equal(targets.Select(target => target.EntryPath).Order(), completed.Order());
        foreach (var (name, output) in outputs)
        {
            Assert.Equal(contents[name], output.ToArray());
            Assert.False(output.CanWrite);
        }
        Assert.True(source.CanRead);
    }

    [Theory]
    [InlineData(ArchiveFormat.Zip)]
    [InlineData(ArchiveFormat.SevenZip)]
    public async Task BatchAvoidsReadingTheArchiveDirectoryAgainForEveryEntry(ArchiveFormat format)
    {
        var contents = Enumerable.Range(0, 24).ToDictionary(index => $"folder/file-{index:D2}.txt",
            index => Encoding.UTF8.GetBytes($"Payload number {index}"));
        using var archive = await CreateArchive(contents, format);
        using var batchSource = new CountingReadStream(archive.ToArray());
        await _engine.ExtractEntriesAsync(batchSource, contents.Keys.Select(name =>
            new ArchiveExtractionTarget(name, () => Stream.Null)).ToArray());
        using var repeatedSource = new CountingReadStream(archive.ToArray());
        foreach (var name in contents.Keys)
        {
            repeatedSource.Position = 0;
            await _engine.ExtractEntryAsync(repeatedSource, name, Stream.Null);
        }

        Assert.True(batchSource.BytesRead * 3 < repeatedSource.BytesRead,
            $"Batch read {batchSource.BytesRead} bytes; repeated extraction read {repeatedSource.BytesRead} bytes.");
    }

    [Fact]
    public async Task SolidLzma2ArchiveIsDecodedInOnePassIncludingSkippedEntries()
    {
        var contents = SolidContents();
        using var source = new CountingReadStream(CreateSolidArchive(contents));
        var outputs = new Dictionary<string, MemoryStream>();
        var selected = contents.Keys.Skip(1).ToArray();
        await _engine.ExtractEntriesAsync(source, selected.Select(name => new ArchiveExtractionTarget(name, () =>
        {
            var output = new MemoryStream(); outputs.Add(name, output); return output;
        })).ToArray());
        foreach (var name in selected) Assert.Equal(contents[name], outputs[name].ToArray());

        using var repeated = new CountingReadStream(CreateSolidArchive(contents));
        foreach (var name in selected)
        {
            repeated.Position = 0;
            await _engine.ExtractEntryAsync(repeated, name, Stream.Null);
        }
        Assert.True(source.BytesRead < repeated.BytesRead,
            $"Solid batch read {source.BytesRead} bytes; separate extraction read {repeated.BytesRead} bytes.");
    }

    [Fact]
    public async Task SolidSevenZipEntryCrcIsCheckedEvenWhenTheCompressedStreamIsValid()
    {
        // Only the first substream CRC is wrong. The LZMA2 stream and both 7z header CRCs are valid.
        using var source = new MemoryStream(CreateSolidArchive(SolidContents(), corruptFirstCrc: true));
        await Assert.ThrowsAnyAsync<InvalidDataException>(() => _engine.ExtractEntriesAsync(source,
            [new ArchiveExtractionTarget("one.txt", () => Stream.Null)]));
        Assert.True(source.CanRead);
    }

    [Fact]
    public async Task SolidSevenZipWithoutPerFileCrcIsNotRejectedAsCorrupt()
    {
        var contents = SolidContents();
        using var source = new MemoryStream(CreateSolidArchive(contents, includeCrc: false));
        var outputs = new Dictionary<string, MemoryStream>();
        await _engine.ExtractEntriesAsync(source, contents.Keys.Select(name => new ArchiveExtractionTarget(name, () =>
        {
            var output = new MemoryStream(); outputs.Add(name, output); return output;
        })).ToArray());
        foreach (var (name, content) in contents) Assert.Equal(content, outputs[name].ToArray());
    }

    [Fact]
    public async Task ARealZeroCrcIsValidatedWithoutConfusingItWithAnAbsentChecksum()
    {
        var contents = SolidContents();
        contents["one.txt"] = [0x9D, 0x0A, 0xD9, 0x6D];
        Assert.Equal(0u, Crc(contents["one.txt"]));
        using var source = new MemoryStream(CreateSolidArchive(contents));
        var output = new MemoryStream();
        await _engine.ExtractEntriesAsync(source, [new ArchiveExtractionTarget("one.txt", () => output)]);
        Assert.Equal(contents["one.txt"], output.ToArray());
    }

    [Fact]
    public async Task AnIncorrectZeroCrcStillFailsValidation()
    {
        using var source = new MemoryStream(CreateSolidArchive(SolidContents(), forceFirstCrcZero: true));
        await Assert.ThrowsAnyAsync<Exception>(() => _engine.ExtractEntriesAsync(source,
            [new ArchiveExtractionTarget("one.txt", () => Stream.Null)]));
    }

    [Fact]
    public async Task CancellationBetweenEntriesDoesNotOpenAnotherDestination()
    {
        using var source = new MemoryStream(CreateSolidArchive(SolidContents()));
        using var cancellation = new CancellationTokenSource();
        var opened = 0;
        var completed = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _engine.ExtractEntriesAsync(source,
            SolidContents().Keys.Select(name => new ArchiveExtractionTarget(name, () =>
            { opened++; return Stream.Null; })).ToArray(), entryCompleted: _ =>
            { completed++; cancellation.Cancel(); }, cancellationToken: cancellation.Token));
        Assert.Equal(1, opened);
        Assert.Equal(1, completed);
        Assert.True(source.CanRead);
    }

    [Fact]
    public async Task CancellationDuringSolidEntryDisposesDestinationAndLeavesSourceOpen()
    {
        var contents = SolidContents();
        using var source = new MemoryStream(CreateSolidArchive(contents));
        using var cancellation = new CancellationTokenSource();
        var output = new CancelOnWriteDestination(cancellation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _engine.ExtractEntriesAsync(source,
            [new ArchiveExtractionTarget("one.txt", () => output)], cancellationToken: cancellation.Token));
        Assert.True(output.ToArray().Length > 0);
        Assert.False(output.CanWrite);
        Assert.True(source.CanRead);
    }

    [Fact]
    public async Task BadZipCrcStillFailsInTheBatchPath()
    {
        var payload = Encoding.ASCII.GetBytes("batch-crc-payload");
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("payload.txt", CompressionLevel.NoCompression);
            using var output = entry.Open(); output.Write(payload);
        }
        var bytes = buffer.ToArray();
        var payloadOffset = 30 + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(26)) +
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(28));
        Assert.Equal(payload, bytes.AsSpan(payloadOffset, payload.Length).ToArray());
        bytes[payloadOffset] ^= 1;
        using var source = new MemoryStream(bytes);
        await Assert.ThrowsAnyAsync<Exception>(() => _engine.ExtractEntriesAsync(source,
            [new ArchiveExtractionTarget("payload.txt", () => Stream.Null)]));
    }

    [Theory]
    [InlineData(ArchiveFormat.Zip)]
    [InlineData(ArchiveFormat.SevenZip)]
    public async Task FileServiceUsesBatchForExtractionAndTesting(ArchiveFormat format)
    {
        var root = Path.Combine(GetRealTemporaryPath(), "BubuZip.Batch.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var archive = await CreateArchive(new Dictionary<string, byte[]>
            { ["资料/one.txt"] = Encoding.UTF8.GetBytes("one"), ["资料/two.txt"] = Encoding.UTF8.GetBytes("two") }, format);
            var path = Path.Combine(root, "input.archive");
            await File.WriteAllBytesAsync(path, archive.ToArray());
            var counted = new BatchOnlyEngine(_engine);
            var files = new ArchiveFileService(counted);
            await files.ExtractAsync(path, Path.Combine(root, "out"));
            Assert.Equal(1, counted.BatchCalls);
            Assert.Equal("two", await File.ReadAllTextAsync(Path.Combine(root, "out", "资料", "two.txt")));
            await files.TestAsync(path);
            Assert.Equal(2, counted.BatchCalls);
            Assert.Equal(0, counted.SingleCalls);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task MissingTargetAndPreCancellationDoNotOpenDestinations()
    {
        using var source = await CreateArchive(new Dictionary<string, byte[]> { ["exists.txt"] = [1, 2, 3] }, ArchiveFormat.Zip);
        var opened = false;
        await Assert.ThrowsAsync<FileNotFoundException>(() => _engine.ExtractEntriesAsync(source,
            [new ArchiveExtractionTarget("missing.txt", () => { opened = true; return Stream.Null; })]));
        Assert.False(opened);
        source.Position = 0;
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _engine.ExtractEntriesAsync(source,
            [new ArchiveExtractionTarget("exists.txt", () => { opened = true; return Stream.Null; })], cancellationToken: cancellation.Token));
        Assert.False(opened);
        Assert.Equal(0, source.Position);
    }

    private async Task<MemoryStream> CreateArchive(IReadOnlyDictionary<string, byte[]> contents, ArchiveFormat format)
    {
        var entries = contents.Select(pair => new ArchiveWriteEntry(pair.Key, new MemoryStream(pair.Value))).ToArray();
        var output = new MemoryStream();
        try { await _engine.CreateAsync(output, entries, format); output.Position = 0; return output; }
        catch { output.Dispose(); throw; }
        finally { foreach (var entry in entries) entry.Content!.Dispose(); }
    }

    private static Dictionary<string, byte[]> SolidContents() => new()
    {
        ["one.txt"] = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("first payload ", 128))),
        ["two.txt"] = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("second payload ", 128))),
        ["three.txt"] = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("third payload ", 128))),
    };

    // A deterministic in-memory fixture: one LZMA2 folder with three substreams, as specified
    // by https://github.com/ip7z/7zip/blob/main/DOC/7zFormat.txt. No executable/tool is invoked.
    private static byte[] CreateSolidArchive(IReadOnlyDictionary<string, byte[]> contents, bool corruptFirstCrc = false,
        bool includeCrc = true, bool forceFirstCrcZero = false)
    {
        var unpacked = contents.Values.SelectMany(bytes => bytes).ToArray();
        using var single = new MemoryStream();
        using (var writer = new SevenZipWriter(single, new SevenZipWriterOptions
        { CompressHeader = false, LeaveStreamOpen = true, LzmaProperties = new LzmaEncoderProperties(false, 1 << 20, 32) }))
        using (var input = new MemoryStream(unpacked)) writer.Write("all", input, null);
        var singleBytes = single.ToArray();
        var packedSize = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(singleBytes.AsSpan(12)));
        var packed = singleBytes.AsSpan(32, packedSize).ToArray();
        using var header = new MemoryStream();
        using (var writer = new BinaryWriter(header, Encoding.UTF8, true))
        {
            writer.Write((byte)1); writer.Write((byte)4); // Header / MainStreamsInfo
            writer.Write((byte)6); Number(writer, 0); Number(writer, 1); // PackInfo, position, count
            writer.Write((byte)9); Number(writer, (ulong)packed.Length); writer.Write((byte)0);
            writer.Write((byte)7); writer.Write((byte)11); Number(writer, 1); writer.Write((byte)0); // UnpackInfo / Folder
            Number(writer, 1); writer.Write((byte)0x21); writer.Write((byte)0x21); Number(writer, 1); writer.Write((byte)16); // LZMA2, 1MiB dictionary
            writer.Write((byte)12); Number(writer, (ulong)unpacked.Length); writer.Write((byte)0);
            writer.Write((byte)8); writer.Write((byte)13); Number(writer, (ulong)contents.Count); // SubStreamsInfo
            writer.Write((byte)9);
            foreach (var data in contents.Values.Take(contents.Count - 1)) Number(writer, (ulong)data.Length);
            if (includeCrc)
            {
                writer.Write((byte)10); writer.Write((byte)1);
                var index = 0;
                foreach (var data in contents.Values)
                {
                    var first = index++ == 0;
                    writer.Write(first && forceFirstCrcZero ? 0u : Crc(data) ^ (first && corruptFirstCrc ? 1u : 0u));
                }
            }
            writer.Write((byte)0); writer.Write((byte)0); // End substreams / streams
            writer.Write((byte)5); Number(writer, (ulong)contents.Count); // FilesInfo
            var names = Encoding.Unicode.GetBytes(string.Join('\0', contents.Keys) + '\0');
            writer.Write((byte)17); Number(writer, (ulong)names.Length + 1); writer.Write((byte)0); writer.Write(names);
            writer.Write((byte)0); writer.Write((byte)0); // End FilesInfo / Header
        }
        var headerBytes = header.ToArray();
        using var startHeader = new MemoryStream();
        using (var writer = new BinaryWriter(startHeader, Encoding.UTF8, true))
        { writer.Write((ulong)packed.Length); writer.Write((ulong)headerBytes.Length); writer.Write(Crc(headerBytes)); }
        using var result = new MemoryStream();
        using (var writer = new BinaryWriter(result, Encoding.UTF8, true))
        {
            writer.Write(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0, 4 });
            writer.Write(Crc(startHeader.ToArray())); writer.Write(startHeader.ToArray()); writer.Write(packed); writer.Write(headerBytes);
        }
        return result.ToArray();
    }

    private static void Number(BinaryWriter writer, ulong value)
    {
        byte prefix = 0;
        for (var extra = 0; extra < 8; extra++)
        {
            if (value < (1UL << (7 + 7 * extra)))
            {
                writer.Write((byte)(prefix | (byte)(value >> (8 * extra))));
                for (var index = 0; index < extra; index++) writer.Write((byte)(value >> (8 * index)));
                return;
            }
            prefix |= (byte)(0x80 >> extra);
        }
        writer.Write((byte)0xFF); writer.Write(value);
    }

    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xEDB88320u);
        }
        return ~crc;
    }

    private static string GetRealTemporaryPath()
    {
        var full = Path.GetFullPath(Path.GetTempPath());
        var root = Path.GetPathRoot(full)!;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            root = Path.Combine(root, part);
            if (new DirectoryInfo(root).ResolveLinkTarget(true) is { } real) root = real.FullName;
        }
        return root;
    }

    private sealed class TrackedDestination(Action disposed) : MemoryStream
    {
        private bool _closed;
        protected override void Dispose(bool disposing)
        { if (!_closed) { _closed = true; disposed(); } base.Dispose(disposing); }
    }

    private sealed class CancelOnWriteDestination(CancellationTokenSource cancellation) : MemoryStream
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { await base.WriteAsync(buffer, cancellationToken); cancellation.Cancel(); }
    }

    private sealed class CountingReadStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data, false);
        public long BytesRead { get; private set; }
        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) { var read = _inner.Read(buffer, offset, count); BytesRead += read; return read; }
        public override int Read(Span<byte> buffer) { var read = _inner.Read(buffer); BytesRead += read; return read; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { var read = await _inner.ReadAsync(buffer, cancellationToken); BytesRead += read; return read; }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }

    private sealed class BatchOnlyEngine(IBatchArchiveEngine engine) : IBatchArchiveEngine
    {
        public int BatchCalls { get; private set; }
        public int SingleCalls { get; private set; }
        public Task CreateAsync(Stream destination, IReadOnlyList<ArchiveWriteEntry> entries, ArchiveFormat format = ArchiveFormat.Zip,
            string? password = null, bool encryptFileNames = false, CancellationToken cancellationToken = default) =>
            engine.CreateAsync(destination, entries, format, password, encryptFileNames, cancellationToken);
        public Task<IReadOnlyList<ArchiveEntryInfo>> ListAsync(Stream source, string? password = null, CancellationToken cancellationToken = default) =>
            engine.ListAsync(source, password, cancellationToken);
        public Task ExtractEntryAsync(Stream source, string entryPath, Stream destination, string? password = null, CancellationToken cancellationToken = default)
        { SingleCalls++; throw new InvalidOperationException("FileService must use the batch path."); }
        public Task ExtractEntriesAsync(Stream source, IReadOnlyList<ArchiveExtractionTarget> targets, string? password = null,
            Action<string>? entryCompleted = null, CancellationToken cancellationToken = default,
            Func<string, CancellationToken, ValueTask>? beforeEntry = null)
        { BatchCalls++; return engine.ExtractEntriesAsync(source, targets, password, entryCompleted, cancellationToken, beforeEntry); }
    }
}
