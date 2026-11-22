using BubuZip;

namespace BubuZip.Desktop;

internal sealed record DesktopCommandLineOptions(
    int? FrameLimit,
    string InitialPath,
    string? ShotPath,
    bool DarkPreview,
    IReadOnlyList<string> ArchivePaths,
    bool ShowHelp = false,
    ArchiveShellRequest? ShellRequest = null,
    bool OpenSettings = false,
    bool CreateArchive = false);

internal static class DesktopCommandLine
{
    internal const string Usage = "Usage: BubuZip [--frames positive-number] [--path folder] [--shot image.png] [--dark] [--settings|--create] [--shell open|extract|extract-here|extract-smart|extract-folder|test|compress|compress-7z|compress-zip] [--] [file ...]";

    internal static DesktopCommandLineOptions Parse(string[] arguments)
    {
        int? frameLimit = null;
        var initialPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string? shotPath = null;
        var darkPreview = false;
        var archivePaths = new List<string>();
        var positionalOnly = false;
        ArchiveShellAction? shellAction = null;
        var openSettings = false;
        var createArchive = false;

        if (arguments is ["--help"] or ["-h"])
            return new(null, initialPath, null, false, archivePaths, ShowHelp: true);

        for (var index = 0; index < arguments.Length; index++)
        {
            var argument = arguments[index];
            if (positionalOnly)
            {
                archivePaths.Add(NormalizePath(argument));
                continue;
            }

            switch (argument)
            {
                case "--": positionalOnly = true; break;
                case "--frames":
                    var value = ReadValue(arguments, ref index);
                    if (!int.TryParse(value, out var frames) || frames <= 0)
                        throw new ArgumentException("--frames requires a positive integer.");
                    frameLimit = frames;
                    break;
                case "--path": initialPath = NormalizePath(ReadValue(arguments, ref index)); break;
                case "--shot": shotPath = NormalizePath(ReadValue(arguments, ref index)); break;
                case "--dark": darkPreview = true; break;
                case "--settings": openSettings = true; break;
                case "--create": createArchive = true; break;
                case "--shell":
                    if (shellAction is not null) throw new ArgumentException("Only one --shell action is allowed.");
                    shellAction = ParseShellAction(ReadValue(arguments, ref index));
                    break;
                default:
                    // Older Finder versions can supply a process serial number on launch.
                    if (argument.StartsWith("-psn_", StringComparison.Ordinal)) break;
                    if (argument.StartsWith('-'))
                        throw new ArgumentException($"Unknown option: {argument}");
                    archivePaths.Add(NormalizePath(argument));
                    break;
            }
        }

        if (shotPath is not null && archivePaths.Count > 0)
            throw new ArgumentException("--shot cannot be combined with archive files.");
        if (shellAction is not null && archivePaths.Count == 0)
            throw new ArgumentException("--shell requires at least one file or folder.");
        if (openSettings && archivePaths.Count > 0)
            throw new ArgumentException("--settings cannot be combined with file-open or shell actions.");
        if (createArchive && (openSettings || archivePaths.Count > 0))
            throw new ArgumentException("--create cannot be combined with settings, file-open or shell actions.");

        return new(frameLimit, initialPath, shotPath, darkPreview, archivePaths.ToArray(),
            ShellRequest: shellAction is { } action ? new(action, archivePaths.ToArray()) : null,
            OpenSettings: openSettings, CreateArchive: createArchive);
    }

    private static ArchiveShellAction ParseShellAction(string value) => value switch
    {
        "open" => ArchiveShellAction.Open,
        "extract" => ArchiveShellAction.Extract,
        "extract-here" => ArchiveShellAction.ExtractHere,
        "extract-smart" => ArchiveShellAction.ExtractSmart,
        "extract-folder" => ArchiveShellAction.ExtractFolder,
        "test" => ArchiveShellAction.Test,
        "compress" => ArchiveShellAction.Compress,
        "compress-7z" => ArchiveShellAction.CompressSevenZip,
        "compress-zip" => ArchiveShellAction.CompressZip,
        _ => throw new ArgumentException($"Unknown shell action: {value}"),
    };

    private static string ReadValue(string[] arguments, ref int index)
    {
        var option = arguments[index];
        if (index + 1 >= arguments.Length || string.IsNullOrWhiteSpace(arguments[index + 1]) || arguments[index + 1].StartsWith('-'))
            throw new ArgumentException($"Missing value for {option}.");
        return arguments[++index];
    }

    private static string NormalizePath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException($"Invalid path: {path}", error);
        }
    }
}
