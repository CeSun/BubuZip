using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BubuZip.Desktop;

/// <summary>AppKit dialogs run synchronously on the Cocoa main thread.</summary>
public sealed class MacDesktopServices : IDesktopServices
{
    private const string AppKitPath = "/System/Library/Frameworks/AppKit.framework/AppKit";
    private const string ObjCLibrary = "/usr/lib/libobjc.A.dylib";
    private static readonly Lazy<nint> AppKit = new(() => NativeLibrary.Load(AppKitPath));

    public IReadOnlyList<string> ChooseFiles(string? initialDirectory = null)
    {
        using var scope = EnterAppKit();
        var panel = CreateOpenPanel("选择要压缩的文件", files: true, directories: false, multiple: true);
        SetText(panel, "setPrompt:", "添加");
        SetInitialDirectory(panel, initialDirectory);
        if (Send(panel, Selector("runModal")) != 1)
            return Array.Empty<string>();

        var urls = Send(panel, Selector("URLs"));
        var count = checked((int)Send(urls, Selector("count")));
        var paths = new List<string>(count);
        for (var index = 0; index < count; index++)
        {
            var url = SendIndex(urls, Selector("objectAtIndex:"), (nuint)index);
            if (FilePath(url) is { } path)
                paths.Add(path);
        }
        return paths;
    }

    public string? ChooseArchive()
    {
        using var scope = EnterAppKit();
        var panel = CreateOpenPanel("打开压缩包", files: true, directories: false, multiple: false);
        return Send(panel, Selector("runModal")) == 1
            ? FilePath(Send(panel, Selector("URL")))
            : null;
    }

    public string? ChooseFolder(string? initialDirectory = null)
    {
        using var scope = EnterAppKit();
        var panel = CreateOpenPanel("选择文件夹", files: false, directories: true, multiple: false);
        SetText(panel, "setPrompt:", "选择");
        SendBool(panel, Selector("setCanCreateDirectories:"), true);
        SetInitialDirectory(panel, initialDirectory);
        return Send(panel, Selector("runModal")) == 1
            ? FilePath(Send(panel, Selector("URL")))
            : null;
    }

