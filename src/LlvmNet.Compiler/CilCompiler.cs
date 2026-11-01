using System.Reflection;
using System.Buffers.Binary;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using LlvmNet.Runtime;
using System.Runtime.InteropServices;

namespace LlvmNet;

internal sealed class CilCompiler(CompilerOptions options)
{
    internal readonly Dictionary<nint, MethodInfo> Methods = [];
    internal readonly Dictionary<nint, FieldBuilder> Globals = [];
    internal readonly Dictionary<nint, MethodBuilder> ThreadGlobals = [];
    /// <summary>Functions whose address the module takes: the pointer value (identity) and the method it calls.</summary>
    internal readonly Dictionary<nint, (MethodInfo Identity, MethodInfo Target)> AddressTaken = [];
    internal TypeSystem Types { get; private set; } = null!;
    internal TypeBuilder Program { get; private set; } = null!;
    // The CLR limits how many fields one type may have, so static fields beyond this many per type go to further
    // internal holder classes.
    private const int FieldsPerType = 32768;
    private readonly List<TypeBuilder> fieldHolders = [];
    private int fieldsInHolder;
    internal HostInterop? Host { get; private set; }
    internal bool TrapMissingArguments => options.TrapMissingArguments;
    internal bool CheckIndirectCalls => options.CheckIndirectCalls;

