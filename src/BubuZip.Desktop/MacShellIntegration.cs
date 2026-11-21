using System.Runtime.InteropServices;

namespace BubuZip.Desktop;

/// <summary>Per-user Launch Services associations and the bundled Finder Sync extension.</summary>
public sealed class MacShellIntegration : IShellIntegrationService
{
    internal const string BundleIdentifier = "io.bubuzip.app";
    internal const string FinderIdentifier = BundleIdentifier + ".finder";
    private readonly string? _bundlePath;
    private readonly IMacAssociationStore _associations;
    private readonly Func<string, IEnumerable<string>, DesktopProcess.Result> _run;
    private readonly Action _showManagement;
    private readonly bool _verifyPlatform;

    public MacShellIntegration() : this(FindApplicationBundle(AppContext.BaseDirectory),
        new MacAssociationStore(), (file, arguments) => DesktopProcess.Run(file, arguments), ShowFinderManagement, true) { }

    internal MacShellIntegration(string? bundlePath, IMacAssociationStore associations,
        Func<string, IEnumerable<string>, DesktopProcess.Result> run, Action showManagement, bool verifyPlatform = false)
    {
        _bundlePath = bundlePath;
        _associations = associations;
        _run = run;
        _showManagement = showManagement;
        _verifyPlatform = verifyPlatform;
    }

    public string PlatformName => "访达";