    public string? ChooseSavePath(string suggestedName, string? initialDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suggestedName);
        var fileName = Path.GetFileName(suggestedName);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        using var scope = EnterAppKit();
        var panel = Send(Class("NSSavePanel"), Selector("savePanel"));
        if (panel == 0)
            throw new InvalidOperationException("macOS 无法创建保存对话框。");
        SetText(panel, "setTitle:", "保存压缩包");
        SetText(panel, "setPrompt:", "保存");
        SetText(panel, "setNameFieldStringValue:", fileName);
        SendBool(panel, Selector("setCanCreateDirectories:"), true);
        SendBool(panel, Selector("setExtensionHidden:"), false);
        SetInitialDirectory(panel, initialDirectory);
        return Send(panel, Selector("runModal")) == 1
            ? FilePath(Send(panel, Selector("URL")))
            : null;
    }

    public bool Confirm(string title, string message, string confirmLabel)
    {
        using var scope = EnterAppKit();
        var alert = CreateAlert(title, message);
        try
        {
            SendInteger(alert, Selector("setAlertStyle:"), 0); // NSAlertStyleWarning
            SendObject(alert, Selector("addButtonWithTitle:"), String(confirmLabel));
            SendObject(alert, Selector("addButtonWithTitle:"), String("取消"));
            return Send(alert, Selector("runModal")) == 1000; // NSAlertFirstButtonReturn
        }
        finally
        {
            SendVoid(alert, Selector("release"));
        }
    }

    public void ShowMessage(string title, string message)
    {
        using var scope = EnterAppKit();
        var alert = CreateAlert(title, message);
        try
        {
            SendInteger(alert, Selector("setAlertStyle:"), 1); // NSAlertStyleInformational
            SendObject(alert, Selector("addButtonWithTitle:"), String("好"));
            Send(alert, Selector("runModal"));
        }
        finally
        {
            SendVoid(alert, Selector("release"));
        }
    }

    public void Reveal(string path) => RunOpen(path, reveal: true);

    public void Open(string path) => RunOpen(path, reveal: false);

    public void Trash(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        // Validate the entire list before moving the first item.
        var fullPaths = paths.Select(FullPath).Distinct(StringComparer.Ordinal).ToArray();
        if (fullPaths.Length == 0)
            return;

        using var scope = EnterAppKit();
        var manager = Send(Class("NSFileManager"), Selector("defaultManager"));
        foreach (var path in fullPaths)
        {
            var url = FileUrl(path);
            if (!SendTrash(manager, Selector("trashItemAtURL:resultingItemURL:error:"), url, 0, out var error))
            {
                var reason = error == 0 ? null : ReadString(Send(error, Selector("localizedDescription")));
                throw new IOException($"无法将“{path}”移到废纸篓：{reason ?? "macOS 未返回错误说明。"}");
            }
        }
    }

    private static nint CreateOpenPanel(string title, bool files, bool directories, bool multiple)
    {
        var panel = Send(Class("NSOpenPanel"), Selector("openPanel"));
        if (panel == 0)
            throw new InvalidOperationException("macOS 无法创建文件选择对话框。");
        SetText(panel, "setTitle:", title);
        SendBool(panel, Selector("setCanChooseFiles:"), files);
        SendBool(panel, Selector("setCanChooseDirectories:"), directories);
        SendBool(panel, Selector("setAllowsMultipleSelection:"), multiple);
        SendBool(panel, Selector("setResolvesAliases:"), true);
        return panel;
    }

    private static nint CreateAlert(string title, string message)
    {
        var alert = Send(Send(Class("NSAlert"), Selector("alloc")), Selector("init"));
        if (alert == 0)
            throw new InvalidOperationException("macOS 无法创建提示对话框。");
        try
        {
            SetText(alert, "setMessageText:", title);
            SetText(alert, "setInformativeText:", message);
            return alert;
        }
        catch
        {
            SendVoid(alert, Selector("release"));
            throw;
        }
    }

    private static void SetInitialDirectory(nint panel, string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return;
        var fullPath = Path.GetFullPath(directory);
        if (Directory.Exists(fullPath))
            SendVoidObject(panel, Selector("setDirectoryURL:"), FileUrl(fullPath));
    }

    private static string? FilePath(nint url) => url == 0 ? null : ReadString(Send(url, Selector("path")));

    private static nint FileUrl(string path) => SendObject(Class("NSURL"), Selector("fileURLWithPath:"), String(path));

    private static void SetText(nint receiver, string selector, string value) => SendVoidObject(receiver, Selector(selector), String(value));

    private static nint String(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Contains('\0'))
            throw new ArgumentException("文本不能包含空字符。", nameof(value));
        var result = SendUtf8(Class("NSString"), Selector("stringWithUTF8String:"), value);
        if (result == 0)
            throw new InvalidOperationException("macOS 无法创建文本对象。");
        return result;
    }

    private static string? ReadString(nint value) => value == 0
        ? null
        : Marshal.PtrToStringUTF8(Send(value, Selector("UTF8String")));

    private static string FullPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.GetFullPath(path);
    }

    private static void RunOpen(string path, bool reveal)
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("文件操作仅支持 macOS。");
        var fullPath = FullPath(path);
        var startInfo = new ProcessStartInfo("/usr/bin/open")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
        };
        if (reveal)
            startInfo.ArgumentList.Add("-R");
        startInfo.ArgumentList.Add(fullPath);

        using var process = Process.Start(startInfo)
            ?? throw new IOException("无法启动 macOS 文件打开服务。");
        var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10_000))
            throw new IOException("macOS 文件打开服务未及时响应。");
        var error = errors.GetAwaiter().GetResult().Trim();
        if (process.ExitCode != 0)
            throw new IOException($"无法打开“{fullPath}”：{(error.Length == 0 ? "macOS 文件打开服务返回失败。" : error)}");
    }

    private static AutoreleaseScope EnterAppKit()
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("原生对话框仅支持 macOS。");
        if (PthreadMain() == 0)
            throw new InvalidOperationException("macOS 原生对话框必须在主线程调用。");
        _ = AppKit.Value; // Keep AppKit loaded for the lifetime of its Objective-C classes.
        var scope = new AutoreleaseScope();
        Send(Class("NSApplication"), Selector("sharedApplication"));
        return scope;
    }

    private static nint Class(string name)
    {
        var value = GetClass(name);
        return value != 0 ? value : throw new InvalidOperationException($"macOS 类型不可用：{name}");
    }

    private sealed class AutoreleaseScope : IDisposable
    {
        private nint _pool = Send(Send(Class("NSAutoreleasePool"), Selector("alloc")), Selector("init"));

        public void Dispose()
        {
            if (_pool == 0)
                return;
            SendVoid(_pool, Selector("drain"));
            _pool = 0;
        }
    }

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "pthread_main_np")]
    private static extern int PthreadMain();

    [DllImport(ObjCLibrary, EntryPoint = "objc_getClass")]
    private static extern nint GetClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjCLibrary, EntryPoint = "sel_registerName")]
    private static extern nint Selector([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector);

    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint SendObject(nint receiver, nint selector, nint argument);

    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint SendIndex(nint receiver, nint selector, nuint index);

    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint SendUtf8(nint receiver, nint selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(nint receiver, nint selector);

    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendVoidObject(nint receiver, nint selector, nint value);

    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendBool(nint receiver, nint selector, [MarshalAs(UnmanagedType.I1)] bool value);

    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendInteger(nint receiver, nint selector, nint value);

    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SendTrash(nint receiver, nint selector, nint url, nint resultingUrl, out nint error);
}
