using BubuZip.Desktop;
using Xunit;

namespace BubuZip.Ui.Tests;

public sealed class MacShellIntegrationTests
{
    [Fact]
    public async Task AssociationsOnlyChangeSelectedSupportedTypesAndVerifyTheResult()
    {
        using var fixture = new Fixture();
        fixture.Store.Failed.Add(".rar");
        var result = await fixture.Service.AssociateAsync([".ZIP", ".zip", ".rar"]);

        Assert.Equal([".zip", ".rar"], fixture.Store.Changed);
        Assert.Equal(1, fixture.Store.Registrations);
        Assert.Contains("已关联 1 种格式", result.Message);
        Assert.Contains(".rar 尚未生效", result.Message);
        Assert.Empty(fixture.Commands);
    }

    [Fact]
    public async Task UnsupportedAssociationDoesNotMakeAnySystemChanges()
    {
        using var fixture = new Fixture();
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.AssociateAsync([".exe"]));
        Assert.Empty(fixture.Store.Changed);
        Assert.Equal(0, fixture.Store.Registrations);
        Assert.Empty(fixture.Commands);
    }

    [Fact]
    public async Task AssociationsAndFinderExtensionUseTheBubuZipIdentity()
    {
        using var fixture = new Fixture();

        var result = await fixture.Service.AssociateAsync([".zip"]);
        var status = await fixture.Service.GetStatusAsync();

        Assert.Equal("io.bubuzip.app", fixture.Store.Defaults[".zip"]);
        Assert.Equal("io.bubuzip.app.finder", MacShellIntegration.FinderIdentifier);
        Assert.Contains("关联到 BubuZip", result.Message);
        Assert.Contains("默认使用 BubuZip", status.AssociationMessage);
        Assert.DoesNotContain("EclipseArchive", result.Message);
    }

    [Fact]
    public async Task RegisteringMenuPassesTheWholeBundlePathAndRequiresUserEnablement()
    {
        using var fixture = new Fixture();
        var result = await fixture.Service.RegisterMenuAsync();

        var command = Assert.Single(fixture.Commands);
        Assert.Equal("/usr/bin/pluginkit", command.File);
        Assert.Equal(["-a", fixture.PluginPath], command.Arguments);
        Assert.Equal(1, fixture.ManagementWindows);
        Assert.Contains("系统设置中启用", result.Message);
        Assert.Contains("BubuZip 菜单", result.Message);
        Assert.DoesNotContain(fixture.Commands, call => call.Arguments.Contains("use"));
    }

    [Fact]
    public async Task RemovingTheMenuDisablesOnlyThisExtensionBeforeRemovingItsRegistration()
    {
        using var fixture = new Fixture();
        await fixture.Service.UnregisterMenuAsync();

        Assert.Equal(2, fixture.Commands.Count);
        Assert.Equal(["-e", "ignore", "-i", MacShellIntegration.FinderIdentifier], fixture.Commands[0].Arguments);
        Assert.Equal(["-r", fixture.PluginPath], fixture.Commands[1].Arguments);
        Assert.True(Directory.Exists(fixture.PluginPath));
        Assert.Empty(fixture.Store.Changed);
    }

    [Fact]
    public async Task StatusUsesTheRegisteredCopyAndDistinguishesInstalledFromEnabled()
    {
        using var fixture = new Fixture();
        fixture.Store.Defaults[".zip"] = MacShellIntegration.BundleIdentifier;
        fixture.Query = new(0, $"- {MacShellIntegration.FinderIdentifier}(0.1.0)\tUUID\t{fixture.PluginPath}\n", "");
        var status = await fixture.Service.GetStatusAsync();

        Assert.Equal([".zip"], status.AssociatedExtensions);
        Assert.True(status.MenuInstalled);
        Assert.False(status.MenuEnabled);
        Assert.Contains("系统设置中启用", status.MenuMessage);

        fixture.Query = new(0, $"+ {MacShellIntegration.FinderIdentifier}(0.1.0)\tUUID\t/Applications/Other.app/Contents/PlugIns/BubuZipFinder.appex\n", "");
        status = await fixture.Service.GetStatusAsync();
        Assert.False(status.MenuInstalled);
        Assert.False(status.MenuEnabled);
    }

    [Fact]
    public async Task FailedStatusQueryIsReportedInsteadOfSayingTheMenuIsNotInstalled()
    {
        using var fixture = new Fixture();
        fixture.Query = new(3, "", "service unavailable");
        var status = await fixture.Service.GetStatusAsync();

        Assert.Contains("无法读取访达扩展状态", status.MenuMessage);
        Assert.Contains("service unavailable", status.MenuMessage);
    }

    [Fact]
    public async Task CancelledAssociationDoesNotRegisterTheApplication()
    {
        using var fixture = new Fixture();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.AssociateAsync([".zip"], new CancellationToken(true)));
        Assert.Empty(fixture.Store.Changed);
        Assert.Equal(0, fixture.Store.Registrations);
    }

    [Fact]
    public void NativeTypeLookupReadsThePackagedMappingWithoutChangingAssociations()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.BundlePath, "Contents", "Info.plist"), """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0"><dict>
              <key>CFBundleIdentifier</key><string>io.bubuzip.test</string>
              <key>CFBundlePackageType</key><string>APPL</string>
              <key>BubuZipArchiveTypes</key><dict>
                <key>.zip</key><string>public.zip-archive</string>
                <key>.zipx</key><string>io.bubuzip.archive.zipx</string>
              </dict>
            </dict></plist>
            """);

        Assert.Equal("public.zip-archive", MacAssociationStore.ReadContentType(fixture.BundlePath, ".zip"));
        Assert.Equal("io.bubuzip.archive.zipx", MacAssociationStore.ReadContentType(fixture.BundlePath, ".zipx"));
        Assert.Throws<IOException>(() => MacAssociationStore.ReadContentType(fixture.BundlePath, ".rar"));
        Assert.Equal(fixture.BundlePath, MacShellIntegration.FindApplicationBundle(Path.Combine(fixture.BundlePath, "Contents", "Resources", "app")));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "bubuzip-mac-integration-" + Guid.NewGuid().ToString("N"));
        public string BundlePath { get; }
        public string PluginPath { get; }
        public Store Store { get; } = new();
        public List<(string File, string[] Arguments)> Commands { get; } = [];
        public DesktopProcess.Result Query { get; set; } = new(0, "", "");
        public int ManagementWindows { get; private set; }
        public MacShellIntegration Service { get; }

        public Fixture()
        {
            BundlePath = Path.Combine(_root, "含 空格的 BubuZip.app");
            PluginPath = Path.Combine(BundlePath, "Contents", "PlugIns", "BubuZipFinder.appex");
            Directory.CreateDirectory(PluginPath);
            File.WriteAllText(Path.Combine(BundlePath, "Contents", "Info.plist"), "test");
            Service = new(BundlePath, Store, (file, arguments) =>
            {
                var items = arguments.ToArray();
                Commands.Add((file, items));
                return items.Contains("-m") ? Query : new(0, "", "");
            }, () => ManagementWindows++);
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    private sealed class Store : IMacAssociationStore
    {
        public int Registrations { get; private set; }
        public Dictionary<string, string> Defaults { get; } = [];
        public HashSet<string> Failed { get; } = [];
        public List<string> Changed { get; } = [];
        public void Register(string bundlePath) => Registrations++;
        public string? GetDefaultHandler(string bundlePath, string extension) => Defaults.GetValueOrDefault(extension);
        public int SetDefaultHandler(string bundlePath, string extension, string bundleIdentifier)
        {
            Changed.Add(extension);
            if (Failed.Contains(extension)) return -1;
            Defaults[extension] = bundleIdentifier;
            return 0;
        }
    }
}
