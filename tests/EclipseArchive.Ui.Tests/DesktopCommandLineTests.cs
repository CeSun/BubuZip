using EclipseArchive.Desktop;
using Xunit;

namespace EclipseArchive.Ui.Tests;

public sealed class DesktopCommandLineTests
{
    [Theory]
    [InlineData("open", ArchiveShellAction.Open)]
    [InlineData("extract", ArchiveShellAction.Extract)]
    [InlineData("extract-here", ArchiveShellAction.ExtractHere)]
    [InlineData("extract-folder", ArchiveShellAction.ExtractFolder)]
    [InlineData("test", ArchiveShellAction.Test)]
    [InlineData("compress", ArchiveShellAction.Compress)]
    [InlineData("compress-7z", ArchiveShellAction.CompressSevenZip)]
    [InlineData("compress-zip", ArchiveShellAction.CompressZip)]
    public void ShellCommandsPreserveEverySelectedPath(string verb, ArchiveShellAction action)
    {
        var options = DesktopCommandLine.Parse(["--shell", verb, "--", "资料 目录", "-leading-dash.zip"]);
        Assert.Equal(action, options.ShellRequest!.Action);
        Assert.Equal(new[] { Path.GetFullPath("资料 目录"), Path.GetFullPath("-leading-dash.zip") }, options.ShellRequest.Paths);
    }

    [Fact]
    public void OpenWithAcceptsMultiplePathsIncludingSpacesAndUnicode()
    {
        var paths = new[] { Path.Combine("文件 目录", "压缩 包.zip"), "second.7z" };
        var options = DesktopCommandLine.Parse(paths);

        Assert.Equal(paths.Select(Path.GetFullPath), options.ArchivePaths);
        Assert.Null(options.ShotPath);
        Assert.False(options.ShowHelp);
    }

    [Fact]
    public void ExistingOptionsCanBeCombinedWithAnArchive()
    {
        var options = DesktopCommandLine.Parse(["--frames", "5", "--path", "output folder", "archive.zip", "--dark"]);

        Assert.Equal(5, options.FrameLimit);
        Assert.Equal(Path.GetFullPath("output folder"), options.InitialPath);
        Assert.True(options.DarkPreview);
        Assert.Equal([Path.GetFullPath("archive.zip")], options.ArchivePaths);
    }

    [Fact]
    public void EndOfOptionsAllowsNamesBeginningWithADash()
    {
        var options = DesktopCommandLine.Parse(["--", "--dark", "-psn_0_123.zip"]);

        Assert.False(options.DarkPreview);
        Assert.Equal(new[] { Path.GetFullPath("--dark"), Path.GetFullPath("-psn_0_123.zip") }, options.ArchivePaths);
    }

    [Fact]
    public void FinderProcessSerialNumberIsIgnored()
    {
        var options = DesktopCommandLine.Parse(["-psn_0_123", "archive.zip"]);

        Assert.Equal([Path.GetFullPath("archive.zip")], options.ArchivePaths);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public void StandaloneHelpDoesNotOpenAnArchive(string argument)
    {
        var options = DesktopCommandLine.Parse([argument]);

        Assert.True(options.ShowHelp);
        Assert.Empty(options.ArchivePaths);
    }

    [Fact]
    public void PreviewModeRetainsItsOptions()
    {
        var options = DesktopCommandLine.Parse(["--shot", "preview image.png", "--dark"]);

        Assert.Equal(Path.GetFullPath("preview image.png"), options.ShotPath);
        Assert.True(options.DarkPreview);
        Assert.Empty(options.ArchivePaths);
    }

    [Fact]
    public void SettingsCanBeOpenedAndPreviewedWithoutPerformingAnIntegrationAction()
    {
        var options = DesktopCommandLine.Parse(["--settings", "--shot", "settings.png"]);
        Assert.True(options.OpenSettings);
        Assert.Null(options.ShellRequest);
        Assert.Empty(options.ArchivePaths);
    }

    [Theory]
    [InlineData("--unknown")]
    [InlineData("--shell")]
    [InlineData("--shell", "test")]
    [InlineData("--shell", "invalid", "file.zip")]
    [InlineData("--shell", "test", "--shell", "extract", "file.zip")]
    [InlineData("--shell", "test", "--shot", "preview.png", "file.zip")]
    [InlineData("--settings", "file.zip")]
    [InlineData("--frames")]
    [InlineData("--frames", "0")]
    [InlineData("--frames", "-1")]
    [InlineData("--frames", "no")]
    [InlineData("--path")]
    [InlineData("--path", "--dark")]
    [InlineData("--shot")]
    [InlineData("--shot", "")]
    [InlineData("--help", "archive.zip")]
    [InlineData("--shot", "preview.png", "archive.zip")]
    [InlineData("")]
    [InlineData("bad\0path.zip")]
    public void InvalidArgumentsAreRejected(params string[] arguments)
    {
        Assert.Throws<ArgumentException>(() => DesktopCommandLine.Parse(arguments));
    }
}
