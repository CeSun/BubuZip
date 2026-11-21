using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using SharpCompress.Archives;
using SharpCompress.Common.Zip;
using SharpCompress.Readers;

namespace BubuZip.Core;

/// <summary>Rebuilds editable archives in isolation, replacing the original only after validation.</summary>
public sealed class ArchiveEditService(IArchiveEngine engine, ArchiveFileService files)
{
    private readonly IArchiveEngine _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    private readonly ArchiveFileService _files = files ?? throw new ArgumentNullException(nameof(files));

    public Task AddAsync(string archivePath, IReadOnlyList<string> sourcePaths, bool replaceExisting = false,
        string? password = null, IProgress<ArchiveOperationProgress>? progress = null,
        CancellationToken cancellationToken = default, string archiveDirectory = "") =>
        EditAsync(archivePath, password, async (context, token) =>
        {
            var content = context.Content;
            ArgumentNullException.ThrowIfNull(sourcePaths);
            if (sourcePaths.Count == 0) throw new ArgumentException("请选择要添加的文件或文件夹。", nameof(sourcePaths));
            var original = ArchivePaths.ResolveUserPath(archivePath);
            var prefix = archiveDirectory.Length == 0 ? "" : NormalizePath(archiveDirectory);
            var plan = new List<SourceEntry>();
            foreach (var source in sourcePaths)
            {
                var fullPath = Path.TrimEndingDirectorySeparator(ArchivePaths.ResolveUserPath(source));
                EnsureNoLinks(fullPath);
                if (string.Equals(fullPath, original, StringComparison.OrdinalIgnoreCase) ||
                    Directory.Exists(fullPath) && IsWithin(original, fullPath))
                    throw new IOException("不能将正在编辑的压缩包或它的父目录添加到自身。");
                var name = Path.GetFileName(fullPath);
                if (name.Length == 0) throw new IOException("不能将文件系统根目录添加到压缩包。");
                AddSource(fullPath, NormalizePath(prefix.Length == 0 ? name : prefix + "/" + name), plan, token);
            }
            var existing = ReadTree(content);
            if (context.Format == ArchiveFormat.SevenZip)
                ValidateEntryPermissions(plan.Select(entry => new ArchiveEntryInfo(entry.RelativePath, 0,
                    entry.IsDirectory, false, Attributes: entry.Attributes)).ToArray());
            if (prefix.Length > 0)
            {
                var parts = prefix.Split('/');
                for (var index = 0; index < parts.Length; index++)
                {
                    var path = string.Join('/', parts.Take(index + 1));
                    if (existing.TryGetValue(path, out var old) && (old.RelativePath != path || !old.IsDirectory))
                        throw new IOException($"添加位置存在文件类型或大小写冲突：{path}");
                    existing.TryAdd(path, new(path, true));
                }
            }
            var planned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in plan)
            {
                token.ThrowIfCancellationRequested();
                if (!planned.Add(entry.RelativePath)) throw new IOException($"所选源文件包含同名项目：{entry.RelativePath}");
                if (existing.TryGetValue(entry.RelativePath, out var old))
                {
                    if (old.RelativePath != entry.RelativePath || old.IsDirectory != entry.IsDirectory)
                        throw new IOException($"文件路径存在大小写或文件类型冲突：{entry.RelativePath}");
                    if (!entry.IsDirectory && !replaceExisting)
                        throw new IOException($"压缩包内已存在同名文件：{entry.RelativePath}");
                }
                else existing.Add(entry.RelativePath, new(entry.RelativePath, entry.IsDirectory));
            }
            if (prefix.Length > 0) Directory.CreateDirectory(InTree(content, prefix));
            progress?.Report(new("添加文件", 0, plan.Count));
            for (var index = 0; index < plan.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var entry = plan[index];
                var target = InTree(content, entry.RelativePath);
                EnsureNoLinks(entry.FilePath);
                if (entry.IsDirectory) Directory.CreateDirectory(target);
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    await using (var source = new FileStream(entry.FilePath, FileMode.Open, FileAccess.Read,
                                     FileShare.Read, 64 * 1024, FileOptions.Asynchronous))
                    await using (var output = new FileStream(target, replaceExisting ? FileMode.Create : FileMode.CreateNew,
                                     FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
                        await source.CopyToAsync(output, token).ConfigureAwait(false);
                    File.SetLastWriteTimeUtc(target, entry.Modified);
                }
                if (!context.Attributes.ContainsKey(entry.RelativePath) || !entry.IsDirectory)
                {
                    context.Attributes[entry.RelativePath] = entry.Attributes;
                    context.Creators[entry.RelativePath] = OperatingSystem.IsWindows() ? (byte)0 : (byte)3;
                }
                progress?.Report(new(entry.RelativePath, index + 1, plan.Count));
            }
            foreach (var entry in plan.Where(entry => entry.IsDirectory).OrderByDescending(entry => entry.RelativePath.Length))
                Directory.SetLastWriteTimeUtc(InTree(content, entry.RelativePath), entry.Modified);
            return true;
        }, progress, cancellationToken);

