// The host application of tests/ext-skia.sh (NX-13), in the role Avalonia plays for AvaloniaStatic: it creates a native
// window (an X11 window, a Win32 HWND, or a Cocoa window's CALayer), keeps its event loop on the main thread, and hands
// the window's handles to a renderer, which draws into it with Skia's GL backend through EGL.
//
//   SkiaHost [--size <w> <h>] [--frames <n>] [--no-window] (--image <portable.dll> | --exec <program> [arguments...])
//
// --image runs a portable program in this process: its entry point gets the handles as arguments on a worker thread,
// and its exit ends the process. Built with EMBEDDED (host-aot/SkiaHostAot.csproj), the host references the portable
// image, so that NativeAOT compiles both into one executable, as an Avalonia application with AvaloniaStatic is one; it
// then needs neither --image nor --exec. The image is found by the name the project records, not through C# types: the
// image is built against System.Private.CoreLib, which C# does not resolve beside the reference assemblies. --exec runs a program (native, a portable NativeAOT executable, or "dotnet image.dll")
// as a child process that renders into this process's window; X11 and Win32 handles work across processes, a CALayer
// does not. On X11 the host reads the window back after the renderer has presented its last frame and prints the hash
// of what the window shows, which must equal the renderer's own frame hash. --no-window (macOS) passes a CALayer that is
// in no window, for sessions without a window server.
using System.Diagnostics;
using System.Reflection;
using System.Numerics;
using System.Runtime.InteropServices;

static class Host
{
    static int width = 480, height = 360, frames = 8;

    static int Main(string[] arguments)
    {
        string? image = null;
        List<string>? command = null;
        bool noWindow = false;
        for (int i = 0; i < arguments.Length; i++)
        {
            switch (arguments[i])
            {
                case "--size": width = int.Parse(arguments[++i]); height = int.Parse(arguments[++i]); break;
                case "--frames": frames = int.Parse(arguments[++i]); break;
                case "--no-window": noWindow = true; break;
                case "--image": image = Path.GetFullPath(arguments[++i]); break;
                case "--exec": command = [.. arguments[(i + 1)..]]; i = arguments.Length; break;
                default: Console.Error.WriteLine($"SkiaHost: unknown argument {arguments[i]}"); return 2;
            }
        }
#if EMBEDDED
        bool embedded = image is null && command is null;
#else
        const bool embedded = false;
#endif
        if (!embedded && image is null == command is null) { Console.Error.WriteLine("SkiaHost: give --image <portable.dll> or --exec <program> [arguments...]"); return 2; }
        IWindow window = OperatingSystem.IsLinux() ? new X11Window() : OperatingSystem.IsWindows() ? new Win32Window() : new CocoaWindow(noWindow);
        Console.WriteLine($"host: {window.Describe(width, height)}");
        string[] size = ["--size", width.ToString(), height.ToString(), "--frames", frames.ToString()];
        if (image is not null || embedded)
        {
            // The portable program ends the process with exit(); the window is read back before that, on the way out.
            AppDomain.CurrentDomain.ProcessExit += (_, _) => window.ReadBack(width, height);
            // As "dotnet image.dll <arguments>" would: the runtime makes argv[0] itself.
            string[] programArguments = [.. window.Handles(inProcess: true), .. size];
#if EMBEDDED
            string name = typeof(Host).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(attribute => attribute.Key == "LlvmNetImage").Value!;
            Assembly assembly = Assembly.Load(name);
#else
            Assembly assembly = Assembly.LoadFrom(image!);
#endif
            MethodInfo entry = assembly.EntryPoint ?? throw new InvalidOperationException($"{assembly.GetName().Name} has no entry point");
            ThreadStart run = () => entry.Invoke(null, new object[] { programArguments });
            var renderer = new Thread(run) { IsBackground = true, Name = "renderer" };
            renderer.Start();
            window.Run(() => !renderer.IsAlive);
            return 0;
        }
        var start = new ProcessStartInfo(command![0]) { UseShellExecute = false };
        foreach (string argument in command.Skip(1).Concat(window.Handles(inProcess: false)).Concat(size)) start.ArgumentList.Add(argument);
        using Process child = Process.Start(start)!;
        window.Run(() => child.HasExited);
        child.WaitForExit();
        window.ReadBack(width, height);
        return child.ExitCode;
    }

    internal static ulong Fnv1a(ReadOnlySpan<byte> bytes)
    {
        ulong hash = 1469598103934665603ul;
        foreach (byte value in bytes) { hash ^= value; hash *= 1099511628211ul; }
        return hash;
    }
}

interface IWindow
{
    string Describe(int width, int height);
    /// <summary>The renderer's arguments that name this window: in-process ones may be pointers of this process.</summary>
    string[] Handles(bool inProcess);
    /// <summary>Runs the event loop on the main thread until <paramref name="done"/> says the renderer has finished.</summary>
    void Run(Func<bool> done);
    void ReadBack(int width, int height);
}

