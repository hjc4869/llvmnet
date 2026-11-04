using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using LlvmNet.Runtime;

namespace LlvmNet;

/// <summary>
/// A native kernel package (ADR-16 Level 3, SV-13): a directory whose manifest.json names the package, its version, the
/// library its kernels are in and, per runtime identifier, that library built natively by the project (nasm, Clang): a
/// shared library for the JIT and a static one for NativeAOT, relative to the directory. Its symbols are the functions the
/// program calls in it, leaf functions (no calls back into the program, short-running: SuppressGCTransition) unless the
/// manifest marks one "leaf": false; an "optional" one returns nothing and does nothing where the running host has no
/// library (ffmpeg's ff_emms_asm, which the C calls whatever the CPU). Its data are the global variables its code and the program's share, each with an
/// accessor in the libraries, llvmnet_kernel_data_&lt;name&gt;, that returns its address: a variable the program defines
/// lives in the library's storage where the library is loaded, with the program's initializer, so that both use one
/// copy (assembly reads C tables); one only the library defines is the library's (C reads assembly tables).
/// <code>{"name": "dsp", "version": "1.0", "library": "dsp-kernels",
///  "runtimes": {"linux-x64": {"shared": "runtimes/linux-x64/native/libdsp-kernels.so", "static": "runtimes/linux-x64/native/libdsp-kernels.a"}},
///  "symbols": {"dsp_add": {}, "dsp_wait": {"leaf": false}, "dsp_reset": {"optional": true}}, "data": ["dsp_table"]}</code>
/// </summary>
internal sealed partial record KernelPackage(string Name, string Version, string Library, string Directory,
    IReadOnlyDictionary<string, (string? Shared, string? Static)> Runtimes, IReadOnlyDictionary<string, KernelSymbol> Symbols, IReadOnlyList<string> Data)
{
    /// <summary>The accessor that returns a data symbol's address in the package's libraries.</summary>
    internal static string Accessor(string data) => "llvmnet_kernel_data_" + data;

    internal string Tag => $"{Name}/{Version} ({string.Join(" ", Runtimes.Keys)})";

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex NamePattern();

    [GeneratedRegex("^[A-Za-z0-9_][A-Za-z0-9_.-]*$")]
    private static partial Regex LibraryPattern();

    [GeneratedRegex("^(linux|linux-musl|win|osx|freebsd)-(x64|arm64)$")]
    private static partial Regex RuntimePattern();

    internal static KernelPackage Read(string directory)
    {
        directory = Path.GetFullPath(directory);
        string manifest = Path.Combine(directory, "manifest.json");
        if (!File.Exists(manifest)) throw new FileNotFoundException($"native kernel package {directory} has no manifest.json.");
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(manifest));
            JsonElement root = document.RootElement;
            string Text(string property) => root.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()! : throw new InvalidDataException($"\"{property}\" must be a string");
            string name = Text("name"), version = Text("version"), library = Text("library");
            if (!NamePattern().IsMatch(name)) throw new InvalidDataException($"name \"{name}\" must be lowercase letters, digits and dashes");
            if (!LibraryPattern().IsMatch(library)) throw new InvalidDataException($"library \"{library}\" must be a file name without a prefix or extension");
            Dictionary<string, (string?, string?)> runtimes = [];
            foreach (JsonProperty runtime in root.GetProperty("runtimes").EnumerateObject())
            {
                if (!RuntimePattern().IsMatch(runtime.Name)) throw new InvalidDataException($"\"{runtime.Name}\" is not a runtime identifier llvmnet supports");
                string? File(string kind)
                {
                    if (!runtime.Value.TryGetProperty(kind, out JsonElement value)) return null;
                    string path = Path.GetFullPath(Path.Combine(directory, value.GetString()!));
                    return System.IO.File.Exists(path) ? path : throw new InvalidDataException($"the {runtime.Name} {kind} library {path} does not exist");
                }
                runtimes[runtime.Name] = (File("shared"), File("static"));
            }
            Dictionary<string, KernelSymbol> symbols = [];
            foreach (JsonProperty symbol in root.GetProperty("symbols").EnumerateObject())
                symbols[symbol.Name] = new KernelSymbol(!(symbol.Value.TryGetProperty("leaf", out JsonElement leaf) && leaf.ValueKind == JsonValueKind.False),
                    symbol.Value.TryGetProperty("optional", out JsonElement optional) && optional.ValueKind == JsonValueKind.True);
            List<string> data = root.TryGetProperty("data", out JsonElement shared) ? [.. shared.EnumerateArray().Select(item => item.GetString()!)] : [];
            if (runtimes.Count == 0 || symbols.Count + data.Count == 0) throw new InvalidDataException("a package needs runtimes and symbols");
            return new KernelPackage(name, version, library, directory, runtimes, symbols, data);
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidDataException($"native kernel package {manifest}: {error.Message}");
        }
    }
}

