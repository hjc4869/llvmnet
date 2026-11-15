using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace LlvmNet;

/// <summary>
/// The compiler's LLVM built by llvmnet itself (XP-04): LLVM's C API and llvmnet's bridge (native/*.cpp) compiled in
/// portable mode into one managed library that embeds the C library (-shared --embed-libc), LlvmNet.Llvm.dll. The same
/// library runs on every .NET host, which lets the compiler run where no native LLVM 22 is installed: a portable
/// toolchain carries it beside llvmnet.dll. When it is active, each method of <see cref="Llvm"/> calls the library's
/// export of the same C name through a function pointer instead of its <see cref="LlvmNative"/> import.
/// </summary>
/// <remarks>
/// LLVMNET_LLVM selects the library: a path to LlvmNet.Llvm.dll, "native" for the host's libLLVM-22 and
/// libllvmnet-llvm, or unset for LlvmNet.Llvm.dll beside the compiler when it is there and the native libraries
/// otherwise. scripts/selfhost/build-library.sh builds the library; `llvmnet --print-llvm-imports` names its roots.
/// </remarks>
internal static unsafe partial class LlvmManaged
{
    internal const string LibraryFileName = "LlvmNet.Llvm.dll";

    /// <summary>The managed library's path when the compiler runs on it, else null (the native libraries).</summary>
    internal static readonly string? LibraryPath = Choose();

    /// <summary>Whether the compiler calls the llvmnet-built LLVM; a constant for the JIT once this class is initialized.</summary>
    internal static readonly bool Active = LibraryPath is not null && Bind(LibraryPath);

    private static string? Choose()
    {
        string? configured = Environment.GetEnvironmentVariable("LLVMNET_LLVM");
        if (configured == "native") return null;
        if (!string.IsNullOrEmpty(configured))
        {
            string path = Path.GetFullPath(configured);
            if (!File.Exists(path)) throw new FileNotFoundException($"LLVMNET_LLVM names {path}, which does not exist; set it to an llvmnet-built {LibraryFileName} or to \"native\".", path);
            return path;
        }
        string beside = Path.Combine(AppContext.BaseDirectory, LibraryFileName);
        if (File.Exists(beside)) return beside;
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException($"The llvmnet compiler on {RuntimeInformation.OSDescription} runs on the llvmnet-built LLVM, {LibraryFileName}, which is not beside {Path.Combine(AppContext.BaseDirectory, "llvmnet.dll")}. Install a portable llvmnet package, which has it, or set LLVMNET_LLVM to it.");
        return null;
    }

    /// <summary>
    /// Loads the library, points every binding at the export of its name after checking that the export takes and returns
    /// what the native import does (a pointer, a string or an out parameter is a pointer there), and runs the library's
    /// initializer, which starts its C library and LLVM's static constructors on this thread.
    /// </summary>
    private static bool Bind(string path)
    {
        Assembly library = Assembly.LoadFrom(path);
        Type program = library.GetType("Program", throwOnError: false) ?? throw new BadImageFormatException($"{path} has no Program class; it is not an llvmnet-built library.");
        Dictionary<string, MethodInfo> exports = new(StringComparer.Ordinal);
        foreach (MethodInfo method in program.GetMethods(BindingFlags.Public | BindingFlags.Static))
            exports.TryAdd(method.Name, method);
        List<string> problems = [];
        foreach (MethodInfo import in typeof(LlvmNative).GetMethods(BindingFlags.NonPublic | BindingFlags.Static))
        {
            if (!import.Attributes.HasFlag(MethodAttributes.PinvokeImpl)) continue;
            string name = import.GetCustomAttribute<DllImportAttribute>()?.EntryPoint ?? import.Name;
            FieldInfo field = typeof(LlvmManaged).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException($"LlvmManaged has no binding for {name}");
            if (!exports.TryGetValue(name, out MethodInfo? export))
            {
                problems.Add($"{name} is not exported");
                continue;
            }
            if (Mismatch(import, export) is string mismatch)
            {
                problems.Add($"{name}: {mismatch}");
                continue;
            }
            field.SetValue(null, export.MethodHandle.GetFunctionPointer());
        }
        if (problems.Count != 0)
            throw new InvalidOperationException($"{path} does not provide what the compiler calls ({string.Join("; ", problems.Take(8))}{(problems.Count > 8 ? $"; and {problems.Count - 8} more" : "")}); rebuild it with scripts/selfhost/build-library.sh from this compiler.");
        RuntimeHelpers.RunClassConstructor(program.TypeHandle);
        return true;
    }

