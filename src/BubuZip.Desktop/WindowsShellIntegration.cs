using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace BubuZip.Desktop;

/// <summary>Per-user file capabilities and modern or classic Explorer menus.</summary>
internal sealed class WindowsShellIntegration : IShellIntegrationService
{
    internal const string ProgId = "BubuZip.Archive";
    internal const string Classes = @"Software\Classes\";
    internal const string Capabilities = @"Software\BubuZip\Capabilities";
    internal const string RegisteredApplications = @"Software\RegisteredApplications";
    internal const string FileMenu = Classes + @"*\shell\BubuZip";
    internal const string DirectoryMenu = Classes + @"Directory\shell\BubuZip";
    internal const string DefaultAppsUri = "ms-settings:defaultapps?registeredAppUser=BubuZip";
    private const string OwnerValue = "BubuZipExecutable";
    private readonly string _executable;
    private readonly IWindowsIntegrationRegistry _registry;
    private readonly Action<string> _openUri;
    private readonly Action _notify;
    private readonly bool _checkExecutableExists;
    private readonly IWindowsModernMenu? _modernMenu;

    public string PlatformName => "Windows";

    public WindowsShellIntegration() : this(Environment.ProcessPath ?? "", CreateRegistry(), OpenUri, Notify, true)
    {
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            _modernMenu = new WindowsModernMenu(_executable, _registry);
    }

    internal WindowsShellIntegration(string executable, IWindowsIntegrationRegistry registry,
        Action<string> openUri, Action notify, bool checkExecutableExists = false, IWindowsModernMenu? modernMenu = null)
    {
        _executable = executable;
        _registry = registry;
        _openUri = openUri;
        _notify = notify;
        _checkExecutableExists = checkExecutableExists;
        _modernMenu = modernMenu;
    }

    public Task<ShellIntegrationStatus> GetStatusAsync(CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var defaults = ArchiveShellFormats.Extensions.Where(extension =>
            {
                var current = _registry.GetDefaultProgId(extension);
                return (string.Equals(current, ProgId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(current, LegacyIntegrationIdentity.ProgId, StringComparison.OrdinalIgnoreCase)) &&
                    _registry.GetString(Classes + current + @"\shell\open\command", "") == OpenCommand(_executable);
            }).ToArray();
            var menu = IsOwnedMenu(FileMenu) && IsOwnedMenu(DirectoryMenu) ||
                IsOwnedLegacyMenu(LegacyIntegrationIdentity.FileMenu) && IsOwnedLegacyMenu(LegacyIntegrationIdentity.DirectoryMenu);
            var modern = _modernMenu?.IsRegistered() == true;
            return new ShellIntegrationStatus(defaults, menu || modern, menu || modern,
                "文件格式注册后仍需在 Windows“默认应用”中确认；这里只显示系统已确认的默认关联。",
                modern ? "已注册 Windows 11 一级右键菜单。右键单个文件或文件夹即可使用 BubuZip；若尚未显示，请重启资源管理器或注销后重新登录。"
                : _modernMenu is not null
                    ? menu ? "当前仅有“显示更多选项”中的旧版菜单，请点击“注册右键菜单”升级为 Windows 11 一级菜单。"
                           : "尚未注册 Windows 11 一级右键菜单。点击注册后，右键单个文件或文件夹即可使用 BubuZip。"
                    : menu ? "已注册资源管理器菜单。Windows 11 请在“显示更多选项”中使用；当前支持单个文件或文件夹。"
                           : "尚未注册资源管理器菜单。注册仅作用于当前用户，无需管理员权限。");
        }, cancellationToken);

