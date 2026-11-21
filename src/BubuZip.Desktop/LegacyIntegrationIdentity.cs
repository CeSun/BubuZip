namespace BubuZip.Desktop;

// Read-only compatibility names for installations predating the BubuZip project rename.
// New registrations always use BubuZip; protected Windows defaults are never rewritten.
internal static class LegacyIntegrationIdentity
{
    internal const string ProgId = "EclipseArchive.Archive";
    internal const string OwnerValue = "EclipseArchiveExecutable";
    internal const string FileMenu = @"Software\Classes\*\shell\EclipseArchive";
    internal const string DirectoryMenu = @"Software\Classes\Directory\shell\EclipseArchive";
    internal const string ModernMenu = @"Software\EclipseArchive\ModernMenu";
}
