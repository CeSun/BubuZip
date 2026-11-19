namespace EclipseArchive.Core;

public sealed record ArchiveOperationProgress(string EntryPath, int CompletedEntries, int TotalEntries)
{
    public double Percent => TotalEntries == 0 ? 100 : 100d * CompletedEntries / TotalEntries;
}
