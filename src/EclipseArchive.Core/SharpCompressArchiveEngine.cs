using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Common.Options;
using SharpCompress.Readers;
using SharpCompress.Writers;
using SharpCompress.Writers.SevenZip;
using SharpCompress.Writers.Zip;
using SharpCompress.Compressors.LZMA;
using ICSharpCode.SharpZipLib.Zip;

namespace EclipseArchive.Core;

public sealed class SharpCompressArchiveEngine : IBatchArchiveEngine, IConfigurableArchiveEngine
{
    public Task CreateAsync(
        Stream destination,
        IReadOnlyList<ArchiveWriteEntry> entries,
        ArchiveFormat format = ArchiveFormat.Zip,
        string? password = null,
        bool encryptFileNames = false,
        CancellationToken cancellationToken = default)
        => CreateWithSettingsAsync(destination, entries, format, password, encryptFileNames, new(), cancellationToken);

    public Task CreateWithSettingsAsync(Stream destination, IReadOnlyList<ArchiveWriteEntry> entries,
        ArchiveFormat format, string? password, bool encryptFileNames, ArchiveCreateSettings settings,
        CancellationToken cancellationToken = default)
    {
        // Async codecs can still perform CPU work synchronously before their first await.
        return Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(destination);
            ArgumentNullException.ThrowIfNull(entries);
            ArgumentNullException.ThrowIfNull(settings);
            if (!destination.CanWrite)
                throw new ArgumentException("The destination stream must be writable.", nameof(destination));
            if (destination.CanSeek && (destination.Position != 0 || destination.Length != 0))
                throw new ArgumentException("The destination stream must be empty and positioned at zero.", nameof(destination));
            if (format is not (ArchiveFormat.Zip or ArchiveFormat.SevenZip))
                throw new ArgumentOutOfRangeException(nameof(format));
            if (format == ArchiveFormat.SevenZip && !destination.CanSeek)
                throw new ArgumentException("7z requires a seekable destination stream.", nameof(destination));
            if (password is "")
                throw new ArgumentException("Use null for an archive without a password.", nameof(password));
            if (!Enum.IsDefined(settings.CompressionLevel) || !Enum.IsDefined(settings.ZipEncryption))
                throw new ArgumentOutOfRangeException(nameof(settings));
            if (format == ArchiveFormat.SevenZip && settings.CompressionLevel == ArchiveCompressionLevel.Store)
                throw new NotSupportedException("7z 暂不支持仅存储模式。");
            if (format == ArchiveFormat.Zip && password is not null && settings.ZipEncryption == ZipEncryptionMethod.ZipCrypto && password.Any(character => character > 0x7f))
                throw new ArgumentException("ZipCrypto 密码仅支持 ASCII 字符；使用中文密码请选择 AES-256。", nameof(password));
            if (encryptFileNames && (format != ArchiveFormat.SevenZip || password is null))
                throw new ArgumentException("Hiding filenames requires a password-protected 7z archive.", nameof(encryptFileNames));

            // Validate the complete input set before the writer can modify the destination.
            var names = new HashSet<string>(StringComparer.Ordinal);
            var inputs = entries.Select(entry =>
            {
                ArgumentNullException.ThrowIfNull(entry);
                var name = NormalizeEntryPath(entry.EntryPath);
                if (!names.Add(name))
                    throw new ArgumentException($"Duplicate archive entry: {name}", nameof(entries));
                if (entry.Content is { CanRead: false })
                    throw new ArgumentException($"Entry content is not readable: {name}", nameof(entries));
                if (ReferenceEquals(entry.Content, destination))
                    throw new ArgumentException("An entry cannot read from the destination stream.", nameof(entries));
                return entry with { EntryPath = name };
            }).ToArray();

            if (format == ArchiveFormat.Zip && password is not null)
            {
                await WriteEncryptedZipAsync(destination, inputs, password, settings, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return;
            }

            IWriterOptions options = format == ArchiveFormat.SevenZip
                ? new SevenZipWriterOptions(CompressionType.LZMA2)
                {
                    Password = password,
                    EncryptHeader = encryptFileNames,
                    LeaveStreamOpen = true,
                    LzmaProperties = LzmaPreset(settings.CompressionLevel),
                }
                : new ZipWriterOptions(settings.CompressionLevel == ArchiveCompressionLevel.Store ? CompressionType.None : CompressionType.Deflate)
                {
                    UseZip64 = true,
                    LeaveStreamOpen = true,
                    CompressionLevel = settings.CompressionLevel == ArchiveCompressionLevel.Store ? 0 : (int)settings.CompressionLevel,
                };
            var archiveType = format == ArchiveFormat.SevenZip ? ArchiveType.SevenZip : ArchiveType.Zip;

            await using (var writer = await WriterFactory.OpenAsyncWriter(
                destination, archiveType, options, cancellationToken).ConfigureAwait(false))
            {
                foreach (var entry in inputs)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entry.Content is null)
                        await writer.WriteDirectoryAsync(entry.EntryPath, entry.LastModified, cancellationToken).ConfigureAwait(false);
                    else
                        await writer.WriteAsync(entry.EntryPath, entry.Content, entry.LastModified, cancellationToken).ConfigureAwait(false);
                }
            }

