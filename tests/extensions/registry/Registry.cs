// A synthetic native extension for the dlopen tests (NX-02): it serves two names of one library and one library whose
// host library is missing, and resolves three symbols to managed stubs. The program registers it itself, before the
// driver can (NX-03), through the exported llvmnet_test_register.
using System.Runtime.InteropServices;
using LlvmNet.Runtime;

namespace LlvmNet.Ext.Registry;

public sealed unsafe class Registry() : NativeExtension("registry", "libllvmnet-registry.so.1", "libllvmnet-registry.so", "libllvmnet-absent.so.1")
{
    private static readonly nint greeting = Marshal.StringToCoTaskMemUTF8("hello from a managed stub");

    public override string? Open(string library) =>
        library == "libllvmnet-absent.so.1" ? "registry: libllvmnet-absent.so.1 is not installed on this host (tried libllvmnet-absent.so.1)" : null;

    public override nint Resolve(string library, string symbol) => symbol switch
    {
        "registry_add" => (nint)(delegate*<int, int, int>)&Add,
        "registry_scale" => (nint)(delegate*<double, int, double>)&Scale,
        "registry_greeting" => (nint)(delegate*<nint>)&Greeting,
        "registry_fail" => throw new InvalidOperationException("the stub table is damaged"),
        _ => 0
    };

    private static int Add(int left, int right) => left + right;

    private static double Scale(double value, int factor) => value * factor;

    private static nint Greeting() => greeting;

    [CExport("llvmnet_test_register")]
    public static void Register() => NativeExtensions.Register(new Registry());
}
