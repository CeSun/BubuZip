using System.Runtime.InteropServices;
using System.Text;
using BubuZip.Desktop;
using Xunit;

namespace BubuZip.Ui.Tests;

public sealed class WindowsFileClipboardTests
{
    [Fact]
    public void WindowsShellReadsUnicodeFileAndFolderPathsFromTheClipboardPayload()
    {
        if (!OperatingSystem.IsWindows()) return;
        string[] paths = [@"C:\临时文件\中文 空格.txt", @"C:\临时文件\folder & items"];
        var data = WindowsFileClipboard.FileDropData(paths);
        var memory = GlobalAlloc(2, (nuint)data.Length);
        Assert.NotEqual(0, memory);
        try
        {
            var pointer = GlobalLock(memory);
            Assert.NotEqual(0, pointer);
            try { Marshal.Copy(data, 0, pointer, data.Length); }
            finally { GlobalUnlock(memory); }
            Assert.Equal(2u, DragQueryFileW(memory, uint.MaxValue, null, 0));
            for (uint index = 0; index < paths.Length; index++)
            {
                var path = new StringBuilder(32768);
                Assert.Equal((uint)paths[index].Length, DragQueryFileW(memory, index, path, (uint)path.Capacity));
                Assert.Equal(paths[index], path.ToString());
            }
        }
        finally { GlobalFree(memory); }
    }

    [Fact]
    public void InvalidFileListsAreRejectedBeforeOpeningTheSystemClipboard()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Throws<ArgumentException>(() => { if (OperatingSystem.IsWindows()) WindowsFileClipboard.Copy([]); });
        Assert.Throws<FileNotFoundException>(() => { if (OperatingSystem.IsWindows()) WindowsFileClipboard.Copy([Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))]); });
    }

    [DllImport("kernel32.dll")] private static extern nint GlobalAlloc(uint flags, nuint size);
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll")] private static extern nint GlobalFree(nint memory);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint DragQueryFileW(nint files, uint index, StringBuilder? path, uint count);
}