    public Task DeleteAsync(string archivePath, IReadOnlyList<string> entryPaths, string? password = null,
        IProgress<ArchiveOperationProgress>? progress = null, CancellationToken cancellationToken = default) =>
        EditAsync(archivePath, password, (context, token) =>
        {
            var content = context.Content;
            var metadata = context.Metadata;
            ArgumentNullException.ThrowIfNull(entryPaths);
            if (entryPaths.Count == 0) throw new ArgumentException("请选择要删除的归档条目。", nameof(entryPaths));
            var selections = entryPaths.Select(NormalizePath).Distinct(StringComparer.Ordinal).OrderBy(path => path.Length).ToArray();
            foreach (var selection in selections) ValidateSelection(metadata, selection);
            var removed = new List<string>();
            foreach (var selection in selections)
            {
                token.ThrowIfCancellationRequested();
                if (removed.Any(parent => IsSelected(selection, parent))) continue;
                var target = InTree(content, selection);
                if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
                else File.Delete(target);
                removed.Add(selection);
            }
            return Task.FromResult(true);
        }, progress, cancellationToken);

    public Task RenameAsync(string archivePath, string entryPath, string newName, string? password = null,
        IProgress<ArchiveOperationProgress>? progress = null, CancellationToken cancellationToken = default) =>
        EditAsync(archivePath, password, (context, token) =>
        {
            var content = context.Content;
            var metadata = context.Metadata;
            var entry = NormalizePath(entryPath);
            var name = NormalizePath(newName);
            if (name.Contains('/')) throw new ArgumentException("新名称只能包含一个文件名。", nameof(newName));
            ValidateSelection(metadata, entry);
            var parent = entry.Contains('/') ? entry[..(entry.LastIndexOf('/') + 1)] : "";
            var renamed = parent + name;
            if (renamed == entry) return Task.FromResult(false);
            var tree = ReadTree(content);
            var source = InTree(content, entry);
            var destination = InTree(content, renamed);
            token.ThrowIfCancellationRequested();
            if (string.Equals(entry, renamed, StringComparison.OrdinalIgnoreCase))
            {
                // A temporary name permits case-only renames on the default macOS filesystem.
                var intermediate = Path.Combine(Path.GetDirectoryName(source)!, ".bubuzip-rename-" + Guid.NewGuid().ToString("N"));
                if (Directory.Exists(source)) { Directory.Move(source, intermediate); Directory.Move(intermediate, destination); }
                else { File.Move(source, intermediate); File.Move(intermediate, destination); }
            }
            else
            {
                if (tree.ContainsKey(renamed)) throw new IOException($"压缩包内已存在此名称：{name}");
                if (Directory.Exists(source)) Directory.Move(source, destination);
                else File.Move(source, destination);
            }
            var renamedAttributes = context.Attributes.Where(pair => IsSelected(pair.Key, entry)).ToArray();
            foreach (var pair in renamedAttributes) context.Attributes.Remove(pair.Key);
            foreach (var pair in renamedAttributes) context.Attributes[renamed + pair.Key[entry.Length..]] = pair.Value;
            var renamedCreators = context.Creators.Where(pair => IsSelected(pair.Key, entry)).ToArray();
            foreach (var pair in renamedCreators) context.Creators.Remove(pair.Key);
            foreach (var pair in renamedCreators) context.Creators[renamed + pair.Key[entry.Length..]] = pair.Value;
            return Task.FromResult(true);
        }, progress, cancellationToken);

