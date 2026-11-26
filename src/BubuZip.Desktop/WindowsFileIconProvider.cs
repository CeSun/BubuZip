using System.Runtime.InteropServices;
using EclipseUI;
using EclipseUI.Rendering;

namespace BubuZip.Desktop;

internal sealed class WindowsFileIconProvider : IFileIconProvider
{
    private readonly FileTypeIconCache _cache = new(ReadIcon);

    public Task<IRasterImage?> GetIconAsync(string name, bool isDirectory)
    {
        if (!OperatingSystem.IsWindows()) return Task.FromResult<IRasterImage?>(null);
        return _cache.GetIconAsync(name, isDirectory);
    }

    private static IRasterImage? ReadIcon(string extension, bool directory)
    {
        var com = CoInitializeEx(0, 0);
        try
        {
            // Synthetic names plus USEFILEATTRIBUTES never extract or inspect archive entries.
            var result = SHGetFileInfoW(directory ? "folder" : "file" + extension,
                directory ? 0x10u : 0x80u, out var info, (uint)Marshal.SizeOf<ShellFileInfo>(), 0x100 | 0x10);
            try
            {
                if (result == 0 || info.Icon == 0) return null;
                var black = Render(info.Icon, 0);
                var white = Render(info.Icon, 255);
                if (black is null || white is null) return null;
                var rgba = new byte[black.Length];
                for (var i = 0; i < rgba.Length; i += 4)
                {
                    // Recover straight alpha, including legacy mask icons, from two composites.
                    var alpha = 255 - Math.Clamp(white[i] - black[i], 0, 255);
                    rgba[i + 3] = (byte)alpha;
                    if (alpha == 0) continue;
                    rgba[i] = (byte)Math.Min(255, black[i + 2] * 255 / alpha);
                    rgba[i + 1] = (byte)Math.Min(255, black[i + 1] * 255 / alpha);
                    rgba[i + 2] = (byte)Math.Min(255, black[i] * 255 / alpha);
                }
                return new ImageHandle(32, 32, rgba);
            }
            finally { if (info.Icon != 0) DestroyIcon(info.Icon); }
        }
        finally { if (com >= 0) CoUninitialize(); }
    }

    private static byte[]? Render(nint icon, byte background)
    {
        var dc = CreateCompatibleDC(0);
        if (dc == 0) return null;
        nint bitmap = 0, previous = 0;
        try
        {
            var info = new BitmapInfo { Size = 40, Width = 32, Height = -32, Planes = 1, Bits = 32 };
            bitmap = CreateDIBSection(dc, ref info, 0, out var pixels, 0, 0);
            if (bitmap == 0 || pixels == 0) return null;
            previous = SelectObject(dc, bitmap);
            if (previous == 0 || previous == -1) return null;
            var buffer = new byte[32 * 32 * 4];
            Array.Fill(buffer, background);
            Marshal.Copy(buffer, 0, pixels, buffer.Length);
            if (!DrawIconEx(dc, 0, 0, icon, 32, 32, 0, 0, 3)) return null;
            GdiFlush();
            Marshal.Copy(pixels, buffer, 0, buffer.Length);
            return buffer;
        }
        finally
        {
            if (previous != 0 && previous != -1) SelectObject(dc, previous);
            if (bitmap != 0) DeleteObject(bitmap);
            DeleteDC(dc);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public nint Icon;
        public int Index;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, Bits;
        public uint Compression, ImageSize;
        public int XPels, YPels;
        public uint ColorsUsed, ColorsImportant, Color;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nuint SHGetFileInfoW(string path, uint attributes, out ShellFileInfo info, uint size, uint flags);
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint mode);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(nint dc, int x, int y, nint icon, int width, int height, uint step, nint brush, uint flags);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint pixels, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
}
