using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using EclipseArchive.Desktop;
using Xunit;

namespace EclipseArchive.Ui.Tests;

public sealed class WindowsModernMenuTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Bubu $ & ' Zip " + Guid.NewGuid().ToString("N"));
    private string Executable => Path.Combine(_directory, "BubuZip.exe");
    private const string FullName = "BubuZip.ContextMenu_0.1.0.0_x64__test";
    private readonly Registry _registry = new();

    public WindowsModernMenuTests()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Executable, "test executable");
        File.WriteAllText(Path.Combine(_directory, "BubuZipShell.dll"), "test DLL");
    }

    [Fact]
    public void MissingComponentsAndUnsignedPackagesNeverStartDeploymentOrWriteOwnership()
    {
        var menu = new WindowsModernMenu(Executable, _registry, (_, _) => throw new Exception("Must not start deployment"));
        Assert.Contains("缺少", Assert.Throws<InvalidOperationException>(menu.Register).Message);
        Package(signed: false);
        Assert.Contains("尚未签名", Assert.Throws<InvalidOperationException>(menu.Register).Message);
        Assert.Empty(_registry.Values);
    }

    [Fact]
    public void WrongArchitectureIsRejectedBeforeDeployment()
    {
        Package(architecture: RuntimeInformation.OSArchitecture == Architecture.X64 ? "arm64" : "x64");
        var menu = new WindowsModernMenu(Executable, _registry, (_, _) => throw new Exception("Must not start deployment"));
        Assert.Contains("架构不匹配", Assert.Throws<InvalidOperationException>(menu.Register).Message);
        Assert.Empty(_registry.Values);
    }

    [Fact]
    public void DeploymentPassesPathsAsDataAndWritesOwnershipOnlyAfterConfirmation()
    {
        Package();
        var menu = new WindowsModernMenu(Executable, _registry, (script, environment) =>
        {
            Assert.DoesNotContain(_directory, script);
            Assert.Equal(_directory, environment["BUBUZIP_DIRECTORY"]);
            Assert.Equal(Path.Combine(_directory, "BubuZip.ContextMenu.msix"), environment["BUBUZIP_PACKAGE"]);
            Assert.DoesNotContain("AllowUnsigned", script);
            Assert.Empty(_registry.Values);
            return FullName + "\r\n";
        }, _ => _directory);
        menu.Register();
        Assert.Equal(Executable, _registry.GetString(WindowsModernMenu.Registration, "Executable"));
        Assert.Equal(FullName, _registry.GetString(WindowsModernMenu.Registration, "PackageFullName"));
    }

    [Fact]
    public void FailedDeploymentPreservesPriorOwnership()
    {
        Package();
        Own("Other.exe");
        var menu = new WindowsModernMenu(Executable, _registry, (_, _) => throw new IOException("Windows refused package"));
        Assert.Throws<IOException>(menu.Register);
        Assert.Equal("Other.exe", _registry.GetString(WindowsModernMenu.Registration, "Executable"));
    }

    [Fact]
    public void RegistryMarkerAloneDoesNotMeanThePackageIsInstalled()
    {
        Own(Executable);
        var absent = new WindowsModernMenu(Executable, _registry, (_, _) => "");
        Assert.False(absent.IsRegistered());
        var present = new WindowsModernMenu(Executable, _registry, (_, environment) =>
        {
            Assert.Equal(FullName, environment["BUBUZIP_PACKAGE_FULL_NAME"]);
            return FullName;
        }, _ => _directory);
        Assert.True(present.IsRegistered());
    }

    [Fact]
    public void RemovalOnlyTargetsThisExecutableAndKeepsMarkerWhenWindowsRefuses()
    {
        Own("Other.exe");
        var calls = 0;
        var menu = new WindowsModernMenu(Executable, _registry, (_, _) => { calls++; throw new IOException("in use"); }, _ => _directory);
        Assert.False(menu.IsRegistered());
        menu.Unregister();
        Assert.Equal(0, calls);
        Own(Executable);
        Assert.Throws<IOException>(menu.Unregister);
        Assert.Equal(Executable, _registry.GetString(WindowsModernMenu.Registration, "Executable"));
        new WindowsModernMenu(Executable, _registry, (_, environment) =>
        {
            Assert.Equal(FullName, environment["BUBUZIP_PACKAGE_FULL_NAME"]);
            return "";
        }, _ => _directory).Unregister();
        Assert.Empty(_registry.Values);
    }

    [Fact]
    public void PackageRelocatedByAnotherInstallationIsNeitherReportedNorRemoved()
    {
        Own(Executable);
        var menu = new WindowsModernMenu(Executable, _registry,
            (_, _) => throw new Exception("Must not touch another installation"), _ => _directory + "-other");
        Assert.False(menu.IsRegistered());
        menu.Unregister();
        Assert.Empty(_registry.Values);
    }

    [Fact]
    public void DeploymentMustConfirmTheActualExternalDirectoryBeforeTakingOwnership()
    {
        Package();
        var menu = new WindowsModernMenu(Executable, _registry, (_, _) => FullName, _ => _directory + "-other");
        Assert.Contains("另一安装目录", Assert.Throws<IOException>(menu.Register).Message);
        Assert.Empty(_registry.Values);
    }

    private void Own(string executable)
    {
        _registry.SetString(WindowsModernMenu.Registration, "Executable", executable);
        _registry.SetString(WindowsModernMenu.Registration, "PackageFullName", FullName);
    }

    private void Package(bool signed = true, string? architecture = null)
    {
        using var zip = ZipFile.Open(Path.Combine(_directory, "BubuZip.ContextMenu.msix"), ZipArchiveMode.Create);
        using (var stream = zip.CreateEntry("AppxManifest.xml").Open())
        {
            XNamespace ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
            new XDocument(new XElement(ns + "Package", new XElement(ns + "Identity",
                new XAttribute("Name", WindowsModernMenu.PackageName), new XAttribute("Publisher", "CN=BubuZip"),
                new XAttribute("ProcessorArchitecture", architecture ?? RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant())))).Save(stream);
        }
        // Presence is a preflight check only; Windows validates the real signature during deployment.
        if (signed) zip.CreateEntry("AppxSignature.p7x");
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private sealed class Registry : IWindowsIntegrationRegistry
    {
        internal readonly Dictionary<(string, string), string> Values = new();
        public string? GetString(string path, string name) => Values.GetValueOrDefault((path, name));
        public string? GetDefaultProgId(string extension) => null;
        public void SetString(string path, string name, string value) => Values[(path, name)] = value;
        public void SetEmptyValue(string path, string name) => throw new NotSupportedException();
        public void DeleteTree(string path)
        {
            foreach (var key in Values.Keys.Where(key => key.Item1 == path).ToArray()) Values.Remove(key);
        }
    }
}
