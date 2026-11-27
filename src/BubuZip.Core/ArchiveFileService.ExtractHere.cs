namespace BubuZip.Core;

public sealed partial class ArchiveFileService
{
    /// <summary>Extract one top-level item here; group multiple top-level items in an archive-named folder.</summary>
    public Task<string> ExtractSmartAsync(string archivePath, string destinationDirectory, string? password = null,
        IProgress<ArchiveOperationProgress>? progress = null, CancellationToken cancellationToken = default,
        ArchiveExtractSettings? settings = null, IArchiveExtractionInteraction? interaction = null) => Task.Run(() =>
        ExtractIntoExistingAsync(archivePath, destinationDirectory, password, null, progress,
            cancellationToken, settings, interaction, smart: true), cancellationToken);

    public Task<string> ExtractHereAsync(string archivePath, string destinationDirectory, string? password = null,
        IProgress<ArchiveOperationProgress>? progress = null, CancellationToken cancellationToken = default,
        ArchiveExtractSettings? settings = null, IArchiveExtractionInteraction? interaction = null) => Task.Run(() =>
        ExtractIntoExistingAsync(archivePath, destinationDirectory, password, null, progress,
            cancellationToken, settings, interaction), cancellationToken);

    /// <summary>Extract into the specified folder, merging directories and asking about existing files.</summary>
    public Task<string> ExtractToFolderAsync(string archivePath, string destinationDirectory, string? password = null,
        IProgress<ArchiveOperationProgress>? progress = null, CancellationToken cancellationToken = default,
        ArchiveExtractSettings? settings = null, IArchiveExtractionInteraction? interaction = null) => Task.Run(() =>
    {
        var destination = ArchivePaths.ResolveUserPath(destinationDirectory);
        return ExtractIntoExistingAsync(archivePath, Path.GetDirectoryName(destination)!, password, null, progress,
            cancellationToken, settings, interaction, folderName: Path.GetFileName(destination));
    }, cancellationToken);

    /// <summary>
    /// Merge into an existing folder. Directories are placed first so parents exist; file conflicts are
    /// resolved as the engine reaches each entry, so prompts interleave with the actual extraction.
    /// Entries stream straight to their final paths — no staging copy and no rollback.
    /// </summary>
    private async Task<string> ExtractIntoExistingAsync(string archivePath, string destinationDirectory, string? password,
        IReadOnlyList<string>? entryPaths, IProgress<ArchiveOperationProgress>? progress, CancellationToken cancellationToken,
        ArchiveExtractSettings? settings, IArchiveExtractionInteraction? interaction, bool smart = false, string? folderName = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var destination = ArchivePaths.ResolveUserPath(destinationDirectory);
        EnsureNoLinks(destination);
        if (!Directory.Exists(destination)) throw new DirectoryNotFoundException(destination);

        await using var source = OpenRead(archivePath);
        var allEntries = await ReadPlan(source, password, cancellationToken).ConfigureAwait(false);
        var entries = MapExtractionPaths(SelectEntries(allEntries, entryPaths), settings ?? new());

        var tree = BuildTree(entries);
        var roots = tree.Children.OrderBy(node => node.Name, StringComparer.Ordinal).ToArray();
        var wrapper = folderName is not null || smart && roots.Length > 1;
        var output = wrapper
            ? Path.Combine(destination, folderName ?? ArchivePaths.GetArchiveBaseName(archivePath))
            : destination;

        var plan = new ExtractionPlan();
        plan.Directories[""] = (destination, null);
        foreach (var entry in entries.Where(entry => !entry.Info.IsDirectory))
            plan.Files[entry.RelativePath] = new(null);

        var reserved = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var node in Enumerate(tree))
            if (node.RelativePath.Length > 0) reserved.Add(Path.Combine(output, ToNative(node.RelativePath)));
        if (wrapper) reserved.Add(output);
        var skippedPrefixes = new List<string>();
        var remembered = new Dictionary<(bool IncomingDirectory, bool ExistingDirectory), ArchiveConflictAction>();
        int replaced = 0, skipped = 0, renamed = 0;

