using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LlvmNet.Runtime;

namespace LlvmNet;

internal sealed class HostInterop : IDisposable
{
    private readonly CilCompiler compiler;
    private readonly CompilerOptions options;
    private readonly nint shim;
    private readonly List<(string Name, nint Handle)> libraries = [];
    private readonly Dictionary<string, MethodInfo> wrappers = [];
    private readonly Dictionary<nint, MethodInfo> callbacks = [];
    private readonly string output;
    internal string LibraryName { get; }

    internal HostInterop(CilCompiler compiler, nint module, string output, CompilerOptions options)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("System ABI mode currently requires a Linux x86-64 compiler and runtime host.");
        string triple = Marshal.PtrToStringUTF8(Llvm.LLVMGetTarget(module)) ?? "";
        if (!triple.StartsWith("x86_64", StringComparison.Ordinal) || !triple.Contains("linux", StringComparison.Ordinal))
            throw new NotSupportedException($"System ABI target is not the supported Linux x86-64 ABI: {triple}");
        this.compiler = compiler;
        this.options = options;
        this.output = output;
        LibraryName = "lib" + Path.GetFileNameWithoutExtension(output) + ".llvmnet-host.so";
        shim = Llvm.CreateNativeShim(module);
        foreach (string library in options.SystemLibraries.Concat(new[] { "libm.so.6", "libc.so.6" }).Distinct())
        {
            if (!NativeLibrary.TryLoad(library, out nint handle))
                throw new DllNotFoundException($"Cannot load system link library {library} on the compiler host.");
            libraries.Add((library, handle));
        }
    }

    internal bool IsSystem(nint function)
    {
        string name = Llvm.Name(function);
        return Llvm.LLVMIsAFunction(function) != 0 && Llvm.LLVMIsDeclaration(function) != 0 &&
            !options.NativeImports.ContainsKey(name) && !compiler.IsManagedImport(name) &&
            !name.StartsWith("llvm.", StringComparison.Ordinal) && !name.StartsWith("_Z", StringComparison.Ordinal) &&
                !name.StartsWith("__cxa_", StringComparison.Ordinal) && !name.StartsWith("_Fortran", StringComparison.Ordinal) && name != "_Unwind_Resume" &&
                Adapter(name) is null && name is not ("pthread_exit" or "pthread_cancel");
    }

            internal static MethodInfo? Adapter(string name) => name switch
            {
            "pthread_create" => typeof(SystemThreads).GetMethod(nameof(SystemThreads.Create)),
            "pthread_key_create" => typeof(SystemThreads).GetMethod(nameof(SystemThreads.KeyCreate)),
            "pthread_key_delete" => typeof(SystemThreads).GetMethod(nameof(SystemThreads.KeyDelete)),
            _ => null
            };

    internal string LibraryFor(string symbol)
    {
        foreach ((string name, nint handle) in libraries)
            if (NativeLibrary.TryGetExport(handle, symbol, out _)) return name;
        throw new NotSupportedException($"Unresolved system ABI symbol {symbol}; add --system-library <shared-library> or provide a bitcode definition.");
    }

    internal MethodInfo Resolve(nint function, nint call = 0)
    {
        string symbol = Llvm.Name(function);
        if (symbol.StartsWith("_Z", StringComparison.Ordinal) || symbol.StartsWith("__cxa_", StringComparison.Ordinal))
            throw new NotSupportedException("Native C++ ABI imports are not supported; provide a C wrapper or compile the C++ implementation to bitcode.");
        LibraryFor(symbol);
        nint signature = Llvm.LLVMGlobalGetValueType(function);
        if (call == 0 && Llvm.LLVMIsFunctionVarArg(signature) != 0)
            throw new NotSupportedException($"Taking the address of native variadic function {symbol} is not implemented.");
        nint[] parameters = call == 0
            ? Enumerable.Range(0, (int)Llvm.LLVMCountParams(function)).Select(index => Llvm.LLVMTypeOf(Llvm.LLVMGetParam(function, (uint)index))).ToArray()
            : Enumerable.Range(0, (int)Llvm.LLVMGetNumArgOperands(call)).Select(index => Llvm.LLVMTypeOf(Llvm.LLVMGetOperand(call, (uint)index))).ToArray();
        string key = symbol + ":" + string.Join(",", parameters.Select(Llvm.PrintType));
        if (wrappers.TryGetValue(key, out MethodInfo? cached)) return cached;
        string exportName = "llvmnet_host_" + wrappers.Count;
        Llvm.AddNativeThunk(shim, function, call, exportName);
        Type returnType = compiler.Types.Map(Llvm.LLVMGetReturnType(signature));
        MethodBuilder wrapper = compiler.Program.DefineMethod("__host_" + wrappers.Count + "_" + symbol,
            MethodAttributes.Private | MethodAttributes.Static, returnType, parameters.Select(compiler.Types.Map).ToArray());
        MethodBuilder native = compiler.Program.DefinePInvokeMethod(exportName, LibraryName, exportName,
            MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.PinvokeImpl, CallingConventions.Standard,
            typeof(void), [typeof(nint), typeof(nint)], CallingConvention.Cdecl, CharSet.Ansi);
        native.SetImplementationFlags(MethodImplAttributes.PreserveSig);
        ILGenerator il = wrapper.GetILGenerator();
        var values = new ValueEmitter(compiler, il);
        long[] offsets = new long[parameters.Length];
        long size = 0;
        for (int index = 0; index < parameters.Length; index++)
        {
            int alignment = compiler.Types.Alignment(parameters[index]);
            size = (size + alignment - 1) & -(long)alignment;
            offsets[index] = size;
            size = checked(size + compiler.Types.Size(parameters[index]));
        }
        LocalBuilder buffer = il.DeclareLocal(typeof(nint));
        il.Emit(OpCodes.Ldc_I8, Math.Max(1, size));
        il.Emit(OpCodes.Conv_U);
        il.Emit(OpCodes.Localloc);
        il.Emit(OpCodes.Stloc, buffer);
        for (int index = 0; index < parameters.Length; index++)
        {
            il.Emit(OpCodes.Ldloc, buffer);
            values.Offset(offsets[index]);
            il.Emit(OpCodes.Ldarg, checked((short)index));
            values.StoreMemory(parameters[index]);
        }
        LocalBuilder? result = returnType == typeof(void) ? null : il.DeclareLocal(returnType);
        if (result is null) { il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Conv_I); }
        else { il.Emit(OpCodes.Ldloca, result); il.Emit(OpCodes.Conv_I); }
        il.Emit(OpCodes.Ldloc, buffer);
        il.Emit(OpCodes.Call, native);
        if (result is not null) il.Emit(OpCodes.Ldloc, result);
        il.Emit(OpCodes.Ret);
        wrappers[key] = wrapper;
        return wrapper;
    }

    internal MethodInfo Callback(nint function, MethodInfo target)
    {
        if (callbacks.TryGetValue(function, out MethodInfo? existing)) return existing;
        nint signature = Llvm.LLVMGlobalGetValueType(function);
        Type[] parameters = compiler.Types.Parameters(signature);
        Type result = compiler.Types.Map(Llvm.LLVMGetReturnType(signature));
        bool scalar(Type type) => type == typeof(int) || type == typeof(long) || type == typeof(float) || type == typeof(double) || type == typeof(nint) || type == typeof(void);
        if (Llvm.LLVMIsFunctionVarArg(signature) != 0 || parameters.Any(type => !scalar(type)) || !scalar(result) ||
            Enumerable.Range(0, (int)Llvm.LLVMCountParams(function)).Any(index => Llvm.ParameterAttribute(function, (uint)index, "byval") != 0 || Llvm.ParameterAttribute(function, (uint)index, "sret") != 0))
            throw new NotSupportedException($"System ABI callback {Llvm.Name(function)} requires a non-variadic scalar C signature.");
        MethodBuilder callback = compiler.Program.DefineMethod("__callback_" + callbacks.Count, MethodAttributes.Private | MethodAttributes.Static, result, parameters);
        callback.SetCustomAttribute(new CustomAttributeBuilder(typeof(UnmanagedCallersOnlyAttribute).GetConstructor(Type.EmptyTypes)!, [],
            [typeof(UnmanagedCallersOnlyAttribute).GetField(nameof(UnmanagedCallersOnlyAttribute.CallConvs))!], [new[] { typeof(CallConvCdecl) }]));
        ILGenerator il = callback.GetILGenerator();
        for (int index = 0; index < parameters.Length; index++) il.Emit(OpCodes.Ldarg, checked((short)index));
        il.Emit(OpCodes.Call, target);
        il.Emit(OpCodes.Ret);
        callbacks[function] = callback;
        return callback;
    }

    internal void Build()
    {
        if (wrappers.Count == 0) return;
        string temporary = Path.Combine(Path.GetTempPath(), "llvmnet-shim-" + Guid.NewGuid().ToString("N") + ".bc");
        try
        {
            if (Llvm.LLVMWriteBitcodeToFile(shim, temporary) != 0) throw new IOException("Cannot write native ABI shim.");
            if (options.NativeAot)
            {
                string objectFile = Path.Combine(Path.GetDirectoryName(output)!, Path.ChangeExtension(LibraryName, ".o"));
                if (Toolchain.Run(Toolchain.Executable("clang-22", "LLVMNET_CLANG"), ["-c", "-fPIC", "-O1", temporary, "-o", objectFile], options.Verbose) != 0)
                    throw new InvalidOperationException("Native ABI shim compilation failed.");
                string archive = Path.Combine(Path.GetDirectoryName(output)!, Path.ChangeExtension(LibraryName, ".a"));
                if (Toolchain.Run(Toolchain.Executable("llvm-ar-22"), ["rcs", archive, objectFile], options.Verbose) != 0)
                    throw new InvalidOperationException("Native ABI shim archive creation failed.");
                File.Delete(objectFile);
                return;
            }
            List<string> command = ["-shared", "-fPIC", "-O1", temporary, "-o", Path.Combine(Path.GetDirectoryName(output)!, LibraryName), "-Wl,-z,defs", "-lm", "-pthread"];
            command.AddRange(options.SystemLibraries.Select(library => Path.IsPathRooted(library) ? library : "-l:" + library));
            if (Toolchain.Run(Toolchain.Executable("clang-22", "LLVMNET_CLANG"), command, options.Verbose) != 0)
                throw new InvalidOperationException("Native ABI shim link failed.");
        }
        finally { File.Delete(temporary); }
    }

    public void Dispose()
    {
        Llvm.LLVMDisposeModule(shim);
        foreach ((_, nint handle) in libraries) NativeLibrary.Free(handle);
    }
}