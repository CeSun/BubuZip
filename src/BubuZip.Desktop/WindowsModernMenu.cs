using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;

namespace BubuZip.Desktop;

internal interface IWindowsModernMenu
{
    bool IsRegistered();
    void Register();
    void Unregister();
}

/// <summary>Registers the native Explorer command with package identity on Windows 11.</summary>
internal sealed class WindowsModernMenu(string executable, IWindowsIntegrationRegistry registry,
    Func<string, Dictionary<string, string>, string>? execute = null,
    Func<string, string?>? packageLocation = null) : IWindowsModernMenu
{
    internal const string Registration = @"Software\BubuZip\ModernMenu";
    internal const string PackageName = "BubuZip.ContextMenu";
    private bool IsOwner(string key) => string.Equals(registry.GetString(key, "Executable"), executable, StringComparison.OrdinalIgnoreCase);
    private string? OwnedRegistration => IsOwner(Registration) ? Registration
        : IsOwner(LegacyIntegrationIdentity.ModernMenu) ? LegacyIntegrationIdentity.ModernMenu : null;

    public bool IsRegistered()
    {
        if (OwnedRegistration is not { } registration) return false;
        var fullName = registry.GetString(registration, "PackageFullName");
        return !string.IsNullOrEmpty(fullName) && IsCurrentLocation(fullName) && Execute("""
            Get-AppxPackage -Name BubuZip.ContextMenu |
                Where-Object { $_.PackageFullName -eq $env:BUBUZIP_PACKAGE_FULL_NAME -and $_.Status -eq 'Ok' } |
                ForEach-Object { $_.PackageFullName }
            """, new() { ["BUBUZIP_PACKAGE_FULL_NAME"] = fullName }).Trim() == fullName;
    }

    public void Register()
    {
        var directory = Path.GetDirectoryName(executable)!;
        var package = Path.Combine(directory, PackageName + ".msix");
        var dll = Path.Combine(directory, "BubuZipShell.dll");
        if (!File.Exists(package) || !File.Exists(dll))
            throw new InvalidOperationException("此版本缺少 Windows 11 一级菜单组件，请使用包含 BubuZipShell.dll 和 BubuZip.ContextMenu.msix 的 Windows 发布版，然后重新注册。");
        if (!Path.GetFileName(executable).Equals("BubuZip.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Windows 11 一级菜单需要从发布版 BubuZip.exe 注册，请勿重命名主程序。");

        using var archive = ZipFile.OpenRead(package);
        using var manifestStream = (archive.GetEntry("AppxManifest.xml") ?? throw new IOException("菜单安装包缺少清单。")).Open();
        var manifest = XDocument.Load(manifestStream);
        var identity = manifest.Root?.Element(manifest.Root.Name.Namespace + "Identity");
        var publisher = (string?)identity?.Attribute("Publisher") ?? "";
        var architecture = (string?)identity?.Attribute("ProcessorArchitecture");
        if ((string?)identity?.Attribute("Name") != PackageName ||
            !string.Equals(architecture, RuntimeInformation.OSArchitecture.ToString(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("菜单安装包与当前 Windows 架构不匹配，请安装对应的 x64 或 ARM64 发布版。");
        if (archive.GetEntry("AppxSignature.p7x") is null)
            throw new InvalidOperationException("Windows 11 菜单安装包尚未签名，请使用由受信任证书签名的发布版；仅有未签名 MSIX 无法启用一级菜单。");
        var fullName = Execute("""
            $arguments = @{ Path = $env:BUBUZIP_PACKAGE; ExternalLocation = $env:BUBUZIP_DIRECTORY; ErrorAction = 'Stop' }
            Add-AppxPackage @arguments
            Get-AppxPackage -Name BubuZip.ContextMenu |
                Where-Object { $_.Publisher -eq $env:BUBUZIP_PUBLISHER -and $_.Status -eq 'Ok' } |
                ForEach-Object { $_.PackageFullName }
            """, new()
            {
                ["BUBUZIP_PACKAGE"] = package, ["BUBUZIP_DIRECTORY"] = directory,
                ["BUBUZIP_PUBLISHER"] = publisher,
            }).Trim();
        if (string.IsNullOrEmpty(fullName) || fullName.Contains('\n'))
            throw new IOException("Windows 未确认菜单安装包注册成功，请刷新状态后重试。");
        if (!IsCurrentLocation(fullName))
            throw new IOException("菜单身份包仍指向另一安装目录，请先在原安装中移除右键菜单，再从当前目录重新注册。");
        registry.SetString(Registration, "Executable", executable);
        registry.SetString(Registration, "PackageFullName", fullName);
        if (IsOwner(LegacyIntegrationIdentity.ModernMenu)) registry.DeleteTree(LegacyIntegrationIdentity.ModernMenu);
    }

    public void Unregister()
    {
        if (OwnedRegistration is not { } registration) return;
        var fullName = registry.GetString(registration, "PackageFullName");
        if (!string.IsNullOrEmpty(fullName) && IsCurrentLocation(fullName))
            Execute("""
                Get-AppxPackage -Name BubuZip.ContextMenu |
                    Where-Object { $_.PackageFullName -eq $env:BUBUZIP_PACKAGE_FULL_NAME } |
                    ForEach-Object { Remove-AppxPackage -Package $_.PackageFullName -ErrorAction Stop }
                """, new() { ["BUBUZIP_PACKAGE_FULL_NAME"] = fullName });
        registry.DeleteTree(registration);
        if (registration != LegacyIntegrationIdentity.ModernMenu && IsOwner(LegacyIntegrationIdentity.ModernMenu))
            registry.DeleteTree(LegacyIntegrationIdentity.ModernMenu);
    }

    private bool IsCurrentLocation(string fullName) =>
        string.Equals(Path.TrimEndingDirectorySeparator((packageLocation ?? GetPackageLocation)(fullName) ?? ""),
            Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(executable)!), StringComparison.OrdinalIgnoreCase);

    private static string? GetPackageLocation(string fullName)
    {
        if (!OperatingSystem.IsWindows()) return null;
        uint length = 0;
        // PackagePathType_EffectiveExternal verifies the actual external directory, not just our registry marker.
        if (GetPackagePathByFullName2(fullName, 5, ref length, null) != 122 || length is 0 or > 32768) return null;
        var path = new StringBuilder((int)length);
        return GetPackagePathByFullName2(fullName, 5, ref length, path) == 0 ? path.ToString() : null;
    }

    [DllImport("kernelbase.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetPackagePathByFullName2(string packageFullName, int packagePathType, ref uint pathLength, StringBuilder? path);

    private string Execute(string script, Dictionary<string, string> environment) => (execute ?? Run)(script, environment);

    private static string Run(string script, Dictionary<string, string> environment)
    {
        // Use the inbox Windows PowerShell for Appx, independent of PATH or the user's profile.
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var prefix = "$ErrorActionPreference = 'Stop'; $ProgressPreference = 'SilentlyContinue'; [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false); try {\n";
        var suffix = "\n} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }";
        var result = DesktopProcess.Run(powershell,
            ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(prefix + script + suffix))], environment);
        DesktopProcess.EnsureSuccess("Windows 11 一级菜单配置", result);
        return result.Output;
    }
}