    /// <summary>CIL-level signature text used by --check-indirect-calls; calli compatibility is decided at this level.</summary>
    internal static string CilSignature(Type returnType, IEnumerable<Type> parameters) =>
        $"{returnType.Name}({string.Join(",", parameters.Select(parameter => parameter.Name))})";
    private readonly Dictionary<nint, FieldBuilder> constants = [];
    private readonly List<(MethodBuilder Method, int Offset, Type ReturnType, Type[] Parameters)> indirectCalls = [];
    private readonly HashSet<string> managedImports = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MethodInfo> runtimeExports = typeof(Memory).Assembly.GetTypes()
        .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static))
        .SelectMany(method => method.GetCustomAttributes<CExportAttribute>().Select(attribute => (attribute.Name, Method: method)))
        .ToDictionary(item => item.Name, item => item.Method, StringComparer.Ordinal);

    private readonly Dictionary<nint, bool> undefinedWeak = [];
    private MethodBuilder constructors = null!;
    private nint libcMain;
    private nint libcFinalizer;

    private static readonly Lazy<HashSet<string>> FrameworkAssemblyNames = new(() =>
        ((AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(path => Path.GetDirectoryName(path) == Path.GetDirectoryName(typeof(object).Assembly.Location))
            .Select(Path.GetFileNameWithoutExtension).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase));

    internal bool IsManagedImport(string name) => managedImports.Contains(name);

    /// <summary>
    /// In portable mode, where musl, libc++ and flang-rt are linked as bitcode, only the host interface, the C++ exception
    /// bridge (the CLR unwinds), non-local jumps, SIMD128 helpers and managed references resolve to managed code.
    /// </summary>
    private bool MuslVisible(string name, MethodInfo method)
    {
        Type owner = method.DeclaringType!;
        if (owner == typeof(HostSyscalls) || owner == typeof(NonLocalJumps) || owner == typeof(Simd128))
            return true;
        if (owner != typeof(Cxx)) return managedImports.Contains(name);
        // The bridge handles throwing, catching and termination (docs/dev/cxx-eh-bridge.md); libc++abi and musl supply
        // the rest of the C++ ABI, such as guards, atexit and the bad_cast/bad_typeid helpers.
        return name is "_Unwind_Resume" or "__clang_call_terminate" or "__cxa_call_unexpected" ||
            name.StartsWith("__cxa_", StringComparison.Ordinal) && name is not ("__cxa_atexit" or "__cxa_finalize" or "__cxa_thread_atexit" or
                "__cxa_thread_atexit_impl" or "__cxa_guard_acquire" or "__cxa_guard_release" or "__cxa_guard_abort" or
                "__cxa_bad_cast" or "__cxa_bad_typeid" or "__cxa_throw_bad_array_new_length") ||
            name.Contains("terminate", StringComparison.Ordinal) || name.Contains("uncaught_exception", StringComparison.Ordinal) ||
            name.Contains("new_handler", StringComparison.Ordinal);
    }

    /// <summary>Follows global aliases to the global value or constant expression they name (opaque pointers need no casts).</summary>
    internal static nint ResolveAlias(nint value)
    {
        for (int depth = 0; depth < 64 && Llvm.LLVMIsAGlobalAlias(value) != 0; depth++)
            value = Llvm.LLVMAliasGetAliasee(value);
        return value;
    }

    /// <summary>
    /// True for an extern_weak declaration that nothing in the link, the runtime or (in system mode) the
    /// native libraries defines. Its address is null, as with an undefined weak symbol in an ELF link.
    /// </summary>
    internal bool IsUndefinedWeak(nint value)
    {
        if (undefinedWeak.TryGetValue(value, out bool cached)) return cached;
        bool result = false;
        if (Llvm.LLVMIsDeclaration(value) != 0 && Llvm.LLVMGetLinkage(value) == Llvm.ExternalWeakLinkage)
        {
            string name = Llvm.Name(value);
            bool function = Llvm.LLVMIsAFunction(value) != 0;
            result = function
                ? !(runtimeExports.TryGetValue(name, out MethodInfo? export) && (options.Runtime != "portable" || MuslVisible(name, export))) &&
                    (Host is null || HostInterop.Adapter(name) is null)
                : !Cxx.HasGlobal(name);
            if (result && Host is not null && !(function && !Host.IsSystem(value)))
            {
                try { Host.LibraryFor(name); result = false; }
                catch (NotSupportedException) { }
            }
        }
        undefinedWeak[value] = result;
        return result;
    }

    internal static bool IsSetJump(nint function) => Llvm.LLVMIsAFunction(function) != 0 && Llvm.LLVMIsDeclaration(function) != 0 &&
        Llvm.Name(function) is "setjmp" or "_setjmp" or "sigsetjmp" or "__sigsetjmp";

    internal void ManagedCalli(ILGenerator il, MethodBuilder method, Type returnType, Type[] parameters)
    {
        if (options.CheckIndirectCalls)
        {
            il.Emit(OpCodes.Ldstr, CilSignature(returnType, parameters));
            il.Emit(OpCodes.Call, typeof(IndirectCalls).GetMethod(nameof(IndirectCalls.Check))!);
        }
        if (NativeScalar(returnType) && parameters.All(NativeScalar))
        {
            il.EmitCalli(OpCodes.Calli, CallingConventions.Standard, returnType, parameters, null);
            return;
        }
        indirectCalls.Add((method, il.ILOffset, returnType, parameters));
        il.EmitCalli(OpCodes.Calli, CallingConventions.Standard, returnType == typeof(void) ? typeof(void) : typeof(nint),
            parameters.Select(_ => typeof(nint)).ToArray(), null);
    }

    private List<(MethodBuilder Method, int Offset, int Token)> FinalizeIndirectSignatures(MetadataBuilder metadata)
    {
        List<(MethodBuilder Method, int Offset, int Token)> fixups = [];
        foreach (var call in indirectCalls)
        {
            SignatureHelper signature = SignatureHelper.GetMethodSigHelper(Program.Module, CallingConventions.Standard, call.ReturnType);
            signature.AddArguments(call.Parameters, null, null);
            StandaloneSignatureHandle handle = metadata.AddStandaloneSignature(metadata.GetOrAddBlob(signature.GetSignature()));
            fixups.Add((call.Method, call.Offset, MetadataTokens.GetToken(handle)));
        }
        return fixups;
    }

    private static unsafe void FinalizeIndirectCalls(byte[] image, List<(MethodBuilder Method, int Offset, int Token)> fixups)
    {
        if (fixups.Count == 0) return;
        using var stream = new MemoryStream(image, false);
        using var reader = new PEReader(stream);
        MetadataReader metadata = reader.GetMetadataReader();
        foreach (var call in fixups)
        {
            MethodDefinition definition = metadata.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(call.Method.MetadataToken));
            MethodBodyBlock body = reader.GetMethodBody(definition.RelativeVirtualAddress);
            PEMemoryBlock section = reader.GetSectionData(definition.RelativeVirtualAddress);
            int headerSize = checked((int)(body.GetILReader().StartPointer - section.Pointer));
            if (!reader.PEHeaders.TryGetDirectoryOffset(new DirectoryEntry(definition.RelativeVirtualAddress, 1), out int methodOffset))
                throw new InvalidOperationException("Cannot locate the indirect-call method body.");
            int offset = checked(methodOffset + headerSize + call.Offset);
            if (image[offset] != unchecked((byte)OpCodes.Calli.Value))
                throw new InvalidOperationException("Indirect-call metadata fixup does not point at calli.");
            BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(offset + 1, 4), call.Token);
        }
    }

    /// <summary>
    /// Replaces the random module version ID and the time-based PE timestamp with values derived from a
    /// SHA-256 of the image (with those fields zeroed), so identical inputs produce identical assemblies.
    /// </summary>
    private static void MakeDeterministic(byte[] image)
    {
        int mvidOffset, stampOffset;
        Guid mvid;
        using (var stream = new MemoryStream(image, false))
        using (var reader = new PEReader(stream))
        {
            MetadataReader metadata = reader.GetMetadataReader();
            GuidHandle handle = metadata.GetModuleDefinition().Mvid;
            mvid = metadata.GetGuid(handle);
            mvidOffset = reader.PEHeaders.MetadataStartOffset + metadata.GetHeapMetadataOffset(HeapIndex.Guid) +
                (MetadataTokens.GetHeapOffset(handle) - 1) * 16;
            stampOffset = reader.PEHeaders.CoffHeaderStartOffset + 4;
        }
        if (!image.AsSpan(mvidOffset, 16).SequenceEqual(mvid.ToByteArray()))
            throw new InvalidOperationException("Cannot locate the module version ID for deterministic output.");
        image.AsSpan(mvidOffset, 16).Clear();
        image.AsSpan(stampOffset, 4).Clear();
        byte[] hash = System.Security.Cryptography.SHA256.HashData(image);
        hash.AsSpan(0, 16).CopyTo(image.AsSpan(mvidOffset, 16));
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(stampOffset, 4), BinaryPrimitives.ReadInt32LittleEndian(hash.AsSpan(16)) & int.MaxValue);
    }

    internal MethodInfo ResolveFunction(nint function)
    {
        if (Methods.TryGetValue(function, out MethodInfo? method))
            return method;
        string name = Llvm.Name(function);
        if (IsSetJump(function))
            throw new NotSupportedException($"{name} requires a direct call in a translated function.");
        nint signature = Llvm.LLVMGlobalGetValueType(function);
        if (Host is not null && Host.IsSystem(function))
            return Host.Resolve(function);
        if (Host is not null && HostInterop.Adapter(name) is MethodInfo adapter)
        {
            if (Types.Map(Llvm.LLVMGetReturnType(signature)) != adapter.ReturnType ||
                !Types.Parameters(signature).SequenceEqual(adapter.GetParameters().Select(parameter => parameter.ParameterType)))
                throw new NotSupportedException($"System runtime adapter ABI mismatch for {name}");
            Methods[function] = adapter;
            return adapter;
        }
        if (name == "__llvmnet_run_constructors")
            return Methods[function] = constructors;
        if (!runtimeExports.TryGetValue(name, out method) || options.Runtime == "portable" && !MuslVisible(name, method))
            throw new NotSupportedException($"Unresolved function: {name}");
        if (Types.Map(Llvm.LLVMGetReturnType(signature)) != method.ReturnType ||
            !Types.Parameters(signature).SequenceEqual(method.GetParameters().Select(parameter => parameter.ParameterType)))
            throw new NotSupportedException($"Runtime ABI mismatch for {name}: {Llvm.PrintType(signature)}");
        Methods[function] = method;
        return method;
    }

    private static bool NativeScalar(Type type) => type == typeof(void) || type == typeof(int) || type == typeof(long) || type == typeof(nint) || type == typeof(float) || type == typeof(double);

    internal void Compile(nint module, string output)
    {
        string assemblyName = Path.GetFileNameWithoutExtension(output);
        if (FrameworkAssemblyNames.Value.Contains(assemblyName))
            throw new ArgumentException($"Output name '{Path.GetFileName(output)}' would create assembly '{assemblyName}', which collides with the .NET framework assembly of the same name; choose another output name.");
        var assembly = new PersistedAssemblyBuilder(new AssemblyName(assemblyName), typeof(object).Assembly);
        ModuleBuilder cilModule = assembly.DefineDynamicModule("program");
        Program = cilModule.DefineType("Program", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        fieldHolders.Add(Program);
        using var types = new TypeSystem(module, cilModule, options.Simd128);
        Types = types;
        constructors = Program.DefineMethod("__llvmnet_run_constructors", MethodAttributes.Public | MethodAttributes.Static, typeof(void), Type.EmptyTypes);
        // In portable mode musl's start object calls main and exit and runs constructors after __init_libc.
        libcMain = Llvm.LLVMGetNamedFunction(module, "__llvmnet_libc_main");
        if (libcMain != 0 && Llvm.LLVMIsDeclaration(libcMain) != 0) libcMain = 0;
        libcFinalizer = Llvm.LLVMGetNamedFunction(module, "__llvmnet_register_finalizer");
        using HostInterop? host = options.Runtime == "system" ? new HostInterop(this, module, output, options) : null;
        Host = host;
        if (options.Simd128)
            foreach (string name in runtimeExports.Keys.Where(name => name.StartsWith("__llvmnet_simd128_", StringComparison.Ordinal)))
                managedImports.Add(name);
        ConstructorInfo metadataAttribute = typeof(AssemblyMetadataAttribute).GetConstructor([typeof(string), typeof(string)])!;
        assembly.SetCustomAttribute(new CustomAttributeBuilder(metadataAttribute, ["llvmnet.abi", options.AbiTag]));
        assembly.SetCustomAttribute(new CustomAttributeBuilder(metadataAttribute, ["llvmnet.version", BuildInfo.Version]));
        assembly.SetCustomAttribute(new CustomAttributeBuilder(metadataAttribute, ["llvmnet.llvm", Llvm.Version]));
        foreach (string reference in options.CilReferences)
        {
            Assembly library = Assembly.LoadFrom(reference);
            string? referenceAbi = library.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(attribute => attribute.Key == "llvmnet.abi")?.Value;
            string? referenceVersion = library.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(attribute => attribute.Key == "llvmnet.version")?.Value;
            if (referenceVersion is not null && !BuildInfo.Compatible(referenceVersion))
                throw new InvalidOperationException($"Managed reference {reference} was compiled by llvmnet {referenceVersion}, which is incompatible with {BuildInfo.Version}; recompile it.");
            if (referenceAbi is not null && referenceAbi != options.AbiTag || options.Runtime == "portable" && referenceAbi is null)
                throw new InvalidOperationException($"Managed reference ABI mismatch: {reference}. Portable references must declare the same llvmnet.abi assembly metadata.");
            foreach (Type type in library.GetExportedTypes())
                foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    foreach (CExportAttribute export in method.GetCustomAttributes<CExportAttribute>())
                    {
                        if (!runtimeExports.TryAdd(export.Name, method))
                            throw new InvalidOperationException($"Duplicate managed export {export.Name} in {reference}");
                        managedImports.Add(export.Name);
                    }
        }
        foreach (nint function in Llvm.Functions(module))
        {
            if (Llvm.LLVMIsDeclaration(function) != 0)
                continue;
            nint signature = Llvm.LLVMGlobalGetValueType(function);
            // A library exports its external, default-visibility definitions as public CExport methods (-fvisibility and
            // visibility attributes decide, as for a native library); the rest, and everything in an executable, is private.
            bool exported = options.Library && Llvm.IsExported(function);
            MethodBuilder method;
            try
            {
                method = Program.DefineMethod(Llvm.Name(function), (exported ? MethodAttributes.Public : MethodAttributes.Private) | MethodAttributes.Static,
                    Types.Map(Llvm.LLVMGetReturnType(signature)), Types.Parameters(signature));
            }
            catch (Exception error) { throw CompilationError.InFunction(error, function); }
            if (exported) method.SetCustomAttribute(new CustomAttributeBuilder(typeof(CExportAttribute).GetConstructor([typeof(string)])!, [Llvm.Name(function)]));
            Methods[function] = method;
        }
        for (nint alias = Llvm.LLVMGetFirstGlobalAlias(module); alias != 0; alias = Llvm.LLVMGetNextGlobalAlias(alias))
        {
            // Library builds export each public alias of an exported function under its own name too.
            if (options.Library && Methods.TryGetValue(ResolveAlias(alias), out MethodInfo? aliased) && aliased is MethodBuilder builder &&
                builder.IsPublic && Llvm.IsExported(alias))
                builder.SetCustomAttribute(new CustomAttributeBuilder(typeof(CExportAttribute).GetConstructor([typeof(string)])!, [Llvm.Name(alias)]));
        }
        for (nint ifunc = Llvm.LLVMGetFirstGlobalIFunc(module); ifunc != 0; ifunc = Llvm.LLVMGetNextGlobalIFunc(ifunc))
            if (Llvm.LLVMGetFirstUse(ifunc) != 0)
                throw new NotSupportedException($"GNU indirect function (ifunc) {Llvm.Name(ifunc)} is not supported; build without ifunc resolvers.");
        for (nint global = Llvm.LLVMGetFirstGlobal(module); global != 0; global = Llvm.LLVMGetNextGlobal(global))
        {
            if (Llvm.Name(global).StartsWith("llvm.", StringComparison.Ordinal) || IsUndefinedWeak(global))
                continue;
            bool threadLocal = Llvm.LLVMIsThreadLocal(global) != 0;
            if (threadLocal && Llvm.LLVMGetInitializer(global) == 0)
                throw new NotSupportedException($"External thread-local global requires a bitcode definition: {Llvm.Name(global)}");
            if (Llvm.LLVMGetInitializer(global) == 0 && Host is not null && !Cxx.HasGlobal(Llvm.Name(global)))
                Host.LibraryFor(Llvm.Name(global));
            else if (Llvm.LLVMGetInitializer(global) == 0 && !Cxx.HasGlobal(Llvm.Name(global)))
                throw new NotSupportedException($"Unresolved external global: {Llvm.Name(global)}");
            Globals[global] = FieldHolder().DefineField($"__global{Globals.Count}", typeof(nint), FieldAttributes.Assembly | FieldAttributes.Static);
            if (threadLocal)
            {
                Globals[global].SetCustomAttribute(new CustomAttributeBuilder(typeof(ThreadStaticAttribute).GetConstructor(Type.EmptyTypes)!, []));
                ThreadGlobals[global] = Program.DefineMethod($"__tls{ThreadGlobals.Count}", MethodAttributes.Private | MethodAttributes.Static, typeof(nint), Type.EmptyTypes);
            }
        }
        foreach ((nint function, MethodInfo method) in Methods.ToArray())
        {
            try { new FunctionEmitter(this, function, (MethodBuilder)method).Emit(); }
            catch (Exception error) { throw CompilationError.InFunction(error, function); }
        }
        EmitInitializer(module);
        MethodBuilder? main = Methods.Where(pair => Llvm.Name(pair.Key) == "main").Select(pair => pair.Value).OfType<MethodBuilder>().SingleOrDefault()
            ?? Methods.Where(pair => Llvm.Name(pair.Key) == "_QQmain").Select(pair => pair.Value).OfType<MethodBuilder>().SingleOrDefault();
        if (main is null && !options.Library)
            throw new NotSupportedException("No main function found.");
        MethodBuilder? entry = options.Library ? null : EmitStartup(cilModule, EmitEntry(main!));
        foreach (TypeBuilder holder in fieldHolders)
            holder.CreateType();
        MetadataBuilder metadata = assembly.GenerateMetadata(out BlobBuilder ilStream, out BlobBuilder fieldData);
        var indirectFixups = FinalizeIndirectSignatures(metadata);
        var pe = new ManagedPEBuilder(new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.LargeAddressAware | (options.Library ? Characteristics.Dll : 0)),
            new MetadataRootBuilder(metadata), ilStream, mappedFieldData: fieldData,
            entryPoint: options.Library ? default : MetadataTokens.MethodDefinitionHandle(entry!.MetadataToken));
        var image = new BlobBuilder();
        pe.Serialize(image);
        byte[] executable = image.ToArray();
        FinalizeIndirectCalls(executable, indirectFixups);
        MakeDeterministic(executable);
        if (options.DiagnoseLimits)
            LimitsReport.Write(executable, Methods.Where(pair => pair.Value is MethodBuilder).ToDictionary(pair => pair.Value.Name, pair =>
                (Llvm.Blocks(pair.Key).Count(), Llvm.Blocks(pair.Key).Sum(block => Llvm.Instructions(block).Count()))), Console.Error);
        File.WriteAllBytes(output, executable);
        File.WriteAllText(Path.ChangeExtension(output, ".runtimeconfig.json"), JsonSerializer.Serialize(new
        {
            // The framework band is pinned: patch and minor updates of .NET 10 roll forward, another major version does not
            // (DOTNET_ROLL_FORWARD=LatestMajor overrides this for a host that only has a later runtime).
            // Dynamic PGO is off: its instrumented tier-0 code costs translated C more than its profile-guided
            // devirtualization gains (6.6% over 24 benchmarks, PF-06); DOTNET_TieredPGO=1 turns it back on.
            runtimeOptions = new
            {
                tfm = "net10.0",
                rollForward = "Minor",
                framework = new { name = "Microsoft.NETCore.App", version = "10.0.0" },
                configProperties = new Dictionary<string, object> { ["System.Runtime.TieredPGO"] = false },
            }
        }));
        string runtimePath = typeof(Memory).Assembly.Location;
        string runtimeOutput = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, Path.GetFileName(runtimePath));
        if (Path.GetFullPath(runtimePath) != runtimeOutput)
            File.Copy(runtimePath, runtimeOutput, true);
        foreach (AssemblyName dependency in typeof(Memory).Assembly.GetReferencedAssemblies())
        {
            if (dependency.Name!.StartsWith("System", StringComparison.Ordinal) || dependency.Name.StartsWith("Microsoft", StringComparison.Ordinal))
                continue;
            string source = Assembly.Load(dependency).Location;
            string destination = Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileName(source));
            if (Path.GetFullPath(source) != Path.GetFullPath(destination))
                File.Copy(source, destination, true);
        }
        foreach (string reference in options.CilReferences)
        {
            string destination = Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileName(reference));
            if (Path.GetFullPath(reference) != Path.GetFullPath(destination))
                File.Copy(reference, destination, true);
        }
        Host?.Build();
    }

    private TypeBuilder FieldHolder()
    {
        if (fieldsInHolder == FieldsPerType)
        {
            fieldHolders.Add(((ModuleBuilder)Program.Module).DefineType($"__Statics{fieldHolders.Count}", TypeAttributes.NotPublic | TypeAttributes.Abstract | TypeAttributes.Sealed));
            fieldsInHolder = 0;
        }
        fieldsInHolder++;
        return fieldHolders[^1];
    }

    internal FieldBuilder Constant(nint value)
    {
        if (!constants.TryGetValue(value, out FieldBuilder? field))
        {
            field = FieldHolder().DefineField($"__constant{constants.Count}", typeof(nint), FieldAttributes.Assembly | FieldAttributes.Static);
            constants[value] = field;
        }
        return field;
    }

    private MethodBuilder EmitEntry(MethodBuilder main)
    {
        nint llvmMain = Methods.First(pair => pair.Value == main).Key;
        Type[] parameters = Types.Parameters(Llvm.LLVMGlobalGetValueType(llvmMain));
        bool fortran = Llvm.Name(llvmMain) == "_QQmain" && main.ReturnType == typeof(void) && parameters.Length == 0;
        // Flang with -fdefault-integer-8 defines i64 main(i64, ...); the entry point converts argc and the exit status
        // at the boundary, as native start code does through registers.
        bool wide = main.ReturnType == typeof(long);
        bool wideCount = parameters.Length >= 1 && parameters[0] == typeof(long);
        // K&R-style main(argc) gets the count alone, as native start code leaves argv unread in its register.
        if (!fortran && (main.ReturnType != typeof(int) && !wide || !(parameters.Length == 0 ||
            parameters.Length is 1 or 2 or 3 && parameters[0] == (wideCount ? typeof(long) : typeof(int)) && parameters.Skip(1).All(parameter => parameter == typeof(nint)))))
            throw new NotSupportedException("The entry point must be int main(void), int main(int[, char **[, char **]]), or a Fortran main.");
        MethodBuilder entry = Program.DefineMethod("__Entry", MethodAttributes.Public | MethodAttributes.Static, typeof(int), [typeof(string[])]);
        ILGenerator il = entry.GetILGenerator();
        if (libcMain != 0)
        {
            // musl: __llvmnet_libc_main(argc, argv, envp, main) initializes libc, runs constructors, calls main and exit.
            MethodBuilder adapter = Program.DefineMethod("__llvmnet_main3", MethodAttributes.Private | MethodAttributes.Static, typeof(int), [typeof(int), typeof(nint), typeof(nint)]);
            ILGenerator call = adapter.GetILGenerator();
            LocalBuilder status = call.DeclareLocal(typeof(int));
            Label returned = call.BeginExceptionBlock();
            for (short index = 0; index < parameters.Length; index++)
            {
                call.Emit(OpCodes.Ldarg, index);
                if (index == 0 && wideCount) call.Emit(OpCodes.Conv_I8);
            }
            call.Emit(OpCodes.Call, main);
            if (fortran) call.Emit(OpCodes.Ldc_I4_0);
            if (wide) call.Emit(OpCodes.Conv_I4);
            call.Emit(OpCodes.Stloc, status);
            call.Emit(OpCodes.Leave, returned);
            EmitUncaughtTerminate(call, returned);
            call.EndExceptionBlock();
            call.Emit(OpCodes.Ldloc, status);
            call.Emit(OpCodes.Ret);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, typeof(ProcessRuntime).GetMethod(nameof(ProcessRuntime.InitializeStartup))!);
            il.Emit(OpCodes.Call, typeof(ProcessRuntime).GetProperty(nameof(ProcessRuntime.Arguments))!.GetMethod!);
            il.Emit(OpCodes.Call, typeof(ProcessRuntime).GetProperty(nameof(ProcessRuntime.EnvironmentVector))!.GetMethod!);
            il.Emit(OpCodes.Ldftn, adapter);
            il.Emit(OpCodes.Call, ResolveFunction(libcMain));
            il.Emit(OpCodes.Ret);
            return entry;
        }
        LocalBuilder result = il.DeclareLocal(typeof(int));
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, typeof(ProcessRuntime).GetMethod(nameof(ProcessRuntime.Initialize))!);
        LocalBuilder count = il.DeclareLocal(typeof(int));
        il.Emit(OpCodes.Stloc, count);
        Label done = il.BeginExceptionBlock();
        if (parameters.Length >= 1)
        {
            il.Emit(OpCodes.Ldloc, count);
            if (wideCount) il.Emit(OpCodes.Conv_I8);
        }
        if (parameters.Length >= 2)
            il.Emit(OpCodes.Call, typeof(ProcessRuntime).GetProperty(nameof(ProcessRuntime.Arguments))!.GetMethod!);
        if (parameters.Length == 3)
            il.Emit(OpCodes.Call, typeof(ProcessRuntime).GetProperty(nameof(ProcessRuntime.EnvironmentVector))!.GetMethod!);
        il.Emit(OpCodes.Call, main);
        if (fortran)
            il.Emit(OpCodes.Ldc_I4_0);
        if (wide)
            il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Stloc, result);
        il.Emit(OpCodes.Leave, done);
        EmitUncaughtTerminate(il, done);
        il.BeginFinallyBlock();
        il.Emit(OpCodes.Call, typeof(ProcessRuntime).GetMethod(nameof(ProcessRuntime.Cleanup))!);
        il.EndExceptionBlock();
        il.Emit(OpCodes.Ldloc, result);
        il.Emit(OpCodes.Ret);
        return entry;
    }

    /// <summary>
    /// The image's entry point. It asks the runtime whether this host can run the image before Program's type initializer
    /// runs, so an unsupported host (32-bit, Mono, an older runtime) prints one line and exits with status 1 instead of
    /// failing inside a type initializer (XP-06).
    /// </summary>
    private MethodBuilder EmitStartup(ModuleBuilder cilModule, MethodBuilder entry)
    {
        TypeBuilder startup = cilModule.DefineType("__Startup", TypeAttributes.NotPublic | TypeAttributes.Abstract | TypeAttributes.Sealed);
        MethodBuilder main = startup.DefineMethod("Main", MethodAttributes.Public | MethodAttributes.Static, typeof(int), [typeof(string[])]);
        ILGenerator il = main.GetILGenerator();
        LocalBuilder problem = il.DeclareLocal(typeof(string));
        Label supported = il.DefineLabel();
        il.Emit(OpCodes.Ldstr, options.AbiTag);
        il.Emit(OpCodes.Ldstr, BuildInfo.Version);
        il.Emit(OpCodes.Call, typeof(AbiContract).GetMethod(nameof(AbiContract.Diagnose), [typeof(string), typeof(string)])!);
        il.Emit(OpCodes.Stloc, problem);
        il.Emit(OpCodes.Ldloc, problem);
        il.Emit(OpCodes.Brfalse, supported);
        il.Emit(OpCodes.Ldloc, problem);
        il.Emit(OpCodes.Call, typeof(AbiContract).GetMethod(nameof(AbiContract.Report))!);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(supported);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, entry);
        il.Emit(OpCodes.Ret);
        startup.CreateType();
        return main;
    }

    /// <summary>A C++ exception no handler catches calls std::terminate with that exception current, as in native C++;
    /// otherwise the CLR would report it as an unhandled managed exception and never run the terminate handler.</summary>
    private static void EmitUncaughtTerminate(ILGenerator il, Label done)
    {
        il.BeginCatchBlock(typeof(CxxException));
        il.Emit(OpCodes.Callvirt, typeof(CxxException).GetProperty(nameof(CxxException.Handle))!.GetMethod!);
        il.Emit(OpCodes.Call, typeof(Cxx).GetMethod(nameof(Cxx.CallTerminate))!);
        il.Emit(OpCodes.Leave, done);
    }

    private void EmitInitializer(nint module)
    {
        foreach ((nint global, MethodBuilder accessor) in ThreadGlobals)
        {
            ILGenerator getter = accessor.GetILGenerator();
            FieldBuilder field = Globals[global];
            Label initialized = getter.DefineLabel();
            getter.Emit(OpCodes.Ldsfld, field);
            getter.Emit(OpCodes.Brtrue, initialized);
            nint type = Llvm.LLVMGlobalGetValueType(global);
            getter.Emit(OpCodes.Ldc_I8, Types.Size(type));
            getter.Emit(OpCodes.Ldc_I4, Math.Max(checked((int)Llvm.LLVMGetAlignment(global)), Types.Alignment(type)));
            getter.Emit(OpCodes.Call, typeof(ThreadStorage).GetMethod(nameof(ThreadStorage.Allocate))!);
            getter.Emit(OpCodes.Stsfld, field);
            InitializeGlobal(getter, Llvm.LLVMGetInitializer(global), field);
            getter.MarkLabel(initialized);
            getter.Emit(OpCodes.Ldsfld, field);
            getter.Emit(OpCodes.Ret);
        }
        ILGenerator il = Program.DefineTypeInitializer().GetILGenerator();
        il.Emit(OpCodes.Ldstr, options.AbiTag);
        il.Emit(OpCodes.Ldstr, BuildInfo.Version);
        il.Emit(OpCodes.Call, typeof(AbiContract).GetMethod(nameof(AbiContract.Require), [typeof(string), typeof(string)])!);
        // When linked, libc++abi's catch matcher (ports/libcxxabi/llvmnet-catch.cpp) decides which handlers catch an exception.
        nint catchMatcher = Llvm.LLVMGetNamedFunction(module, "__llvmnet_cxx_can_catch");
        if (catchMatcher != 0 && Llvm.LLVMIsDeclaration(catchMatcher) == 0)
        {
            il.Emit(OpCodes.Ldftn, Methods[catchMatcher]);
            il.Emit(OpCodes.Call, typeof(Cxx).GetMethod(nameof(Cxx.UseCatchMatcher))!);
        }
        foreach ((nint global, FieldBuilder field) in Globals)
        {
            if (ThreadGlobals.ContainsKey(global)) continue;
            if (Llvm.LLVMGetInitializer(global) == 0)
            {
                if (Host is not null && !Cxx.HasGlobal(Llvm.Name(global)))
                {
                    il.Emit(OpCodes.Ldstr, Host.LibraryFor(Llvm.Name(global)));
                    il.Emit(OpCodes.Ldstr, Llvm.Name(global));
                    il.Emit(OpCodes.Call, typeof(SystemAbi).GetMethod(nameof(SystemAbi.Symbol))!);
                }
                else
                {
                    il.Emit(OpCodes.Ldstr, Llvm.Name(global));
                    il.Emit(OpCodes.Call, typeof(Cxx).GetMethod(nameof(Cxx.Global))!);
                }
                il.Emit(OpCodes.Stsfld, field);
            }
            else
                Allocate(Llvm.LLVMGlobalGetValueType(global), field, checked((int)Llvm.LLVMGetAlignment(global)));
        }
        foreach ((nint value, FieldBuilder field) in constants)
            Allocate(Llvm.LLVMTypeOf(value), field, 0);
        foreach ((nint global, FieldBuilder field) in Globals)
            if (Llvm.LLVMGetInitializer(global) != 0 && !ThreadGlobals.ContainsKey(global))
            {
                try { InitializeGlobal(il, Llvm.LLVMGetInitializer(global), field); }
                catch (Exception error) { throw CompilationError.InGlobal(error, global); }
            }
        foreach ((nint value, FieldBuilder field) in constants)
            InitializeGlobal(il, value, field);
        // Before any translated code runs, so pointers taken anywhere resolve to their methods (PF-13).
        foreach ((nint function, (MethodInfo identity, MethodInfo target)) in AddressTaken)
        {
            bool variadic = Llvm.LLVMIsFunctionVarArg(Llvm.LLVMGlobalGetValueType(function)) != 0;
            il.Emit(OpCodes.Ldftn, identity);
            il.Emit(OpCodes.Ldftn, target);
            il.Emit(OpCodes.Call, typeof(SystemAbi).GetMethod(variadic ? nameof(SystemAbi.RegisterVariadicCallback) : nameof(SystemAbi.RegisterCallback))!);
            il.Emit(OpCodes.Pop);
        }
        if (libcMain == 0) il.Emit(OpCodes.Call, constructors);
        il.Emit(OpCodes.Ret);
        ILGenerator lifecycle = constructors.GetILGenerator();
        foreach (nint function in LifecycleFunctions("llvm.global_dtors"))
        {
            MethodInfo destructor = ResolveFunction(function);
            lifecycle.Emit(OpCodes.Ldftn, Host is null ? destructor : Host.Callback(function, destructor));
            if (libcMain != 0 && libcFinalizer != 0)
            {
                // musl runs these through atexit, before it flushes stdio, as it does .fini_array entries.
                lifecycle.Emit(OpCodes.Call, ResolveFunction(libcFinalizer));
                lifecycle.Emit(OpCodes.Pop);
            }
            else
                lifecycle.Emit(OpCodes.Call, typeof(ProcessRuntime).GetMethod(nameof(ProcessRuntime.AtFini))!);
        }
        foreach (nint function in LifecycleFunctions("llvm.global_ctors"))
            lifecycle.Emit(OpCodes.Call, ResolveFunction(function));
        lifecycle.Emit(OpCodes.Ret);

        IEnumerable<nint> LifecycleFunctions(string name)
        {
            nint global = Llvm.LLVMGetNamedGlobal(module, name);
            if (global == 0)
                return [];
            nint initializer = Llvm.LLVMGetInitializer(global);
            List<(long Priority, nint Function)> functions = [];
            for (uint index = 0; index < Types.ElementCount(Llvm.LLVMTypeOf(initializer)); index++)
            {
                nint entry = Llvm.LLVMGetAggregateElement(initializer, index);
                nint function = Llvm.LLVMGetAggregateElement(entry, 1);
                if (Llvm.LLVMIsNull(function) != 0)
                    continue;
                if (Llvm.LLVMCountParams(function) != 0 || Types.Map(Llvm.LLVMGetReturnType(Llvm.LLVMGlobalGetValueType(function))) != typeof(void))
                    throw new NotSupportedException($"Unsupported global constructor/destructor signature: {Llvm.Name(function)}");
                functions.Add((Llvm.LLVMConstIntGetSExtValue(Llvm.LLVMGetAggregateElement(entry, 0)), function));
            }
            return functions.OrderBy(item => item.Priority).Select(item => item.Function);
        }

        void Allocate(nint type, FieldBuilder field, int alignment)
        {
            il.Emit(OpCodes.Ldc_I8, Types.Size(type));
            il.Emit(OpCodes.Ldc_I4, Math.Max(alignment, Types.Alignment(type)));
            il.Emit(OpCodes.Call, typeof(Memory).GetMethod(nameof(Memory.AllocateGlobal))!);
            il.Emit(OpCodes.Stsfld, field);
        }
    }

    private void InitializeGlobal(ILGenerator il, nint value, FieldBuilder field)
    {
        var emitter = new ValueEmitter(this, il);
        var data = new ConstantData(Types, value);
        foreach ((long index, byte[] bytes) in data.Chunks)
        {
            long offset = index * ConstantData.ChunkSize;
            FieldBuilder blob = FieldHolder().DefineInitializedData($"__data{field.Name}_{offset}", bytes, FieldAttributes.Assembly | FieldAttributes.Static);
            il.Emit(OpCodes.Ldsfld, field);
            emitter.Offset(offset);
            il.Emit(OpCodes.Ldsflda, blob);
            il.Emit(OpCodes.Conv_I);
            il.Emit(OpCodes.Ldc_I8, (long)bytes.Length);
            il.Emit(OpCodes.Call, typeof(Memory).GetMethod(nameof(Memory.Copy))!);
            il.Emit(OpCodes.Pop);
        }
        foreach ((long offset, nint relocation) in data.Relocations)
        {
            il.Emit(OpCodes.Ldsfld, field);
            emitter.Offset(offset);
            emitter.Load(relocation);
            emitter.StoreMemory(Llvm.LLVMTypeOf(relocation));
        }
    }
}
