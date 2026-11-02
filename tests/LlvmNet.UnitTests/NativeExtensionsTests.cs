using System.Runtime.InteropServices;
using LlvmNet.Runtime;

namespace LlvmNet.UnitTests;

/// <summary>
/// The registry of native extensions and the dynamic-loading entry points over it (NX-02): registration rules, and the
/// buffer contract of __llvmnet_dlerror that musl's port relies on. tests/extensions.sh runs the C side end to end.
/// </summary>
public sealed unsafe class NativeExtensionsTests
{
    private sealed class Synthetic(string name, params string[] libraries) : NativeExtension(name, libraries)
    {
        public override nint Resolve(string library, string symbol) => symbol == "unit_answer" ? (nint)(delegate*<int>)&Answer : 0;

        private static int Answer() => 42;
    }

    private static nint Open(string name, int flags = 2)
    {
        nint text = Marshal.StringToCoTaskMemUTF8(name);
        try { return HostSyscalls.DlOpen(text, flags); }
        finally { Marshal.FreeCoTaskMem(text); }
    }

    private static nint Symbol(nint handle, string name)
    {
        nint text = Marshal.StringToCoTaskMemUTF8(name);
        try { return HostSyscalls.DlSym(handle, text); }
        finally { Marshal.FreeCoTaskMem(text); }
    }

    private static string Error()
    {
        long length = HostSyscalls.DlError(0, 0);
        byte[] buffer = new byte[length + 1];
        fixed (byte* pointer = buffer) Assert.Equal(length, HostSyscalls.DlError((nint)pointer, buffer.Length));
        Assert.Equal(0, buffer[^1]);
        return System.Text.Encoding.UTF8.GetString(buffer, 0, (int)length);
    }

    [Fact]
    public void AnotherExtensionServingTheSameLibraryIsRefused()
    {
        NativeExtensions.Register(new Synthetic("unit-clash-a", "libunit-clash.so.1"));
        var error = Assert.Throws<InvalidOperationException>(() => NativeExtensions.Register(new Synthetic("unit-clash-b", "libunit-other.so.1", "libunit-clash.so.1")));
        Assert.Equal("The native extensions unit-clash-a and unit-clash-b both serve libunit-clash.so.1.", error.Message);
        Assert.Equal(0, Open("libunit-other.so.1"));
    }

    [Fact]
    public void RegisteringANameAgainIsIgnored()
    {
        NativeExtensions.Register(new Synthetic("unit-twice", "libunit-twice.so.1"));
        NativeExtensions.Register(new Synthetic("unit-twice", "libunit-twice-other.so.1"));
        Assert.NotEqual(0, Open("libunit-twice.so.1"));
        Assert.Equal(0, Open("libunit-twice-other.so.1"));
        Assert.Equal("Dynamic loading not supported: libunit-twice-other.so.1", Error());
    }

    [Fact]
    public void LookupsReturnTheStubAndReportFailures()
    {
        NativeExtensions.Register(new Synthetic("unit-lookup", "libunit-lookup.so.1"));
        nint handle = Open("/usr/lib/libunit-lookup.so.1");
        Assert.NotEqual(0, handle);
        Assert.Equal("libunit-lookup.so.1", Marshal.PtrToStringUTF8(handle));
        nint stub = Symbol(handle, "unit_answer");
        Assert.Equal(42, ((delegate*<int>)stub)());
        Assert.Equal(0, Symbol(handle, "unit_missing"));
        Assert.Equal("Symbol not found: unit_missing", Error());
        Assert.Equal(0, HostSyscalls.DlClose(handle));
        Assert.Equal(1, HostSyscalls.DlClose(0x40));
        Assert.Equal("Invalid library handle 0x40", Error());
    }

    [Fact]
    public void ErrorMessagesAreTruncatedToTheBuffer()
    {
        Assert.Equal(0, Open("libunit-truncated.so.1"));
        byte[] buffer = [0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff];
        fixed (byte* pointer = buffer)
        {
            Assert.Equal("Dynamic loading not supported: libunit-truncated.so.1".Length, HostSyscalls.DlError((nint)pointer, 5));
            Assert.Equal("Dyna\0"u8.ToArray(), buffer[..5]);
            Assert.Equal(0xff, buffer[5]);
        }
    }
}
