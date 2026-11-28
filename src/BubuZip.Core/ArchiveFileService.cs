using System.Text;

namespace BubuZip.Core;

/// <summary>Filesystem policy for archive operations. Outputs are committed only after success.</summary>
public sealed partial class ArchiveFileService(IArchiveEngine engine)
{
    private readonly IArchiveEngine _engine = engine ?? throw new ArgumentNullException(nameof(engine));

    public Task CreateAsync(IReadOnlyList<string> sourcePaths, string destinationPath, ArchiveFormat format,
        string? password = null, bool encryptFileNames = false,
        IProgress<ArchiveOperationProgress>? progress = null, CancellationToken cancellationToken = default,
        ArchiveCreateSettings? settings = null) =>
        Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(sourcePaths);
            if (sourcePaths.Count == 0) throw new ArgumentException("Choose at least one source file or folder.", nameof(sourcePaths));
            var destination = ValidateNewDestination(destinationPath);
            var inputs = new List<SourceEntry>();
            foreach (var sourcePath in sourcePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fullPath = ArchivePaths.ResolveUserPath(sourcePath);
                EnsureNoLinks(fullPath);
                var attributes = File.GetAttributes(fullPath);
                if ((attributes & FileAttributes.Directory) != 0 && IsWithin(destination, fullPath))
                    throw new IOException("The archive destination cannot be inside a source folder.");
                var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(fullPath));
                if (name.Length == 0) throw new IOException("Archiving a filesystem root is not supported.");
                AddSource(fullPath, name, inputs, cancellationToken);
            }
            ValidateEntryTree(inputs.Select(input => (input.EntryPath, input.IsDirectory)));

            var tracker = new ArchiveProgressTracker(progress, inputs.Count,
                inputs.Where(input => !input.IsDirectory).Sum(input => new FileInfo(input.FilePath).Length), cancellationToken);
            tracker.Report();
            var streams = new List<SourceFileStream>();
            var temporary = TemporarySibling(destination);
            try
            {
                var entries = inputs.Select(input =>
                {
                    SourceFileStream? stream = null;
                    if (!input.IsDirectory)
                    {
                        stream = new SourceFileStream(input.FilePath, () => tracker.Complete(input.EntryPath),
                            tracker.Checkpoint, position => tracker.Advance(input.EntryPath, position));
                        streams.Add(stream);
                    }
                    return new ArchiveWriteEntry(input.EntryPath, stream, input.LastModified);
                }).ToArray();
                await using (var output = new ProgressWriteStream(temporary, tracker.Checkpoint, tracker.Packed))
                {
                    if (settings is not null && _engine is IConfigurableArchiveEngine configurable)
                        await configurable.CreateWithSettingsAsync(output, entries, format, password, encryptFileNames, settings, cancellationToken).ConfigureAwait(false);
                    else if (settings is null || settings == new ArchiveCreateSettings())
                        await _engine.CreateAsync(output, entries, format, password, encryptFileNames, cancellationToken).ConfigureAwait(false);
                    else throw new NotSupportedException("当前压缩引擎不支持所选压缩选项。");
                }
                foreach (var input in inputs) tracker.Complete(input.EntryPath);
                cancellationToken.ThrowIfCancellationRequested();
                EnsureNoLinks(Path.GetDirectoryName(destination)!);
                File.Move(temporary, destination, overwrite: false);
            }
            finally
            {
                foreach (var stream in streams) stream.Dispose();
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }, cancellationToken);

    public Task ExtractAsync(string archivePath, string destinationDirectory, string? password = null,
        IReadOnlyList<string>? entryPaths = null, IProgress<ArchiveOperationProgress>? progress = null,
        CancellationToken cancellationToken = default, ArchiveExtractSettings? settings = null,
        IArchiveExtractionInteraction? interaction = null) => Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (interaction is not null && Directory.Exists(ArchivePaths.ResolveUserPath(destinationDirectory)))
            {
                await ExtractIntoExistingAsync(archivePath, destinationDirectory, password, entryPaths, progress,
                    cancellationToken, settings, interaction).ConfigureAwait(false);
                return;
            }
            var destination = ValidateNewDestination(destinationDirectory);
            await using var source = OpenRead(archivePath);
            var allEntries = await ReadPlan(source, password, cancellationToken).ConfigureAwait(false);
            var entries = MapExtractionPaths(SelectEntries(allEntries, entryPaths), settings ?? new());
            var tracker = new ArchiveProgressTracker(progress, entries.Count,
                entries.Where(entry => !entry.Info.IsDirectory).Sum(entry => Math.Max(0, entry.Info.Size)), cancellationToken);
            var packedEntries = entries.Where(entry => !entry.Info.IsDirectory).ToArray();
            if (packedEntries.All(entry => entry.Info.CompressedSize is > 0 || entry.Info.Size == 0 && entry.Info.CompressedSize == 0))
                tracker.Packed(packedEntries.Sum(entry => entry.Info.CompressedSize!.Value));
            tracker.Report();
            Directory.CreateDirectory(destination);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await ApplyPlanAsync(source, entries, NewDestinationPlan(entries, destination), password, tracker,
                cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public Task TestAsync(string archivePath, string? password = null,
        IProgress<ArchiveOperationProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var source = OpenRead(archivePath);
            var entries = await ReadPlan(source, password, cancellationToken).ConfigureAwait(false);
            progress?.Report(new("", 0, entries.Count));
            var completed = 0;
            foreach (var entry in entries.Where(entry => entry.Info.IsDirectory))
                progress?.Report(new(entry.RelativePath, ++completed, entries.Count));
            // Link payloads were CRC-checked while reading the plan; never ask the
            // content extractor to treat them as ordinary files.
            foreach (var entry in entries.Where(entry => entry.LinkTargetPath is not null))
                progress?.Report(new(entry.RelativePath, ++completed, entries.Count));
            var files = entries.Where(entry => !entry.Info.IsDirectory && entry.LinkTargetPath is null).ToArray();
            if (_engine is IBatchArchiveEngine batch)
            {
                var byKey = files.ToDictionary(entry => EntryKey(entry.Info.EntryPath), StringComparer.Ordinal);
                var targets = files.Select(entry => new ArchiveExtractionTarget(entry.Info.EntryPath, () => Stream.Null)).ToArray();
                source.Position = 0;
                await batch.ExtractEntriesAsync(source, targets, password, name =>
                    progress?.Report(new(byKey[name].RelativePath, ++completed, entries.Count)), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                foreach (var entry in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    source.Position = 0;
                    await _engine.ExtractEntryAsync(source, entry.Info.EntryPath, Stream.Null, password, cancellationToken).ConfigureAwait(false);
                    progress?.Report(new(entry.RelativePath, ++completed, entries.Count));
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
        }, cancellationToken);

    /// <summary>A null target marks an entry skipped by a conflict decision.</summary>
    private sealed record ExtractionPlacement(string? Target, bool Overwrite = false);

    /// <summary>
    /// Final write instructions resolved from an archive plan against the destination filesystem.
    /// Entries stream straight to their target paths; partial output is kept on failure, like mainstream extractors.
    /// </summary>
    private sealed class ExtractionPlan
    {
        public Dictionary<string, ExtractionPlacement> Files { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, (string Target, DateTime? Modified)> Directories { get; } = new(StringComparer.Ordinal);
        public List<(string Target, DateTime? Modified)> ExtraDirectories { get; } = [];
    }

    private static ExtractionPlan NewDestinationPlan(IReadOnlyList<PlannedEntry> entries, string destination)
    {
        var plan = new ExtractionPlan();
        foreach (var entry in entries)
        {
            var target = Path.Combine(destination, ToNative(entry.RelativePath));
            if (entry.Info.IsDirectory) plan.Directories[entry.RelativePath] = (target, entry.Info.LastModified);
            else plan.Files[entry.RelativePath] = new(target);
        }
        return plan;
    }

    private static string ToNative(string path) => path.Replace('/', Path.DirectorySeparatorChar);

    /// <summary>
    /// Apply a plan by streaming each entry straight to its final path. No staging copy, no rollback:
    /// output already written before a failure stays, matching mainstream extractors.
    /// </summary>
    private async Task ApplyPlanAsync(Stream source, IReadOnlyList<PlannedEntry> entries, ExtractionPlan plan,
        string? password, ArchiveProgressTracker tracker, CancellationToken cancellationToken,
        Func<PlannedEntry, CancellationToken, ValueTask>? beforeEntry = null)
    {
        foreach (var entry in entries.Where(entry => entry.Info.IsDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (plan.Directories.TryGetValue(entry.RelativePath, out var directory))
            {
                EnsureNoLinks(directory.Target);
                Directory.CreateDirectory(directory.Target);
            }
            tracker.Complete(entry.RelativePath);
        }
        foreach (var extra in plan.ExtraDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureNoLinks(extra.Target);
            Directory.CreateDirectory(extra.Target);
            if (extra.Modified is { } modified)
                Directory.SetLastWriteTimeUtc(extra.Target, modified.ToUniversalTime());
        }

        var files = entries.Where(entry => !entry.Info.IsDirectory && entry.LinkTargetPath is null).ToArray();
        Stream OpenTarget(PlannedEntry entry)
        {
            var placement = plan.Files[entry.RelativePath];
            if (placement.Target is null) return Stream.Null;
            EnsureNoLinks(placement.Target);
            EnsureNoLinks(Path.GetDirectoryName(placement.Target)!);
            Directory.CreateDirectory(Path.GetDirectoryName(placement.Target)!);
            tracker.Advance(entry.RelativePath, 0);
            return new ProgressWriteStream(placement.Target, tracker.Checkpoint,
                position => tracker.Advance(entry.RelativePath, position), placement.Overwrite);
        }
        void Complete(PlannedEntry entry) => tracker.Complete(entry.RelativePath);
        if (_engine is IBatchArchiveEngine batch)
        {
            var byKey = files.ToDictionary(entry => EntryKey(entry.Info.EntryPath), StringComparer.Ordinal);
            var targets = files.Select(entry => new ArchiveExtractionTarget(entry.Info.EntryPath, () => OpenTarget(entry))).ToArray();
            async ValueTask Before(string name, CancellationToken token)
            {
                if (beforeEntry is null) return;
                if (byKey.TryGetValue(name, out var entry)) await beforeEntry(entry, token).ConfigureAwait(false);
            }
            source.Position = 0;
            await batch.ExtractEntriesAsync(source, targets, password, name => Complete(byKey[name]),
                cancellationToken, Before).ConfigureAwait(false);
        }
        else
        {
            foreach (var entry in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (beforeEntry is not null) await beforeEntry(entry, cancellationToken).ConfigureAwait(false);
                source.Position = 0;
                await using (var output = OpenTarget(entry))
                    await _engine.ExtractEntryAsync(source, entry.Info.EntryPath, output, password, cancellationToken).ConfigureAwait(false);
                Complete(entry);
            }
        }
        foreach (var entry in files.Where(entry => entry.Info.LastModified is not null && plan.Files[entry.RelativePath].Target is not null))
            File.SetLastWriteTimeUtc(plan.Files[entry.RelativePath].Target!, entry.Info.LastModified!.Value.ToUniversalTime());
        await CreateLinksAsync(entries, plan, tracker, cancellationToken, beforeEntry).ConfigureAwait(false);
        foreach (var pair in plan.Directories.Where(pair => pair.Value.Modified is not null)
                     .OrderByDescending(pair => pair.Value.Target.Length))
            Directory.SetLastWriteTimeUtc(pair.Value.Target, pair.Value.Modified!.Value.ToUniversalTime());
    }

    private async Task<List<PlannedEntry>> ReadPlan(Stream source, string? password, CancellationToken cancellationToken)
    {
        var metadata = await _engine.ListAsync(source, password, cancellationToken).ConfigureAwait(false);
        var entries = metadata.Select(entry =>
        {
            // Other handlers may expose hard links through LinkTarget too. Do not
            // interpret those using ZIP's relative symbolic-link semantics.
            if (entry.LinkTarget is not null && !entry.IsSymbolicLink)
                throw new NotSupportedException($"This archive link type is not supported: {entry.EntryPath}");
            if (entry.IsSymbolicLink && (entry.IsDirectory || string.IsNullOrEmpty(entry.LinkTarget)))
                throw new InvalidDataException($"Invalid archive symbolic link: {entry.EntryPath}");
            return new PlannedEntry(entry, NormalizeEntryPath(entry.EntryPath));
        }).ToList();
        ValidateEntryTree(entries.Select(entry => (entry.RelativePath, entry.Info.IsDirectory)));
        var paths = entries.ToDictionary(entry => entry.RelativePath, StringComparer.Ordinal);
        var directories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var parts = entry.RelativePath.Split('/');
            for (var index = 1; index < parts.Length; index++) directories.Add(string.Join('/', parts.Take(index)));
            if (entry.Info.IsDirectory) directories.Add(entry.RelativePath);
        }
        entries = entries.Select(entry => entry.Info.IsSymbolicLink
            ? entry with { LinkTargetPath = ResolveArchiveLink(entry, paths, directories) } : entry).ToList();
        _ = OrderLinks(entries);
        return entries;
    }

    private static string EntryKey(string path) => path.Replace('\\', '/').TrimEnd('/');

    private static List<PlannedEntry> SelectEntries(List<PlannedEntry> entries, IReadOnlyList<string>? selectedPaths)
    {
        if (selectedPaths is null) return entries;
        if (selectedPaths.Count == 0) throw new ArgumentException("Choose at least one archive entry.", nameof(selectedPaths));
        var selected = selectedPaths.Select(NormalizeEntryPath).Distinct(StringComparer.Ordinal).ToArray();
        static bool Matches(string path, string selection) => path == selection || path.StartsWith(selection + "/", StringComparison.Ordinal);
        foreach (var selection in selected)
            if (!entries.Any(entry => Matches(entry.RelativePath, selection)))
                throw new FileNotFoundException($"Archive entry was not found: {selection}", selection);
        return entries.Where(entry => selected.Any(selection => Matches(entry.RelativePath, selection))).ToList();
    }

    private static List<PlannedEntry> MapExtractionPaths(List<PlannedEntry> entries, ArchiveExtractSettings settings)
    {
        if (!Enum.IsDefined(settings.PathMode)) throw new ArgumentOutOfRangeException(nameof(settings));
        var prefix = string.IsNullOrEmpty(settings.CurrentPrefix) ? "" : NormalizeEntryPath(settings.CurrentPrefix);
        var mapped = new List<PlannedEntry>(entries.Count);
        foreach (var entry in entries)
        {
            var path = entry.RelativePath;
            if (settings.PathMode == ExtractionPathMode.CurrentFolder && prefix.Length > 0)
            {
                if (path == prefix && entry.Info.IsDirectory) continue;
                if (!path.StartsWith(prefix + "/", StringComparison.Ordinal))
                    throw new InvalidDataException("所选条目不在当前压缩包目录中。");
                path = path[(prefix.Length + 1)..];
            }
            else if (settings.PathMode == ExtractionPathMode.NoPaths)
            {
                if (entry.Info.IsDirectory) continue;
                path = path[(path.LastIndexOf('/') + 1)..];
            }
            mapped.Add(entry with { RelativePath = path });
        }
        ValidateEntryTree(mapped.Select(entry => (entry.RelativePath, entry.Info.IsDirectory)));
        var selectedPaths = mapped.ToDictionary(entry => NormalizeEntryPath(entry.Info.EntryPath),
            entry => entry.RelativePath, StringComparer.Ordinal);
        mapped = mapped.Select(entry => entry.LinkTargetPath is { } target
            ? entry with { LinkTargetPath = selectedPaths.TryGetValue(target, out var mappedTarget) ? mappedTarget
                : throw new InvalidDataException($"链接目标未包含在本次解压选择中：{entry.Info.EntryPath} → {target}") }
            : entry).ToList();
        _ = OrderLinks(mapped);
        return mapped;
    }

    private static void AddSource(string path, string entryPath, List<SourceEntry> entries, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureNoLinks(path);
        var attributes = File.GetAttributes(path);
        var isDirectory = (attributes & FileAttributes.Directory) != 0;
        entries.Add(new(path, NormalizeEntryPath(entryPath), isDirectory, File.GetLastWriteTimeUtc(path)));
        if (!isDirectory) return;
        foreach (var child in Directory.EnumerateFileSystemEntries(path).Order(StringComparer.Ordinal))
            AddSource(child, entryPath + "/" + Path.GetFileName(child), entries, cancellationToken);
    }

    private static string ValidateNewDestination(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var destination = Path.TrimEndingDirectorySeparator(ArchivePaths.ResolveUserPath(path));
        EnsureNoLinks(destination);
        if (File.Exists(destination) || Directory.Exists(destination))
            throw new IOException("The destination already exists. Choose a new path.");
        var parent = Path.GetDirectoryName(destination);
        if (parent is null || !Directory.Exists(parent))
            throw new DirectoryNotFoundException("The destination parent folder must already exist.");
        return destination;
    }

    private static string TemporarySibling(string destination) =>
        Path.Combine(Path.GetDirectoryName(destination)!, ".bubuzip-" + Guid.NewGuid().ToString("N"));

    private static FileStream OpenWrite(string path) => new(path, FileMode.CreateNew, FileAccess.Write,
        FileShare.None, 64 * 1024, FileOptions.Asynchronous);

    private static FileStream OpenRead(string path)
    {
        var fullPath = ArchivePaths.ResolveUserPath(path);
        EnsureNoLinks(fullPath);
        return new(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
    }

    private static bool IsWithin(string path, string directory) =>
        path.Equals(Path.TrimEndingDirectorySeparator(directory), StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void EnsureNoLinks(string path)
    {
        string? current = ArchivePaths.ResolveUserPath(path);
        while (current is not null)
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"Symbolic links are not supported in filesystem paths: {current}");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(current);
        }
    }

    private static string NormalizeEntryPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalized = path.Replace('\\', '/').TrimEnd('/').Normalize(NormalizationForm.FormC);
        if (normalized.Length == 0 || normalized.StartsWith('/') || normalized.Contains('\0') || normalized.Contains(':') ||
            normalized.Split('/').Any(part => part is "" or "." or ".." ||
                OperatingSystem.IsWindows() && (part.EndsWith('.') || part.EndsWith(' '))))
            throw new InvalidDataException($"Unsafe archive entry path: {path}");
        return normalized;
    }

    private static void ValidateEntryTree(IEnumerable<(string Path, bool IsDirectory)> entries)
    {
        var paths = new Dictionary<string, (string Spelling, bool IsDirectory)>(StringComparer.OrdinalIgnoreCase);
        var explicitPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, isDirectory) in entries)
        {
            var parts = path.Split('/');
            for (var index = 0; index < parts.Length; index++)
            {
                var prefix = string.Join('/', parts.Take(index + 1));
                var directory = index < parts.Length - 1 || isDirectory;
                if (paths.TryGetValue(prefix, out var existing))
                {
                    if (existing.Spelling != prefix || existing.IsDirectory != directory)
                        throw new InvalidDataException($"Archive entries have conflicting filesystem paths: {path}");
                }
                else paths.Add(prefix, (prefix, directory));
            }
            if (!explicitPaths.Add(path))
                throw new InvalidDataException($"Duplicate archive entry path: {path}");
        }
    }

    private sealed record SourceEntry(string FilePath, string EntryPath, bool IsDirectory, DateTime LastModified);
    private sealed record PlannedEntry(ArchiveEntryInfo Info, string RelativePath, string? LinkTargetPath = null);

    // Open one file at a time rather than holding a file descriptor for every selected file.
    private sealed class SourceFileStream(string filePath, Action completed, Action? checkpoint = null, Action<long>? advanced = null) : Stream
    {
        private readonly long _length = new FileInfo(filePath).Length;
        private FileStream? _file;
        private long _position;
        private bool _disposed;
        private bool _reported;
        public override bool CanRead => !_disposed;
        public override bool CanSeek => !_disposed;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _position; set => Seek(value, SeekOrigin.Begin); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            checkpoint?.Invoke();
            if (AtEnd()) return 0;
            var read = File().Read(buffer[..(int)Math.Min(buffer.Length, _length - _position)]);
            Advance(read, buffer.Length);
            return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            checkpoint?.Invoke();
            if (AtEnd()) return 0;
            var read = await File().ReadAsync(buffer[..(int)Math.Min(buffer.Length, _length - _position)], cancellationToken).ConfigureAwait(false);
            Advance(read, buffer.Length);
            return read;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        private FileStream File()
        {
            if (_file is not null) return _file;
            _file = OpenRead(filePath);
            _file.Position = _position;
            return _file;
        }
        private void Advance(int read, int requested)
        {
            if (read == 0 && requested != 0 && _position < _length)
                throw new IOException($"Source file changed while archiving: {filePath}");
            _position += read;
            advanced?.Invoke(_position);
            AtEnd();
        }
        private bool AtEnd()
        {
            if (_position < _length) return false;
            _file?.Dispose();
            _file = null;
            if (!_reported) { _reported = true; completed(); }
            return true;
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            if (position < 0 || position > _length) throw new IOException("Invalid source stream position.");
            _position = position;
            if (_file is not null) _file.Position = position;
            return position;
        }
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _file?.Dispose();
            _disposed = true;
            base.Dispose(disposing);
        }
    }
}
