using BubuZip.Desktop;
using BubuUI;
using Xunit;

namespace BubuZip.Ui.Tests;

public class MacFileIconTests
{
    [Fact]
    public async Task VirtualFilesWithTheSameExtensionShareTheirIcon()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var provider = new MacFileIconProvider();
        var missingDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        var icons = await Task.WhenAll(
            provider.GetIconAsync(Path.Combine(missingDirectory, "报告.PDF"), false),
            provider.GetIconAsync("another.pdf", false));

        AssertVisibleIcon(icons[0]);
        Assert.Same(icons[0], icons[1]);
        Assert.False(Directory.Exists(missingDirectory));
    }

    [Fact]
    public async Task FoldersWithFileExtensionsRemainDistinctFromDocuments()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var provider = new MacFileIconProvider();
        var folder = await provider.GetIconAsync("report.pdf", true);
        var document = await provider.GetIconAsync("report.pdf", false);

        AssertVisibleIcon(folder);
        AssertVisibleIcon(document);
        Assert.Same(folder, await provider.GetIconAsync("照片", true));
        Assert.False(folder!.Pixels.Span.SequenceEqual(document!.Pixels.Span));
    }

    [Theory]
    [InlineData("README")]
    [InlineData("document.bubuzipunknowntype")]
    public async Task FilesWithoutAKnownTypeGetAVisibleFileIcon(string name)
    {
        if (!OperatingSystem.IsMacOS()) return;
        AssertVisibleIcon(await new MacFileIconProvider().GetIconAsync(name, false));
    }

    [Fact]
    public async Task HiddenFilesOnlyUseAnExtensionAfterTheLeadingDot()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var provider = new MacFileIconProvider();
        var generic = await provider.GetIconAsync("README", false);
        var pdf = await provider.GetIconAsync("example.pdf", false);

        AssertVisibleIcon(generic);
        AssertVisibleIcon(pdf);
        Assert.Same(generic, await provider.GetIconAsync("archive/.pdf", false));
        Assert.Same(pdf, await provider.GetIconAsync("archive/.hidden.PDF", false));
    }

    private static void AssertVisibleIcon(IRasterImage? icon)
    {
        Assert.NotNull(icon);
        Assert.Equal(icon.Width * icon.Height * 4, icon.Pixels.Length);
        Assert.Contains(Enumerable.Range(0, icon.Width * icon.Height),
            index => icon.Pixels.Span[index * 4 + 3] != 0);
    }
}
