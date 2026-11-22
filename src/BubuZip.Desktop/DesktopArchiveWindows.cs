using System.Diagnostics;

namespace BubuZip.Desktop;

/// <summary>Each document owns its UI loop, so closing one window leaves the others usable.</summary>
internal sealed class DesktopArchiveWindows : IArchiveWindows
{
    public void Create(string initialPath) => Launch(["--create", "--path", initialPath]);
    public void Open(string archivePath) => Launch(["--", archivePath]);

    internal static ProcessStartInfo CreateStartInfo(string executable, string applicationPath, IEnumerable<string> arguments)
        => CreateStartInfo(executable, applicationPath, arguments, OperatingSystem.IsMacOS());

    internal static ProcessStartInfo CreateStartInfo(string executable, string applicationPath, IEnumerable<string> arguments, bool isMacOS)
    {
        var managedHost = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        var bundle = isMacOS && !managedHost ? FindApplicationBundle(executable) : null;
        var start = new ProcessStartInfo(bundle is null ? executable : "/usr/bin/open")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = bundle is not null,
        };
        if (bundle is not null)
        {
            // Launch Services supplies the bundle's Dock identity and foreground activation.
            // -n keeps each document in its own process; --args passes paths without shell parsing.
            start.ArgumentList.Add("-n");
            start.ArgumentList.Add("-a");
            start.ArgumentList.Add(bundle);
            start.ArgumentList.Add("--args");
        }
        // An unbundled dotnet launch needs the managed entry assembly before application arguments.
        else if (managedHost)
            start.ArgumentList.Add(applicationPath);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    private static string? FindApplicationBundle(string executable)
    {
        if (!Path.IsPathFullyQualified(executable)) return null;
        var directory = new FileInfo(executable).Directory;
        // Accept the packaged payload and standard Cocoa executable layouts only. An unrelated
        // development directory ending in .app must not turn into a different application launch.
        var contents = directory?.Name == "MacOS" ? directory.Parent
            : directory?.Name == "app" && directory.Parent?.Name == "Resources" ? directory.Parent.Parent
            : null;
        return contents?.Name == "Contents" && contents.Parent is { } bundle
            && bundle.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
            && File.Exists(Path.Combine(contents.FullName, "Info.plist"))
            ? bundle.FullName : null;
    }

    private static void Launch(IEnumerable<string> arguments)
    {
        var executable = Environment.ProcessPath ?? throw new IOException("无法确定 BubuZip 的启动路径。");
        var start = CreateStartInfo(executable, Path.Combine(AppContext.BaseDirectory, "BubuZip.dll"), arguments);
        using var process = Process.Start(start) ?? throw new IOException("无法打开 BubuZip 窗口。");
        if (!start.RedirectStandardError) return;

        // Only wait for Launch Services to accept the request, never for the document to close.
        var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10_000))
            throw new IOException("macOS 应用打开服务未及时响应。");
        var error = errors.GetAwaiter().GetResult().Trim();
        if (process.ExitCode != 0)
            throw new IOException($"无法打开 BubuZip 窗口：{(error.Length == 0 ? "macOS 应用打开服务返回失败。" : error)}");
    }
}