            // 7z writes its directory and final header during DisposeAsync, which is not cancellable.
            cancellationToken.ThrowIfCancellationRequested();
        }, cancellationToken);
    }

    private static LzmaEncoderProperties LzmaPreset(ArchiveCompressionLevel level) => level switch
    {
        ArchiveCompressionLevel.Fastest => new(false, 1 << 18, 16),
        ArchiveCompressionLevel.Fast => new(false, 1 << 19, 24),
        ArchiveCompressionLevel.Normal => new(false, 1 << 20, 32),
        ArchiveCompressionLevel.Maximum => new(false, 1 << 22, 48),
        ArchiveCompressionLevel.Ultra => new(false, 1 << 23, 64),
        _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };

    private static async Task WriteEncryptedZipAsync(Stream destination, IReadOnlyList<ArchiveWriteEntry> entries,
        string password, ArchiveCreateSettings settings, CancellationToken cancellationToken)
    {
        using var writer = new ZipOutputStream(destination) { IsStreamOwner = false, UseZip64 = UseZip64.Dynamic, NameTransform = null };
        writer.SetLevel((int)settings.CompressionLevel);
        foreach (var input in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = input.Content is null;
            writer.Password = directory ? null : password;
            var entry = new ICSharpCode.SharpZipLib.Zip.ZipEntry(directory ? input.EntryPath.TrimEnd('/') + "/" : input.EntryPath)
            {
                IsUnicodeText = true,
                AESKeySize = !directory && settings.ZipEncryption == ZipEncryptionMethod.Aes256 ? 256 : 0,
                CompressionMethod = settings.CompressionLevel == ArchiveCompressionLevel.Store || directory
                    ? ICSharpCode.SharpZipLib.Zip.CompressionMethod.Stored : ICSharpCode.SharpZipLib.Zip.CompressionMethod.Deflated,
            };
            if (input.LastModified is { } modified && modified.Year is >= 1980 and <= 2107) entry.DateTime = modified.ToLocalTime();
            writer.PutNextEntry(entry);
            if (input.Content is not null) await input.Content.CopyToAsync(writer, 81920, cancellationToken).ConfigureAwait(false);
            writer.CloseEntry();
        }
        writer.Finish();
    }

    public Task<IReadOnlyList<ArchiveEntryInfo>> ListAsync(
        Stream source,
        string? password = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<ArchiveEntryInfo>>(async () =>
        {
            ValidateArchiveSource(source);
            await using var archive = await ArchiveFactory.OpenAsyncArchive(
                source, ReadOptions(password), cancellationToken).ConfigureAwait(false);
            var entries = new List<ArchiveEntryInfo>();
            await foreach (var entry in archive.EntriesAsync.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = ReadAttributes(entry);
                entries.Add(new ArchiveEntryInfo(entry.Key ?? "", entry.Size, entry.IsDirectory, entry.IsEncrypted,
                    entry.LinkTarget, entry.LastModifiedTime, entry.CompressedSize,
                    entry.LinkTarget is not null || archive.Type == ArchiveType.Zip && attributes is int flags &&
                        ((unchecked((uint)flags) >> 16) & 0xF000u) == 0xA000u, attributes));
            }

            return entries;
        }, cancellationToken);
    }

    public Task ExtractEntryAsync(
        Stream source,
        string entryPath,
        Stream destination,
        string? password = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(async () =>
        {
            ValidateArchiveSource(source);
            ArgumentNullException.ThrowIfNull(destination);
            if (!destination.CanWrite)
                throw new ArgumentException("The destination stream must be writable.", nameof(destination));
            if (ReferenceEquals(source, destination))
                throw new ArgumentException("Source and destination streams must be different.", nameof(destination));
            var name = NormalizeEntryPath(entryPath);

            await using var archive = await ArchiveFactory.OpenAsyncArchive(
                source, ReadOptions(password), cancellationToken).ConfigureAwait(false);
            await foreach (var entry in archive.EntriesAsync.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (!Matches(entry, name))
                    continue;
                if (entry.IsDirectory)
                    throw new InvalidOperationException("A directory has no file content to extract.");

                await entry.WriteToAsync(destination, new ExtractionOptions { CheckCrc = true },
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return;
            }

            throw new FileNotFoundException($"Archive entry was not found: {name}", name);
        }, cancellationToken);
    }

    public Task ExtractEntriesAsync(
        Stream source,
        IReadOnlyList<ArchiveExtractionTarget> targets,
        string? password = null,
        Action<string>? entryCompleted = null,
        CancellationToken cancellationToken = default) => Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateArchiveSource(source);
            ArgumentNullException.ThrowIfNull(targets);
            var requested = new Dictionary<string, ArchiveExtractionTarget>(StringComparer.Ordinal);
            foreach (var target in targets)
            {
                ArgumentNullException.ThrowIfNull(target);
                ArgumentNullException.ThrowIfNull(target.OpenDestination);
                var name = NormalizeEntryPath(target.EntryPath);
                if (!requested.TryAdd(name, target))
                    throw new ArgumentException($"Duplicate extraction target: {name}", nameof(targets));
            }
            if (requested.Count == 0) return;

            await using var archive = await ArchiveFactory.OpenAsyncArchive(
                source, ReadOptions(password), cancellationToken).ConfigureAwait(false);
            var metadata = new Dictionary<string, IArchiveEntry>(StringComparer.Ordinal);
            await foreach (var entry in archive.EntriesAsync.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                var name = NormalizeEntryPath(entry.Key ?? "");
                if (!metadata.TryAdd(name, entry))
                    throw new InvalidDataException($"Duplicate archive entry: {name}");
            }
            foreach (var name in requested.Keys)
            {
                if (!metadata.TryGetValue(name, out var entry))
                    throw new FileNotFoundException($"Archive entry was not found: {name}", name);
                if (entry.IsDirectory)
                    throw new InvalidOperationException("A directory has no file content to extract.");
                if (entry.LinkTarget is not null)
                    throw new NotSupportedException($"Archive links are not supported: {name}");
            }

            if (archive.Type == ArchiveType.SevenZip || await archive.IsSolidAsync().ConfigureAwait(false))
            {
                var deferredValidation = new List<(string Name, IArchiveEntry Entry)>();
                var remaining = new HashSet<string>(requested.Keys, StringComparer.Ordinal);
                await using (var reader = await archive.ExtractAllEntriesAsync().ConfigureAwait(false))
                {
                    // One reader retains the solid decoder dictionary between entries.
                    while (remaining.Count > 0 && await reader.MoveToNextEntryAsync(cancellationToken).ConfigureAwait(false))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var name = NormalizeEntryPath(reader.Entry.Key ?? "");
                        if (!remaining.Remove(name)) continue;
                        var entry = metadata[name];
                        await using (var destination = OpenDestination(requested[name], source))
                        await using (var content = await reader.OpenEntryStreamAsync(cancellationToken).ConfigureAwait(false))
                        {
                            try
                            {
                                var crc = await CopySequentialEntryAsync(content, destination, entry.Size,
                                    cancellationToken).ConfigureAwait(false);
                                if (archive.Type == ArchiveType.SevenZip && entry.Crc != 0 && crc != unchecked((uint)entry.Crc))
                                    throw new InvalidDataException($"CRC checksum mismatch: {name}");
                            }
                            catch
                            {
                                // EntryStream otherwise drains the rest of a solid entry while disposing.
                                reader.Cancel();
                                throw;
                            }
                        }
                        // The public 7z Crc property uses zero for both a missing CRC and a real zero CRC.
                        // Let the library's checksum descriptor distinguish those rare cases after releasing
                        // the sequential reader; validation cannot seek the shared input while it is active.
                        if (archive.Type == ArchiveType.SevenZip && entry.Crc == 0 && entry.Size > 0)
                            deferredValidation.Add((name, entry));
                        else
                            entryCompleted?.Invoke(name);
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                }
                if (remaining.Count > 0)
                    throw new InvalidDataException("The sequential archive reader did not return all requested entries.");
                foreach (var (name, entry) in deferredValidation)
                {
                    await entry.WriteToAsync(Stream.Null, new ExtractionOptions { CheckCrc = true },
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    entryCompleted?.Invoke(name);
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
            else
            {
                foreach (var (name, entry) in metadata)
                {
                    if (!requested.TryGetValue(name, out var target)) continue;
                    cancellationToken.ThrowIfCancellationRequested();
                    await using (var destination = OpenDestination(target, source))
                        await entry.WriteToAsync(destination, new ExtractionOptions { CheckCrc = true },
                            cancellationToken: cancellationToken).ConfigureAwait(false);
                    entryCompleted?.Invoke(name);
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
        }, cancellationToken);

    private static Stream OpenDestination(ArchiveExtractionTarget target, Stream source)
    {
        var stream = target.OpenDestination() ?? throw new InvalidOperationException("The destination factory returned no stream.");
        if (ReferenceEquals(source, stream))
            throw new ArgumentException("Source and destination streams must be different.", nameof(target));
        if (!stream.CanWrite)
        {
            stream.Dispose();
            throw new ArgumentException("The destination stream must be writable.", nameof(target));
        }
        return stream;
    }

    private static readonly uint[] Crc32Table = CreateCrc32Table();

    private static uint[] CreateCrc32Table()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++) value = (value >> 1) ^ ((value & 1) != 0 ? 0xEDB88320u : 0);
            table[index] = value;
        }
        return table;
    }

    private static async Task<uint> CopySequentialEntryAsync(
        Stream source, Stream destination, long expectedLength, CancellationToken cancellationToken)
    {
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(64 * 1024);
        var crc = uint.MaxValue;
        long copied = 0;
        try
        {
            int count;
            while ((count = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
            {
                for (var index = 0; index < count; index++) crc = Crc32Table[(byte)(crc ^ buffer[index])] ^ (crc >> 8);
                copied = checked(copied + count);
                if (copied > expectedLength) throw new InvalidDataException("Archive entry exceeded its declared size.");
                await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            }
            if (copied != expectedLength) throw new InvalidDataException("Archive entry is incomplete.");
            return ~crc;
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static bool Matches(IEntry entry, string name) =>
        string.Equals(entry.Key?.Replace('\\', '/').TrimEnd('/'), name, StringComparison.Ordinal);

    private static int? ReadAttributes(IEntry entry)
    {
        // Some supported handlers inherit Entry.Attrib's unimplemented default.
        try { return entry.Attrib; }
        catch (NotImplementedException) { return null; }
    }

    private static ReaderOptions ReadOptions(string? password) => new()
    {
        Password = password,
        LeaveStreamOpen = true,
    };

    private static void ValidateArchiveSource(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead || !source.CanSeek)
            throw new ArgumentException("The archive stream must be readable and seekable.", nameof(source));
    }

    private static string NormalizeEntryPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalized = path.Replace('\\', '/').TrimEnd('/');
        if (normalized.Length == 0 || normalized.StartsWith('/') || normalized.Contains('\0') ||
            (normalized.Length > 1 && normalized[1] == ':') ||
            normalized.Split('/').Any(part => part is "" or "." or ".."))
            throw new ArgumentException("Archive entries must use relative paths without parent traversal.", nameof(path));
        return normalized;
    }
}
