namespace BubuZip.Core;

public sealed partial class ArchiveFileService
{
    /// <summary>Extract beside an archive without overwriting or merging existing top-level items.</summary>
    public Task ExtractHereAsync(string archivePath, string destinationDirectory, string? password = null,
        IProgress<ArchiveOperationProgress>? progress = null, CancellationToken cancellationToken = default,
        ArchiveExtractSettings? settings = null) => Task.Run(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var destination = ArchivePaths.ResolveUserPath(destinationDirectory);
        EnsureNoLinks(destination);
        if (!Directory.Exists(destination)) throw new DirectoryNotFoundException(destination);
        var staging = Path.Combine(destination, ".bubuzip-" + Guid.NewGuid().ToString("N"));
        var payload = Path.Combine(staging, "payload");
        var committed = new Stack<(string Source, string Target, bool Directory)>();
        var preserveStaging = false;
        try
        {
            Directory.CreateDirectory(staging);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(staging, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await ExtractAsync(archivePath, payload, password, progress: progress,
                cancellationToken: cancellationToken, settings: settings).ConfigureAwait(false);
            var entries = Directory.EnumerateFileSystemEntries(payload).Select(path =>
                (Source: path, Target: Path.Combine(destination, Path.GetFileName(path)), Directory: Directory.Exists(path))).ToArray();
            // Check every top-level item before publishing any of them.
            foreach (var entry in entries)
            {
                EnsureNoLinks(entry.Target);
                if (File.Exists(entry.Target) || Directory.Exists(entry.Target))
                    throw new IOException($"已存在同名项目，未覆盖或合并：{Path.GetFileName(entry.Target)}");
            }
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureNoLinks(destination);
                if (entry.Directory) Directory.Move(entry.Source, entry.Target);
                else File.Move(entry.Source, entry.Target, overwrite: false);
                committed.Push(entry);
            }
        }
        catch (Exception original)
        {
            var rollbackFailures = new List<Exception>();
            while (committed.TryPop(out var entry))
            {
                try
                {
                    EnsureNoLinks(entry.Target);
                    EnsureNoLinks(Path.GetDirectoryName(entry.Source)!);
                    if (entry.Directory) Directory.Move(entry.Target, entry.Source);
                    else File.Move(entry.Target, entry.Source, overwrite: false);
                }
                catch (Exception rollback) { rollbackFailures.Add(rollback); }
            }
            if (rollbackFailures.Count > 0)
            {
                preserveStaging = true;
                throw new IOException($"提取未完全完成，已保留相关文件。暂存内容位于：{staging}",
                    new AggregateException(new[] { original }.Concat(rollbackFailures)));
            }
            throw;
        }
        finally
        {
            if (!preserveStaging && Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }, cancellationToken);
}
