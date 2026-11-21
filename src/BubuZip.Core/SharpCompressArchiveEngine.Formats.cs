using SharpCompress.Common;
using SharpCompress.Detection;
using SharpCompress.Providers;
using SharpCompress.Readers;
using SharpCompress.Writers;
using SharpCompress.Writers.GZip;
using SharpCompress.Writers.Tar;

namespace BubuZip.Core;

public sealed partial class SharpCompressArchiveEngine
{
    private readonly record struct ReadKind(bool ReaderOnly, CompressionType? RawCompression, CompressionType? OuterCompression);

    private static async Task<ReadKind> DetectAsync(Stream source, string? password, CancellationToken token)
    {
        var position = source.Position;
        try
        {
            var detection = await ArchiveDetector.DetectArchiveAsync(source, ReadOptions(password), token).ConfigureAwait(false);
            if (detection?.ContainerType == ArchiveType.Lzw) return new(false, CompressionType.Lzw, null);
            if (detection is not null)
                return new(!detection.SupportedApis.HasFlag(ArchiveAccessMode.Archive) && detection.SupportedApis.HasFlag(ArchiveAccessMode.Reader), null, detection.OuterCompressionType);
            source.Position = position;
            var signature = new byte[6];
            var count = await source.ReadAtLeastAsync(signature, signature.Length, throwOnEndOfStream: false, token).ConfigureAwait(false);
            CompressionType? raw = count >= 3 && signature.AsSpan(0, 3).SequenceEqual("BZh"u8) ? CompressionType.BZip2 :
                count >= 6 && signature.AsSpan(0, 6).SequenceEqual(new byte[] { 0xfd, 0x37, 0x7a, 0x58, 0x5a, 0x00 }) ? CompressionType.Xz :
                count >= 4 && signature.AsSpan(0, 4).SequenceEqual("LZIP"u8) ? CompressionType.LZip :
                count >= 4 && signature.AsSpan(0, 4).SequenceEqual(new byte[] { 0x28, 0xb5, 0x2f, 0xfd }) ? CompressionType.ZStandard : null;
            return new(false, raw, null);
        }
        finally { source.Position = position; }
    }

    private static string SingleEntryName(Stream source)
    {
        var name = source is FileStream file ? Path.GetFileNameWithoutExtension(file.Name) : "content";
        return string.IsNullOrWhiteSpace(name) || name is "." or ".." ? "content" : name;
    }

    private static string EntryName(IEntry entry, ArchiveType type, Stream source) =>
        type == ArchiveType.GZip && string.IsNullOrWhiteSpace(entry.Key) ? SingleEntryName(source) : entry.Key ?? "";

    private static async Task<IReadOnlyList<ArchiveEntryInfo>> ListSequentialAsync(Stream source, string? password, CancellationToken token)
    {
        await using var reader = await ReaderFactory.OpenAsyncReader(source, ReadOptions(password), token).ConfigureAwait(false);
        var entries = new List<ArchiveEntryInfo>();
        while (await reader.MoveToNextEntryAsync(token).ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();
            var entry = reader.Entry;
            var attributes = ReadAttributes(entry);
            long size;
            try { size = entry.Size; }
            catch (NotImplementedException) { size = 0; }
            entries.Add(new(entry.Key ?? "", size, entry.IsDirectory, entry.IsEncrypted, entry.LinkTarget,
                entry.LastModifiedTime, entry.CompressedSize, entry.LinkTarget is not null, attributes));
        }
        return entries;
    }

