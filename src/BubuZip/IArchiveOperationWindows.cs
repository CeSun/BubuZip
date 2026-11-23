using BubuZip.Core;

namespace BubuZip;

/// <summary>A single operation presented in its own progress window.</summary>
public sealed record ArchiveOperationSpec(
    string Title,
    string Description,
    string SuccessMessage,
    Func<IProgress<ArchiveOperationProgress>, CancellationToken, Task> Execute,
    string? ResultPath = null,
    string? ResultFolder = null,
    string? DisplayPath = null,
    bool IsExtraction = false,
    Func<string?>? ResolveExtractionOutput = null);

public sealed record ArchiveOperationOutcome(bool Succeeded, bool Cancelled, string? Error);

/// <summary>Shows an operation window until the user closes it and its work has been cleaned up.</summary>
public interface IArchiveOperationWindows
{
    ArchiveOperationOutcome Show(ArchiveOperationSpec operation);
}
