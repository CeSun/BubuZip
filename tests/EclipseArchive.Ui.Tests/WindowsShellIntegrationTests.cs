using EclipseArchive.Desktop;
using Xunit;

namespace EclipseArchive.Ui.Tests;

public sealed class WindowsShellIntegrationTests
{
    private const string Executable = @"C:\Program Files\BubuZip\BubuZip.exe";

    [Fact]
    public async Task AssociationRegistersCandidatesAndOpensSettingsWithoutChangingProtectedDefaults()
    {
        var registry = new MemoryRegistry();
        registry.SetString(WindowsShellIntegration.Classes + ".zip", "", "Other.Zip");
        registry.SetString(@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.zip\UserChoice", "ProgId", "Other.Zip");
        registry.Defaults[".zip"] = "Other.Zip";
        var opened = new List<string>();
        var notifications = 0;
        var service = new WindowsShellIntegration(Executable, registry, opened.Add, () => notifications++);

        var result = await service.AssociateAsync([".ZIP", ".7z", ".zip"]);

        Assert.Contains("确认", result.Message);
        Assert.Contains("BubuZip", result.Message);
        Assert.Equal(WindowsShellIntegration.DefaultAppsUri, Assert.Single(opened));
        Assert.Equal(1, notifications);
        Assert.Equal("Other.Zip", registry.GetString(WindowsShellIntegration.Classes + ".zip", ""));
        Assert.Equal("Other.Zip", registry.GetString(@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.zip\UserChoice", "ProgId"));
        Assert.Equal(WindowsShellIntegration.ProgId, registry.GetString(WindowsShellIntegration.Capabilities + @"\FileAssociations", ".zip"));
        Assert.Equal(WindowsShellIntegration.ProgId, registry.GetString(WindowsShellIntegration.Capabilities + @"\FileAssociations", ".7z"));
        Assert.IsType<byte[]>(registry.Values[(WindowsShellIntegration.Classes + @".zip\OpenWithProgids", WindowsShellIntegration.ProgId)]);
        Assert.Equal(WindowsShellIntegration.Capabilities, registry.GetString(WindowsShellIntegration.RegisteredApplications, "EclipseArchive"));
        Assert.Equal("BubuZip", registry.GetString(WindowsShellIntegration.Capabilities, "ApplicationName"));
        Assert.Equal("BubuZip 压缩文件", registry.GetString(WindowsShellIntegration.Classes + WindowsShellIntegration.ProgId, ""));
        Assert.Equal('"' + Executable + "\" -- \"%1\"", registry.GetString(WindowsShellIntegration.Classes + WindowsShellIntegration.ProgId + @"\shell\open\command", ""));
        Assert.Empty((await service.GetStatusAsync()).AssociatedExtensions);

        // The Windows settings confirmation, not candidate registration, changes the actual default.
        registry.Defaults[".zip"] = WindowsShellIntegration.ProgId;
        Assert.Equal([".zip"], (await service.GetStatusAsync()).AssociatedExtensions);
    }

    [Fact]
    public async Task ExplorerCommandsQuoteTheExecutableAndUseSingleSelectionWithoutACommandShell()
    {
        const string specialPath = @"C:\Apps\Bubu $ & Zip\BubuZip.exe";
        var registry = new MemoryRegistry();
        var service = new WindowsShellIntegration(specialPath, registry, _ => { }, () => { });

        await service.RegisterMenuAsync();

        var commands = registry.Values.Where(pair => pair.Key.Path.EndsWith(@"\command", StringComparison.Ordinal)).ToArray();
        Assert.Equal(11, commands.Length); // Eight file actions and three directory compression actions.
        foreach (var command in commands)
        {
            var value = Assert.IsType<string>(command.Value);
            Assert.StartsWith('"' + specialPath + "\" --shell ", value);
            Assert.EndsWith(" -- \"%1\"", value);
            Assert.DoesNotContain("cmd.exe", value, StringComparison.OrdinalIgnoreCase);
            var verb = command.Key.Path[..^@"\command".Length];
            Assert.Equal("Single", registry.GetString(verb, "MultiSelectModel"));
        }
        Assert.Equal("Single", registry.GetString(WindowsShellIntegration.FileMenu, "MultiSelectModel"));
        Assert.Equal("Single", registry.GetString(WindowsShellIntegration.DirectoryMenu, "MultiSelectModel"));
        Assert.Equal("BubuZip", registry.GetString(WindowsShellIntegration.FileMenu, "MUIVerb"));
        Assert.Equal("BubuZip", registry.GetString(WindowsShellIntegration.DirectoryMenu, "MUIVerb"));
        var status = await service.GetStatusAsync();
        Assert.True(status.MenuInstalled);
        Assert.True(status.MenuEnabled);
        Assert.Contains("显示更多选项", status.MenuMessage);
    }

    [Fact]
    public async Task RegisteringBubuZipAfterAnOlderInstallationRefreshesCommandsInTheExistingKeys()
    {
        var registry = new MemoryRegistry();
        const string oldExecutable = @"C:\Program Files\Eclipse Archive\EclipseArchive.Desktop.exe";
        var oldService = new WindowsShellIntegration(oldExecutable, registry, _ => { }, () => { });
        await oldService.AssociateAsync([".zip"]);
        await oldService.RegisterMenuAsync();
        registry.Defaults[".zip"] = WindowsShellIntegration.ProgId;
        var service = new WindowsShellIntegration(Executable, registry, _ => { }, () => { });

        await service.AssociateAsync([".zip"]);
        await service.RegisterMenuAsync();

        Assert.Equal("EclipseArchive.Archive", WindowsShellIntegration.ProgId);
        Assert.Equal("ms-settings:defaultapps?registeredAppUser=EclipseArchive", WindowsShellIntegration.DefaultAppsUri);
        Assert.Equal(WindowsShellIntegration.Capabilities, registry.GetString(WindowsShellIntegration.RegisteredApplications, "EclipseArchive"));
        Assert.Null(registry.GetString(WindowsShellIntegration.RegisteredApplications, "BubuZip"));
        Assert.Equal(Executable, registry.GetString(WindowsShellIntegration.FileMenu, "EclipseArchiveExecutable"));
        Assert.Equal(Executable, registry.GetString(WindowsShellIntegration.DirectoryMenu, "EclipseArchiveExecutable"));
        Assert.All(registry.Values.Where(pair => pair.Key.Path.EndsWith(@"\command", StringComparison.Ordinal)),
            command => Assert.StartsWith('"' + Executable + '"', Assert.IsType<string>(command.Value)));
        Assert.Equal([".zip"], (await service.GetStatusAsync()).AssociatedExtensions);
        Assert.True((await service.GetStatusAsync()).MenuInstalled);
    }

    [Fact]
    public async Task ArchiveActionsAreFilteredBySupportedExtensionAndFolderMenusOnlyOfferCompression()
    {
        var registry = new MemoryRegistry();
        var service = new WindowsShellIntegration(Executable, registry, _ => { }, () => { });

        await service.RegisterMenuAsync();

        foreach (var verb in new[] { "01open", "02extract", "03extract-here", "04extract-folder", "05test" })
        {
            var filter = registry.GetString(WindowsShellIntegration.FileMenu + @"\shell\" + verb, "AppliesTo");
            Assert.Contains("System.FileExtension:=\".zip\"", filter);
            Assert.Contains("System.FileExtension:=\".7z\"", filter);
            Assert.DoesNotContain(".txt", filter);
            Assert.DoesNotContain(registry.Values.Keys, key => key.Path.StartsWith(WindowsShellIntegration.DirectoryMenu + @"\shell\" + verb, StringComparison.Ordinal));
        }
        Assert.Null(registry.GetString(WindowsShellIntegration.FileMenu + @"\shell\06compress", "AppliesTo"));
    }

    [Fact]
    public async Task UnregisterRemovesOnlyThisExecutableMenusAndKeepsAssociationsAndOtherApplications()
    {
        var registry = new MemoryRegistry();
        var service = new WindowsShellIntegration(Executable, registry, _ => { }, () => { });
        registry.SetString(@"Software\Classes\*\shell\OtherApp", "MUIVerb", "OtherApp");
        await service.AssociateAsync([".zip"]);
        await service.RegisterMenuAsync();
        registry.SetString(WindowsShellIntegration.DirectoryMenu, "EclipseArchiveExecutable", @"C:\Other install\EclipseArchive.Desktop.exe");

        await service.UnregisterMenuAsync();

        Assert.Equal([WindowsShellIntegration.FileMenu], registry.DeletedTrees);
        Assert.Equal("OtherApp", registry.GetString(@"Software\Classes\*\shell\OtherApp", "MUIVerb"));
        Assert.NotNull(registry.GetString(WindowsShellIntegration.DirectoryMenu, "MUIVerb"));
        Assert.Equal(WindowsShellIntegration.ProgId, registry.GetString(WindowsShellIntegration.Capabilities + @"\FileAssociations", ".zip"));
        Assert.NotNull(registry.GetString(WindowsShellIntegration.Classes + WindowsShellIntegration.ProgId + @"\shell\open\command", ""));
        Assert.False((await service.GetStatusAsync()).MenuInstalled);
    }

    [Theory]
    [InlineData(@"C:\Program Files\dotnet\dotnet.exe")]
    [InlineData("dotnet")]
    [InlineData("relative.exe")]
    [InlineData("C:\\Apps\\bad\" name.exe")]
    public async Task RegistrationRejectsDevelopmentHostsAndUnsafeExecutablePathsBeforeWriting(string executable)
    {
        var registry = new MemoryRegistry();
        var service = new WindowsShellIntegration(executable, registry, _ => throw new InvalidOperationException(), () => { });

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RegisterMenuAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AssociateAsync([".zip"]));

        Assert.Empty(registry.Values);
    }

    [Theory]
    [InlineData(@".zip\shell\bad")]
    [InlineData(".exe")]
    [InlineData("zip")]
    public async Task UnrecognizedExtensionsAreRejectedBeforeWriting(string extension)
    {
        var registry = new MemoryRegistry();
        var service = new WindowsShellIntegration(Executable, registry, _ => { }, () => { });

        await Assert.ThrowsAsync<ArgumentException>(() => service.AssociateAsync([extension]));

        Assert.Empty(registry.Values);
    }

    [Fact]
    public async Task Windows11MigratesOwnedClassicMenusOnlyAfterModernRegistrationSucceeds()
    {
        var registry = new MemoryRegistry();
        await new WindowsShellIntegration(Executable, registry, _ => { }, () => { }).RegisterMenuAsync();
        var modern = new ModernMenu();
        var service = new WindowsShellIntegration(Executable, registry, _ => { }, () => { }, modernMenu: modern);
        Assert.Contains("旧版菜单", (await service.GetStatusAsync()).MenuMessage);

        var result = await service.RegisterMenuAsync();

        Assert.True(modern.Registered);
        Assert.Contains("一级", result.Message);
        Assert.Null(registry.GetString(WindowsShellIntegration.FileMenu, "MUIVerb"));
        Assert.Null(registry.GetString(WindowsShellIntegration.DirectoryMenu, "MUIVerb"));
        var status = await service.GetStatusAsync();
        Assert.True(status.MenuInstalled);
        Assert.True(status.MenuEnabled);
        Assert.Contains("一级", status.MenuMessage);
        await service.UnregisterMenuAsync();
        Assert.False(modern.Registered);
        Assert.False((await service.GetStatusAsync()).MenuInstalled);
    }

    [Fact]
    public async Task FailedModernRegistrationPreservesClassicMenuAndDoesNotClaimModernSuccess()
    {
        var registry = new MemoryRegistry();
        await new WindowsShellIntegration(Executable, registry, _ => { }, () => { }).RegisterMenuAsync();
        var modern = new ModernMenu { FailRegistration = true };
        var notifications = 0;
        var service = new WindowsShellIntegration(Executable, registry, _ => { }, () => notifications++, modernMenu: modern);

        await Assert.ThrowsAsync<IOException>(() => service.RegisterMenuAsync());

        Assert.Equal(0, notifications);
        Assert.Empty(registry.DeletedTrees);
        Assert.False(modern.Registered);
        Assert.Contains("旧版菜单", (await service.GetStatusAsync()).MenuMessage);
    }

    [Fact]
    public async Task ModernRegistrationDoesNotRemoveAnotherExecutableClassicMenu()
    {
        var registry = new MemoryRegistry();
        await new WindowsShellIntegration(@"C:\Other\BubuZip.exe", registry, _ => { }, () => { }).RegisterMenuAsync();
        var modern = new ModernMenu();
        var service = new WindowsShellIntegration(Executable, registry, _ => { }, () => { }, modernMenu: modern);
        await service.RegisterMenuAsync();
        Assert.Empty(registry.DeletedTrees);
        Assert.True((await service.GetStatusAsync()).MenuInstalled);
    }

    [Fact]
    public async Task CancelledRegistrationDoesNotInstallModernPackage()
    {
        var modern = new ModernMenu();
        var service = new WindowsShellIntegration(Executable, new MemoryRegistry(), _ => { }, () => { }, modernMenu: modern);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RegisterMenuAsync(new CancellationToken(true)));
        Assert.False(modern.Registered);
    }

    private sealed class ModernMenu : IWindowsModernMenu
    {
        internal bool Registered;
        internal bool FailRegistration;
        public bool IsRegistered() => Registered;
        public void Register()
        {
            if (FailRegistration) throw new IOException("Package registration failed.");
            Registered = true;
        }
        public void Unregister() => Registered = false;
    }

    [Fact]
    public async Task CancellationBeforeRegistrationDoesNotWriteAnySettings()
    {
        var registry = new MemoryRegistry();
        var service = new WindowsShellIntegration(Executable, registry, _ => { }, () => { });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.AssociateAsync([".zip"], cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RegisterMenuAsync(cancellation.Token));

        Assert.Empty(registry.Values);
    }

    private sealed class MemoryRegistry : IWindowsIntegrationRegistry
    {
        internal Dictionary<(string Path, string Name), object> Values { get; } = new();
        internal Dictionary<string, string> Defaults { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal List<string> DeletedTrees { get; } = new();
        public string? GetString(string path, string name) => Values.TryGetValue((path, name), out var value) ? value as string : null;
        public string? GetDefaultProgId(string extension) => Defaults.GetValueOrDefault(extension);
        public void SetString(string path, string name, string value) => Values[(path, name)] = value;
        public void SetEmptyValue(string path, string name) => Values[(path, name)] = Array.Empty<byte>();
        public void DeleteTree(string path)
        {
            DeletedTrees.Add(path);
            foreach (var key in Values.Keys.Where(key => key.Path == path || key.Path.StartsWith(path + '\\', StringComparison.Ordinal)).ToArray())
                Values.Remove(key);
        }
    }
}
