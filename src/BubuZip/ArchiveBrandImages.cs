using System.IO.Compression;
using EclipseUI.Rendering;

namespace BubuZip;

internal static class ArchiveBrandImages
{
    private static readonly Lazy<ImageHandle> ApplicationImage = new(() => Load("Application", 256));
    private static readonly Lazy<ImageHandle> ArchiveImage = new(() => Load("Archive", 64));

    public static ImageHandle Application => ApplicationImage.Value;
    public static ImageHandle Archive => ArchiveImage.Value;

    // Predecoded RGBA resources keep image loading portable and NativeAOT-compatible.
    private static ImageHandle Load(string name, int size)
    {
        using var resource = typeof(ArchiveBrandImages).Assembly.GetManifestResourceStream("BubuZip.Icons." + name)
            ?? throw new InvalidOperationException("Missing brand image: " + name);
        using var gzip = new GZipStream(resource, CompressionMode.Decompress);
        var pixels = new byte[size * size * 4];
        gzip.ReadExactly(pixels);
        return new ImageHandle(size, size, pixels);
    }
}
