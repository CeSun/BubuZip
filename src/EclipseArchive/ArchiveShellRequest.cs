namespace EclipseArchive;

public enum ArchiveShellAction
{
    Open, Extract, ExtractHere, ExtractFolder, Test, Compress, CompressSevenZip, CompressZip,
}

public sealed record ArchiveShellRequest(ArchiveShellAction Action, IReadOnlyList<string> Paths);