    private static async Task ExtractSequentialAsync(Stream source, IReadOnlyList<ArchiveExtractionTarget> targets,
        string? password, Action<string>? completed, CompressionType? outerCompression, CancellationToken token)
    {
        var start = source.Position;
        if (outerCompression is { } outer)
        {
            try
            {
                await using var decoded = await CompressionProviderRegistry.Default.CreateDecompressStreamAsync(outer, new NonClosingStream(source), token).ConfigureAwait(false);
                await decoded.CopyToAsync(Stream.Null, 81920, token).ConfigureAwait(false);
            }
            finally { source.Position = start; }
        }
        var metadata = await ListSequentialAsync(source, password, token).ConfigureAwait(false);
        var entries = new Dictionary<string, ArchiveEntryInfo>(StringComparer.Ordinal);
        foreach (var item in metadata)
            if (!entries.TryAdd(NormalizeEntryPath(item.EntryPath), item)) throw new InvalidDataException("Duplicate archive entry.");
        var requested = new Dictionary<string, ArchiveExtractionTarget>(StringComparer.Ordinal);
        foreach (var target in targets)
        {
            var name = NormalizeEntryPath(target.EntryPath);
            if (!requested.TryAdd(name, target)) throw new ArgumentException("Duplicate extraction target.");
            if (!entries.TryGetValue(name, out var info)) throw new FileNotFoundException($"Archive entry was not found: {name}");
            if (info.IsDirectory) throw new InvalidOperationException("A directory has no file content to extract.");
            if (info.IsSymbolicLink || info.LinkTarget is not null) throw new NotSupportedException($"Archive links are not supported: {name}");
        }
        source.Position = start;
        await using var reader = await ReaderFactory.OpenAsyncReader(source, ReadOptions(password), token).ConfigureAwait(false);
        while (requested.Count > 0 && await reader.MoveToNextEntryAsync(token).ConfigureAwait(false))
        {
            var name = NormalizeEntryPath(reader.Entry.Key ?? "");
            if (!requested.Remove(name, out var target)) continue;
            try
            {
                await using (var output = OpenDestination(target, source))
                {
                    var checkedOutput = new IntegrityWriteStream(output, reader.Type);
                    await reader.WriteEntryToAsync(checkedOutput, token).ConfigureAwait(false);
                    checkedOutput.Verify(reader.Entry, name);
                }
            }
            catch { reader.Cancel(); throw; }
            completed?.Invoke(name);
        }
        if (requested.Count != 0) throw new InvalidDataException("Archive reader did not return all requested entries.");
    }

    private static async Task<IReadOnlyList<ArchiveEntryInfo>> ListRawAsync(Stream source, CompressionType type, CancellationToken token)
    {
        var position = source.Position;
        try
        {
            await using var decoded = await CompressionProviderRegistry.Default.CreateDecompressStreamAsync(type, new NonClosingStream(source), token).ConfigureAwait(false);
            var buffer = new byte[81920];
            long length = 0;
            int count;
            while ((count = await decoded.ReadAsync(buffer, token).ConfigureAwait(false)) != 0) length = checked(length + count);
            return [new(SingleEntryName(source), length, false, false, CompressedSize: source.Length - position)];
        }
        finally { source.Position = position; }
    }

    private static async Task ExtractRawAsync(Stream source, string name, Stream destination, CompressionType type, CancellationToken token)
    {
        if (!string.Equals(name, SingleEntryName(source), StringComparison.Ordinal)) throw new FileNotFoundException($"Archive entry was not found: {name}");
        await using var decoded = await CompressionProviderRegistry.Default.CreateDecompressStreamAsync(type, new NonClosingStream(source), token).ConfigureAwait(false);
        await decoded.CopyToAsync(destination, 81920, token).ConfigureAwait(false);
    }

