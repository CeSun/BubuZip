using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Microsoft.VisualBasic.FileIO;

namespace EclipseArchive.Desktop;

[SupportedOSPlatform("windows")]
public sealed class WindowsDesktopServices : IDesktopServices, IAsyncDesktopDialogService
{
    private const string DialogScript = """
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false
        Add-Type -AssemblyName System.Windows.Forms
        $owner = $null
        if ($env:ECLIPSE_DIALOG_OWNER -and $env:ECLIPSE_DIALOG_OWNER -ne '0') {
            Add-Type -ReferencedAssemblies System.Windows.Forms -TypeDefinition @'
            using System;
            using System.Windows.Forms;
            public sealed class EclipseDialogOwner : IWin32Window {
                public EclipseDialogOwner(long handle) { Handle = new IntPtr(handle); }
                public IntPtr Handle { get; private set; }
            }
        '@
            $owner = New-Object EclipseDialogOwner -ArgumentList ([Int64]$env:ECLIPSE_DIALOG_OWNER)
        }
        function Show-Picker($dialog) {
            if ($null -ne $owner) { return $dialog.ShowDialog($owner) }
            return $dialog.ShowDialog()
        }
        $mode = $env:ECLIPSE_DIALOG_MODE
        if ($mode -eq 'inputs') {
            $choice = if ($null -ne $owner) {
                [System.Windows.Forms.MessageBox]::Show($owner, '选择“是”添加文件，选择“否”添加文件夹。', '选择压缩内容', 'YesNoCancel', 'Question')
            } else {
                [System.Windows.Forms.MessageBox]::Show('选择“是”添加文件，选择“否”添加文件夹。', '选择压缩内容', 'YesNoCancel', 'Question')
            }
            if ($choice -eq 'Cancel') { exit 1 }
            if ($choice -eq 'No') { $mode = 'folder' } else { $mode = 'files' }
        }
        if ($mode -eq 'folder') {
            $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
            $dialog.Description = '选择文件夹'
            $dialog.ShowNewFolderButton = $true
            if ($env:ECLIPSE_DIALOG_DIRECTORY) { $dialog.SelectedPath = $env:ECLIPSE_DIALOG_DIRECTORY }
            if ((Show-Picker $dialog) -ne 'OK') { exit 1 }
            $paths = @($dialog.SelectedPath)
        } elseif ($mode -eq 'save') {
            $dialog = New-Object System.Windows.Forms.SaveFileDialog
            $dialog.Title = '保存压缩包'
            $dialog.FileName = $env:ECLIPSE_DIALOG_NAME
            $dialog.OverwritePrompt = $false
            $dialog.AddExtension = $false
            if ($env:ECLIPSE_DIALOG_DIRECTORY) { $dialog.InitialDirectory = $env:ECLIPSE_DIALOG_DIRECTORY }
            if ((Show-Picker $dialog) -ne 'OK') { exit 1 }
            $paths = @($dialog.FileName)
        } else {
            $dialog = New-Object System.Windows.Forms.OpenFileDialog
            $dialog.Title = if ($mode -eq 'files') { '选择要压缩的文件' } else { '打开压缩包' }
            $dialog.Multiselect = $mode -eq 'files'
            if ($mode -eq 'archive') {
                $dialog.Filter = '压缩文件|*.zip;*.zipx;*.7z;*.rar;*.tar;*.gz;*.gzip;*.tgz;*.bz2;*.bzip2;*.tbz;*.tbz2;*.xz;*.txz;*.lz;*.lzip;*.tlz;*.zst;*.zstd;*.tzst;*.z;*.taz;*.arc;*.arj;*.ace|所有文件|*.*'
            }
            if ((Show-Picker $dialog) -ne 'OK') { exit 1 }
            $paths = @($dialog.FileNames)
        }
        [Console]::Out.Write((ConvertTo-Json -InputObject @($paths) -Compress))
        """;

    public Task<T> ShowDialogAsync<T>(Func<T> showDialog) => Task.Run(showDialog);

    public IReadOnlyList<string> ChooseInputs() => Choose("inputs");
    public string? ChooseArchive() => Choose("archive").FirstOrDefault();
    public string? ChooseFolder(string? initialDirectory = null) => Choose("folder", initialDirectory).FirstOrDefault();
    public string? ChooseSavePath(string suggestedName, string? initialDirectory = null)
        => Choose("save", initialDirectory, Path.GetFileName(suggestedName)).FirstOrDefault();

    private static IReadOnlyList<string> Choose(string mode, string? directory = null, string? name = null)
    {
        var script = Convert.ToBase64String(Encoding.Unicode.GetBytes(DialogScript));
        var environment = new Dictionary<string, string>
        {
            ["ECLIPSE_DIALOG_MODE"] = mode,
            ["ECLIPSE_DIALOG_DIRECTORY"] = directory ?? "",
            ["ECLIPSE_DIALOG_NAME"] = name ?? "",
            ["ECLIPSE_DIALOG_OWNER"] = Process.GetCurrentProcess().MainWindowHandle.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var result = DesktopProcess.Run(executable, ["-NoProfile", "-NonInteractive", "-STA", "-EncodedCommand", script], environment);
        if (result.ExitCode == 1 && string.IsNullOrWhiteSpace(result.Error)) return [];
        DesktopProcess.EnsureSuccess("文件选择", result);
        return JsonSerializer.Deserialize<string[]>(result.Output) ?? [];
    }

    public bool Confirm(string title, string message, string confirmLabel)
        => MessageBox(0, message, title, 0x00000001 | 0x00000030 | 0x00010000) == 1;
    public void ShowMessage(string title, string message) => MessageBox(0, message, title, 0x00000040 | 0x00010000);
    public void Open(string path)
    {
        var fullPath = Path.GetFullPath(path);
        Process.Start(new ProcessStartInfo(fullPath) { UseShellExecute = true });
    }
    public void Reveal(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        if (Directory.Exists(fullPath)) info.ArgumentList.Add(fullPath);
        else { info.ArgumentList.Add("/select,"); info.ArgumentList.Add(fullPath); }
        Process.Start(info);
    }
    public void Trash(IReadOnlyList<string> paths)
    {
        var fullPaths = paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var path in fullPaths)
            if (!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException("项目不存在。", path);
        foreach (var path in fullPaths)
            if (Directory.Exists(path)) FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
            else FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBox(nint owner, string text, string caption, uint type);
}