    public Task<ShellIntegrationStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        VerifyPlatform();
        cancellationToken.ThrowIfCancellationRequested();
        if (_bundlePath is null)
            return new ShellIntegrationStatus([], false, false, "请从已安装的 BubuZip.app 打开设置。", "请使用包含访达扩展的 macOS 应用包。");
        var associated = new List<string>();
        string? associationError = null;
        foreach (var extension in ArchiveShellFormats.Extensions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (_associations.GetDefaultHandler(_bundlePath, extension) == BundleIdentifier) associated.Add(extension);
            }
            catch (IOException error) { associationError = error.Message; break; }
        }
        var plugin = PluginPath(_bundlePath);
        var result = _run("/usr/bin/pluginkit", ["-m", "-v", "-i", FinderIdentifier]);
        var (installed, enabled) = ReadPluginStatus(result.Output, plugin);
        return new ShellIntegrationStatus(associated, installed, enabled,
            associationError ?? $"当前有 {associated.Count} 种格式默认使用 BubuZip 打开。",
            result.ExitCode != 0 ? $"无法读取访达扩展状态：{(string.IsNullOrWhiteSpace(result.Error) ? $"退出代码 {result.ExitCode}" : result.Error.Trim())}"
                : !Directory.Exists(plugin) ? "安装包未包含访达扩展，请更新应用。" : enabled
                ? "访达菜单已启用，作用于个人文件夹和 /Volumes 下的磁盘。"
                : installed ? "访达扩展已注册，请在系统设置中启用。" : "尚未注册访达菜单。");
    }, cancellationToken);

    public Task<ShellIntegrationResult> AssociateAsync(IReadOnlyList<string> extensions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(extensions);
        if (extensions.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("请选择有效的文件格式。", nameof(extensions));
        var normalized = extensions.Select(value => value.ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToArray();
        if (normalized.Length == 0 || normalized.Any(value => !ArchiveShellFormats.Extensions.Contains(value)))
            throw new ArgumentException("请选择应用支持的压缩文件格式。", nameof(extensions));
        return Task.Run(() =>
        {
            VerifyPlatform();
            var bundle = RequireBundle();
            cancellationToken.ThrowIfCancellationRequested();
            _associations.Register(bundle);
            var completed = 0;
            var pending = new List<string>();
            foreach (var extension in normalized)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var status = _associations.SetDefaultHandler(bundle, extension, BundleIdentifier);
                if (status == 0 && _associations.GetDefaultHandler(bundle, extension) == BundleIdentifier) completed++;
                else pending.Add(extension);
            }
            return new ShellIntegrationResult(pending.Count == 0
                ? $"已将 {completed} 种格式关联到 BubuZip。"
                : $"已关联 {completed} 种格式；{string.Join("、", pending)} 尚未生效，请确认 macOS 的更改提示后刷新状态。");
        }, cancellationToken);
    }

    public async Task<ShellIntegrationResult> RegisterMenuAsync(CancellationToken cancellationToken = default)
    {
        VerifyPlatform();
        var bundle = RequireBundle();
        var plugin = PluginPath(bundle);
        if (!Directory.Exists(plugin)) throw new IOException("安装包未包含访达扩展，请重新发布并安装完整的 BubuZip.app。");
        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            _associations.Register(bundle);
            DesktopProcess.EnsureSuccess("注册访达扩展", _run("/usr/bin/pluginkit", ["-a", plugin]));
        }, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        // Preserve the caller's UI context for Apple's extension-management interface.
        _showManagement();
        return new ShellIntegrationResult("访达扩展已注册。请在打开的系统设置中启用“BubuZip 菜单”；启用后返回并刷新状态。菜单适用于个人文件夹和 /Volumes 下的磁盘。");
    }

    public Task<ShellIntegrationResult> UnregisterMenuAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        VerifyPlatform();
        var bundle = RequireBundle();
        cancellationToken.ThrowIfCancellationRequested();
        DesktopProcess.EnsureSuccess("停用访达扩展", _run("/usr/bin/pluginkit", ["-e", "ignore", "-i", FinderIdentifier]));
        cancellationToken.ThrowIfCancellationRequested();
        // Finder can rediscover bundled extensions. The ignore election above keeps it disabled.
        DesktopProcess.EnsureSuccess("移除访达扩展注册", _run("/usr/bin/pluginkit", ["-r", PluginPath(bundle)]));
        return new ShellIntegrationResult("访达菜单已停用并移除注册。应用包中的扩展文件会保留，之后可重新注册并启用。");
    }, cancellationToken);

    private string RequireBundle() => _bundlePath ?? throw new IOException("请先安装 BubuZip.app，并从应用包打开设置。");
    private void VerifyPlatform()
    {
        if (_verifyPlatform && !OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("此操作仅适用于 macOS。");
    }

    private static string PluginPath(string bundle) => Path.Combine(bundle, "Contents", "PlugIns", "BubuZipFinder.appex");

    internal static string? FindApplicationBundle(string directory)
    {
        for (var item = new DirectoryInfo(Path.GetFullPath(directory)); item is not null; item = item.Parent)
            if (item.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(item.FullName, "Contents", "Info.plist")))
                return item.FullName;
        return null;
    }

    internal static (bool Installed, bool Enabled) ReadPluginStatus(string output, string expectedPath)
    {
        var matching = output.Split('\n').Where(line => line.Contains(FinderIdentifier, StringComparison.Ordinal) &&
            line.Contains(expectedPath, StringComparison.Ordinal)).ToArray();
        return (matching.Length > 0, matching.Any(line => line.TrimStart().StartsWith('+')));
    }

    private static readonly Lazy<nint> FinderFramework = new(() => NativeLibrary.Load("/System/Library/Frameworks/FinderSync.framework/FinderSync"));
    private static void ShowFinderManagement()
    {
        if (PthreadMain() == 0)
        {
            DesktopProcess.EnsureSuccess("打开扩展设置", DesktopProcess.Run("/usr/bin/open",
                ["x-apple.systempreferences:com.apple.ExtensionsPreferences?extensionPointIdentifier=com.apple.FinderSync"]));
            return;
        }
        _ = FinderFramework.Value;
        var type = ObjCGetClass("FIFinderSyncController");
        if (type == 0) throw new IOException("macOS 未提供访达扩展设置接口。");
        ObjCSendVoid(type, Selector("showExtensionManagementInterface"));
    }

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "pthread_main_np")]
    private static extern int PthreadMain();
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_getClass")]
    private static extern nint ObjCGetClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "sel_registerName")]
    private static extern nint Selector([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void ObjCSendVoid(nint receiver, nint selector);
}

internal interface IMacAssociationStore
{
    void Register(string bundlePath);
    string? GetDefaultHandler(string bundlePath, string extension);
    int SetDefaultHandler(string bundlePath, string extension, string bundleIdentifier);
}

/// <summary>C APIs avoid reflection and Objective-C completion-block marshalling in NativeAOT.</summary>
internal sealed class MacAssociationStore : IMacAssociationStore
{
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string CoreServices = "/System/Library/Frameworks/CoreServices.framework/CoreServices";
    private const uint Utf8 = 0x08000100, AllRoles = 0xFFFFFFFF;

    public void Register(string bundlePath)
    {
        var url = CreateFileUrl(bundlePath);
        try
        {
            var status = LSRegisterURL(url, true);
            if (status != 0) throw new IOException($"macOS 无法注册应用（错误 {status}）。");
        }
        finally { CFRelease(url); }
    }

    public string? GetDefaultHandler(string bundlePath, string extension)
    {
        var type = CreateString(ReadContentType(bundlePath, extension));
        try
        {
            var handler = LSCopyDefaultRoleHandlerForContentType(type, AllRoles);
            if (handler == 0) return null;
            try { return ReadString(handler); }
            finally { CFRelease(handler); }
        }
        finally { CFRelease(type); }
    }

    public int SetDefaultHandler(string bundlePath, string extension, string bundleIdentifier)
    {
        var type = CreateString(ReadContentType(bundlePath, extension));
        var identifier = CreateString(bundleIdentifier);
        try { return LSSetDefaultRoleHandlerForContentType(type, AllRoles, identifier); }
        finally { CFRelease(identifier); CFRelease(type); }
    }

    internal static string ReadContentType(string bundlePath, string extension)
    {
        var url = CreateFileUrl(bundlePath);
        var bundle = CFBundleCreate(0, url);
        CFRelease(url);
        if (bundle == 0) throw new IOException("macOS 无法读取应用包信息。");
        var mapKey = CreateString("BubuZipArchiveTypes");
        var extensionKey = CreateString(extension);
        try
        {
            var map = CFBundleGetValueForInfoDictionaryKey(bundle, mapKey);
            if (map == 0 || CFGetTypeID(map) != CFDictionaryGetTypeID())
                throw new IOException("安装包缺少文件格式声明，请使用新版 BubuZip.app。");
            var type = CFDictionaryGetValue(map, extensionKey);
            if (type == 0 || CFGetTypeID(type) != CFStringGetTypeID())
                throw new IOException($"安装包尚未声明 {extension}，请更新应用。");
            return ReadString(type);
        }
        finally { CFRelease(extensionKey); CFRelease(mapKey); CFRelease(bundle); }
    }

    private static nint CreateFileUrl(string path)
    {
        var value = CreateString(Path.GetFullPath(path));
        try
        {
            var url = CFURLCreateWithFileSystemPath(0, value, 0, true);
            return url != 0 ? url : throw new IOException("macOS 无法处理应用路径。");
        }
        finally { CFRelease(value); }
    }

    private static nint CreateString(string value)
    {
        var text = CFStringCreateWithCString(0, value, Utf8);
        return text != 0 ? text : throw new IOException("macOS 无法创建文本对象。");
    }

    private static string ReadString(nint value)
    {
        var length = checked((int)CFStringGetMaximumSizeForEncoding(CFStringGetLength(value), Utf8) + 1);
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!CFStringGetCString(value, buffer, length, Utf8)) throw new IOException("macOS 无法读取文本对象。");
            return Marshal.PtrToStringUTF8(buffer) ?? "";
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [DllImport(CoreServices)] private static extern int LSRegisterURL(nint url, [MarshalAs(UnmanagedType.I1)] bool update);
    [DllImport(CoreServices)] private static extern nint LSCopyDefaultRoleHandlerForContentType(nint contentType, uint role);
    [DllImport(CoreServices)] private static extern int LSSetDefaultRoleHandlerForContentType(nint contentType, uint role, nint bundleIdentifier);
    [DllImport(CoreFoundation)] private static extern nint CFStringCreateWithCString(nint allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, uint encoding);
    [DllImport(CoreFoundation)] private static extern nint CFStringGetLength(nint value);
    [DllImport(CoreFoundation)] private static extern nint CFStringGetMaximumSizeForEncoding(nint length, uint encoding);
    [DllImport(CoreFoundation)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFStringGetCString(nint value, nint buffer, nint size, uint encoding);
    [DllImport(CoreFoundation)] private static extern nint CFURLCreateWithFileSystemPath(nint allocator, nint path, int style, [MarshalAs(UnmanagedType.I1)] bool isDirectory);
    [DllImport(CoreFoundation)] private static extern nint CFBundleCreate(nint allocator, nint bundleUrl);
    [DllImport(CoreFoundation)] private static extern nint CFBundleGetValueForInfoDictionaryKey(nint bundle, nint key);
    [DllImport(CoreFoundation)] private static extern nint CFDictionaryGetValue(nint dictionary, nint key);
    [DllImport(CoreFoundation)] private static extern nuint CFGetTypeID(nint value);
    [DllImport(CoreFoundation)] private static extern nuint CFDictionaryGetTypeID();
    [DllImport(CoreFoundation)] private static extern nuint CFStringGetTypeID();
    [DllImport(CoreFoundation)] private static extern void CFRelease(nint value);
}