    private static async Task WriteAdditionalFormatAsync(Stream destination, IReadOnlyList<ArchiveWriteEntry> entries,
        ArchiveFormat format, ArchiveCreateSettings settings, CancellationToken token)
    {
        var single = format is ArchiveFormat.GZip or ArchiveFormat.BZip2 or ArchiveFormat.LZip or ArchiveFormat.ZStandard;
        if (single && (entries.Count != 1 || entries[0].Content is null))
            throw new ArgumentException("此格式只能压缩单个文件；多个文件或文件夹请选择 TAR 组合格式。");
        if (!single && entries.Any(entry => entry.Content is { CanSeek: false } || entry.Content is { CanSeek: true } stream && stream.Position != 0))
            throw new ArgumentException("TAR 格式要求可定位且位于开头的输入流。");
        Stream? gzipInput = null;
        if (format == ArchiveFormat.GZip)
        {
            var input = entries[0].Content!;
            var firstByte = new byte[1];
            var count = await input.ReadAsync(firstByte, token).ConfigureAwait(false);
            if (count == 0)
            {
                // SharpCompress does not emit a header for an empty GZip entry.
                await destination.WriteAsync(Convert.FromHexString("1f8b080000000000020a03000000000000000000"), token).ConfigureAwait(false);
                return;
            }
            if (input.CanSeek) { input.Position--; gzipInput = input; }
            else gzipInput = new PrefixReadStream(input, firstByte[0]);
        }
        if (format == ArchiveFormat.GZip && entries[0].EntryPath.Any(character => character > 0x7f))
        {
            // GZip filename headers are not consistently decoded as UTF-8. Omit the header
            // and let file managers derive the original Unicode name from the .gz filename.
            await using var gzip = await CompressionProviderRegistry.Default.CreateCompressStreamAsync(
                CompressionType.GZip, new NonClosingStream(destination), (int)settings.CompressionLevel, token).ConfigureAwait(false);
            await gzipInput!.CopyToAsync(gzip, 81920, token).ConfigureAwait(false);
            return;
        }
        if (format == ArchiveFormat.GZip)
        {
            await using var writer = await WriterFactory.OpenAsyncWriter(destination, ArchiveType.GZip,
                new GZipWriterOptions((int)settings.CompressionLevel) { LeaveStreamOpen = true }, token).ConfigureAwait(false);
            await writer.WriteAsync(entries[0].EntryPath, gzipInput!, entries[0].LastModified, token).ConfigureAwait(false);
            return;
        }
        if (single)
        {
            var compression = format switch { ArchiveFormat.BZip2 => CompressionType.BZip2, ArchiveFormat.LZip => CompressionType.LZip, _ => CompressionType.ZStandard };
            await using var stream = await CompressionProviderRegistry.Default.CreateCompressStreamAsync(compression, new NonClosingStream(destination), 3, token).ConfigureAwait(false);
            await entries[0].Content!.CopyToAsync(stream, 81920, token).ConfigureAwait(false);
            return;
        }
        var type = format switch
        {
            ArchiveFormat.Tar => CompressionType.None, ArchiveFormat.TarGZip => CompressionType.GZip,
            ArchiveFormat.TarBZip2 => CompressionType.BZip2, ArchiveFormat.TarLZip => CompressionType.LZip,
            ArchiveFormat.TarZStandard => CompressionType.ZStandard, _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
        Stream output = destination;
        Stream? zstd = null;
        try
        {
            if (type == CompressionType.ZStandard)
            {
                zstd = await CompressionProviderRegistry.Default.CreateCompressStreamAsync(type, new NonClosingStream(destination), 3, token).ConfigureAwait(false);
                output = zstd; type = CompressionType.None;
            }
            await using var writer = await WriterFactory.OpenAsyncWriter(output, ArchiveType.Tar,
                new TarWriterOptions(type, true) { LeaveStreamOpen = true, CompressionLevel = type == CompressionType.GZip ? (int)settings.CompressionLevel : 0 }, token).ConfigureAwait(false);
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                if (entry.Content is null) await writer.WriteDirectoryAsync(entry.EntryPath, entry.LastModified, token).ConfigureAwait(false);
                else await writer.WriteAsync(entry.EntryPath, entry.Content, entry.LastModified, token).ConfigureAwait(false);
            }
        }
        finally { if (zstd is not null) await zstd.DisposeAsync().ConfigureAwait(false); }
    }

    private sealed class PrefixReadStream(Stream inner, byte first) : Stream
    {
        private bool _pending = true;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (_pending && !buffer.IsEmpty) { _pending = false; buffer[0] = first; return 1; }
            return inner.Read(buffer);
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (_pending && !buffer.IsEmpty) { _pending = false; buffer.Span[0] = first; return ValueTask.FromResult(1); }
            return inner.ReadAsync(buffer, token);
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class IntegrityWriteStream(Stream inner, ArchiveType type) : Stream
    {
        private uint _crc32 = uint.MaxValue;
        private ushort _crc16;
        private long _length;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => _length; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken token) => inner.FlushAsync(token);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Update(buffer);
            inner.Write(buffer);
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            Update(buffer.Span);
            await inner.WriteAsync(buffer, token).ConfigureAwait(false);
        }
        private void Update(ReadOnlySpan<byte> bytes)
        {
            _length = checked(_length + bytes.Length);
            if (type is ArchiveType.Arj or ArchiveType.Ace)
                foreach (var value in bytes) _crc32 = Crc32Table[(byte)(_crc32 ^ value)] ^ (_crc32 >> 8);
            else if (type == ArchiveType.Arc)
                foreach (var value in bytes)
                {
                    var crc = (uint)(_crc16 ^ value);
                    for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xA001u : 0);
                    _crc16 = (ushort)crc;
                }
        }
        public void Verify(IEntry entry, string name)
        {
            if (type != ArchiveType.Lzw)
            {
                long? size;
                try { size = entry.Size; }
                catch (NotImplementedException) { size = null; }
                if (size is >= 0 && _length != size.Value) throw new InvalidDataException($"Archive entry is incomplete: {name}");
            }
            var expected = unchecked((uint)entry.Crc);
            if (type == ArchiveType.Arc && _crc16 != (ushort)expected ||
                type == ArchiveType.Arj && ~_crc32 != expected ||
                type == ArchiveType.Ace && _crc32 != expected)
                throw new InvalidDataException($"CRC checksum mismatch: {name}");
        }
    }

    private sealed class NonClosingStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => inner.WriteAsync(buffer, cancellationToken);
        protected override void Dispose(bool disposing) { }
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
