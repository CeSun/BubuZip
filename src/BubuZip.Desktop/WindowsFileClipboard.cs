using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace BubuZip.Desktop;

[SupportedOSPlatform("windows")]
internal static class WindowsFileClipboard
{
    internal static byte[] FileDropData(IReadOnlyList<string> paths)
    {
        // DROPFILES: 20-byte header followed by a double-NUL-terminated UTF-16 path list.
        var names = Encoding.Unicode.GetBytes(string.Join('\0', paths) + "\0\0");
        var data = new byte[20 + names.Length];
        BitConverter.GetBytes(20u).CopyTo(data, 0);
        BitConverter.GetBytes(1).CopyTo(data, 16); // fWide
        names.CopyTo(data, 20);
        return data;
    }

    public static void Copy(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) throw new ArgumentException("请选择要复制的文件。", nameof(paths));
        var fullPaths = paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var path in fullPaths)
            if (!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException("复制的文件已不存在。", path);

        nint owner = 0, files = 0, effect = 0;
        var opened = false;
        try
        {
            files = Allocate(FileDropData(fullPaths));
            effect = Allocate(BitConverter.GetBytes(1u)); // DROPEFFECT_COPY, never move the extracted files.
            var effectFormat = RegisterClipboardFormatW("Preferred DropEffect");
            if (effectFormat == 0) throw Error();
            // A message-only owner avoids depending on whichever application has foreground focus.
            owner = CreateWindowExW(0, "STATIC", "", 0, 0, 0, 0, 0, -3, 0, 0, 0);
            if (owner == 0) throw Error();
            for (var attempt = 0; attempt < 6; attempt++)
            {
                if (OpenClipboard(owner)) { opened = true; break; }
                if (attempt < 5) Thread.Sleep(20);
            }
            if (!opened) throw new IOException("剪贴板正被其他程序使用，请稍后重试。", Error());
            if (!EmptyClipboard()) throw Error();
            // Publish paths last: if publishing fails, callers may safely discard the staged files.
            if (SetClipboardData(effectFormat, effect) == 0) throw Error();
            effect = 0;
            if (SetClipboardData(15, files) == 0) throw Error(); // CF_HDROP
            files = 0;
        }
        finally
        {
            if (opened) CloseClipboard();
            if (owner != 0) DestroyWindow(owner);
            if (files != 0) GlobalFree(files);
            if (effect != 0) GlobalFree(effect);
        }
    }

    private static nint Allocate(byte[] data)
    {
        var memory = GlobalAlloc(2, (nuint)data.Length); // GMEM_MOVEABLE; ownership transfers on SetClipboardData.
        if (memory == 0) throw Error();
        var pointer = GlobalLock(memory);
        if (pointer == 0) { var error = Error(); GlobalFree(memory); throw error; }
        try { Marshal.Copy(data, 0, pointer, data.Length); }
        catch { GlobalUnlock(memory); GlobalFree(memory); throw; }
        GlobalUnlock(memory);
        return memory;
    }

    private static Win32Exception Error() => new(Marshal.GetLastWin32Error());
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetClipboardData(uint format, nint memory);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint RegisterClipboardFormatW(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowExW(uint extendedStyle, string className, string name, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(nint window);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GlobalAlloc(uint flags, nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll")] private static extern nint GlobalFree(nint memory);
}
