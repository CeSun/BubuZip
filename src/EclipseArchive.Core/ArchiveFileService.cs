using System.Text;

namespace EclipseArchive.Core;

/// <summary>Filesystem policy for archive operations. Outputs are committed only after success.</summary>
public sealed class ArchiveFileService(IArchiveEngine engine)
{
    private readonly IArchiveEngine _engine = engine ?? throw new ArgumentNullException(nameof(engine));

    public Task CreateAsync(IReadOnlyList<string> sourcePaths, string destinationPath, ArchiveFormat format,
        string? password = null, bool encryptFileNames = false,
        IProgress<ArchiveOperationProgress>? progress = null, CancellationToken cancellationToken = default) =>
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

            var completed = new HashSet<string>(StringComparer.Ordinal);
            void Complete(string name)
            {
                lock (completed)
                    if (completed.Add(name))
                        progress?.Report(new(name, completed.Count, inputs.Count));
            }
            progress?.Report(new("", 0, inputs.Count));
            var streams = new List<SourceFileStream>();
            var temporary = TemporarySibling(destination);
            try
            {
                var entries = inputs.Select(input =>
                {
                    SourceFileStream? stream = null;
                    if (!input.IsDirectory)
                    {
                        stream = new SourceFileStream(input.FilePath, () => Complete(input.EntryPath));
                        streams.Add(stream);
                    }
                    return new ArchiveWriteEntry(input.EntryPath, stream, input.LastModified);
                }).ToArray();
                await using (var output = OpenWrite(temporary))
                    await _engine.CreateAsync(output, entries, format, password, encryptFileNames, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                EnsureNoLinks(Path.GetDirectoryName(destination)!);
                File.Move(temporary, destination, overwrite: false);
                foreach (var input in inputs) Complete(input.EntryPath);
            }
            finally
            {
                foreach (var stream in streams) stream.Dispose();
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }, cancellationToken);

    public Task ExtractAsync(string archivePath, string destinationDirectory, string? password = null,
        IReadOnlyList<string>? entryPaths = null, IProgress<ArchiveOperationProgress>? progress = null,
        CancellationToken cancellationToken = default) => Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = ValidateNewDestination(destinationDirectory);
            await using var source = OpenRead(archivePath);
            var allEntries = await ReadPlan(source, password, cancellationToken).ConfigureAwait(false);
            var entries = SelectEntries(allEntries, entryPaths);
            var temporary = TemporarySibling(destination);
            progress?.Report(new("", 0, entries.Count));
            try
            {
                Directory.CreateDirectory(temporary);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                var completed = 0;
                foreach (var entry in entries.Where(entry => entry.Info.IsDirectory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var target = Path.Combine(temporary, entry.RelativePath);
                    EnsureNoLinks(Path.GetDirectoryName(target)!);
                    Directory.CreateDirectory(target);
                    progress?.Report(new(entry.RelativePath, ++completed, entries.Count));
                }
                var files = entries.Where(entry => !entry.Info.IsDirectory).ToArray();
                Stream OpenTarget(PlannedEntry entry)
                {
                    var target = Path.Combine(temporary, entry.RelativePath);
                    EnsureNoLinks(Path.GetDirectoryName(target)!);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    return OpenWrite(target);
                }
                void Complete(PlannedEntry entry)
                {
                    if (entry.Info.LastModified is { } modified)
                        File.SetLastWriteTimeUtc(Path.Combine(temporary, entry.RelativePath), modified.ToUniversalTime());
                    progress?.Report(new(entry.RelativePath, ++completed, entries.Count));
                }
                if (_engine is IBatchArchiveEngine batch)
                {
                    var byKey = files.ToDictionary(entry => EntryKey(entry.Info.EntryPath), StringComparer.Ordinal);
                    var targets = files.Select(entry => new ArchiveExtractionTarget(entry.Info.EntryPath, () => OpenTarget(entry))).ToArray();
                    source.Position = 0;
                    await batch.ExtractEntriesAsync(source, targets, password, name => Complete(byKey[name]),
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    foreach (var entry in files)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        source.Position = 0;
                        await using (var output = OpenTarget(entry))
                            await _engine.ExtractEntryAsync(source, entry.Info.EntryPath, output, password, cancellationToken).ConfigureAwait(false);
                        Complete(entry);
                    }
                }
                foreach (var entry in entries.Where(entry => entry.Info.IsDirectory && entry.Info.LastModified is not null)
                             .OrderByDescending(entry => entry.RelativePath.Length))
                    Directory.SetLastWriteTimeUtc(Path.Combine(temporary, entry.RelativePath), entry.Info.LastModified!.Value.ToUniversalTime());
                cancellationToken.ThrowIfCancellationRequested();
                EnsureNoLinks(Path.GetDirectoryName(destination)!);
                Directory.Move(temporary, destination);
            }
            finally
            {
                if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
            }
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
            var files = entries.Where(entry => !entry.Info.IsDirectory).ToArray();
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

    private async Task<List<PlannedEntry>> ReadPlan(Stream source, string? password, CancellationToken cancellationToken)
    {
        var metadata = await _engine.ListAsync(source, password, cancellationToken).ConfigureAwait(false);
        var entries = metadata.Select(entry =>
        {
            if (entry.IsSymbolicLink || entry.LinkTarget is not null)
                throw new NotSupportedException($"Archive links are not supported: {entry.EntryPath}");
            return new PlannedEntry(entry, NormalizeEntryPath(entry.EntryPath));
        }).ToList();
        ValidateEntryTree(entries.Select(entry => (entry.RelativePath, entry.Info.IsDirectory)));
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
        Path.Combine(Path.GetDirectoryName(destination)!, ".eclipsearchive-" + Guid.NewGuid().ToString("N"));

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
        if (normalized.Length == 0 || normalized.StartsWith('/') || normalized.Contains('\0') ||
            (normalized.Length > 1 && normalized[1] == ':') ||
            normalized.Split('/').Any(part => part is "" or "." or ".."))
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
    private sealed record PlannedEntry(ArchiveEntryInfo Info, string RelativePath);

    // Open one file at a time rather than holding a file descriptor for every selected file.
    private sealed class SourceFileStream(string filePath, Action completed) : Stream
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
            if (AtEnd()) return 0;
            var read = File().Read(buffer[..(int)Math.Min(buffer.Length, _length - _position)]);
            Advance(read, buffer.Length);
            return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
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