    /// <summary>Why an export cannot stand for a native import, or null: the same count of parameters, each and the result of the same size and kind.</summary>
    private static string? Mismatch(MethodInfo import, MethodInfo export)
    {
        ParameterInfo[] expected = import.GetParameters(), actual = export.GetParameters();
        if (expected.Length != actual.Length) return $"takes {actual.Length} parameters, not {expected.Length}";
        for (int index = 0; index < expected.Length; index++)
            if (Kind(expected[index].ParameterType) != Kind(actual[index].ParameterType))
                return $"parameter {index + 1} is {actual[index].ParameterType.Name}, not {expected[index].ParameterType.Name}";
        return Kind(import.ReturnType) == Kind(export.ReturnType) ? null : $"returns {export.ReturnType.Name}, not {import.ReturnType.Name}";
    }

    /// <summary>How a value crosses the call: pointers, strings and by-reference values are machine words; integers by size.</summary>
    private static string Kind(Type type) =>
        type.IsPointer || type.IsByRef || type == typeof(string) || type == typeof(nint) || type == typeof(nuint) ? "word"
        : type == typeof(long) || type == typeof(ulong) ? "word"
        : type == typeof(int) || type == typeof(uint) ? "int32"
        : type == typeof(double) ? "double" : type == typeof(float) ? "float" : type == typeof(void) ? "void" : type.FullName ?? type.Name;

    /// <summary>A string as the NUL-terminated UTF-8 the C API reads.</summary>
    internal static byte[] Utf8(string text)
    {
        byte[] bytes = new byte[Encoding.UTF8.GetByteCount(text) + 1];
        Encoding.UTF8.GetBytes(text, bytes);
        return bytes;
    }

    /// <summary>
    /// A host path as the library, a portable program, names it: on Windows a drive is a root directory named by its letter
    /// ("C:\x\y.bc" is "/c/x/y.bc") and separators are "/", which LLVM's POSIX path functions split correctly; elsewhere the
    /// path itself (docs/dev/host-abi.md).
    /// </summary>
    internal static string ProgramPath(string path)
    {
        if (!OperatingSystem.IsWindows() || path.Length == 0) return path;
        path = path.Replace('\\', '/');
        if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')
            return "/" + char.ToLowerInvariant(path[0]) + (path.Length > 2 && path[2] == '/' ? path[2..] : "/" + path[2..]);
        return path;
    }

