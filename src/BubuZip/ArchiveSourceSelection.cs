using BubuZip.Core;
using Path = System.IO.Path;

namespace BubuZip;

/// <summary>Validates the selected roots without walking folder contents on the UI thread.</summary>
public static class ArchiveSourceSelection
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static string[] Merge(IReadOnlyList<string> existing, IReadOnlyList<string> additions)
    {
        var paths = existing.Concat(additions).Select(path => Path.TrimEndingDirectorySeparator(ArchivePaths.ResolveUserPath(path)))
            .Distinct(PathComparer).ToArray();
        foreach (var path in paths)
        {
            if (!File.Exists(path) && !Directory.Exists(path)) throw new IOException($"项目已不存在或无法访问：{path}");
            if (Path.GetFileName(path).Length == 0) throw new IOException("请选择具体的文件或文件夹，不能添加整个磁盘根目录。");
        }
        var folders = paths.Where(Directory.Exists).ToArray();
        var roots = paths.Where(path => !folders.Any(folder => !PathComparer.Equals(path, folder)
            && path.StartsWith(folder + Path.DirectorySeparatorChar, PathComparison))).ToArray();
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in roots)
        {
            var name = Path.GetFileName(path);
            if (names.TryGetValue(name, out var other))
                throw new IOException($"存在同名项目“{name}”，本次添加未生效。请先重命名或移除其中一项。\n{other}\n{path}");
            names.Add(name, path);
        }
        return roots;
    }
}
