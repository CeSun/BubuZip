using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using EclipseArchive.Core;
using Microsoft.Win32.SafeHandles;

namespace EclipseArchive.Desktop;

/// <summary>Consumes private, single-use Finder requests delivered as document-open events.</summary>
[SupportedOSPlatform("macos")]
internal sealed class MacFinderRequestFile
{
    internal const string Suffix = ".eclipsearchive-request";
    internal const int MaximumBytes = 256 * 1024, MaximumPaths = 256;
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";
    private readonly string _requestRoot;
    private readonly Func<DateTimeOffset> _clock;
    private readonly uint _currentUser;

    public MacFinderRequestFile() : this(DefaultRequestRoot, () => DateTimeOffset.UtcNow) { }

    internal MacFinderRequestFile(string requestRoot, Func<DateTimeOffset> clock, uint? currentUser = null)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("访达请求仅用于 macOS。");
        _requestRoot = Path.TrimEndingDirectorySeparator(ArchivePaths.ResolveUserPath(requestRoot));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _currentUser = currentUser ?? GetUid();
    }

    internal static string DefaultRequestRoot => Path.Combine(CurrentUserHome(), "Library", "Containers",
        MacShellIntegration.FinderIdentifier, "Data", "Library", "Application Support", "EclipseArchive", "FinderRequests");

    public ArchiveShellRequest? ReadIfRequest(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!path.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase)) return null;
        if (!Path.IsPathFullyQualified(path) || path.Any(char.IsControl))
            throw Invalid("请求路径不是有效的本地绝对路径。");
        var fullPath = ArchivePaths.ResolveUserPath(path);
        var filename = Path.GetFileName(fullPath);
        var stem = filename[..^Suffix.Length];
        if (!string.Equals(Path.GetDirectoryName(fullPath), _requestRoot, StringComparison.Ordinal) ||
            !Guid.TryParseExact(stem, "D", out var identifier) || filename != identifier.ToString("D") + Suffix)
            throw Invalid("请求不来自访达扩展的私有请求目录。");

        EnsureNoLinks(_requestRoot);
        CheckPath(_requestRoot, directory: true);
        EnsureNoLinks(fullPath);
        CheckPath(fullPath, directory: false);

        // NOFOLLOW_ANY checks every path component atomically when opening on macOS 13+.
        // NONBLOCK prevents an unexpected special file from hanging the application.
        // Darwin rejects combining O_NOFOLLOW_ANY with O_NOFOLLOW (EINVAL).
        var fd = Open(fullPath, 0x20000000 | 0x00000004 | 0x01000000);
        if (fd < 0) throw new IOException($"无法读取访达请求（错误 {Marshal.GetLastPInvokeError()}）。");
        using var handle = new SafeFileHandle(fd, ownsHandle: true);
        using var stream = new FileStream(handle, FileAccess.Read);
        var attributes = AttributeList.Create();
        if (GetFileAttributes(fd, ref attributes, out var metadata, (nuint)Marshal.SizeOf<OwnerAttributes>(), 0) != 0)
            throw new IOException($"无法验证访达请求所有者（错误 {Marshal.GetLastPInvokeError()}）。");
        ValidateOwner(metadata, directory: false);
        ValidateMode(File.GetUnixFileMode(handle), directory: false);
        if (stream.Length is <= 0 or > MaximumBytes) throw Invalid("请求文件大小超出限制。");
        var now = _clock();
        CheckFreshness(new DateTimeOffset(File.GetLastWriteTimeUtc(handle)), now);
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw Invalid("请求文件在读取时发生变化。");

        MacFinderWireRequest request;
        try
        {
            request = JsonSerializer.Deserialize(bytes, MacFinderRequestJsonContext.Default.MacFinderWireRequest)
                ?? throw Invalid("请求内容为空。");
        }
        catch (JsonException error) { throw new InvalidDataException("访达请求格式无效。", error); }
        if (request.Version != 1) throw Invalid("不支持此版本的访达请求。");
        var action = ParseAction(request.Action);
        if (request.Paths is not { Length: > 0 and <= MaximumPaths } paths ||
            paths.Any(value => string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value) || value.Any(char.IsControl)))
            throw Invalid("请求包含无效或过多的文件路径。");
        if (request.CreatedAt < now.ToUnixTimeSeconds() - 600 || request.CreatedAt > now.ToUnixTimeSeconds() + 60)
            throw Invalid("访达请求已过期或时间无效，请重新选择菜单操作。");
        CheckFreshness(new DateTimeOffset(File.GetLastWriteTimeUtc(handle)), now);
        EnsureNoLinks(fullPath);
        CheckPath(_requestRoot, directory: true);
        CheckPath(fullPath, directory: false);

        // Claim only a fully validated request. Atomic rename means concurrent consumers
        // cannot both return the same action, and replaying the original URL fails closed.
        var claimed = Path.Combine(_requestRoot, ".consumed-" + Guid.NewGuid().ToString("N"));
        File.Move(fullPath, claimed, overwrite: false);
        File.Delete(claimed);
        return new(action, paths.ToArray());
    }

    private void CheckPath(string path, bool directory)
    {
        var attributes = AttributeList.Create();
        if (GetPathAttributes(path, ref attributes, out var metadata, (nuint)Marshal.SizeOf<OwnerAttributes>(), 1) != 0)
            throw new IOException($"无法验证访达请求路径（错误 {Marshal.GetLastPInvokeError()}）。");
        ValidateOwner(metadata, directory);
        ValidateMode(File.GetUnixFileMode(path), directory);
    }

    private void ValidateOwner(OwnerAttributes attributes, bool directory)
    {
        // sys/vnode.h: VREG=1, VDIR=2. getattrlist returns only the attributes requested.
        if (attributes.Length != Marshal.SizeOf<OwnerAttributes>() || attributes.Owner != _currentUser ||
            attributes.ObjectType != (directory ? 2u : 1u))
            throw Invalid("访达请求的类型或所有者无效。");
    }

    private static void ValidateMode(UnixFileMode mode, bool directory)
    {
        const UnixFileMode forbidden = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute |
            UnixFileMode.SetGroup | UnixFileMode.SetUser | UnixFileMode.StickyBit;
        if ((mode & forbidden) != 0 || (mode & UnixFileMode.UserRead) == 0 ||
            !directory && (mode & UnixFileMode.UserExecute) != 0)
            throw Invalid("访达请求的权限不安全。");
    }

    private static void EnsureNoLinks(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw Invalid("访达请求路径不能包含符号链接。");
    }

    private static void CheckFreshness(DateTimeOffset modified, DateTimeOffset now)
    {
        if (modified < now.AddMinutes(-10) || modified > now.AddSeconds(60))
            throw Invalid("访达请求文件已过期或时间无效，请重新选择菜单操作。");
    }

    private static ArchiveShellAction ParseAction(string? verb) => verb switch
    {
        "open" => ArchiveShellAction.Open,
        "extract" => ArchiveShellAction.Extract,
        "extract-here" => ArchiveShellAction.ExtractHere,
        "extract-folder" => ArchiveShellAction.ExtractFolder,
        "test" => ArchiveShellAction.Test,
        "compress" => ArchiveShellAction.Compress,
        "compress-7z" => ArchiveShellAction.CompressSevenZip,
        "compress-zip" => ArchiveShellAction.CompressZip,
        _ => throw Invalid("访达请求的操作无效。"),
    };

    private static InvalidDataException Invalid(string message) => new(message);

    private static string CurrentUserHome()
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        var pool = AutoreleasePoolPush();
        var user = CreateString(0, Environment.UserName, 0x08000100);
        try
        {
            if (user == 0) throw new IOException("无法读取当前用户。");
            var home = HomeDirectoryForUser(user);
            if (home == 0) throw new IOException("无法读取当前用户的主目录。");
            return Marshal.PtrToStringUTF8(Send(home, Selector("UTF8String")))
                ?? throw new IOException("无法读取当前用户的主目录。");
        }
        finally
        {
            if (user != 0) Release(user);
            AutoreleasePoolPop(pool);
        }
    }

    // Darwin sys/attr.h: five bitmap groups, each a uint32. Only OBJTYPE and OWNERID
    // are requested, avoiding the architecture-sensitive stat structure entirely.
    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeList
    {
        public ushort Count, Reserved;
        public uint Common, Volume, Directory, File, Fork;
        public static AttributeList Create() => new() { Count = 5, Common = 0x00000008 | 0x00008000 };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OwnerAttributes { public uint Length, ObjectType, Owner; }

    [DllImport(LibSystem, EntryPoint = "getuid")] internal static extern uint GetUid();
    [DllImport(LibSystem, EntryPoint = "getattrlist", SetLastError = true)]
    private static extern int GetPathAttributes([MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        ref AttributeList attributes, out OwnerAttributes result, nuint size, uint options);
    [DllImport(LibSystem, EntryPoint = "fgetattrlist", SetLastError = true)]
    private static extern int GetFileAttributes(int fd, ref AttributeList attributes, out OwnerAttributes result, nuint size, uint options);
    [DllImport(LibSystem, EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("/System/Library/Frameworks/Foundation.framework/Foundation", EntryPoint = "NSHomeDirectoryForUser")]
    private static extern nint HomeDirectoryForUser(nint user);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation", EntryPoint = "CFStringCreateWithCString")]
    private static extern nint CreateString(nint allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, uint encoding);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation", EntryPoint = "CFRelease")]
    private static extern void Release(nint value);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_autoreleasePoolPush")]
    private static extern nint AutoreleasePoolPush();
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_autoreleasePoolPop")]
    private static extern void AutoreleasePoolPop(nint pool);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "sel_registerName")]
    private static extern nint Selector([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector);
}

internal sealed record MacFinderWireRequest(int Version, string? Action, string[]? Paths, long CreatedAt);

[JsonSerializable(typeof(MacFinderWireRequest))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    GenerationMode = JsonSourceGenerationMode.Metadata, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
internal partial class MacFinderRequestJsonContext : JsonSerializerContext
{
}
