namespace EclipseArchive.Core;

/// <summary>Normalizes macOS system entry aliases without resolving user-controlled symbolic links.</summary>
public static class ArchivePaths
{
    private static readonly (string Alias, string Target)[] MacSystemAliases =
        [("/tmp", "/private/tmp"), ("/var", "/private/var"), ("/etc", "/private/etc")];

    public static string ResolveUserPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!OperatingSystem.IsMacOS()) return fullPath;

        foreach (var (alias, expectedTarget) in MacSystemAliases)
        {
            if (fullPath != alias && !fullPath.StartsWith(alias + "/", StringComparison.Ordinal)) continue;
            try
            {
                var directory = new DirectoryInfo(alias);
                if (directory.LinkTarget is not null &&
                    directory.ResolveLinkTarget(returnFinalTarget: true) is { } target &&
                    string.Equals(Path.GetFullPath(target.FullName), expectedTarget, StringComparison.Ordinal))
                    return expectedTarget + fullPath[alias.Length..];
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            // An absent, unreadable or unexpected alias must go through the caller's usual
            // link checks; never resolve its remaining path segments on the user's behalf.
            return fullPath;
        }

        return fullPath;
    }
}
