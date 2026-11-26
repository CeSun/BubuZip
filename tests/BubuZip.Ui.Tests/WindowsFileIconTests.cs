using BubuZip.Desktop;
using Xunit;

namespace BubuZip.Ui.Tests;

public class WindowsFileIconTests
{
    [Fact]
    public void KeysUseOnlyTypeAndKeepFoldersDistinct()
    {
        Assert.Equal(".pdf", WindowsFileIconProvider.CacheKey("private/Report.PDF", false));
        Assert.Equal("", WindowsFileIconProvider.CacheKey("README", false));
        Assert.Equal(WindowsFileIconProvider.CacheKey("one.zip", true), WindowsFileIconProvider.CacheKey("two.txt", true));
        Assert.NotEqual(WindowsFileIconProvider.CacheKey("folder", true), WindowsFileIconProvider.CacheKey("folder", false));
    }

    [Fact]
    public async Task WindowsLoadsAndCachesIconsWithoutExistingFiles()
    {
        if (!OperatingSystem.IsWindows()) return;
        var provider = new WindowsFileIconProvider();
        var first = provider.GetIconAsync("nonexistent-file.TXT", false);
        Assert.Same(first, provider.GetIconAsync("another.txt", false));
        foreach (var image in new[] { await first, await provider.GetIconAsync("nonexistent-folder", true), await provider.GetIconAsync("unknown.bubuzipunknown", false) })
        {
            Assert.NotNull(image);
            Assert.Equal(32 * 32 * 4, image.Pixels.Length);
            var bytes = image.Pixels.ToArray();
            Assert.Contains(Enumerable.Range(0, 32 * 32), i => bytes[i * 4 + 3] > 0);
            Assert.Contains(Enumerable.Range(0, 32 * 32), i => bytes[i * 4 + 3] == 0);
        }
    }
}