        var tracker = new ArchiveProgressTracker(progress, entries.Count,
            entries.Where(entry => !entry.Info.IsDirectory).Sum(entry => Math.Max(0, entry.Info.Size)), cancellationToken);
        var packedEntries = entries.Where(entry => !entry.Info.IsDirectory).ToArray();
        if (packedEntries.All(entry => entry.Info.CompressedSize is > 0 || entry.Info.Size == 0 && entry.Info.CompressedSize == 0))
            tracker.Packed(packedEntries.Sum(entry => entry.Info.CompressedSize!.Value));
        tracker.Report();

        if (wrapper)
            output = await PlaceDirectoryAsync(tree, output, cancellationToken).ConfigureAwait(false) ?? destination;
        else
            foreach (var root in roots.Where(root => root.IsDirectory))
                await PlaceDirectoryAsync(root, Path.Combine(destination, root.Name), cancellationToken).ConfigureAwait(false);

        // Destination file symlinks are a pre-check like the directory planning above: reject the
        // whole extraction before the first write instead of leaving earlier entries behind.
        foreach (var entry in entries.Where(entry => !entry.Info.IsDirectory))
            if (FileTarget(entry.RelativePath) is { } target) EnsureNoLinks(target);

        await ApplyPlanAsync(source, entries, plan, password, tracker, cancellationToken,
            ResolveFileAsync).ConfigureAwait(false);
        interaction?.ReportExtractionSummary(new(replaced, skipped, renamed));
        return output;

        void Reserve(VirtualNode node, string target)
        {
            reserved.Add(target);
            foreach (var child in node.Children)
            {
                var childTarget = Path.Combine(target, child.Name);
                reserved.Add(childTarget);
                if (child.IsDirectory) Reserve(child, childTarget);
            }
        }

        bool IsSkipped(string relativePath) => skippedPrefixes.Any(prefix =>
            prefix.Length == 0 || relativePath.Equals(prefix, StringComparison.Ordinal) ||
            relativePath.StartsWith(prefix + "/", StringComparison.Ordinal));

        string NextName(string target, bool directory)
        {
            var parent = Path.GetDirectoryName(target)!;
            var extension = directory ? "" : Path.GetExtension(target);
            var name = directory ? Path.GetFileName(target) : Path.GetFileNameWithoutExtension(target);
            var index = 2;
            string candidate;
            do { candidate = Path.Combine(parent, $"{name} ({index++}){extension}"); }
            while (reserved.Contains(candidate) || File.Exists(candidate) || Directory.Exists(candidate));
            reserved.Add(candidate);
            return candidate;
        }

