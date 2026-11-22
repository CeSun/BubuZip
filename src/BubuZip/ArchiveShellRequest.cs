namespace BubuZip;

public enum ArchiveShellAction
{
    Open, Extract, ExtractHere, ExtractFolder, Test, Compress, CompressSevenZip, CompressZip, ExtractSmart,
}

public sealed record ArchiveShellRequest(ArchiveShellAction Action, IReadOnlyList<string> Paths);
