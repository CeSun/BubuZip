namespace BubuZip.Core;

public sealed record ArchiveOperationProgress(string EntryPath, int CompletedEntries, int TotalEntries)
{
    public long? ProcessedBytes { get; init; }
    public long? TotalBytes { get; init; }
    public long? CompressedBytes { get; init; }
    public double Percent => TotalBytes is > 0 && ProcessedBytes is { } bytes
        ? Math.Clamp(100d * bytes / TotalBytes.Value, 0, 100)
        : TotalEntries == 0 ? 100 : 100d * CompletedEntries / TotalEntries;
}

/// <summary>Optional cooperative pause boundary, called from the operation worker.</summary>
public interface IArchiveOperationControl
{
    void Checkpoint(CancellationToken cancellationToken);
}
