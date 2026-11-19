namespace EclipseArchive.Core;

public enum ArchiveFormat
{
    Zip,
    SevenZip,
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
    Task ExtractEntriesAsync(
        Stream source,
        IReadOnlyList<ArchiveExtractionTarget> targets,
        string? password = null,
        Action<string>? entryCompleted = null,
        CancellationToken cancellationToken = default);
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
