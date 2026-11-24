namespace BubuZip.Core;

public enum ArchiveFormat
{
    Zip,
    SevenZip,
    Tar,
    TarGZip,
    TarBZip2,
    TarLZip,
    TarZStandard,
    GZip,
    BZip2,
    LZip,
    ZStandard,
}

public enum ArchiveCompressionLevel { Store = 0, Fastest = 1, Fast = 3, Normal = 5, Maximum = 7, Ultra = 9 }
public enum ZipEncryptionMethod { Aes256, ZipCrypto }
public enum ExtractionPathMode { FullPaths, CurrentFolder, NoPaths }

public sealed record ArchiveCreateSettings(
    ArchiveCompressionLevel CompressionLevel = ArchiveCompressionLevel.Normal,
    ZipEncryptionMethod ZipEncryption = ZipEncryptionMethod.Aes256);

public sealed record ArchiveExtractSettings(
    ExtractionPathMode PathMode = ExtractionPathMode.FullPaths,
    string CurrentPrefix = "");

/// <summary>Optional archive creation settings; engines that do not implement this retain the original interface.</summary>
public interface IConfigurableArchiveEngine : IArchiveEngine
{
    Task CreateWithSettingsAsync(Stream destination, IReadOnlyList<ArchiveWriteEntry> entries,
        ArchiveFormat format, string? password, bool encryptFileNames, ArchiveCreateSettings settings,
        CancellationToken cancellationToken = default);
}

/// <summary>A null content stream represents a directory. The caller owns all streams.</summary>
public sealed record ArchiveWriteEntry(string EntryPath, Stream? Content, DateTime? LastModified = null);

public sealed record ArchiveEntryInfo(
    string EntryPath, long Size, bool IsDirectory, bool IsEncrypted,
    string? LinkTarget = null, DateTime? LastModified = null, long? CompressedSize = null,
    bool IsSymbolicLink = false, int? Attributes = null);

/// <summary>
/// A file to extract. The engine opens its destination only when processing that entry,
/// and disposes the returned stream after extraction. The caller still owns the archive stream.
/// </summary>
public sealed record ArchiveExtractionTarget(string EntryPath, Func<Stream> OpenDestination);

/// <summary>Optional efficient extraction of several entries while keeping one archive open.</summary>
public interface IBatchArchiveEngine : IArchiveEngine
{
    /// <summary>
    /// Extracts the requested entries. <paramref name="beforeEntry"/> is awaited immediately before
    /// each entry's destination is opened, so callers can resolve conflicts or other asynchronous
    /// decisions at the moment that entry is actually extracted.
    /// </summary>
    Task ExtractEntriesAsync(
        Stream source,
        IReadOnlyList<ArchiveExtractionTarget> targets,
        string? password = null,
        Action<string>? entryCompleted = null,
        CancellationToken cancellationToken = default,
        Func<string, CancellationToken, ValueTask>? beforeEntry = null);
}

/// <summary>
/// Archive operations without UI or filesystem policy. Streams must not be shared between
/// concurrent operations. Position input archives at their beginning before each call.
/// Creation requires an empty output stream. On failure or cancellation, discard partial output.
/// </summary>
public interface IArchiveEngine
{
    Task CreateAsync(
        Stream destination,
        IReadOnlyList<ArchiveWriteEntry> entries,
        ArchiveFormat format = ArchiveFormat.Zip,
        string? password = null,
        bool encryptFileNames = false,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ArchiveEntryInfo>> ListAsync(
        Stream source,
        string? password = null,
        CancellationToken cancellationToken = default);

    Task ExtractEntryAsync(
        Stream source,
        string entryPath,
        Stream destination,
        string? password = null,
        CancellationToken cancellationToken = default);
}
