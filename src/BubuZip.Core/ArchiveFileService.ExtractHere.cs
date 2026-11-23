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

    private async Task<string> ExtractIntoExistingAsync(string archivePath, string destinationDirectory, string? password,
        IReadOnlyList<string>? entryPaths, IProgress<ArchiveOperationProgress>? progress, CancellationToken cancellationToken,
        ArchiveExtractSettings? settings, IArchiveExtractionInteraction? interaction, bool smart = false, string? folderName = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var destination = ArchivePaths.ResolveUserPath(destinationDirectory);
        EnsureNoLinks(destination);
        if (!Directory.Exists(destination)) throw new DirectoryNotFoundException(destination);
        var staging = Path.Combine(destination, ".bubuzip-" + Guid.NewGuid().ToString("N"));
        var payload = Path.Combine(staging, "payload");
        var undo = new Stack<(string Source, string Target, bool Directory)>();
        var preserveStaging = false;
        var placements = new List<ExtractionPlacement>();
        var remembered = new Dictionary<(bool IncomingDirectory, bool ExistingDirectory), ArchiveConflictAction>();
        var reserved = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        int replaced = 0, skipped = 0, renamed = 0;
        try
        {
            Directory.CreateDirectory(staging);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(staging, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await ExtractAsync(archivePath, payload, password, entryPaths, progress,
                cancellationToken, settings).ConfigureAwait(false);
            var roots = Directory.EnumerateFileSystemEntries(payload).Order(StringComparer.Ordinal).ToArray();
            var output = destination;
            if (folderName is not null || smart && roots.Length > 1)
            {
                var name = folderName ?? ArchivePaths.GetArchiveBaseName(archivePath);
                output = Path.Combine(destination, name);
            }
            foreach (var path in Directory.EnumerateFileSystemEntries(payload, "*", SearchOption.AllDirectories))
                reserved.Add(Path.Combine(output, Path.GetRelativePath(payload, path)));

            // Resolve every conflict before modifying any existing destination file.
            if (output != destination) output = await Plan(payload, output).ConfigureAwait(false) ?? destination;
            else foreach (var path in roots)
                await Plan(path, Path.Combine(destination, Path.GetFileName(path))).ConfigureAwait(false);

            var backups = Path.Combine(staging, "backups");
            Directory.CreateDirectory(backups);
            foreach (var placement in placements)
            {
                cancellationToken.ThrowIfCancellationRequested();
                (progress as IArchiveOperationControl)?.Checkpoint(cancellationToken);
                EnsureNoLinks(placement.Target);
                if (placement.ExistingSize is { } size)
                {
                    var current = new FileInfo(placement.Target);
                    if (!current.Exists || current.Length != size || current.LastWriteTimeUtc != placement.ExistingModified)
                        throw new IOException($"等待处理期间，目标文件已发生变化。本次解压已撤销，请重试：{placement.Target}");
                    var backup = Path.Combine(backups, Guid.NewGuid().ToString("N"));
                    File.Move(placement.Target, backup, overwrite: false);
                    undo.Push((backup, placement.Target, false));
                }
                if (placement.Directory) Directory.Move(placement.Source, placement.Target);
                else File.Move(placement.Source, placement.Target, overwrite: false);
                undo.Push((placement.Target, placement.Source, placement.Directory));
            }
            interaction?.ReportExtractionSummary(new(replaced, skipped, renamed));
            return output;
        }
        catch (Exception original)
        {
            var rollbackFailures = new List<Exception>();
            while (undo.TryPop(out var entry))
            {
                try
                {
                    EnsureNoLinks(entry.Target);
                    EnsureNoLinks(Path.GetDirectoryName(entry.Source)!);
                    if (entry.Directory) Directory.Move(entry.Source, entry.Target);
                    else File.Move(entry.Source, entry.Target, overwrite: false);
                }
                catch (Exception rollback) { rollbackFailures.Add(rollback); }
            }
            if (rollbackFailures.Count > 0)
            {
                preserveStaging = true;
                throw new IOException($"解压未完成，部分文件无法恢复。已保留原文件备份和解压内容，请查看：{staging}",
                    new AggregateException(new[] { original }.Concat(rollbackFailures)));
            }
            throw;
        }
        finally
        {
            if (!preserveStaging && Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }

        async Task<string?> Plan(string source, string target)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureNoLinks(target);
            var incomingDirectory = Directory.Exists(source);
            var existingDirectory = Directory.Exists(target);
            var existingFile = File.Exists(target);
            if (!existingDirectory && !existingFile)
            {
                placements.Add(new(source, target, incomingDirectory));
                return target;
            }
            if (incomingDirectory && existingDirectory)
            {
                foreach (var child in Directory.EnumerateFileSystemEntries(source).Order(StringComparer.Ordinal))
                    await Plan(child, Path.Combine(target, Path.GetFileName(child))).ConfigureAwait(false);
                return target;
            }
            if (interaction is null)
                throw new IOException($"目标位置已有同名项目：{target}\n本次未写入任何文件，已有内容保持不变。请选择其他输出位置。");
            var conflict = new ArchiveExtractionConflict(Path.GetRelativePath(payload, source), target,
                incomingDirectory, existingDirectory, incomingDirectory ? null : new FileInfo(source).Length,
                existingFile ? new FileInfo(target).Length : null,
                File.GetLastWriteTimeUtc(source), File.GetLastWriteTimeUtc(target));
            var kind = (incomingDirectory, existingDirectory);
            ArchiveConflictDecision decision;
            if (remembered.TryGetValue(kind, out var action)) decision = new(action);
            else decision = await interaction.ResolveConflictAsync(conflict, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (decision.Action == ArchiveConflictAction.Cancel) throw new OperationCanceledException(cancellationToken);
            if (decision.ApplyToAll) remembered[kind] = decision.Action;
            switch (decision.Action)
            {
                case ArchiveConflictAction.Skip:
                    skipped++;
                    return null;
                case ArchiveConflictAction.KeepBoth:
                    var parent = Path.GetDirectoryName(target)!;
                    var extension = incomingDirectory ? "" : Path.GetExtension(target);
                    var name = incomingDirectory ? Path.GetFileName(target) : Path.GetFileNameWithoutExtension(target);
                    var index = 2;
                    string candidate;
                    do { candidate = Path.Combine(parent, $"{name} ({index++}){extension}"); }
                    while (reserved.Contains(candidate) || File.Exists(candidate) || Directory.Exists(candidate));
                    reserved.Add(candidate);
                    placements.Add(new(source, candidate, incomingDirectory));
                    renamed++;
                    return candidate;
                case ArchiveConflictAction.Replace when conflict.CanReplace:
                    placements.Add(new(source, target, false, conflict.ExistingSize, conflict.ExistingModified));
                    replaced++;
                    return target;
                default:
                    throw new IOException("文件与文件夹类型不同，不能直接替换。请选择“跳过”或“保留两者”。");
            }
        }
    }

    private sealed record ExtractionPlacement(string Source, string Target, bool Directory,
        long? ExistingSize = null, DateTime? ExistingModified = null);
}
