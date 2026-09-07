using System.Reflection;
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
    internal TypeSystem Types { get; private set; } = null!;
    internal TypeBuilder Program { get; private set; } = null!;
    internal HostInterop? Host { get; private set; }
    private readonly Dictionary<nint, FieldBuilder> constants = [];
    private readonly HashSet<string> managedImports = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MethodInfo> runtimeExports = typeof(Memory).Assembly.GetTypes()
        .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static))
        .SelectMany(method => method.GetCustomAttributes<CExportAttribute>().Select(attribute => (attribute.Name, Method: method)))
        .ToDictionary(item => item.Name, item => item.Method, StringComparer.Ordinal);

    internal bool IsManagedImport(string name) => managedImports.Contains(name);

    internal MethodInfo ResolveFunction(nint function)
    {
        if (Methods.TryGetValue(function, out MethodInfo? method))
            return method;
        string name = Llvm.Name(function);
        nint signature = Llvm.LLVMGlobalGetValueType(function);
        if (options.NativeImports.TryGetValue(name, out NativeImport? import))
        {
            Type returnType = Types.Map(Llvm.LLVMGetReturnType(signature));
            Type[] parameters = Types.Parameters(signature);
            bool aggregateAbi = Enumerable.Range(0, checked((int)Llvm.LLVMCountParams(function)))
                .Any(index => Llvm.ParameterAttribute(function, (uint)index, "byval") != 0 || Llvm.ParameterAttribute(function, (uint)index, "sret") != 0);
            if (aggregateAbi || Llvm.LLVMIsFunctionVarArg(signature) != 0 || parameters.Any(parameter => !NativeScalar(parameter)) || !NativeScalar(returnType))
                throw new NotSupportedException($"Native import {name} requires a non-variadic scalar C ABI signature; aggregates and C++ ABI are not supported.");
            MethodBuilder native = Program.DefinePInvokeMethod($"__native_{name}", import.Library, import.EntryPoint,
                MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.PinvokeImpl, CallingConventions.Standard,
                returnType, parameters, CallingConvention.Cdecl, CharSet.Ansi);
            native.SetImplementationFlags(MethodImplAttributes.PreserveSig);
            Methods[function] = native;
            return native;
        }
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
        if (!runtimeExports.TryGetValue(name, out method))
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
        var assembly = new PersistedAssemblyBuilder(new AssemblyName(Path.GetFileNameWithoutExtension(output)), typeof(object).Assembly);
        ModuleBuilder cilModule = assembly.DefineDynamicModule("program");
        Program = cilModule.DefineType("Program", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        using var types = new TypeSystem(module, cilModule);
        Types = types;
        using HostInterop? host = options.Runtime == "system" ? new HostInterop(this, module, output, options) : null;
        Host = host;
        assembly.SetCustomAttribute(new CustomAttributeBuilder(typeof(AssemblyMetadataAttribute).GetConstructor([typeof(string), typeof(string)])!, ["llvmnet.abi", options.AbiTag]));
        foreach (string reference in options.CilReferences)
        {
            Assembly library = Assembly.LoadFrom(reference);
            string? referenceAbi = library.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(attribute => attribute.Key == "llvmnet.abi")?.Value;
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
            MethodBuilder method = Program.DefineMethod(Llvm.Name(function), MethodAttributes.Public | MethodAttributes.Static,
                Types.Map(Llvm.LLVMGetReturnType(signature)), Types.Parameters(signature));
            method.SetCustomAttribute(new CustomAttributeBuilder(typeof(CExportAttribute).GetConstructor([typeof(string)])!, [Llvm.Name(function)]));
            Methods[function] = method;
        }
        for (nint global = Llvm.LLVMGetFirstGlobal(module); global != 0; global = Llvm.LLVMGetNextGlobal(global))
        {
            if (Llvm.Name(global).StartsWith("llvm.", StringComparison.Ordinal))
                continue;
            if (Llvm.LLVMIsThreadLocal(global) != 0)
                throw new NotSupportedException($"Thread-local global: {Llvm.Name(global)}");
            if (Llvm.LLVMGetInitializer(global) == 0 && Host is not null && !Cxx.HasGlobal(Llvm.Name(global)))
                Host.LibraryFor(Llvm.Name(global));
            else if (Llvm.LLVMGetInitializer(global) == 0 && !Stdio.HasGlobal(Llvm.Name(global)) && !Cxx.HasGlobal(Llvm.Name(global)))
                throw new NotSupportedException($"Unresolved external global: {Llvm.Name(global)}");
            Globals[global] = Program.DefineField($"__global{Globals.Count}", typeof(nint), FieldAttributes.Private | FieldAttributes.Static);
        }
        foreach ((nint function, MethodInfo method) in Methods.ToArray())
            new FunctionEmitter(this, function, (MethodBuilder)method).Emit();
        EmitInitializer(module);
        MethodBuilder? main = Methods.Where(pair => Llvm.Name(pair.Key) == "main").Select(pair => pair.Value).OfType<MethodBuilder>().SingleOrDefault()
            ?? Methods.Where(pair => Llvm.Name(pair.Key) == "_QQmain").Select(pair => pair.Value).OfType<MethodBuilder>().SingleOrDefault();
        if (main is null && !options.Library)
            throw new NotSupportedException("No main function found.");
        MethodBuilder? entry = options.Library ? null : EmitEntry(main!);
        Program.CreateType();
        MetadataBuilder metadata = assembly.GenerateMetadata(out BlobBuilder ilStream, out BlobBuilder fieldData);
        var pe = new ManagedPEBuilder(new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.LargeAddressAware | (options.Library ? Characteristics.Dll : 0)),
            new MetadataRootBuilder(metadata), ilStream, mappedFieldData: fieldData,
            entryPoint: options.Library ? default : MetadataTokens.MethodDefinitionHandle(entry!.MetadataToken));
        var image = new BlobBuilder();
        pe.Serialize(image);
        using (FileStream stream = File.Create(output))
            image.WriteContentTo(stream);
        File.WriteAllText(Path.ChangeExtension(output, ".runtimeconfig.json"), JsonSerializer.Serialize(new
        {
            runtimeOptions = new { tfm = "net10.0", framework = new { name = "Microsoft.NETCore.App", version = "10.0.0" } }
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

    internal FieldBuilder Constant(nint value)
    {
        if (!constants.TryGetValue(value, out FieldBuilder? field))
        {
            field = Program.DefineField($"__constant{constants.Count}", typeof(nint), FieldAttributes.Private | FieldAttributes.Static);
            constants[value] = field;
        }
        return field;
    }

    private MethodBuilder EmitEntry(MethodBuilder main)
    {
        nint llvmMain = Methods.First(pair => pair.Value == main).Key;
        Type[] parameters = Types.Parameters(Llvm.LLVMGlobalGetValueType(llvmMain));
        bool fortran = Llvm.Name(llvmMain) == "_QQmain" && main.ReturnType == typeof(void) && parameters.Length == 0;
        if (!fortran && (main.ReturnType != typeof(int) || !(parameters.Length == 0 || parameters.SequenceEqual(new[] { typeof(int), typeof(nint) }) || parameters.SequenceEqual(new[] { typeof(int), typeof(nint), typeof(nint) }))))
            throw new NotSupportedException("The entry point must be int main(void), int main(int, char **[, char **]), or a Fortran main.");
        MethodBuilder entry = Program.DefineMethod("__Entry", MethodAttributes.Public | MethodAttributes.Static, typeof(int), [typeof(string[])]);
        ILGenerator il = entry.GetILGenerator();
        LocalBuilder result = il.DeclareLocal(typeof(int));
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, typeof(ProcessRuntime).GetMethod(nameof(ProcessRuntime.Initialize))!);
        LocalBuilder count = il.DeclareLocal(typeof(int));
        il.Emit(OpCodes.Stloc, count);
        Label done = il.BeginExceptionBlock();
        if (parameters.Length >= 2)
        {
            il.Emit(OpCodes.Ldloc, count);
            il.Emit(OpCodes.Call, typeof(ProcessRuntime).GetProperty(nameof(ProcessRuntime.Arguments))!.GetMethod!);
        }
        if (parameters.Length == 3)
            il.Emit(OpCodes.Call, typeof(ProcessRuntime).GetProperty(nameof(ProcessRuntime.EnvironmentVector))!.GetMethod!);
        il.Emit(OpCodes.Call, main);
        if (fortran)
            il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, result);
        il.Emit(OpCodes.Leave, done);
        il.BeginFinallyBlock();
        if (Host is not null)
            il.Emit(OpCodes.Call, typeof(SystemAbi).GetMethod(nameof(SystemAbi.Flush))!);
        il.Emit(OpCodes.Call, typeof(ProcessRuntime).GetMethod(nameof(ProcessRuntime.Cleanup))!);
        il.EndExceptionBlock();
        il.Emit(OpCodes.Ldloc, result);
        il.Emit(OpCodes.Ret);
        return entry;
    }

    private void EmitInitializer(nint module)
    {
        ILGenerator il = Program.DefineTypeInitializer().GetILGenerator();
        il.Emit(OpCodes.Ldstr, options.AbiTag);
        il.Emit(OpCodes.Call, typeof(AbiContract).GetMethod(nameof(AbiContract.Require))!);
        var emitter = new ValueEmitter(this, il);
        foreach ((nint global, FieldBuilder field) in Globals)
        {
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
                    il.Emit(OpCodes.Call, Stdio.HasGlobal(Llvm.Name(global)) ? typeof(Stdio).GetMethod(nameof(Stdio.Global))! : typeof(Cxx).GetMethod(nameof(Cxx.Global))!);
                }
                il.Emit(OpCodes.Stsfld, field);
            }
            else
                Allocate(Llvm.LLVMGlobalGetValueType(global), field, checked((int)Llvm.LLVMGetAlignment(global)));
        }
        foreach ((nint value, FieldBuilder field) in constants)
            Allocate(Llvm.LLVMTypeOf(value), field, 0);
        foreach ((nint global, FieldBuilder field) in Globals)
            if (Llvm.LLVMGetInitializer(global) != 0)
                Initialize(Llvm.LLVMGetInitializer(global), field);
        foreach ((nint value, FieldBuilder field) in constants)
            Initialize(value, field);
        foreach (nint function in LifecycleFunctions("llvm.global_dtors"))
        {
            MethodInfo destructor = ResolveFunction(function);
            il.Emit(OpCodes.Ldftn, Host is null ? destructor : Host.Callback(function, destructor));
            il.Emit(OpCodes.Call, typeof(ProcessRuntime).GetMethod(nameof(ProcessRuntime.AtExit))!);
            il.Emit(OpCodes.Pop);
        }
        foreach (nint function in LifecycleFunctions("llvm.global_ctors"))
            il.Emit(OpCodes.Call, ResolveFunction(function));
        il.Emit(OpCodes.Ret);

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
            il.Emit(OpCodes.Call, typeof(Memory).GetMethod(nameof(Memory.Allocate))!);
            il.Emit(OpCodes.Stsfld, field);
        }

        void Initialize(nint value, FieldBuilder field)
        {
            var data = new ConstantData(Types, value);
            if (data.Bytes.Any(item => item != 0))
            {
                FieldBuilder blob = Program.DefineInitializedData($"__data{field.Name}", data.Bytes, FieldAttributes.Private | FieldAttributes.Static);
                il.Emit(OpCodes.Ldsfld, field);
                il.Emit(OpCodes.Ldsflda, blob);
                il.Emit(OpCodes.Conv_I);
                il.Emit(OpCodes.Ldc_I8, (long)data.Bytes.Length);
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
}