    private Task EditAsync(string archivePath, string? password,
        Func<EditContext, CancellationToken, Task<bool>> edit,
        IProgress<ArchiveOperationProgress>? progress, CancellationToken cancellationToken) => Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
            if (password is "") throw new ArgumentException("无密码时请使用 null。", nameof(password));
            var archive = ArchivePaths.ResolveUserPath(archivePath);
            EnsureNoLinks(archive);
            byte[] originalHash;
            ArchiveFormat format;
            IReadOnlyList<ArchiveEntryInfo> metadata;
            var creators = new Dictionary<string, byte>(StringComparer.Ordinal);
            await using (var input = Read(archive))
            {
                format = await DetectFormat(input, cancellationToken).ConfigureAwait(false);
                input.Position = 0;
                originalHash = await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false);
                input.Position = 0;
                metadata = await _engine.ListAsync(input, password, cancellationToken).ConfigureAwait(false);
                if (format == ArchiveFormat.Zip)
                {
                    var headers = ZipHeaders(input, metadata.Count, cancellationToken);
                    for (var index = 0; index < metadata.Count; index++)
                        creators[NormalizePath(metadata[index].EntryPath)] = headers[index].Creator;
                }
            }
            if (format == ArchiveFormat.Zip && metadata.Any(entry => entry.IsEncrypted))
                throw new NotSupportedException("当前不能编辑加密 ZIP，以免重写后丢失密码保护。");
            if (format == ArchiveFormat.SevenZip && metadata.Any(entry => entry.IsEncrypted) && password is null)
                throw new InvalidOperationException("编辑此 7z 压缩包需要输入密码。");
            // ZIP attributes can be restored through the framework writer; 7z's writer has no equivalent API.
            if (format == ArchiveFormat.SevenZip) ValidateEntryPermissions(metadata);
            if (format == ArchiveFormat.Zip)
            {
                await using var input = Read(archive);
                await RejectZipComments(input, cancellationToken).ConfigureAwait(false);
            }
            var outputPassword = format == ArchiveFormat.SevenZip ? password : null;
            UnixFileMode? permissions = OperatingSystem.IsWindows() ? null : File.GetUnixFileMode(archive);
            var working = Path.Combine(Path.GetDirectoryName(archive)!, ".bubuzip-edit-" + Guid.NewGuid().ToString("N"));
            var content = Path.Combine(working, "content");
            var rebuilt = Path.Combine(working, "updated.archive");
            var committed = false;
            try
            {
                Directory.CreateDirectory(working);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(working, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                await _files.ExtractAsync(archive, content, password, progress: progress, cancellationToken: cancellationToken).ConfigureAwait(false);
                var attributes = metadata.ToDictionary(entry => NormalizePath(entry.EntryPath), entry => entry.Attributes, StringComparer.Ordinal);
                var context = new EditContext(content, metadata, format, attributes, creators);
                if (!await edit(context, cancellationToken).ConfigureAwait(false)) return;
                cancellationToken.ThrowIfCancellationRequested();
                var sources = Directory.GetFileSystemEntries(content);
                if (sources.Length > 0)
                    await _files.CreateAsync(sources, rebuilt, format, outputPassword, outputPassword is not null,
                        progress, cancellationToken).ConfigureAwait(false);
                else
                {
                    await using var output = new FileStream(rebuilt, FileMode.CreateNew, FileAccess.Write,
                        FileShare.None, 64 * 1024, FileOptions.Asynchronous);
                    await _engine.CreateAsync(output, [], format, outputPassword, outputPassword is not null, cancellationToken).ConfigureAwait(false);
                }
                if (format == ArchiveFormat.Zip) RestoreZipAttributes(rebuilt, context, cancellationToken);
                await _files.TestAsync(rebuilt, outputPassword, progress, cancellationToken).ConfigureAwait(false);
                await ValidateRebuiltTree(rebuilt, content, outputPassword, cancellationToken).ConfigureAwait(false);
                // Remove extracted data while cancellation and errors can still leave the original untouched.
                Directory.Delete(content, recursive: true);
                EnsureNoLinks(archive);
                await using (var current = Read(archive))
                {
                    var currentHash = await SHA256.HashDataAsync(current, cancellationToken).ConfigureAwait(false);
                    if (!CryptographicOperations.FixedTimeEquals(originalHash, currentHash))
                        throw new IOException("压缩包已被其他程序更改，已停止更新以保留外部修改。");
                }
                if (permissions is { } mode && !OperatingSystem.IsWindows()) File.SetUnixFileMode(rebuilt, mode);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(rebuilt, archive, overwrite: true);
                committed = true;
                // No cancellation checks or fallible progress callbacks after the commit boundary.
            }
            finally
            {
                if (Directory.Exists(working))
                {
                    if (!committed) Directory.Delete(working, recursive: true);
                    else
                    {
                        // Failure to remove an empty bookkeeping directory must not report a committed edit as failed.
                        try { Directory.Delete(working); }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                }
            }
        }, cancellationToken);

    private static void RestoreZipAttributes(string rebuilt, EditContext context, CancellationToken token)
    {
        string[] names;
        using (var input = Read(rebuilt))
        using (var archive = new System.IO.Compression.ZipArchive(input, System.IO.Compression.ZipArchiveMode.Read))
            names = archive.Entries.Select(entry => NormalizePath(entry.FullName)).ToArray();
        // These fixed central-directory fields do not affect compressed data or local headers.
        // Preserve the creator OS along with attributes: DOS high words are not POSIX modes.
        using var updated = new FileStream(rebuilt, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var headers = ZipHeaders(updated, names.Length, token);
        Span<byte> rawAttributes = stackalloc byte[4];
        for (var index = 0; index < headers.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var path = names[index];
            var source = InTree(context.Content, path);
            var attributes = context.Attributes.TryGetValue(path, out var original) && original.HasValue
                ? original.Value : FilesystemAttributes(source, Directory.Exists(source));
            updated.Position = headers[index].Position + 38;
            BinaryPrimitives.WriteInt32LittleEndian(rawAttributes, attributes);
            updated.Write(rawAttributes);
            updated.Position = headers[index].Position + 5;
            updated.WriteByte(context.Creators.TryGetValue(path, out var creator)
                ? creator : OperatingSystem.IsWindows() ? (byte)0 : (byte)3);
        }
    }

    private static List<(long Position, byte Creator)> ZipHeaders(Stream source, int expectedCount, CancellationToken token)
    {
        if (source.Length < 22) throw new InvalidDataException("ZIP 结束记录不完整。");
        var tail = new byte[(int)Math.Min(source.Length, 22L + ushort.MaxValue)];
        source.Position = source.Length - tail.Length;
        source.ReadExactly(tail);
        var end = -1;
        for (var index = tail.Length - 22; index >= 0; index--)
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(index)) == 0x06054B50u &&
                BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(index + 20)) == tail.Length - index - 22)
            { end = index; break; }
        if (end < 0) throw new InvalidDataException("无法定位 ZIP 结束记录。");
        var endPosition = source.Length - tail.Length + end;
        var count = (ulong)BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(end + 10));
        var offset = (ulong)BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(end + 16));
        if (BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(end + 4)) != 0 ||
            BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(end + 6)) != 0)
            throw new NotSupportedException("当前不能修改分卷 ZIP 压缩包。");
        if (count == ushort.MaxValue || offset == uint.MaxValue)
        {
            if (endPosition < 20) throw new InvalidDataException("ZIP64 定位记录不完整。");
            var locator = new byte[20]; source.Position = endPosition - 20; source.ReadExactly(locator);
            if (BinaryPrimitives.ReadUInt32LittleEndian(locator) != 0x07064B50u ||
                BinaryPrimitives.ReadUInt32LittleEndian(locator.AsSpan(4)) != 0 ||
                BinaryPrimitives.ReadUInt32LittleEndian(locator.AsSpan(16)) != 1)
                throw new InvalidDataException("不支持此 ZIP64 定位记录。");
            var extendedPosition = BinaryPrimitives.ReadUInt64LittleEndian(locator.AsSpan(8));
            if (extendedPosition > (ulong)Math.Max(0, source.Length - 56)) throw new InvalidDataException("ZIP64 目录位置无效。");
            var extended = new byte[56]; source.Position = (long)extendedPosition; source.ReadExactly(extended);
            if (BinaryPrimitives.ReadUInt32LittleEndian(extended) != 0x06064B50u ||
                BinaryPrimitives.ReadUInt32LittleEndian(extended.AsSpan(16)) != 0 ||
                BinaryPrimitives.ReadUInt32LittleEndian(extended.AsSpan(20)) != 0)
                throw new InvalidDataException("不支持此 ZIP64 目录。");
            count = BinaryPrimitives.ReadUInt64LittleEndian(extended.AsSpan(32));
            offset = BinaryPrimitives.ReadUInt64LittleEndian(extended.AsSpan(48));
        }
        if (count != (ulong)expectedCount || offset > (ulong)source.Length)
            throw new InvalidDataException("ZIP 中央目录与归档条目不一致。");
        var result = new List<(long Position, byte Creator)>(expectedCount);
        source.Position = (long)offset;
        var header = new byte[46];
        for (var index = 0; index < expectedCount; index++)
        {
            token.ThrowIfCancellationRequested();
            var position = source.Position;
            if (position > endPosition - header.Length) throw new InvalidDataException("ZIP 中央目录不完整。");
            source.ReadExactly(header);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x02014B50u)
                throw new InvalidDataException("ZIP 中央目录签名无效。");
            result.Add((position, header[5]));
            source.Position += BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28)) +
                BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(30)) + BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(32));
            if (source.Position > endPosition) throw new InvalidDataException("ZIP 条目元数据长度无效。");
        }
        return result;
    }

    private async Task ValidateRebuiltTree(string rebuilt, string content, string? password, CancellationToken token)
    {
        var expected = ReadTree(content).Keys.ToHashSet(StringComparer.Ordinal);
        await using var source = Read(rebuilt);
        var metadata = await _engine.ListAsync(source, password, token).ConfigureAwait(false);
        var actual = metadata.Select(entry => NormalizePath(entry.EntryPath)).ToHashSet(StringComparer.Ordinal);
        if (!expected.SetEquals(actual))
            throw new IOException("重建后的归档路径与原文件树不一致，已停止更新以保护原始内容。");
    }

    private static void ValidateEntryPermissions(IReadOnlyList<ArchiveEntryInfo> entries)
    {
        foreach (var entry in entries)
        {
            if (entry.Attributes is not int raw) continue;
            var attributes = unchecked((uint)raw);
            // Only inspect the POSIX permission bits when the high word has a recognizable Unix file type.
            var unixMode = attributes >> 16;
            var unixType = unixMode & 0xF000u;
            if (unixType is 0x8000u or 0x4000u)
            {
                var expectedType = entry.IsDirectory ? 0x4000u : 0x8000u;
                var expectedPermissions = entry.IsDirectory ? 0x1EDu : 0x1A4u; // 0755 / 0644
                if (unixType != expectedType || (unixMode & 0xFFFu) != expectedPermissions)
                    throw new NotSupportedException($"此压缩包包含当前无法保留的 Unix 文件权限，已停止修改以保护可执行位或访问权限：{entry.EntryPath}");
            }
            else if (unixType is 0x1000u or 0x2000u or 0x6000u or 0xA000u or 0xC000u)
                throw new NotSupportedException($"此压缩包包含当前无法保留的特殊 Unix 文件类型，已停止修改：{entry.EntryPath}");
            // 7z's Unix-extension bit is 0x8000, distinct from these ordinary DOS attributes.
            if ((attributes & 0x7u) != 0)
                throw new NotSupportedException($"此压缩包包含当前无法保留的只读、隐藏或系统属性，已停止修改：{entry.EntryPath}");
        }
    }

    private static async Task RejectZipComments(Stream source, CancellationToken token)
    {
        await using var archive = await ArchiveFactory.OpenAsyncArchive(source,
            new ReaderOptions { LeaveStreamOpen = true }, token).ConfigureAwait(false);
        await foreach (var entry in archive.EntriesAsync.WithCancellation(token).ConfigureAwait(false))
            if (entry is ZipEntry { Comment: { Length: > 0 } })
                throw new NotSupportedException("此 ZIP 包含当前无法保留的条目注释，已停止修改以保留原始信息。");
        // The end record (and therefore the volume comment) is loaded after enumerating all entries.
        await foreach (var volume in archive.VolumesAsync.WithCancellation(token).ConfigureAwait(false))
            if (volume is ZipVolume { Comment: { Length: > 0 } })
                throw new NotSupportedException("此 ZIP 包含当前无法保留的归档注释，已停止修改以保留原始信息。");
    }

    private static async Task<ArchiveFormat> DetectFormat(Stream source, CancellationToken token)
    {
        var header = new byte[6];
        var read = 0;
        while (read < header.Length)
        {
            var count = await source.ReadAsync(header.AsMemory(read), token).ConfigureAwait(false);
            if (count == 0) break;
            read += count;
        }
        if (read >= 4 && header[0] == 0x50 && header[1] == 0x4B &&
            (header[2] == 3 && header[3] == 4 || header[2] == 5 && header[3] == 6)) return ArchiveFormat.Zip;
        if (read == 6 && header.AsSpan().SequenceEqual(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C })) return ArchiveFormat.SevenZip;
        throw new NotSupportedException("当前仅支持编辑 ZIP 和 7z 压缩包。");
    }

    private static void AddSource(string path, string relativePath, List<SourceEntry> plan, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        EnsureNoLinks(path);
        var directory = (File.GetAttributes(path) & FileAttributes.Directory) != 0;
        plan.Add(new(path, NormalizePath(relativePath), directory, File.GetLastWriteTimeUtc(path), FilesystemAttributes(path, directory)));
        if (directory)
            foreach (var child in Directory.EnumerateFileSystemEntries(path).Order(StringComparer.Ordinal))
                AddSource(child, relativePath + "/" + Path.GetFileName(child), plan, token);
    }

    private static Dictionary<string, TreeEntry> ReadTree(string root)
    {
        var tree = new Dictionary<string, TreeEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            EnsureNoLinks(path);
            var relative = NormalizePath(Path.GetRelativePath(root, path));
            if (!tree.TryAdd(relative, new(relative, Directory.Exists(path))))
                throw new IOException($"归档包含冲突路径：{relative}");
        }
        return tree;
    }

    private static void ValidateSelection(IReadOnlyList<ArchiveEntryInfo> metadata, string selection)
    {
        if (!metadata.Any(entry => IsSelected(NormalizePath(entry.EntryPath), selection)))
            throw new FileNotFoundException($"归档内不存在此项目：{selection}", selection);
    }

    private static bool IsSelected(string path, string selection) => path == selection || path.StartsWith(selection + "/", StringComparison.Ordinal);
    private static bool IsWithin(string path, string directory) => string.Equals(path, directory, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string InTree(string root, string relative) => Path.Combine(root, NormalizePath(relative).Replace('/', Path.DirectorySeparatorChar));

    private static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalized = path.Replace('\\', '/').TrimEnd('/').Normalize(NormalizationForm.FormC);
        if (normalized.Length == 0 || normalized.StartsWith('/') || normalized.Contains('\0') ||
            (normalized.Length > 1 && normalized[1] == ':') || normalized.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException("归档路径必须是相对路径，且不能包含父级跳转。");
        return normalized;
    }

    private static FileStream Read(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
        64 * 1024, FileOptions.Asynchronous);

    private static int FilesystemAttributes(string path, bool directory)
    {
        var dos = (uint)(File.GetAttributes(path) & (FileAttributes.ReadOnly | FileAttributes.Hidden |
            FileAttributes.System | FileAttributes.Directory | FileAttributes.Archive));
        if (OperatingSystem.IsWindows()) return (int)dos;
        var unix = (uint)File.GetUnixFileMode(path) | (directory ? 0x4000u : 0x8000u);
        return unchecked((int)((unix << 16) | dos));
    }

    private static void EnsureNoLinks(string path)
    {
        string? current = ArchivePaths.ResolveUserPath(path);
        while (current is not null)
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"不支持符号链接路径：{current}");
            current = Path.GetDirectoryName(current);
        }
    }

    private sealed record SourceEntry(string FilePath, string RelativePath, bool IsDirectory, DateTime Modified, int Attributes);
    private sealed record TreeEntry(string RelativePath, bool IsDirectory);
    private sealed record EditContext(string Content, IReadOnlyList<ArchiveEntryInfo> Metadata,
        ArchiveFormat Format, Dictionary<string, int?> Attributes, Dictionary<string, byte> Creators);
}
