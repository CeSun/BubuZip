using System.Text;

namespace BubuZip.Core;

public sealed partial class ArchiveFileService
{
    private static string ResolveArchiveLink(PlannedEntry entry, IReadOnlyDictionary<string, PlannedEntry> entries,
        IReadOnlySet<string> directories)
    {
        var target = entry.Info.LinkTarget!.Replace('\\', '/').Normalize(NormalizationForm.FormC);
        if (target.Length is 0 or > 32768 || target.StartsWith('/') || target.EndsWith('/') ||
            target.Contains(':') || target.Any(char.IsControl))
            throw new InvalidDataException($"Unsafe archive link target: {entry.RelativePath} → {target}");
        var parts = entry.RelativePath.Split('/').SkipLast(1).ToList();
        foreach (var part in target.Split('/'))
        {
            // Never normalize through a file or another link (including link/..).
            // Its filesystem meaning could differ from lexical normalization.
            if (parts.Count > 0 && !directories.Contains(string.Join('/', parts)))
                throw new InvalidDataException($"Archive link traverses a missing directory, file or link: {entry.RelativePath}");
            if (part is "" or ".") continue;
            if (part == "..")
            {
                if (parts.Count == 0)
                    throw new InvalidDataException($"Archive link escapes the extraction root: {entry.RelativePath}");
                parts.RemoveAt(parts.Count - 1);
            }
            else parts.Add(part);
        }
        var path = NormalizeEntryPath(string.Join('/', parts));
        if (!entries.TryGetValue(path, out var destination))
            throw new InvalidDataException($"链接目标不是包内文件或不存在：{entry.RelativePath} → {target}");
        if (destination.Info.IsDirectory)
            throw new NotSupportedException($"暂不支持目录符号链接：{entry.RelativePath}");
        return path;
    }

    /// <summary>Iterative topological order avoids stack overflow on hostile link chains.</summary>
    private static List<PlannedEntry> OrderLinks(IReadOnlyList<PlannedEntry> entries)
    {
        var paths = entries.ToDictionary(entry => entry.RelativePath, StringComparer.Ordinal);
        var ordered = new List<PlannedEntry>();
        var complete = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries.Where(entry => entry.LinkTargetPath is not null))
        {
            var chain = new List<PlannedEntry>();
            var visiting = new HashSet<string>(StringComparer.Ordinal);
            var current = entry;
            while (current.LinkTargetPath is { } target && !complete.Contains(current.RelativePath))
            {
                if (!visiting.Add(current.RelativePath))
                    throw new InvalidDataException($"Archive contains a symbolic link cycle: {current.RelativePath}");
                chain.Add(current);
                if (!paths.TryGetValue(target, out current) || current.Info.IsDirectory)
                    throw new InvalidDataException($"链接目标未包含在本次解压的文件中：{target}");
            }
            for (var index = chain.Count - 1; index >= 0; index--)
            {
                ordered.Add(chain[index]);
                complete.Add(chain[index].RelativePath);
            }
        }
        return ordered;
    }

    private static async Task CreateLinksAsync(IReadOnlyList<PlannedEntry> entries, ExtractionPlan plan,
        ArchiveProgressTracker tracker, CancellationToken token,
        Func<PlannedEntry, CancellationToken, ValueTask>? beforeEntry)
    {
        // No ordinary archive entry is written after this pass starts. Each link
        // targets a file extracted by this operation, never pre-existing content.
        var finalTargets = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in OrderLinks(entries))
        {
            token.ThrowIfCancellationRequested();
            if (beforeEntry is not null) await beforeEntry(entry, token).ConfigureAwait(false);
            var placement = plan.Files[entry.RelativePath];
            if (placement.Target is null) { tracker.Complete(entry.RelativePath); continue; }
            var target = plan.Files[entry.LinkTargetPath!].Target;
            if (target is null)
                throw new IOException($"链接目标已被跳过，无法创建链接：{entry.RelativePath}");
            var finalTarget = finalTargets.GetValueOrDefault(entry.LinkTargetPath!) ?? target;
            EnsureNoLinks(finalTarget);
            if (!File.Exists(finalTarget))
                throw new IOException($"链接目标未成功解压：{entry.RelativePath}");
            if (finalTarget != target)
            {
                // A chain may use only links created in this pass, still resolving
                // to the previously validated plain file after conflict callbacks.
                EnsureNoLinks(Path.GetDirectoryName(target)!);
                var resolved = new FileInfo(target).ResolveLinkTarget(returnFinalTarget: true)?.FullName;
                if (!string.Equals(resolved, finalTarget, OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new IOException($"链接目标在解压过程中发生变化：{entry.RelativePath}");
            }
            EnsureNoLinks(placement.Target);
            var parent = Path.GetDirectoryName(placement.Target)!;
            EnsureNoLinks(parent);
            Directory.CreateDirectory(parent);
            var relativeTarget = Path.GetRelativePath(parent, target);
            // Preserve the archived spelling when paths were not remapped. Signed
            // macOS bundles can seal the link text as well as the target's content.
            var originalTarget = ToNative(entry.Info.LinkTarget!);
            var hasInteriorTraversal = entry.Info.LinkTarget!.Replace('\\', '/').Split('/')
                .SkipWhile(part => part is "" or "." or "..").Any(part => part is "." or "..");
            if (!hasInteriorTraversal && string.Equals(Path.GetFullPath(Path.Combine(parent, originalTarget)), target,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                relativeTarget = originalTarget;
            var temporary = TemporarySibling(placement.Target);
            try
            {
                File.CreateSymbolicLink(temporary, relativeTarget);
                token.ThrowIfCancellationRequested();
                EnsureNoLinks(placement.Target);
                File.Move(temporary, placement.Target, placement.Overwrite);
            }
            catch (IOException error) when (OperatingSystem.IsWindows() && (error.HResult & 0xffff) == 1314)
            {
                throw new IOException("Windows 未授予创建符号链接的权限，无法保留压缩包中的文件链接。", error);
            }
            finally
            {
                if (new FileInfo(temporary).LinkTarget is not null) File.Delete(temporary);
            }
            finalTargets[entry.RelativePath] = finalTarget;
            tracker.Advance(entry.RelativePath, Math.Max(0, entry.Info.Size));
            tracker.Complete(entry.RelativePath);
        }
    }
}
