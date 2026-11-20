using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;

namespace EclipseArchive.Desktop;

/// <summary>Receives Finder open-document requests through SDL's Cocoa delegate.</summary>
internal sealed class MacOpenDocuments : IDisposable
{
    private const string Library = "EclipseArchive.SDL3.MacOS";
    private const uint InitEvents = 0x4000, DropFile = 0x1000;
    private readonly Action<string> _open;
    private readonly EventFilter _watch;
    private bool _ownsEvents, _registered;
    private nint _launchObserver, _notificationCenter, _defaultLaunchKey;
    private int _launchCompleted, _hasDocuments, _defaultLaunch = 1;

    public bool IsLaunchCompleted => Volatile.Read(ref _launchCompleted) != 0;
    public bool HasDocuments => Volatile.Read(ref _hasDocuments) != 0;
    public bool IsDefaultLaunch => Volatile.Read(ref _defaultLaunch) != 0;

    static MacOpenDocuments() => NativeLibrary.SetDllImportResolver(typeof(MacOpenDocuments).Assembly, ResolveLibrary);

    public MacOpenDocuments(Action<string> open)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        _open = open;
        _watch = OnEvent;
        try
        {
            // Cocoa may complete launch while SDL initializes video or first pumps
            // events. Observe it before either can happen; an empty queue alone
            // cannot distinguish a normal launch from a pending document event.
            ObserveLaunch();
            SDL_SetMainReady();
            // Register before SDL initializes Cocoa/video, which can receive the initial
            // Finder request. Keep our own event-subsystem reference until the watch is removed.
            Check(SDL_InitSubSystem(InitEvents));
            _ownsEvents = true;
            SDL_SetEventEnabled(DropFile, true);
            Check(SDL_AddEventWatch(_watch, 0));
            _registered = true;
        }
        catch { Dispose(); throw; }
    }

    private bool OnEvent(nint userdata, ref DropEvent message)
    {
        try
        {
            // Cocoa openFile events have no window ID; do not filter by the active window.
            // SDL owns the UTF-8 buffer. Copy it now, before this callback returns.
            if (message.Type == DropFile && Marshal.PtrToStringUTF8(message.Data) is { Length: > 0 } path)
            {
                _open(path);
                // Publish readiness after the request is queued, including if an
                // event watch is invoked from a thread other than the message pump.
                Volatile.Write(ref _hasDocuments, 1);
            }
        }
        catch (Exception error)
        {
            // Managed exceptions must never unwind through SDL/AppKit.
            Console.Error.WriteLine($"Cannot receive the file-open request: {error.Message}");
        }
        return true;
    }

    public void Dispose()
    {
        if (_registered)
        {
            SDL_RemoveEventWatch(_watch, 0);
            _registered = false;
        }
        if (_ownsEvents)
        {
            SDL_QuitSubSystem(InitEvents);
            _ownsEvents = false;
        }
        if (_launchObserver != 0)
        {
            SendObject(_notificationCenter, Selector("removeObserver:"), _launchObserver);
            LaunchNotifications.Observers.TryRemove(_launchObserver, out _);
            SendVoid(_launchObserver, Selector("release"));
            _launchObserver = 0;
        }
        if (_defaultLaunchKey != 0)
        {
            Release(_defaultLaunchKey);
            _defaultLaunchKey = 0;
        }
    }

    private void ObserveLaunch()
    {
        _notificationCenter = Send(GetClass("NSNotificationCenter"), Selector("defaultCenter"));
        // Loading AppKit registers its notification classes before SDL creates NSApp.
        if (_notificationCenter == 0)
        {
            _ = NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
            _notificationCenter = Send(GetClass("NSNotificationCenter"), Selector("defaultCenter"));
        }
        _defaultLaunchKey = CreateString(0, "NSApplicationLaunchIsDefaultLaunchKey", 0x08000100);
        _launchObserver = Send(Send(LaunchNotifications.ObserverClass.Value, Selector("alloc")), Selector("init"));
        if (_notificationCenter == 0 || _defaultLaunchKey == 0 || _launchObserver == 0)
            throw new InvalidOperationException("无法监听 macOS 应用启动事件。");
        LaunchNotifications.Observers[_launchObserver] = this;
        var name = CreateString(0, "NSApplicationDidFinishLaunchingNotification", 0x08000100);
        if (name == 0) throw new InvalidOperationException("无法监听 macOS 应用启动事件。");
        try
        {
            AddObserver(_notificationCenter, Selector("addObserver:selector:name:object:"), _launchObserver,
                Selector("launchFinished:"), name, 0);
        }
        finally { Release(name); }
    }

    private void DidFinishLaunching(nint notification)
    {
        try
        {
            var information = Send(notification, Selector("userInfo"));
            var value = information == 0 ? 0 : SendObject(information, Selector("objectForKey:"), _defaultLaunchKey);
            // A direct command-line launch may omit the key. It is an ordinary launch.
            Volatile.Write(ref _defaultLaunch, value == 0 || SendBool(value, Selector("boolValue")) ? 1 : 0);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Cannot inspect the application launch event: {error.Message}");
        }
        finally { Volatile.Write(ref _launchCompleted, 1); }
    }

    private static class LaunchNotifications
    {
        internal static readonly ConcurrentDictionary<nint, MacOpenDocuments> Observers = new();
        private static readonly NotificationCallback Callback = OnNotification;
        internal static readonly Lazy<nint> ObserverClass = new(CreateObserverClass);

        private static nint CreateObserverClass()
        {
            var type = AllocateClass(GetClass("NSObject"), "EclipseArchiveLaunchNotificationObserver", 0);
            if (type == 0 || !AddMethod(type, Selector("launchFinished:"), Marshal.GetFunctionPointerForDelegate(Callback), "v@:@"))
                throw new InvalidOperationException("无法创建 macOS 启动事件监听器。");
            RegisterClass(type);
            return type;
        }

        private static void OnNotification(nint observer, nint selector, nint notification)
        {
            // Never allow a managed exception to cross an Objective-C callback.
            try
            {
                if (Observers.TryGetValue(observer, out var owner)) owner.DidFinishLaunching(notification);
            }
            catch (Exception error) { Console.Error.WriteLine($"Cannot receive the application launch event: {error.Message}"); }
        }
    }

    private static nint ResolveLibrary(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != Library) return 0;
        // Match the platform host's search order so both use the same SDL instance.
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "libSDL3.dylib"),
            Path.Combine(AppContext.BaseDirectory, "libSDL3.0.dylib"),
            Path.Combine(AppContext.BaseDirectory, "SDL3.framework", "SDL3"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Frameworks", "SDL3.framework", "SDL3")),
            "/opt/homebrew/lib/libSDL3.dylib",
            "/usr/local/lib/libSDL3.dylib",
            "/Library/Frameworks/SDL3.framework/SDL3",
            "SDL3.framework/SDL3", "libSDL3.dylib", "libSDL3.0.dylib",
        ];
        foreach (var candidate in candidates)
            if (NativeLibrary.TryLoad(candidate, assembly, searchPath, out var library)) return library;
        return 0;
    }

    private static void Check(bool success)
    {
        if (!success) throw new InvalidOperationException("SDL3: " + Marshal.PtrToStringUTF8(SDL_GetError()));
    }

    // SDL3's 128-byte event union; SDL_DropEvent.data is at offset 40 on macOS x64/arm64.
    [StructLayout(LayoutKind.Explicit, Size = 128)]
    private struct DropEvent
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(40)] public nint Data;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool EventFilter(nint userdata, ref DropEvent message);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void NotificationCallback(nint observer, nint selector, nint notification);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_getClass")]
    private static extern nint GetClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "sel_registerName")]
    private static extern nint Selector([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_allocateClassPair")]
    private static extern nint AllocateClass(nint superclass, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, nuint extraBytes);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_registerClassPair")]
    private static extern void RegisterClass(nint type);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "class_addMethod")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool AddMethod(nint type, nint selector, nint implementation, [MarshalAs(UnmanagedType.LPUTF8Str)] string types);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint SendObject(nint receiver, nint selector, nint value);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(nint receiver, nint selector);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SendBool(nint receiver, nint selector);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void AddObserver(nint center, nint selector, nint observer, nint callback, nint name, nint sender);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation", EntryPoint = "CFStringCreateWithCString")]
    private static extern nint CreateString(nint allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, uint encoding);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation", EntryPoint = "CFRelease")]
    private static extern void Release(nint value);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_SetMainReady();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SDL_InitSubSystem(uint flags);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_QuitSubSystem(uint flags);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_SetEventEnabled(uint type, [MarshalAs(UnmanagedType.I1)] bool enabled);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint SDL_GetError();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SDL_AddEventWatch(EventFilter watch, nint userdata);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_RemoveEventWatch(EventFilter watch, nint userdata);
}