/// <summary>An X11 window through Xlib (libX11.so.6), as Avalonia's X11 backend makes one.</summary>
sealed unsafe class X11Window : IWindow
{
    const string X11 = "libX11.so.6";
    [DllImport(X11)] static extern int XInitThreads();
    [DllImport(X11)] static extern nint XOpenDisplay(nint name);
    [DllImport(X11)] static extern int XDefaultScreen(nint display);
    [DllImport(X11)] static extern nuint XRootWindow(nint display, int screen);
    [DllImport(X11)] static extern nuint XCreateSimpleWindow(nint display, nuint parent, int x, int y, uint width, uint height, uint border, nuint borderPixel, nuint background);
    [DllImport(X11)] static extern int XStoreName(nint display, nuint window, string name);
    [DllImport(X11)] static extern int XSelectInput(nint display, nuint window, nint mask);
    [DllImport(X11)] static extern int XMapWindow(nint display, nuint window);
    [DllImport(X11)] static extern int XSync(nint display, int discard);
    [DllImport(X11)] static extern int XPending(nint display);
    [DllImport(X11)] static extern int XNextEvent(nint display, byte* xevent);
    [DllImport(X11)] static extern nint XGetImage(nint display, nuint drawable, int x, int y, uint width, uint height, nuint planeMask, int format);

    readonly nint display;
    nuint window;
    bool read;

    public X11Window()
    {
        XInitThreads();   // the renderer's EGL uses the same connection from its own thread
        display = XOpenDisplay(0);
        if (display == 0) throw new InvalidOperationException("SkiaHost: cannot open the X display (DISPLAY is unset or the server is gone)");
    }

    public string Describe(int width, int height)
    {
        int screen = XDefaultScreen(display);
        window = XCreateSimpleWindow(display, XRootWindow(display, screen), 0, 0, (uint)width, (uint)height, 0, 0, 0);
        XStoreName(display, window, "llvmnet Skia GL");
        XSelectInput(display, window, (1 << 15) | (1 << 17));   // ExposureMask | StructureNotifyMask
        XMapWindow(display, window);
        byte* xevent = stackalloc byte[192];
        do XNextEvent(display, xevent); while (*(int*)xevent != 19);   // MapNotify
        return $"X11 window 0x{window:x} ({width}x{height}) on display {Environment.GetEnvironmentVariable("DISPLAY")}";
    }

    public string[] Handles(bool inProcess) => ["--x11", inProcess ? $"0x{display:x}" : "0", $"0x{window:x}"];

    public void Run(Func<bool> done)
    {
        byte* xevent = stackalloc byte[192];
        while (!done())
        {
            while (XPending(display) > 0) XNextEvent(display, xevent);
            Thread.Sleep(5);
        }
    }

    public void ReadBack(int width, int height)
    {
        if (read) return;
        read = true;
        XSync(display, 0);
        nint ximage = XGetImage(display, window, 0, 0, (uint)width, (uint)height, unchecked((nuint)~0ul), 2 /* ZPixmap */);
        if (ximage == 0) { Console.WriteLine("host: the window could not be read back"); return; }
        // struct XImage: width, height, xoffset, format, data, byte_order, bitmap_unit, bitmap_bit_order, bitmap_pad,
        // depth, bytes_per_line, bits_per_pixel, red_mask, green_mask, blue_mask.
        byte* data = *(byte**)(ximage + 16);
        int bytesPerLine = *(int*)(ximage + 44), bitsPerPixel = *(int*)(ximage + 48);
        ulong red = *(ulong*)(ximage + 56), green = *(ulong*)(ximage + 64), blue = *(ulong*)(ximage + 72);
        if (bitsPerPixel != 32) { Console.WriteLine($"host: the window has {bitsPerPixel} bits per pixel; not read back"); return; }
        byte[] rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                uint pixel = *(uint*)(data + y * bytesPerLine + x * 4);
                int at = (y * width + x) * 4;
                rgba[at] = (byte)((pixel & red) >> BitOperations.TrailingZeroCount(red));
                rgba[at + 1] = (byte)((pixel & green) >> BitOperations.TrailingZeroCount(green));
                rgba[at + 2] = (byte)((pixel & blue) >> BitOperations.TrailingZeroCount(blue));
                rgba[at + 3] = 0xff;
            }
        Console.WriteLine($"host: the window shows hash {Host.Fnv1a(rgba):x16}");
    }
}