    public Task<ShellIntegrationResult> AssociateAsync(IReadOnlyList<string> extensions, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsurePublishExecutable();
            var selected = ValidateExtensions(extensions);
            var program = Classes + ProgId;
            _registry.SetString(program, "", "BubuZip 压缩文件");
            _registry.SetString(program, OwnerValue, _executable);
            // Register as a candidate without silently taking over another application's defaults.
            _registry.SetString(program, "AllowSilentDefaultTakeOver", "");
            _registry.SetString(program + @"\DefaultIcon", "",
                Icon(Path.Combine(Path.GetDirectoryName(_executable) ?? "", "BubuZip.Archive.ico")));
            _registry.SetString(program + @"\shell\open", "MultiSelectModel", "Document");
            _registry.SetString(program + @"\shell\open\command", "", OpenCommand(_executable));
            _registry.SetString(Capabilities, "ApplicationName", "BubuZip");
            _registry.SetString(Capabilities, "ApplicationDescription", "浏览、创建、提取和测试压缩文件。");
            _registry.SetString(Capabilities, "ApplicationIcon", Icon(_executable));
            foreach (var extension in selected)
            {
                _registry.SetEmptyValue(Classes + extension + @"\OpenWithProgids", ProgId);
                _registry.SetString(Capabilities + @"\FileAssociations", extension, ProgId);
            }
            _registry.SetString(RegisteredApplications, "BubuZip", Capabilities);
            _notify();
            _openUri(DefaultAppsUri);
            return new ShellIntegrationResult("已注册所选格式并打开 Windows 默认应用设置，请在那里确认使用 BubuZip。实际默认关联会在刷新后显示。");
        }, cancellationToken);

    public Task<ShellIntegrationResult> RegisterMenuAsync(CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsurePublishExecutable();
            if (_modernMenu is not null)
            {
                _modernMenu.Register();
                // Migrate only after package registration succeeds, preserving the working legacy menu on failure.
                RemoveOwnedClassicMenus();
                _notify();
                return new ShellIntegrationResult("已注册 Windows 11 一级右键菜单。右键单个文件或文件夹即可使用 BubuZip；若尚未显示，请重启资源管理器或注销后重新登录。");
            }
            RegisterMenu(FileMenu, includeArchiveCommands: true);
            RegisterMenu(DirectoryMenu, includeArchiveCommands: false);
            RemoveOwnedLegacyMenus();
            _notify();
            return new ShellIntegrationResult("已为当前用户注册资源管理器菜单。Windows 11 请展开“显示更多选项”；当前支持单个文件或文件夹。");
        }, cancellationToken);

    public Task<ShellIntegrationResult> UnregisterMenuAsync(CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            _modernMenu?.Unregister();
            RemoveOwnedClassicMenus();
            _notify();
            return new ShellIntegrationResult("已移除此应用注册的资源管理器菜单。文件格式关联保持原有状态。");
        }, cancellationToken);

    private void RemoveOwnedClassicMenus()
    {
        foreach (var menu in new[] { FileMenu, DirectoryMenu })
            if (IsOwnedMenu(menu)) _registry.DeleteTree(menu);
        RemoveOwnedLegacyMenus();
    }

    private bool IsOwnedLegacyMenu(string path) =>
        string.Equals(_registry.GetString(path, LegacyIntegrationIdentity.OwnerValue), _executable, StringComparison.OrdinalIgnoreCase);

    private void RemoveOwnedLegacyMenus()
    {
        foreach (var menu in new[] { LegacyIntegrationIdentity.FileMenu, LegacyIntegrationIdentity.DirectoryMenu })
            if (IsOwnedLegacyMenu(menu)) _registry.DeleteTree(menu);
    }

    private void RegisterMenu(string path, bool includeArchiveCommands)
    {
        // The empty SubCommands value uses verbs nested beneath this menu, avoiding HKLM CommandStore.
        _registry.SetString(path, "MUIVerb", "BubuZip");
        _registry.SetString(path, "Icon", Icon(_executable));
        _registry.SetString(path, "SubCommands", "");
        _registry.SetString(path, "MultiSelectModel", "Single");
        _registry.SetString(path, OwnerValue, _executable);
        if (includeArchiveCommands)
        {
            var archiveFilter = string.Join(" OR ", ArchiveShellFormats.Extensions.Select(extension => $"System.FileExtension:=\"{extension}\""));
            AddMenuVerb(path, "01open", "打开压缩包", "open", archiveFilter);
            AddMenuVerb(path, "02extract", "提取文件…", "extract", archiveFilter);
            AddMenuVerb(path, "03extract-here", "提取到当前位置", "extract-here", archiveFilter);
            AddMenuVerb(path, "03extract-smart", "提取到当前位置(智能)", "extract-smart", archiveFilter);
            AddMenuVerb(path, "04extract-folder", "提取到同名文件夹", "extract-folder", archiveFilter);
            AddMenuVerb(path, "05test", "测试压缩包", "test", archiveFilter);
        }
        AddMenuVerb(path, "06compress", "添加到压缩包…", "compress");
        AddMenuVerb(path, "07compress-7z", "压缩为同名 7z 文件", "compress-7z");
        AddMenuVerb(path, "08compress-zip", "压缩为同名 ZIP 文件", "compress-zip");
    }

    private void AddMenuVerb(string menu, string key, string title, string action, string? appliesTo = null)
    {
        var verb = menu + @"\shell\" + key;
        _registry.SetString(verb, "MUIVerb", title);
        _registry.SetString(verb, "MultiSelectModel", "Single");
        if (appliesTo is not null) _registry.SetString(verb, "AppliesTo", appliesTo);
        _registry.SetString(verb + @"\command", "", ShellCommand(_executable, action));
    }

    private bool IsOwnedMenu(string path) =>
        string.Equals(_registry.GetString(path, OwnerValue), _executable, StringComparison.OrdinalIgnoreCase);

    private static string[] ValidateExtensions(IReadOnlyList<string> extensions)
    {
        ArgumentNullException.ThrowIfNull(extensions);
        if (extensions.Count == 0) throw new ArgumentException("请至少选择一种文件格式。", nameof(extensions));
        var selected = extensions.Select(extension => extension?.ToLowerInvariant() ?? "").Distinct(StringComparer.Ordinal).ToArray();
        if (selected.Any(extension => !ArchiveShellFormats.Extensions.Contains(extension, StringComparer.Ordinal)))
            throw new ArgumentException("所选文件格式不在应用支持的格式列表中。", nameof(extensions));
        return selected;
    }

    private void EnsurePublishExecutable()
    {
        var name = _executable.Replace('\\', '/').Split('/').LastOrDefault() ?? "";
        if (string.IsNullOrWhiteSpace(_executable) || _executable.IndexOfAny(['"', '\r', '\n', '\0']) >= 0 ||
            !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || name.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase) ||
            !(_executable.StartsWith(@"\\", StringComparison.Ordinal) ||
              _executable.Length >= 3 && char.IsAsciiLetter(_executable[0]) && _executable[1] == ':' && _executable[2] == '\\') ||
            _checkExecutableExists && !File.Exists(_executable))
            throw new InvalidOperationException("请先使用 Windows 发布版 BubuZip.exe 启动，再注册文件格式或资源管理器菜单；开发宿主不支持注册。");
    }

    internal static string OpenCommand(string executable) => Quote(executable) + " -- \"%1\"";
    internal static string ShellCommand(string executable, string action)
    {
        if (action is not ("open" or "extract" or "extract-here" or "extract-smart" or "extract-folder" or "test" or "compress" or "compress-7z" or "compress-zip"))
            throw new ArgumentException("Unknown shell action.", nameof(action));
        return Quote(executable) + " --shell " + action + " -- \"%1\"";
    }
    private static string Quote(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.IndexOfAny(['"', '\0', '\r', '\n']) >= 0)
            throw new ArgumentException("Invalid executable path.", nameof(text));
        return '"' + text + '"';
    }
    private static string Icon(string executable) => Quote(executable) + ",0";

    private static IWindowsIntegrationRegistry CreateRegistry()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("资源管理器集成仅在 Windows 上可用。");
        return new WindowsIntegrationRegistry();
    }
    private static void OpenUri(string uri)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var process = Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
    }
    private static void Notify()
    {
        if (OperatingSystem.IsWindows()) SHChangeNotify(0x08000000, 0, 0, 0);
    }
    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, nint item1, nint item2);
}

