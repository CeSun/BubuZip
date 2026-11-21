using System.Diagnostics;

namespace BubuZip.Desktop;

/// <summary>Each document owns its UI loop, so closing one window leaves the others usable.</summary>
internal sealed class DesktopArchiveWindows : IArchiveWindows
{
    public void Create(string initialPath) => Launch(["--create", "--path", initialPath]);
    public void Open(string archivePath) => Launch(["--", archivePath]);

    internal static ProcessStartInfo CreateStartInfo(string executable, string applicationPath, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        // dotnet run uses the managed entry assembly; published apps launch their own executable.
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(applicationPath);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    private static void Launch(IEnumerable<string> arguments)
    {
        var executable = Environment.ProcessPath ?? throw new IOException("无法确定 BubuZip 的启动路径。");
        var start = CreateStartInfo(executable, Path.Combine(AppContext.BaseDirectory, "BubuZip.dll"), arguments);
        using var process = Process.Start(start) ?? throw new IOException("无法打开 BubuZip 窗口。");
    }
}
