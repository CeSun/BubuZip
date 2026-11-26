using BubuZip.Desktop;
using Xunit;

namespace BubuZip.Ui.Tests;

public class WindowsFileIconTests
{
    [Fact]
    public void KeysUseOnlyTypeAndKeepFoldersDistinct()
    {
        Assert.Equal(".pdf", FileTypeIconCache.CacheKey("private/Report.PDF", false));
        Assert.Equal("", FileTypeIconCache.CacheKey("README", false));
        Assert.Equal(FileTypeIconCache.CacheKey("one.zip", true), FileTypeIconCache.CacheKey("two.txt", true));
        Assert.NotEqual(FileTypeIconCache.CacheKey("folder", true), FileTypeIconCache.CacheKey("folder", false));
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