/// <summary>A kernel's flags in its package's manifest.</summary>
internal readonly record struct KernelSymbol(bool Leaf, bool Optional);

/// <summary>
/// The stubs through which a program calls the kernels of its packages. Each kernel the program declares gets a wrapper
/// with the declaration's managed signature, which a direct call targets and a function pointer holds: it extends narrow
/// integer arguments to 32 bits as their signext or zeroext attribute says, which the native calling conventions expect,
/// and calls one of two P/Invoke methods. Under the JIT (RuntimeFeature.IsDynamicCodeCompiled) the one whose library
/// name is "llvmnet-kernel:&lt;package&gt;:&lt;symbol&gt;", which binds at its first call, when the image's resolver loads
/// the library from runtimes/&lt;rid&gt;/native/ beside the image or fails naming the kernel; as NativeAOT the one named
/// after the package's library, which DirectPInvoke binds to its static library, SuppressGCTransition for leaf kernels.
/// An optional kernel's wrapper returns at once where the running host has no library. Kernels take and return integers,
/// pointers, float and double; anything else is a compile error naming the kernel.
/// </summary>
internal sealed class KernelStubs(CilCompiler compiler, IReadOnlyList<KernelPackage> packages)
{
    private readonly Dictionary<nint, MethodInfo> stubs = [];
    private static readonly ConstructorInfo SuppressGCTransition = typeof(SuppressGCTransitionAttribute).GetConstructor(Type.EmptyTypes)!;

    internal KernelPackage? PackageOf(string name) => packages.FirstOrDefault(package => package.Symbols.ContainsKey(name));

    internal KernelPackage? DataPackageOf(string name) => packages.FirstOrDefault(package => package.Data.Contains(name));

    private readonly Dictionary<string, MethodInfo> accessors = [];

    /// <summary>The stub of a data symbol's accessor, a leaf kernel with no parameters that returns its address.</summary>
    internal MethodInfo Accessor(KernelPackage package, string data)
    {
        string accessor = KernelPackage.Accessor(data);
        if (!accessors.TryGetValue(accessor, out MethodInfo? stub))
        {
            MethodBuilder wrapper = Define(package, accessor, typeof(nint), [], [], new KernelSymbol(Leaf: true, Optional: false), function: 0, result: 0);
            // The image's initializer calls it once; inlined there, the JIT bound its NativeAOT import when it compiled
            // the initializer of an image loaded into a collectible context (tests/jit-validation.sh).
            wrapper.SetImplementationFlags(MethodImplAttributes.NoInlining | MethodImplAttributes.AggressiveOptimization);
            accessors[accessor] = stub = wrapper;
        }
        return stub;
    }

    internal MethodInfo? Stub(nint function)
    {
        if (stubs.TryGetValue(function, out MethodInfo? stub)) return stub;
        string name = Llvm.Name(function);
        if (PackageOf(name) is not KernelPackage package) return null;
        nint signature = Llvm.LLVMGlobalGetValueType(function);
        if (Llvm.LLVMIsFunctionVarArg(signature) != 0)
            throw new NotSupportedException($"native kernel {name} of package {package.Name} is variadic; kernels take a fixed list of integers, pointers, float and double.");
        nint result = Llvm.LLVMGetReturnType(signature);
        nint[] parameters = Enumerable.Range(0, (int)Llvm.LLVMCountParamTypes(signature)).Select(index => Llvm.LLVMTypeOf(Llvm.LLVMGetParam(function, (uint)index))).ToArray();
        Type Native(nint type, string role)
        {
            int kind = TypeSystem.Kind(type);
            return kind switch
            {
                0 when role == "result" => typeof(void),
                2 => typeof(float),
                3 => typeof(double),
                8 when TypeSystem.Width(type) <= 32 => typeof(int),
                8 when TypeSystem.Width(type) == 64 => typeof(long),
                12 => typeof(nint),
                _ => throw new NotSupportedException($"native kernel {name} of package {package.Name} has a {Llvm.PrintType(type)} {role}; kernels take and return integers of up to 64 bits, pointers, float and double (no structures by value, vectors or long double).")
            };
        }
        // A structure passed or returned in memory arrives as a pointer with byval, sret or a sibling attribute; the native
        // convention copies it instead, so such a kernel cannot be called through a pointer argument.
        for (uint index = 0; index <= parameters.Length; index++)
            foreach (string memory in new[] { "byval", "sret", "inalloca", "preallocated", "byref" })
                if (Llvm.LLVMGetEnumAttributeAtIndex(function, index == 0 ? 0u : index, Llvm.LLVMGetEnumAttributeKindForName(memory, (nuint)memory.Length)) != 0)
                    throw new NotSupportedException($"native kernel {name} of package {package.Name} passes a structure by value ({memory}); kernels take and return integers of up to 64 bits, pointers, float and double.");
        Type nativeResult = Native(result, "result");
        Type[] nativeParameters = parameters.Select(parameter => Native(parameter, "parameter")).ToArray();
        if (package.Symbols[name].Optional && nativeResult != typeof(void))
            throw new NotSupportedException($"native kernel {name} of package {package.Name} is optional but returns a value; an optional kernel returns nothing, as it does nothing where the host has no library.");
        MethodInfo wrapper = Define(package, name, nativeResult, nativeParameters, parameters, package.Symbols[name], function, result);
        stubs[function] = wrapper;
        return wrapper;
    }

