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
    private readonly nint module;
    // Native objects (SV-15): the functions and data they define, and the variables they share with the program, by name.
    private readonly HashSet<string> objectFunctions = [], objectData = [], sharedData = [];
    private readonly Dictionary<string, MethodInfo> accessors = [];
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
        this.module = module;
        shim = Llvm.CreateNativeShim(module);
        foreach (string library in options.SystemLibraries.Concat(new[] { "libm.so.6", "libc.so.6", "libgcc_s.so.1" }).Distinct())
        {
            if (!NativeLibrary.TryLoad(library, out nint handle))
                throw new DllNotFoundException($"Cannot load system link library {library} on the compiler host.");
            libraries.Add((library, handle));
        }
        if (options.NativeObjects.Count != 0) ClassifyObjects();
    }

    /// <summary>
    /// What the native objects define and reference (SV-15). Their functions are called through thunks in the image's
    /// native library, which links them. Variables they read that the program defines, and variables they define that the
    /// program uses, live in that library, as for a kernel package (Driver.KernelDataSource), so that both use one copy.
    /// A reference to a function of the program is an error: native code calls back only through function pointers.
    /// </summary>
    private void ClassifyObjects()
    {
        foreach (NativeObject native in options.NativeObjects)
        {
            objectFunctions.UnionWith(native.Functions);
            objectData.UnionWith(native.Data);
        }
        List<string> missing = [];
        foreach (string name in options.NativeObjects.SelectMany(native => native.Undefined).Distinct().Order(StringComparer.Ordinal))
        {
            if (objectFunctions.Contains(name) || objectData.Contains(name)) continue;
            nint global = Llvm.LLVMGetNamedGlobal(module, name);
            nint function = Llvm.LLVMGetNamedFunction(module, name);
            if (global != 0 && Llvm.LLVMGetInitializer(global) != 0) sharedData.Add(name);
            else if (function != 0 && Llvm.LLVMIsDeclaration(function) == 0)
                throw new NotSupportedException($"native object {options.NativeObjects.First(native => native.Undefined.Contains(name)).Source} calls {name}, a function of the program; native code calls translated code only through function pointers it is given.");
            else if (!libraries.Any(library => NativeLibrary.TryGetExport(library.Handle, name, out _)) && name is not ("_GLOBAL_OFFSET_TABLE_" or "__dso_handle"))
                missing.Add(name);
        }
        if (missing.Count != 0)
            throw new NotSupportedException($"native objects reference {string.Join(", ", missing.Take(8))}{(missing.Count > 8 ? $" and {missing.Count - 8} more" : "")}, which neither they, the program nor a system library defines.");
        for (nint global = Llvm.LLVMGetFirstGlobal(module); global != 0; global = Llvm.LLVMGetNextGlobal(global))
            if (Llvm.LLVMGetInitializer(global) == 0 && objectData.Contains(Llvm.Name(global))) sharedData.Add(Llvm.Name(global));
    }

    /// <summary>
    /// The address of a variable shared with the native objects, from its accessor in the image's native library, or
    /// null for other globals; the program's initializer then copies a defined variable's initializer there.
    /// </summary>
    internal MethodInfo? DataAccessor(nint global)
    {
        string name = Llvm.Name(global);
        if (!sharedData.Contains(name)) return null;
        if (!accessors.TryGetValue(name, out MethodInfo? accessor))
        {
            nint pointer = Llvm.LLVMPointerTypeInContext(Llvm.LLVMGetModuleContext(module), 0);
            nint declaration = Llvm.LLVMAddFunction(module, KernelPackage.Accessor(name), Llvm.FunctionType(pointer));
            accessors[name] = accessor = Resolve(declaration);
        }
        return accessor;
    }

    internal bool IsSystem(nint function)
    {
        string name = Llvm.Name(function);
        return Llvm.LLVMIsAFunction(function) != 0 && Llvm.LLVMIsDeclaration(function) != 0 &&
            !compiler.IsManagedImport(name) &&
            !name.StartsWith("llvm.", StringComparison.Ordinal) && !name.StartsWith("_Z", StringComparison.Ordinal) &&
                !name.StartsWith("__cxa_", StringComparison.Ordinal) && !name.StartsWith("_Fortran", StringComparison.Ordinal) && name != "_Unwind_Resume" &&
                Adapter(name) is null && name is not ("pthread_exit" or "pthread_cancel");
    }

    internal static MethodInfo? Adapter(string name) => name switch
    {
        "pthread_create" => typeof(SystemThreads).GetMethod(nameof(SystemThreads.Create)),
        "pthread_key_create" => typeof(SystemThreads).GetMethod(nameof(SystemThreads.KeyCreate)),
        "pthread_key_delete" => typeof(SystemThreads).GetMethod(nameof(SystemThreads.KeyDelete)),
        "longjmp" or "_longjmp" or "siglongjmp" or "__longjmp_chk" => typeof(NonLocalJumps).GetMethod(nameof(NonLocalJumps.Jump)),
        "__dynamic_cast" => typeof(Cxx).GetMethod(nameof(Cxx.DynamicCast)),
        "atexit" => typeof(ProcessRuntime).GetMethod(nameof(ProcessRuntime.AtExit)),
        // exit must run the managed handler list before the native exit, which never sees those handlers.
        "exit" => typeof(ProcessRuntime).GetMethod(nameof(ProcessRuntime.Exit)),
        // A forked child keeps running translated code; the adapters refuse while generated code is shared memory.
        "fork" or "vfork" => typeof(ProcessRuntime).GetMethod(nameof(ProcessRuntime.Fork)),
        "_Fork" => typeof(ProcessRuntime).GetMethod(nameof(ProcessRuntime.ForkWithoutHandlers)),
        "forkpty" => typeof(ProcessRuntime).GetMethod(nameof(ProcessRuntime.ForkPty)),
        _ => null
    };

    internal string LibraryFor(string symbol)
    {
        if (objectFunctions.Contains(symbol) || objectData.Contains(symbol) || symbol.StartsWith("llvmnet_kernel_data_", StringComparison.Ordinal) && sharedData.Contains(symbol["llvmnet_kernel_data_".Length..]))
            return LibraryName;
        foreach ((string name, nint handle) in libraries)
            if (NativeLibrary.TryGetExport(handle, symbol, out _)) return name;
        throw new NotSupportedException($"Unresolved system ABI symbol {symbol}; add --system-library <shared-library> or provide a bitcode definition.");
    }

    internal MethodInfo Resolve(nint function, nint call = 0)
    {
        bool indirect = function == 0;
        bool assembly = indirect && Llvm.LLVMIsAInlineAsm(Llvm.LLVMGetCalledValue(call)) != 0;
        string symbol = assembly ? "assembly:" + Llvm.Print(Llvm.LLVMGetCalledValue(call)) : indirect ? "indirect" : Llvm.Name(function);
        if (symbol.StartsWith("_Z", StringComparison.Ordinal) || symbol.StartsWith("__cxa_", StringComparison.Ordinal))
            throw new NotSupportedException("Native C++ ABI imports are not supported; provide a C wrapper or compile the C++ implementation to bitcode.");
        if (!indirect) LibraryFor(symbol);
        nint signature = indirect ? Llvm.LLVMGetCalledFunctionType(call) : Llvm.LLVMGlobalGetValueType(function);
        if (call == 0 && Llvm.LLVMIsFunctionVarArg(signature) != 0)
            throw new NotSupportedException($"Taking the address of native variadic function {symbol} is not implemented.");
        nint[] parameters = call == 0
            ? Enumerable.Range(0, (int)Llvm.LLVMCountParams(function)).Select(index => Llvm.LLVMTypeOf(Llvm.LLVMGetParam(function, (uint)index))).ToArray()
            : Enumerable.Range(0, (int)Llvm.LLVMGetNumArgOperands(call)).Select(index => Llvm.LLVMTypeOf(Llvm.LLVMGetOperand(call, (uint)index))).ToArray();
        if (indirect && !assembly) parameters = [.. parameters, Llvm.LLVMTypeOf(Llvm.LLVMGetCalledValue(call))];
        string key = symbol + ":" + Llvm.PrintType(signature) + ":" + string.Join(",", parameters.Select(Llvm.PrintType));
        if (wrappers.TryGetValue(key, out MethodInfo? cached)) return cached;
        if (!indirect && objectFunctions.Contains(symbol) && Direct(function, signature, parameters) is MethodInfo direct)
            return wrappers[key] = direct;
        string exportName = "llvmnet_host_" + wrappers.Count;
        Llvm.AddNativeThunk(shim, function, call, exportName);
        Type returnType = compiler.Types.Return(Llvm.LLVMGetReturnType(signature));
        // The wrapper calls its P/Invoke method, which is private to their class.
        TypeBuilder holder = compiler.MethodHolder(2);
        MethodBuilder wrapper = holder.DefineMethod("__host_" + wrappers.Count + "_" + (assembly ? "assembly" : symbol),
            MethodAttributes.Assembly | MethodAttributes.Static, returnType, parameters.Select(compiler.Types.Map).ToArray());
        MethodBuilder native = holder.DefinePInvokeMethod(exportName, LibraryName, exportName,
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

    /// <summary>
    /// A direct call of a native object's function whose parameters and result are 32- or 64-bit integers, pointers, float
    /// or double (SV-15): a forwarder in the native library that tail-calls it, and a P/Invoke of the forwarder without
    /// the GC transition, as kernels are leaf functions (docs/user/native-kernels.md). Other signatures go through a thunk.
    /// </summary>
    private MethodInfo? Direct(nint function, nint signature, nint[] parameters)
    {
        static bool Scalar(nint type, bool result) => TypeSystem.Kind(type) switch
        {
            0 => result,
            2 or 3 or 12 => true,
            8 => TypeSystem.Width(type) is 32 or 64,
            _ => false
        };
        if (Llvm.LLVMIsFunctionVarArg(signature) != 0 || !Scalar(Llvm.LLVMGetReturnType(signature), true) || !parameters.All(type => Scalar(type, false)))
            return null;
        for (uint index = 0; index <= parameters.Length; index++)
            foreach (string memory in new[] { "byval", "sret", "inalloca", "preallocated", "byref" })
                if (Llvm.LLVMGetEnumAttributeAtIndex(function, index, Llvm.LLVMGetEnumAttributeKindForName(memory, (nuint)memory.Length)) != 0)
                    return null;
        string symbol = Llvm.Name(function), forwarder = "llvmnet_object_" + symbol;
        Llvm.AddNativeForwarder(shim, function, forwarder);
        Type result = compiler.Types.Return(Llvm.LLVMGetReturnType(signature));
        Type[] types = [.. parameters.Select(compiler.Types.Map)];
        TypeBuilder holder = compiler.MethodHolder(2);
        MethodBuilder native = holder.DefinePInvokeMethod(forwarder, LibraryName, forwarder,
            MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.PinvokeImpl, CallingConventions.Standard, result, types, CallingConvention.Cdecl, CharSet.Ansi);
        native.SetImplementationFlags(MethodImplAttributes.PreserveSig);
        native.SetCustomAttribute(new CustomAttributeBuilder(typeof(SuppressGCTransitionAttribute).GetConstructor(Type.EmptyTypes)!, []));
        // A function pointer to the kernel is the wrapper's, as for other native functions.
        MethodBuilder wrapper = holder.DefineMethod("__object_" + symbol, MethodAttributes.Assembly | MethodAttributes.Static, result, types);
        wrapper.SetImplementationFlags(MethodImplAttributes.AggressiveInlining);
        ILGenerator il = wrapper.GetILGenerator();
        for (int index = 0; index < types.Length; index++) il.Emit(OpCodes.Ldarg, checked((short)index));
        il.Emit(OpCodes.Call, native);
        il.Emit(OpCodes.Ret);
        return wrapper;
    }

    internal MethodInfo Callback(nint function, MethodInfo target)
    {
        if (callbacks.TryGetValue(function, out MethodInfo? existing)) return existing;
        nint signature = Llvm.LLVMGlobalGetValueType(function);
        Type[] parameters = compiler.Types.Parameters(signature);
        Type result = compiler.Types.Return(Llvm.LLVMGetReturnType(signature));
        bool variadic = Llvm.LLVMIsFunctionVarArg(signature) != 0;
        bool scalar(Type type) => type == typeof(int) || type == typeof(long) || type == typeof(float) || type == typeof(double) || type == typeof(nint) || type == typeof(void);
        bool aggregate = parameters.Any(type => !scalar(type)) || !scalar(result) ||
            Enumerable.Range(0, (int)Llvm.LLVMCountParams(function)).Any(index => Llvm.ParameterAttribute(function, (uint)index, "byval") != 0 || Llvm.ParameterAttribute(function, (uint)index, "sret") != 0);
        bool nativeCallable = !variadic && !aggregate;
        MethodBuilder callback = compiler.MethodHolder().DefineMethod("__callback_" + callbacks.Count, MethodAttributes.Assembly | MethodAttributes.Static,
            nativeCallable ? result : typeof(void), nativeCallable ? parameters : Type.EmptyTypes);
        callback.SetCustomAttribute(new CustomAttributeBuilder(typeof(UnmanagedCallersOnlyAttribute).GetConstructor(Type.EmptyTypes)!, [],
            [typeof(UnmanagedCallersOnlyAttribute).GetField(nameof(UnmanagedCallersOnlyAttribute.CallConvs))!], [new[] { typeof(CallConvCdecl) }]));
        ILGenerator il = callback.GetILGenerator();
        if (!nativeCallable)
        {
            il.Emit(OpCodes.Ldstr, $"Native invocation of {(variadic ? "variadic" : "aggregate")} callback {Llvm.Name(function)} is not supported.");
            il.Emit(OpCodes.Call, typeof(Environment).GetMethod(nameof(Environment.FailFast), [typeof(string)])!);
        }
        else
        {
            // Native callers extend narrow integer arguments to 32 bits, signed ones with their sign; llvmnet keeps narrow
            // integers zero-extended, so they are masked on the way in.
            var values = new ValueEmitter(compiler, il);
            for (int index = 0; index < parameters.Length; index++)
            {
                il.Emit(OpCodes.Ldarg, checked((short)index));
                nint type = Llvm.LLVMTypeOf(Llvm.LLVMGetParam(function, (uint)index));
                if (TypeSystem.Kind(type) == 8 && TypeSystem.Width(type) < 32) values.Normalize(type);
            }
            il.Emit(OpCodes.Call, target);
        }
        il.Emit(OpCodes.Ret);
        callbacks[function] = callback;
        return callback;
    }

    internal void Build()
    {
        if (wrappers.Count == 0) return;
        string temporary = Path.Combine(Path.GetTempPath(), "llvmnet-shim-" + Guid.NewGuid().ToString("N") + ".bc");
        // Native objects (SV-15) join the library with the storage and accessors of the data they share with the program;
        // -Bsymbolic binds their references to its own symbols, as assembly addresses them relative to the instruction.
        string? data = sharedData.Count == 0 ? null : Path.ChangeExtension(temporary, ".c");
        string[] objects = options.NativeObjects.Count == 0 ? [] : [.. options.NativeObjects.Select(native => native.Path)];
        string clang = Toolchain.Executable("clang-22", "LLVMNET_CLANG");
        try
        {
            if (Llvm.LLVMWriteBitcodeToFile(shim, temporary) != 0) throw new IOException("Cannot write native ABI shim.");
            if (data is not null) File.WriteAllText(data, Driver.KernelDataSource(module, sharedData.Order(StringComparer.Ordinal)));
            if (options.NativeAot)
            {
                string objectFile = Path.Combine(Path.GetDirectoryName(output)!, Path.ChangeExtension(LibraryName, ".o"));
                string dataObject = Path.Combine(Path.GetDirectoryName(output)!, Path.ChangeExtension(LibraryName, ".data.o"));
                if (Toolchain.Run(clang, ["-c", "-fPIC", "-O1", temporary, "-o", objectFile], options.Verbose) != 0 ||
                    data is not null && Toolchain.Run(clang, ["-c", "-fPIC", "-O1", data, "-o", dataObject], options.Verbose) != 0)
                    throw new InvalidOperationException("Native ABI shim compilation failed.");
                string archive = Path.Combine(Path.GetDirectoryName(output)!, Path.ChangeExtension(LibraryName, ".a"));
                File.Delete(archive);
                if (Toolchain.Run(Toolchain.Executable("llvm-ar-22"), ["rcs", archive, objectFile, .. data is null ? [] : new[] { dataObject }, .. objects], options.Verbose) != 0)
                    throw new InvalidOperationException("Native ABI shim archive creation failed.");
                File.Delete(objectFile);
                File.Delete(dataObject);
                return;
            }
            List<string> command = ["-shared", "-fPIC", "-O1", temporary, .. data is null ? [] : new[] { data }, .. objects,
                "-o", Path.Combine(Path.GetDirectoryName(output)!, LibraryName), "-Wl,-z,defs", "-lm", "-pthread"];
            if (objects.Length != 0) command.AddRange(["-Wl,-Bsymbolic", "-Wl,-z,noexecstack"]);
            command.AddRange(options.SystemLibraries.Select(library => Path.IsPathRooted(library) ? library : "-l:" + library));
            if (Toolchain.Run(clang, command, options.Verbose) != 0)
                throw new InvalidOperationException("Native ABI shim link failed.");
        }
        finally
        {
            File.Delete(temporary);
            if (data is not null) File.Delete(data);
        }
    }

    public void Dispose()
    {
        Llvm.LLVMDisposeModule(shim);
        foreach ((_, nint handle) in libraries) NativeLibrary.Free(handle);
    }
}
