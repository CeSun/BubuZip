namespace EclipseArchive.Desktop;

/// <summary>Uses the desktop's GTK dialogs and freedesktop file-management utilities.</summary>
public sealed class LinuxDesktopServices : IDesktopServices, IAsyncDesktopDialogService
{
    private const string Separator = "\u001f";
    private readonly Func<string, IEnumerable<string>, DesktopProcess.Result> _run;
    public LinuxDesktopServices() : this((command, args) => DesktopProcess.Run(command, args)) { }
    internal LinuxDesktopServices(Func<string, IEnumerable<string>, DesktopProcess.Result> run) => _run = run;
    public Task<T> ShowDialogAsync<T>(Func<T> showDialog) => Task.Run(showDialog);

    public IReadOnlyList<string> ChooseInputs()
    {
        var choice = _run("zenity", ["--question", "--title=选择压缩内容", "--text=添加文件还是文件夹？", "--ok-label=文件", "--cancel-label=取消", "--extra-button=文件夹"]);
        if (choice.Output.Trim() == "文件夹") return ChooseFolder() is { } folder ? [folder] : [];
        if (choice.ExitCode == 1) return [];
        DesktopProcess.EnsureSuccess("选择压缩内容", choice);
        return Choose(["--file-selection", "--multiple", "--separator=" + Separator, "--title=选择要压缩的文件"]);
    }
    public string? ChooseArchive() => Choose(["--file-selection", "--title=打开压缩包",
        "--file-filter=压缩文件 | *.zip *.7z *.rar *.tar *.gz *.tgz *.bz2 *.xz *.lz *.zst",
        "--file-filter=所有文件 | *"]).FirstOrDefault();
    public string? ChooseFolder(string? initialDirectory = null)
    {
        var args = new List<string> { "--file-selection", "--directory", "--title=选择文件夹" };
        if (!string.IsNullOrEmpty(initialDirectory) && Directory.Exists(initialDirectory)) args.Add("--filename=" + Path.GetFullPath(initialDirectory).TrimEnd('/') + "/");
        return Choose(args).FirstOrDefault();
    }
    public string? ChooseSavePath(string suggestedName, string? initialDirectory = null)
    {
        var name = Path.GetFileName(suggestedName);
        var path = Path.Combine(initialDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), name);
        return Choose(["--file-selection", "--save", "--title=保存压缩包", "--filename=" + path]).FirstOrDefault();
    }
    private IReadOnlyList<string> Choose(IEnumerable<string> args)
    {
        var result = _run("zenity", args);
        if (result.ExitCode == 1) return [];
        DesktopProcess.EnsureSuccess("文件选择", result);
        var output = result.Output.EndsWith('\n') ? result.Output[..^1] : result.Output;
        return output.Split(Separator, StringSplitOptions.RemoveEmptyEntries);
    }
    public bool Confirm(string title, string message, string confirmLabel)
    {
        var result = _run("zenity", ["--question", "--title=" + title, "--text=" + message, "--ok-label=" + confirmLabel, "--cancel-label=取消", "--no-markup"]);
        if (result.ExitCode == 1) return false;
        DesktopProcess.EnsureSuccess("确认对话框", result);
        return true;
    }
    public void ShowMessage(string title, string message)
    {
        var result = _run("zenity", ["--info", "--title=" + title, "--text=" + message, "--no-markup"]);
        if (result.ExitCode != 1) DesktopProcess.EnsureSuccess("信息对话框", result);
    }
    public void Reveal(string path)
    {
        var fullPath = Path.GetFullPath(path);
        Open(Directory.Exists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath)!);
    }
    public void Open(string path) => DesktopProcess.EnsureSuccess("打开文件", _run("xdg-open", [Path.GetFullPath(path)]));
    public void Trash(IReadOnlyList<string> paths)
    {
        var fullPaths = paths.Select(Path.GetFullPath).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var path in fullPaths)
            if (!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException("项目不存在。", path);
        if (fullPaths.Length > 0) DesktopProcess.EnsureSuccess("移到废纸篓", _run("gio", new[] { "trash", "--" }.Concat(fullPaths)));
    }
}