/// <summary>A Win32 window (user32), created and pumped on the main thread.</summary>
sealed unsafe class Win32Window : IWindow
{
    [StructLayout(LayoutKind.Sequential)]
    struct WindowClass
    {
        public uint Size, Style;
        public nint Procedure;
        public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background, MenuName, ClassName, SmallIcon;
    }
    [DllImport("user32.dll")] static extern ushort RegisterClassExW(WindowClass* windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern nint CreateWindowExW(uint exStyle, nint className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] static extern int ShowWindow(nint window, int command);
    [DllImport("user32.dll")] static extern int PeekMessageW(byte* message, nint window, uint first, uint last, uint remove);
    [DllImport("user32.dll")] static extern int TranslateMessage(byte* message);
    [DllImport("user32.dll")] static extern nint DispatchMessageW(byte* message);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern nint GetModuleHandleW(string? name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] static extern nint GetProcAddress(nint module, string name);

    nint window;

    public string Describe(int width, int height)
    {
        nint instance = GetModuleHandleW(null);
        nint className = Marshal.StringToHGlobalUni("LlvmNetSkiaHost");
        var windowClass = new WindowClass
        {
            Size = (uint)sizeof(WindowClass), Style = 0x20 /* CS_OWNDC */, Procedure = GetProcAddress(GetModuleHandleW("user32.dll"), "DefWindowProcW"),
            Instance = instance, ClassName = className,
        };
        if (RegisterClassExW(&windowClass) == 0) throw new InvalidOperationException("SkiaHost: RegisterClassExW failed");
        // WS_POPUP | WS_VISIBLE: the client area is the whole window, so the surface is exactly width x height.
        window = CreateWindowExW(0, className, "llvmnet Skia GL", 0x80000000u | 0x10000000u, 0, 0, width, height, 0, 0, instance, 0);
        if (window == 0) throw new InvalidOperationException("SkiaHost: CreateWindowExW failed");
        ShowWindow(window, 5 /* SW_SHOW */);
        return $"Win32 window 0x{window:x} ({width}x{height})";
    }

    public string[] Handles(bool inProcess) => ["--win32", $"0x{window:x}"];

    public void Run(Func<bool> done)
    {
        byte* message = stackalloc byte[64];
        while (!done())
        {
            while (PeekMessageW(message, 0, 0, 0, 1 /* PM_REMOVE */) != 0) { TranslateMessage(message); DispatchMessageW(message); }
            Thread.Sleep(5);
        }
    }

    public void ReadBack(int width, int height) => Console.WriteLine("host: window read-back is only implemented for X11");
}

/// <summary>A Cocoa window whose content view is layer-backed, or a CALayer in no window (--no-window).</summary>
sealed unsafe class CocoaWindow(bool noWindow) : IWindow
{
    const string ObjC = "/usr/lib/libobjc.A.dylib";
    [DllImport(ObjC)] static extern nint objc_getClass(string name);
    [DllImport(ObjC)] static extern nint sel_registerName(string name);
    [DllImport(ObjC)] static extern nint objc_msgSend(nint receiver, nint selector);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")] static extern int CFRunLoopRunInMode(nint mode, double seconds, byte returnAfterSourceHandled);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")] static extern nint CFStringCreateWithCString(nint allocator, string text, uint encoding);
    [DllImport("/usr/lib/libSystem.dylib")] static extern nint dlopen(string path, int mode);

    [StructLayout(LayoutKind.Sequential)] struct Rect { public double X, Y, Width, Height; }

    static nint Send(nint receiver, string selector) => objc_msgSend(receiver, sel_registerName(selector));
    nint layer, application;

    public string Describe(int width, int height)
    {
        dlopen("/System/Library/Frameworks/AppKit.framework/AppKit", 1);
        dlopen("/System/Library/Frameworks/QuartzCore.framework/QuartzCore", 1);
        var send = (delegate* unmanaged<nint, nint, nint, void>)NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "objc_msgSend");
        var sendBool = (delegate* unmanaged<nint, nint, byte, void>)NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "objc_msgSend");
        var sendLong = (delegate* unmanaged<nint, nint, long, void>)NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "objc_msgSend");
        var sendFrame = (delegate* unmanaged<nint, nint, Rect, void>)NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "objc_msgSend");
        var create = (delegate* unmanaged<nint, nint, Rect, ulong, ulong, byte, nint>)NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "objc_msgSend");
        var rect = new Rect { X = 0, Y = 0, Width = width, Height = height };
        if (noWindow)
        {
            layer = Send(objc_getClass("CALayer"), "layer");
            sendFrame(layer, sel_registerName("setFrame:"), rect);
            return $"CALayer 0x{layer:x} in no window ({width}x{height})";
        }
        application = Send(objc_getClass("NSApplication"), "sharedApplication");
        sendLong(application, sel_registerName("setActivationPolicy:"), 1 /* accessory */);
        nint window = create(Send(objc_getClass("NSWindow"), "alloc"), sel_registerName("initWithContentRect:styleMask:backing:defer:"), rect, 1 /* titled */, 2 /* buffered */, 0);
        nint view = Send(window, "contentView");
        sendBool(view, sel_registerName("setWantsLayer:"), 1);
        send(window, sel_registerName("makeKeyAndOrderFront:"), 0);
        layer = Send(view, "layer");
        return $"NSWindow 0x{window:x} with the CALayer 0x{layer:x} of its content view ({width}x{height})";
    }

    public string[] Handles(bool inProcess) => ["--layer", $"0x{layer:x}"];

    public void Run(Func<bool> done)
    {
        // The main run loop serves the main dispatch queue, which layer and Metal work may be sent to.
        nint mode = CFStringCreateWithCString(0, "kCFRunLoopDefaultMode", 0x08000100);
        while (!done()) CFRunLoopRunInMode(mode, 0.01, 0);
    }

    public void ReadBack(int width, int height) => Console.WriteLine("host: window read-back is only implemented for X11");
}
