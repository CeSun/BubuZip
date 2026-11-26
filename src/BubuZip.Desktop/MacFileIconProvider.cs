using System.Runtime.InteropServices;
using EclipseUI;
using EclipseUI.Rendering;

namespace BubuZip.Desktop;

internal sealed class MacFileIconProvider : IFileIconProvider
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const int PixelSize = 40; // The file list draws at 20 points, including on Retina displays.
    private readonly FileTypeIconCache _cache = new(ReadIcon);

    public Task<IRasterImage?> GetIconAsync(string name, bool isDirectory)
    {
        if (!OperatingSystem.IsMacOS()) return Task.FromResult<IRasterImage?>(null);
        // Finder treats a leading dot alone as part of the basename (e.g. ".profile").
        name = Path.GetFileName(name);
        if (name.StartsWith('.') && !name.AsSpan(1).Contains('.')) name = string.Empty;
        return _cache.GetIconAsync(name, isDirectory);
    }

    // Also load AppKit and UniformTypeIdentifiers when called from the headless preview.
    private static readonly Lazy<nint> WorkspaceClass = new(() => LoadClass("AppKit", "NSWorkspace"));
    private static readonly Lazy<nint> TypeClass = new(() => LoadClass("UniformTypeIdentifiers", "UTType"));

    private static nint LoadClass(string framework, string name)
    {
        NativeLibrary.Load($"/System/Library/Frameworks/{framework}.framework/{framework}");
        return GetClass(name);
    }

    private static IRasterImage? ReadIcon(string extension, bool directory)
    {
        var pool = AutoreleasePoolPush();
        try
        {
            var workspace = Send(WorkspaceClass.Value, Selector("sharedWorkspace"));
            nint type = 0;
            if (!directory && extension.Length > 1)
                type = SendObject(TypeClass.Value, Selector("typeWithFilenameExtension:"), String(extension[1..]));
            if (type == 0)
                type = SendObject(TypeClass.Value, Selector("typeWithIdentifier:"), String(directory ? "public.folder" : "public.data"));
            if (type == 0) return null;

            var image = SendObject(workspace, Selector("iconForContentType:"), type);
            var rect = new NativeRect(0, 0, PixelSize, PixelSize);
            var cgImage = GetImage(image, Selector("CGImageForProposedRect:context:hints:"), ref rect, 0, 0);
            return cgImage == 0 ? null : Rasterize(cgImage);
        }
        finally { AutoreleasePoolPop(pool); }
    }

    private static IRasterImage? Rasterize(nint image)
    {
        var srgb = Send(GetClass("NSColorSpace"), Selector("sRGBColorSpace"));
        var colorSpace = Send(srgb, Selector("CGColorSpace"));
        if (colorSpace == 0) return null;
        const uint rgbaPremultipliedLast = 0x4001; // kCGBitmapByteOrder32Big | kCGImageAlphaPremultipliedLast
        var context = CGBitmapContextCreate(0, PixelSize, PixelSize, 8, PixelSize * 4, colorSpace, rgbaPremultipliedLast);
        if (context == 0) return null;
        try
        {
            var rect = new NativeRect(0, 0, PixelSize, PixelSize);
            CGContextClearRect(context, rect);
            CGContextSetInterpolationQuality(context, 3); // kCGInterpolationHigh
            CGContextDrawImage(context, rect, image);
            var pixels = new byte[PixelSize * PixelSize * 4];
            Marshal.Copy(CGBitmapContextGetData(context), pixels, 0, pixels.Length);
            // EclipseUI takes straight-alpha sRGB, while Quartz draws premultiplied RGBA.
            for (var index = 0; index < pixels.Length; index += 4)
            {
                var alpha = pixels[index + 3];
                if (alpha is 0 or 255) continue;
                for (var channel = index; channel < index + 3; channel++)
                    pixels[channel] = (byte)Math.Min(255, (pixels[channel] * 255 + alpha / 2) / alpha);
            }
            return new ImageHandle(PixelSize, PixelSize, pixels);
        }
        finally { CGContextRelease(context); }
    }

    private static nint String(string value) =>
        StringWithCharacters(GetClass("NSString"), Selector("stringWithCharacters:length:"), value, (nuint)value.Length);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeRect(double X, double Y, double Width, double Height);

    [DllImport(ObjC, EntryPoint = "objc_autoreleasePoolPush")] private static extern nint AutoreleasePoolPush();
    [DllImport(ObjC, EntryPoint = "objc_autoreleasePoolPop")] private static extern void AutoreleasePoolPop(nint pool);
    [DllImport(ObjC, EntryPoint = "objc_getClass")] private static extern nint GetClass(string name);
    [DllImport(ObjC, EntryPoint = "sel_registerName")] private static extern nint Selector(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint Send(nint receiver, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint SendObject(nint receiver, nint selector, nint value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint StringWithCharacters(nint receiver, nint selector, [MarshalAs(UnmanagedType.LPWStr)] string text, nuint length);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint GetImage(nint receiver, nint selector, ref NativeRect rect, nint context, nint hints);
    [DllImport(CoreGraphics)] private static extern nint CGBitmapContextCreate(nint data, nuint width, nuint height, nuint bitsPerComponent, nuint bytesPerRow, nint colorSpace, uint bitmapInfo);
    [DllImport(CoreGraphics)] private static extern nint CGBitmapContextGetData(nint context);
    [DllImport(CoreGraphics)] private static extern void CGContextClearRect(nint context, NativeRect rect);
    [DllImport(CoreGraphics)] private static extern void CGContextSetInterpolationQuality(nint context, int quality);
    [DllImport(CoreGraphics)] private static extern void CGContextDrawImage(nint context, NativeRect rect, nint image);
    [DllImport(CoreGraphics)] private static extern void CGContextRelease(nint context);
}
