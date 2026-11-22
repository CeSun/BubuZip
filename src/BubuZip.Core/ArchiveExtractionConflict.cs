namespace BubuZip.Core;

public enum ArchiveConflictAction { Replace, Skip, KeepBoth, Cancel }

public sealed record ArchiveConflictDecision(ArchiveConflictAction Action, bool ApplyToAll = false);

public sealed record ArchiveExtractionConflict(string EntryPath, string DestinationPath,
    bool IncomingIsDirectory, bool ExistingIsDirectory, long? IncomingSize, long? ExistingSize,
    DateTime IncomingModified, DateTime ExistingModified)
{
    public bool CanReplace => !IncomingIsDirectory && !ExistingIsDirectory;
}

public sealed record ArchiveExtractionSummary(int Replaced, int Skipped, int Renamed);

/// <summary>Optional interaction supplied by the operation UI; core callers remain non-interactive by default.</summary>
public interface IArchiveExtractionInteraction
{
    Task<ArchiveConflictDecision> ResolveConflictAsync(ArchiveExtractionConflict conflict, CancellationToken cancellationToken);
    void ReportExtractionSummary(ArchiveExtractionSummary summary);
}