    internal static nint llvmnet_constant_bits;
    internal static nint llvmnet_link;
    internal static nint llvmnet_native_objects;
    internal static nint llvmnet_stamp_abi;
    internal static nint llvmnet_isa_levels;
    internal static nint llvmnet_create_native_shim;
    internal static nint llvmnet_lower_constrained_fp;
    internal static nint llvmnet_wrap_vector_invokes;
    internal static nint llvmnet_narrow_vector_integers;
    internal static nint llvmnet_expand_cttz_elts;
    internal static nint llvmnet_expand_vector_geps;
    internal static nint llvmnet_fuse_multiply_adds;
    internal static nint llvmnet_scalarize_functions;
    internal static nint llvmnet_rounding_averages;
    internal static nint llvmnet_widening_reductions;
    internal static nint llvmnet_strided_gathers;
    internal static nint llvmnet_add_native_thunk;
    internal static nint llvmnet_add_native_forwarder;
    internal static nint LLVMWriteBitcodeToFile;
    internal static nint LLVMGetTarget;
    internal static nint LLVMGetVersion;
    internal static nint LLVMContextCreate;
    internal static nint LLVMContextDispose;
    internal static nint LLVMCreateMemoryBufferWithContentsOfFile;
    internal static nint LLVMParseIRInContext;
    internal static nint LLVMDisposeModule;
    internal static nint LLVMDisposeMessage;
    internal static nint LLVMGetFirstFunction;
    internal static nint LLVMGetNextFunction;
    internal static nint LLVMIsDeclaration;
    internal static nint LLVMGetValueName2;
    internal static nint LLVMGlobalGetValueType;
    internal static nint LLVMGetReturnType;
    internal static nint LLVMCountParams;
    internal static nint LLVMGetParam;
    internal static nint LLVMTypeOf;
    internal static nint LLVMGetTypeKind;
    internal static nint LLVMGetIntTypeWidth;
    internal static nint LLVMGetFirstBasicBlock;
    internal static nint LLVMGetNextBasicBlock;
    internal static nint LLVMGetFirstInstruction;
    internal static nint LLVMGetNextInstruction;
    internal static nint LLVMGetInstructionOpcode;
    internal static nint LLVMGetOperand;
    internal static nint LLVMGetFirstUse;
    internal static nint LLVMGetNextUse;
    internal static nint LLVMGetUser;
    internal static nint LLVMIsAInstruction;
    internal static nint LLVMGetInstructionParent;
    internal static nint LLVMGetBasicBlockParent;
    internal static nint LLVMIsAConstantInt;
    internal static nint LLVMConstIntGetSExtValue;
    internal static nint LLVMPrintValueToString;
    internal static nint LLVMPrintTypeToString;
    internal static nint LLVMGetDataLayoutStr;
    internal static nint LLVMCreateTargetData;
    internal static nint LLVMDisposeTargetData;
    internal static nint LLVMByteOrder;
    internal static nint LLVMPointerSize;
    internal static nint LLVMABISizeOfType;
    internal static nint LLVMABIAlignmentOfType;
    internal static nint LLVMOffsetOfElement;
    internal static nint LLVMGetElementType;
    internal static nint LLVMCountStructElementTypes;
    internal static nint LLVMStructGetTypeAtIndex;
    internal static nint LLVMGetArrayLength2;
    internal static nint LLVMGetVectorSize;
    internal static nint LLVMStoreSizeOfType;
    internal static nint LLVMGetNumMaskElements;
    internal static nint LLVMGetMaskValue;
    internal static nint LLVMGetFastMathFlags;
    internal static nint LLVMCountParamTypes;
    internal static nint LLVMGetParamTypes;
    internal static nint LLVMIsFunctionVarArg;
    internal static nint LLVMGetFirstGlobal;
    internal static nint LLVMGetNamedGlobal;
    internal static nint LLVMGetNextGlobal;
    internal static nint LLVMGetInitializer;
    internal static nint LLVMGetAlignment;
    internal static nint LLVMIsThreadLocal;
    internal static nint LLVMGetFirstGlobalAlias;
    internal static nint LLVMGetNextGlobalAlias;
    internal static nint LLVMAliasGetAliasee;
    internal static nint LLVMIsAGlobalAlias;
    internal static nint LLVMGetNumOperands;
    internal static nint LLVMIsNull;
    internal static nint LLVMIsConstant;
    internal static nint LLVMIsUndef;
    internal static nint LLVMIsPoison;
    internal static nint LLVMIsAConstantFP;
    internal static nint LLVMIsAConstantExpr;
    internal static nint LLVMIsAInlineAsm;
    internal static nint LLVMGetInlineAsmAsmString;
    internal static nint LLVMGetInlineAsmConstraintString;
    internal static nint LLVMIsAFunction;
    internal static nint LLVMGetConstOpcode;
    internal static nint LLVMConstRealGetDouble;
    internal static nint LLVMGetAggregateElement;
    internal static nint LLVMCountIncoming;
    internal static nint LLVMGetIncomingValue;
    internal static nint LLVMGetIncomingBlock;
    internal static nint LLVMGetNumSuccessors;
    internal static nint LLVMGetSuccessor;
    internal static nint LLVMGetSwitchCaseValue;
    internal static nint LLVMGetCondition;
    internal static nint LLVMGetAllocatedType;
    internal static nint LLVMGetOrdering;
    internal static nint LLVMGetVolatile;
    internal static nint LLVMGetAtomicRMWBinOp;
    internal static nint LLVMGetGEPSourceElementType;
    internal static nint LLVMGetICmpPredicate;
    internal static nint LLVMGetFCmpPredicate;
    internal static nint LLVMGetNumArgOperands;
    internal static nint LLVMGetCalledValue;
    internal static nint LLVMGetCalledFunctionType;
    internal static nint LLVMGetTailCallKind;
    internal static nint LLVMGetInstructionCallConv;
    internal static nint LLVMGetFunctionCallConv;
    internal static nint LLVMGetNumIndices;
    internal static nint LLVMGetNumClauses;
    internal static nint LLVMGetClause;
    internal static nint LLVMGetEnumAttributeKindForName;
    internal static nint LLVMGetEnumAttributeAtIndex;
    internal static nint LLVMGetCallSiteEnumAttribute;
    internal static nint LLVMGetTypeAttributeValue;
    internal static nint LLVMGetEnumAttributeValue;
    internal static nint LLVMGetIndices;
    internal static nint llvmnet_demangle;
    internal static nint LLVMGetDebugLocFilename;
    internal static nint LLVMGetDebugLocDirectory;
    internal static nint LLVMGetDebugLocLine;
    internal static nint LLVMGetDebugLocColumn;
    internal static nint LLVMGetLinkage;
    internal static nint LLVMGetVisibility;
    internal static nint LLVMGetNamedFunction;
    internal static nint LLVMAddFunction;
    internal static nint LLVMFunctionType;
    internal static nint LLVMPointerTypeInContext;
    internal static nint LLVMGetModuleContext;
    internal static nint LLVMGetFirstGlobalIFunc;
    internal static nint LLVMGetNextGlobalIFunc;
    internal static nint LLVMIsAGlobalIFunc;
    internal static nint LLVMIsAGlobalVariable;
    internal static nint LLVMRemoveStringAttributeAtIndex;
}
