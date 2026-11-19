using EclipseArchive.Core;

namespace EclipseArchive;

public sealed record BrowserItem(
    string Name, string Path, bool IsDirectory, long Size, long? PackedSize, DateTime? Modified,
    bool IsArchive = false, bool IsLink = false, bool IsEncrypted = false)
{
    public string TypeLabel => IsLink ? "符号链接" : IsDirectory ? "文件夹" : IsArchive ? "压缩文件" :
        System.IO.Path.GetExtension(Name) is { Length: > 1 } extension ? extension[1..].ToUpperInvariant() + " 文件" : "文件";
    public string SizeLabel => IsDirectory ? "" : FormatSize(Size);
    public string PackedSizeLabel => PackedSize is long size && !IsDirectory ? FormatSize(size) : "";
    public string ModifiedLabel => Modified?.ToString("yyyy-MM-dd HH:mm") ?? "";
    public string AttributesLabel => IsEncrypted ? "加密" : IsLink ? "链接" : IsDirectory ? "D" : "";
    public string Icon => IsDirectory ? "▸" : IsArchive ? "▣" : "·";
    public static string FormatSize(long size) => size < 1024 ? $"{size:N0} B" : size < 1024L * 1024 ? $"{size / 1024d:N1} KB" :
        size < 1024L * 1024 * 1024 ? $"{size / (1024d * 1024):N1} MB" : $"{size / (1024d * 1024 * 1024):N2} GB";
}

public sealed record BrowserSnapshot(
    string DirectoryPath, string? ArchivePath, string ArchivePrefix, IReadOnlyList<BrowserItem> Items,
    string? ArchiveDisplayPath = null)
{
    public string DisplayPath => ArchivePath is null ? DirectoryPath : (ArchiveDisplayPath ?? ArchivePath) + "!/" + ArchivePrefix;
}

public sealed class BrowserService(IArchiveEngine engine)
{
    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".zip", ".zipx", ".7z", ".rar", ".tar", ".gz", ".gzip", ".tgz", ".bz2", ".bzip2", ".tbz", ".tbz2", ".xz", ".txz", ".lz", ".lzip", ".tlz", ".zst", ".zstd", ".tzst", ".z", ".taz", ".arc", ".arj", ".ace" };

    public static bool IsArchiveFile(string path) => ArchiveExtensions.Contains(System.IO.Path.GetExtension(path));

    public BrowserSnapshot BrowseDirectory(string path, bool showHidden = false)
    {
        var directory = new DirectoryInfo(System.IO.Path.GetFullPath(path));
        if (!directory.Exists) throw new DirectoryNotFoundException(directory.FullName);
        var items = directory.EnumerateFileSystemInfos()
            .Where(info => showHidden || !info.Name.StartsWith('.'))
            .Select(info =>
            {
                var isDirectory = (info.Attributes & FileAttributes.Directory) != 0;
                var isLink = info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0;
                return new BrowserItem(info.Name, info.FullName, isDirectory,
                    info is FileInfo file ? file.Length : 0, null, info.LastWriteTime,
                    !isDirectory && IsArchiveFile(info.Name), isLink);
            });
        return new BrowserSnapshot(directory.FullName, null, "", Sort(items));
    }

    public async Task<BrowserSnapshot> BrowseArchiveAsync(
        string archivePath, string prefix = "", string? password = null, CancellationToken cancellationToken = default)
    {
        var fullPath = System.IO.Path.GetFullPath(archivePath);
        var normalizedPrefix = prefix.Replace('\\', '/').Trim('/');
        if (normalizedPrefix.Length > 0) normalizedPrefix += "/";
        await using var source = File.OpenRead(fullPath);
        var entries = await engine.ListAsync(source, password, cancellationToken).ConfigureAwait(false);
        var items = new Dictionary<string, BrowserItem>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = entry.EntryPath.Replace('\\', '/');
            if (!key.StartsWith(normalizedPrefix, StringComparison.Ordinal)) continue;
            var remainder = key[normalizedPrefix.Length..].TrimEnd('/');
            if (remainder.Length == 0) continue;
            var slash = remainder.IndexOf('/');
            var isDirectory = slash >= 0 || entry.IsDirectory;
            var name = slash >= 0 ? remainder[..slash] : remainder;
            var itemPath = normalizedPrefix + name + (isDirectory ? "/" : "");
            if (!items.ContainsKey(itemPath) || slash < 0)
                items[itemPath] = new BrowserItem(name, itemPath, isDirectory,
                    isDirectory ? 0 : entry.Size, isDirectory ? null : entry.CompressedSize,
                    slash < 0 ? entry.LastModified : null, !isDirectory && IsArchiveFile(name),
                    slash < 0 && (entry.IsSymbolicLink || entry.LinkTarget is not null), entry.IsEncrypted);
        }
        if (normalizedPrefix.Length > 0 && items.Count == 0 &&
            !entries.Any(entry => entry.IsDirectory && entry.EntryPath.Replace('\\', '/').TrimEnd('/') + "/" == normalizedPrefix))
            throw new DirectoryNotFoundException($"归档内不存在文件夹：{normalizedPrefix}");
        return new BrowserSnapshot(System.IO.Path.GetDirectoryName(fullPath)!, fullPath, normalizedPrefix, Sort(items.Values));
    }

    private static BrowserItem[] Sort(IEnumerable<BrowserItem> items) => items.OrderByDescending(item => item.IsDirectory)
        .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
}