    /// <summary>The wrapper and the two P/Invoke methods of one kernel (see the class); <paramref name="function"/> is its
    /// declaration, whose attributes say how narrow arguments extend, or 0 for an accessor.</summary>
    private MethodBuilder Define(KernelPackage package, string name, Type nativeResult, Type[] nativeParameters, nint[] parameters, KernelSymbol symbol, nint function, nint result)
    {
        TypeBuilder holder = compiler.MethodHolder(3);
        MethodBuilder Import(string library, string suffix, bool withoutTransition)
        {
            MethodBuilder import = holder.DefinePInvokeMethod("__kernel_" + name + suffix, library, name,
                MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.PinvokeImpl, CallingConventions.Standard,
                nativeResult, nativeParameters, CallingConvention.Cdecl, CharSet.Ansi);
            import.SetImplementationFlags(MethodImplAttributes.PreserveSig);
            if (withoutTransition) import.SetCustomAttribute(new CustomAttributeBuilder(SuppressGCTransition, []));
            return import;
        }
        // A P/Invoke without the GC transition has no lazy binding stub: the JIT binds it whenever it compiles a method that
        // calls it, called or not (tiered recompilation, RuntimeHelpers.PrepareMethod), which needs the library on hosts
        // that should never call the kernel. The JIT's import keeps the transition and binds at its first call; NativeAOT
        // binds its import statically, so a leaf kernel goes without the transition there.
        MethodBuilder jit = Import($"llvmnet-kernel:{package.Name}:{name}", "_jit", withoutTransition: false);
        MethodBuilder aot = Import(package.Library, "_aot", withoutTransition: symbol.Leaf);
        Type managedResult = result == 0 ? nativeResult : compiler.Types.Map(result);
        MethodBuilder wrapper = holder.DefineMethod(name, MethodAttributes.Assembly | MethodAttributes.Static, managedResult == typeof(void) ? typeof(void) : managedResult,
            parameters.Select(compiler.Types.Map).ToArray());
        // Optimized from the start: unoptimized code (tier 0) keeps the NativeAOT branch, whose transition-free import the JIT
        // would bind when it compiles the wrapper.
        wrapper.SetImplementationFlags(MethodImplAttributes.AggressiveInlining | MethodImplAttributes.AggressiveOptimization);
        ILGenerator il = wrapper.GetILGenerator();
        uint signext = Llvm.LLVMGetEnumAttributeKindForName("signext", 7);
        void Arguments()
        {
            for (int index = 0; index < parameters.Length; index++)
            {
                il.Emit(OpCodes.Ldarg, checked((short)index));
                int width = TypeSystem.Kind(parameters[index]) == 8 ? TypeSystem.Width(parameters[index]) : 64;
                if (width < 32 && function != 0 && Llvm.LLVMGetEnumAttributeAtIndex(function, (uint)index + 1, signext) != 0)
                {
                    il.Emit(OpCodes.Ldc_I4, 32 - width);
                    il.Emit(OpCodes.Shl);
                    il.Emit(OpCodes.Ldc_I4, 32 - width);
                    il.Emit(OpCodes.Shr);
                }
            }
        }
        Label native = il.DefineLabel(), done = il.DefineLabel();
        if (symbol.Optional)
        {
            il.Emit(OpCodes.Ldstr, package.Name);
            il.Emit(OpCodes.Call, typeof(NativeKernels).GetMethod(nameof(NativeKernels.IsAvailable))!);
            il.Emit(OpCodes.Brfalse, done);
        }
        il.Emit(OpCodes.Call, typeof(RuntimeFeature).GetProperty(nameof(RuntimeFeature.IsDynamicCodeCompiled))!.GetMethod!);
        il.Emit(OpCodes.Brfalse, native);
        Arguments();
        il.Emit(OpCodes.Call, jit);
        il.Emit(OpCodes.Br, done);
        il.MarkLabel(native);
        Arguments();
        il.Emit(OpCodes.Call, aot);
        il.MarkLabel(done);
        // A narrow result's upper bits are the callee's to leave undefined; llvmnet keeps narrow integers zero-extended.
        if (result != 0 && TypeSystem.Kind(result) == 8 && TypeSystem.Width(result) < 32)
        {
            il.Emit(OpCodes.Ldc_I4, (int)((1L << TypeSystem.Width(result)) - 1));
            il.Emit(OpCodes.And);
        }
        il.Emit(OpCodes.Ret);
        return wrapper;
    }
}
