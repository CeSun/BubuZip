using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace BubuZip.Desktop;

internal static class DesktopProcess
{
    internal readonly record struct Result(int ExitCode, string Output, string Error);

    internal static Result Run(string executable, IEnumerable<string> arguments, IReadOnlyDictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (environment is not null)
            foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
        try
        {
            using var process = Process.Start(start) ?? throw new IOException($"无法启动 {executable}。");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            return new(process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
        }
        catch (Win32Exception error)
        {
            throw new IOException($"无法启动 {executable}。请确认所需的桌面组件已安装。", error);
        }
    }

    internal static void EnsureSuccess(string action, Result result)
    {
        if (result.ExitCode != 0)
            throw new IOException($"{action}失败：{(string.IsNullOrWhiteSpace(result.Error) ? $"退出代码 {result.ExitCode}" : result.Error.Trim())}");
    }
}
