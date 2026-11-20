namespace EclipseArchive;

/// <summary>Operating-system file associations and file-manager menu installation.</summary>
public interface IShellIntegrationService
{
    string PlatformName { get; }
    Task<ShellIntegrationStatus> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<ShellIntegrationResult> AssociateAsync(IReadOnlyList<string> extensions, CancellationToken cancellationToken = default);
    Task<ShellIntegrationResult> RegisterMenuAsync(CancellationToken cancellationToken = default);
    Task<ShellIntegrationResult> UnregisterMenuAsync(CancellationToken cancellationToken = default);
}

/// <param name="AssociatedExtensions">Formats whose current default application is EclipseArchive.</param>
public sealed record ShellIntegrationStatus(
    IReadOnlyList<string> AssociatedExtensions,
    bool MenuInstalled,
    bool MenuEnabled,
    string AssociationMessage,
    string MenuMessage);

public sealed record ShellIntegrationResult(string Message);

/// <summary>Only archive extensions supported by the application's reader are advertised.</summary>
public static class ArchiveShellFormats
{
    public static IReadOnlyList<string> Extensions { get; } = Array.AsReadOnly(new[]
    {
        ".zip", ".zipx", ".7z", ".rar", ".tar", ".gz", ".gzip", ".tgz", ".bz2", ".bzip2",
        ".tbz", ".tbz2", ".xz", ".txz", ".lz", ".lzip", ".tlz", ".zst", ".zstd", ".tzst",
        ".z", ".taz", ".arc", ".arj", ".ace",
    });
}
