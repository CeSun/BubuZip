using BubuZip.Desktop;
using Xunit;

namespace BubuZip.Ui.Tests;

public sealed class DesktopArchiveWindowsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bubuzip-launch-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void MacDocumentsLaunchAsNewForegroundBundleInstances(bool standardLayout, bool create)
    {
        var (bundle, executable) = MakeBundle(standardLayout);
        var path = Path.Combine(_root, "中文 空格 '$` 目录", "--archive.zip");
        string[] arguments = create ? ["--create", "--path", path] : ["--", path];

        var start = DesktopArchiveWindows.CreateStartInfo(executable, "unused.dll", arguments, isMacOS: true);

        Assert.Equal("/usr/bin/open", start.FileName);
        Assert.False(start.UseShellExecute);
        Assert.True(start.CreateNoWindow);
        Assert.True(start.RedirectStandardError);
        Assert.Empty(start.Arguments);
        Assert.Equal(new[] { "-n", "-a", bundle, "--args" }.Concat(arguments), start.ArgumentList);
        // The child receives precisely its command-line request, including the positional-only
        // separator for an archive, rather than a second Finder open-document event.
        var options = DesktopCommandLine.Parse(start.ArgumentList.Skip(4).ToArray());
        Assert.Equal(create, options.CreateArchive);
        if (create) Assert.Equal(path, options.InitialPath);
        else Assert.Equal([path], options.ArchivePaths);
    }

    [Fact]
    public void NonMacPlatformsKeepLaunchingTheirExecutableDirectly()
    {
        var (_, executable) = MakeBundle(standardLayout: false);
        var start = DesktopArchiveWindows.CreateStartInfo(executable, "unused.dll", ["--", "archive.zip"], isMacOS: false);

        Assert.Equal(executable, start.FileName);
        Assert.False(start.RedirectStandardError);
        Assert.Equal(new[] { "--", "archive.zip" }, start.ArgumentList);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DevelopmentLaunchDoesNotMistakeAppNamedDirectoriesForBundles(bool missingMetadata)
    {
        var (bundle, packagedExecutable) = MakeBundle(standardLayout: false);
        var executable = missingMetadata ? packagedExecutable : Path.Combine(bundle, "bin", "BubuZip");
        if (missingMetadata) File.Delete(Path.Combine(bundle, "Contents", "Info.plist"));

        var start = DesktopArchiveWindows.CreateStartInfo(executable, "unused.dll", ["--create"], isMacOS: true);

        Assert.Equal(executable, start.FileName);
        Assert.Equal(new[] { "--create" }, start.ArgumentList);
    }

    [Fact]
    public void ManagedDevelopmentHostUsesTheEntryAssemblyEvenInsideABundle()
    {
        var (_, packagedExecutable) = MakeBundle(standardLayout: false);
        var executable = Path.Combine(Path.GetDirectoryName(packagedExecutable)!, "dotnet");
        var assembly = Path.Combine(_root, "开发 目录", "BubuZip.dll");
        var start = DesktopArchiveWindows.CreateStartInfo(executable, assembly, ["--create"], isMacOS: true);

        Assert.Equal(executable, start.FileName);
        Assert.Equal(new[] { assembly, "--create" }, start.ArgumentList);
    }

    private (string Bundle, string Executable) MakeBundle(bool standardLayout)
    {
        var bundle = Path.Combine(_root, "应用 程序 '$`", "BubuZip.app");
        var contents = Path.Combine(bundle, "Contents");
        Directory.CreateDirectory(contents);
        File.WriteAllText(Path.Combine(contents, "Info.plist"), "<?xml version=\"1.0\"?><plist version=\"1.0\"><dict/></plist>");
        var directory = standardLayout ? Path.Combine(contents, "MacOS") : Path.Combine(contents, "Resources", "app");
        Directory.CreateDirectory(directory);
        return (bundle, Path.Combine(directory, "BubuZip"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
