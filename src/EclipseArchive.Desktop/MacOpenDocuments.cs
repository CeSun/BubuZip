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

    static MacOpenDocuments() => NativeLibrary.SetDllImportResolver(typeof(MacOpenDocuments).Assembly, ResolveLibrary);

    public MacOpenDocuments(Action<string> open)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        _open = open;
        _watch = OnEvent;
        try
        {
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
                _open(path);
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