        async Task<ArchiveConflictDecision> Decide(ArchiveExtractionConflict conflict,
            (bool IncomingDirectory, bool ExistingDirectory) kind, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (remembered.TryGetValue(kind, out var action)) return new(action);
            var decision = await interaction!.ResolveConflictAsync(conflict, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (decision.ApplyToAll) remembered[kind] = decision.Action;
            if (decision.Action == ArchiveConflictAction.Cancel) throw new OperationCanceledException(token);
            return decision;
        }

        async Task<string?> PlaceDirectoryAsync(VirtualNode node, string target, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            EnsureNoLinks(target);
            var existingDirectory = Directory.Exists(target);
            var existingFile = File.Exists(target);
            if (existingFile && !existingDirectory)
            {
                if (interaction is null)
                    throw new IOException($"目标位置已有同名项目：{target}\n本次未写入任何文件，已有内容保持不变。请选择其他输出位置。");
                var conflict = new ArchiveExtractionConflict(node.RelativePath, target, true, false, null,
                    new FileInfo(target).Length, default, File.GetLastWriteTimeUtc(target));
                var decision = await Decide(conflict, (true, false), token).ConfigureAwait(false);
                if (decision.Action == ArchiveConflictAction.Skip)
                {
                    skipped++;
                    skippedPrefixes.Add(node.RelativePath);
                    return null;
                }
                if (decision.Action != ArchiveConflictAction.KeepBoth)
                    throw new IOException("文件与文件夹类型不同，不能直接替换。请选择“跳过”或“保留两者”。");
                target = NextName(target, directory: true);
                renamed++;
            }
            plan.Directories[node.RelativePath] = (target, node.Entry?.Info.LastModified);
            Reserve(node, target);
            foreach (var child in node.Children.Where(child => child.IsDirectory))
                await PlaceDirectoryAsync(child, Path.Combine(target, child.Name), token).ConfigureAwait(false);
            return target;
        }

        // Natural target for a file entry; null when its parent folder was skipped.
        string? FileTarget(string relative)
        {
            var separator = relative.LastIndexOf('/');
            var parent = separator < 0 ? "" : relative[..separator];
            if (IsSkipped(parent)) return null;
            var parentTarget = plan.Directories.TryGetValue(parent, out var directory) ? directory.Target : destination;
            return Path.Combine(parentTarget, Path.GetFileName(ToNative(relative)));
        }

        async ValueTask ResolveFileAsync(PlannedEntry entry, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var relative = entry.RelativePath;
            if (FileTarget(relative) is not { } target)
            {
                plan.Files[relative] = new(null);
                return;
            }
            EnsureNoLinks(target);
            var existingDirectory = Directory.Exists(target);
            var existingFile = File.Exists(target);
            if (!existingDirectory && !existingFile)
            {
                plan.Files[relative] = new(target);
                return;
            }
            if (interaction is null)
                throw new IOException($"目标位置已有同名项目：{target}\n本次未写入任何文件，已有内容保持不变。请选择其他输出位置。");

            var conflict = new ArchiveExtractionConflict(relative, target, false, existingDirectory,
                entry.Info.Size, existingFile ? new FileInfo(target).Length : null,
                entry.Info.LastModified?.ToUniversalTime() ?? default, File.GetLastWriteTimeUtc(target));
            var decision = await Decide(conflict, (false, existingDirectory), token).ConfigureAwait(false);
            switch (decision.Action)
            {
                case ArchiveConflictAction.Skip:
                    skipped++;
                    plan.Files[relative] = new(null);
                    break;
                case ArchiveConflictAction.KeepBoth:
                    plan.Files[relative] = new(NextName(target, directory: false));
                    renamed++;
                    break;
                case ArchiveConflictAction.Replace when conflict.CanReplace:
                    plan.Files[relative] = new(target, Overwrite: true);
                    replaced++;
                    break;
                default:
                    throw new IOException("文件与文件夹类型不同，不能直接替换。请选择“跳过”或“保留两者”。");
            }
        }

        static IEnumerable<VirtualNode> Enumerate(VirtualNode node)
        {
            yield return node;
            foreach (var child in node.Children)
                foreach (var descendant in Enumerate(child))
                    yield return descendant;
        }

        static VirtualNode BuildTree(IReadOnlyList<PlannedEntry> entries)
        {
            var root = new VirtualNode("", "", true, null);
            foreach (var entry in entries)
            {
                var parts = entry.RelativePath.Split('/');
                var node = root;
                var relative = "";
                for (var index = 0; index < parts.Length; index++)
                {
                    relative = index == 0 ? parts[index] : relative + "/" + parts[index];
                    var last = index == parts.Length - 1;
                    var child = node.Children.FirstOrDefault(item => item.Name == parts[index]);
                    if (child is null)
                    {
                        child = new VirtualNode(parts[index], relative, last ? entry.Info.IsDirectory : true, null);
                        node.Children.Add(child);
                    }
                    if (last) { child.Entry = entry; child.IsDirectory = entry.Info.IsDirectory; }
                    node = child;
                }
            }
            return root;
        }
    }

    private sealed class VirtualNode(string name, string relativePath, bool isDirectory, PlannedEntry? entry)
    {
        public string Name { get; } = name;
        public string RelativePath { get; } = relativePath;
        public bool IsDirectory { get; set; } = isDirectory;
        public PlannedEntry? Entry { get; set; } = entry;
        public List<VirtualNode> Children { get; } = [];
    }
}