// Injectable so registration and ownership behavior can be tested without touching a registry.
internal interface IWindowsIntegrationRegistry
{
    string? GetString(string path, string name);
    string? GetDefaultProgId(string extension);
    void SetString(string path, string name, string value);
    void SetEmptyValue(string path, string name);
    void DeleteTree(string path);
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsIntegrationRegistry : IWindowsIntegrationRegistry
{
    public string? GetString(string path, string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path);
        return key?.GetValue(name) as string;
    }
    public string? GetDefaultProgId(string extension)
    {
        uint length = 0;
        // ASSOCSTR_PROGID reflects Windows' effective user default, including protected UserChoice.
        _ = AssocQueryString(0, 20, extension, null, null, ref length);
        if (length is 0 or > 32768) return null;
        var result = new StringBuilder((int)length);
        return AssocQueryString(0, 20, extension, null, result, ref length) == 0 ? result.ToString() : null;
    }
    public void SetString(string path, string name, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(path, writable: true);
        key.SetValue(name, value, RegistryValueKind.String);
    }
    public void SetEmptyValue(string path, string name)
    {
        using var key = Registry.CurrentUser.CreateSubKey(path, writable: true);
        key.SetValue(name, Array.Empty<byte>(), RegistryValueKind.None);
    }
    public void DeleteTree(string path) => Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);

    [DllImport("shlwapi.dll", EntryPoint = "AssocQueryStringW", CharSet = CharSet.Unicode)]
    private static extern int AssocQueryString(uint flags, uint text, string association, string? extra, StringBuilder? output, ref uint outputLength);
}
