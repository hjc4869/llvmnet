using System.Runtime.InteropServices;

namespace LlvmNet;

internal static unsafe class Llvm
{
    internal const string Library = "libLLVM-22.so";
    private static uint ConstantBits(nint value, byte* destination, uint capacity) => LlvmManaged.Active ? ((delegate*<nint, nint, uint, uint>)LlvmManaged.llvmnet_constant_bits)(value, (nint)destination, capacity) : LlvmNative.ConstantBits(value, destination, capacity);
    private static int LinkFiles(nint* paths, int* wholeArchive, uint count, nint* entry, uint entryCount, nint* required, uint requiredCount, string output, string abi, out nint message)
    {
        if (!LlvmManaged.Active) return LlvmNative.LinkFiles(paths, wholeArchive, count, entry, entryCount, required, requiredCount, output, abi, out message);
        message = default;
        fixed (byte* outputUtf8 = LlvmManaged.Utf8(LlvmManaged.ProgramPath(output)))
        fixed (byte* abiUtf8 = LlvmManaged.Utf8(abi))
        fixed (nint* messagePointer = &message)
            return ((delegate*<nint, nint, uint, nint, uint, nint, uint, nint, nint, nint, int>)LlvmManaged.llvmnet_link)((nint)paths, (nint)wholeArchive, count, (nint)entry, entryCount, (nint)required, requiredCount, (nint)outputUtf8, (nint)abiUtf8, (nint)messagePointer);
    }
    private static int ReadNativeObjects(string path, string directory, out nint listing, out nint message)
    {
        if (!LlvmManaged.Active) return LlvmNative.ReadNativeObjects(path, directory, out listing, out message);
        listing = default;
        message = default;
        fixed (byte* pathUtf8 = LlvmManaged.Utf8(LlvmManaged.ProgramPath(path)))
        fixed (byte* directoryUtf8 = LlvmManaged.Utf8(LlvmManaged.ProgramPath(directory)))
        fixed (nint* listingPointer = &listing)
        fixed (nint* messagePointer = &message)
            return ((delegate*<nint, nint, nint, nint, int>)LlvmManaged.llvmnet_native_objects)((nint)pathUtf8, (nint)directoryUtf8, (nint)listingPointer, (nint)messagePointer);
    }
    private static int StampFile(string path, string abi, string isa, int textual, out nint message)
    {
        if (!LlvmManaged.Active) return LlvmNative.StampFile(path, abi, isa, textual, out message);
        message = default;
        fixed (byte* pathUtf8 = LlvmManaged.Utf8(LlvmManaged.ProgramPath(path)))
        fixed (byte* abiUtf8 = LlvmManaged.Utf8(abi))
        fixed (byte* isaUtf8 = LlvmManaged.Utf8(isa))
        fixed (nint* messagePointer = &message)
            return ((delegate*<nint, nint, nint, int, nint, int>)LlvmManaged.llvmnet_stamp_abi)((nint)pathUtf8, (nint)abiUtf8, (nint)isaUtf8, textual, (nint)messagePointer);
    }
    private static nint IsaLevelsOf(nint module) => LlvmManaged.Active ? ((delegate*<nint, nint>)LlvmManaged.llvmnet_isa_levels)(module) : LlvmNative.IsaLevelsOf(module);
    internal static nint CreateNativeShim(nint source) => LlvmManaged.Active ? ((delegate*<nint, nint>)LlvmManaged.llvmnet_create_native_shim)(source) : LlvmNative.CreateNativeShim(source);
    internal static uint LowerConstrainedFloatingPoint(nint module) => LlvmManaged.Active ? ((delegate*<nint, uint>)LlvmManaged.llvmnet_lower_constrained_fp)(module) : LlvmNative.LowerConstrainedFloatingPoint(module);
    internal static uint WrapVectorInvokes(nint source) => LlvmManaged.Active ? ((delegate*<nint, uint>)LlvmManaged.llvmnet_wrap_vector_invokes)(source) : LlvmNative.WrapVectorInvokes(source);
    internal static uint NarrowVectorIntegers(nint module) => LlvmManaged.Active ? ((delegate*<nint, uint>)LlvmManaged.llvmnet_narrow_vector_integers)(module) : LlvmNative.NarrowVectorIntegers(module);
    internal static uint ExpandCountTrailingZeroElements(nint module) => LlvmManaged.Active ? ((delegate*<nint, uint>)LlvmManaged.llvmnet_expand_cttz_elts)(module) : LlvmNative.ExpandCountTrailingZeroElements(module);
    internal static uint ExpandVectorGeps(nint module) => LlvmManaged.Active ? ((delegate*<nint, uint>)LlvmManaged.llvmnet_expand_vector_geps)(module) : LlvmNative.ExpandVectorGeps(module);
    internal static uint FuseMultiplyAdds(nint module) => LlvmManaged.Active ? ((delegate*<nint, uint>)LlvmManaged.llvmnet_fuse_multiply_adds)(module) : LlvmNative.FuseMultiplyAdds(module);
    internal static uint ScalarizeFunctions(nint* functions, uint count) => LlvmManaged.Active ? ((delegate*<nint, uint, uint>)LlvmManaged.llvmnet_scalarize_functions)((nint)functions, count) : LlvmNative.ScalarizeFunctions(functions, count);
    internal static uint RoundingAverages(nint function, uint fragmentBits) => LlvmManaged.Active ? ((delegate*<nint, uint, uint>)LlvmManaged.llvmnet_rounding_averages)(function, fragmentBits) : LlvmNative.RoundingAverages(function, fragmentBits);
    internal static uint WideningReductions(nint function, uint fragmentBits, out uint products)
    {
        if (!LlvmManaged.Active) return LlvmNative.WideningReductions(function, fragmentBits, out products);
        products = default;
        fixed (uint* productsPointer = &products)
            return ((delegate*<nint, uint, nint, uint>)LlvmManaged.llvmnet_widening_reductions)(function, fragmentBits, (nint)productsPointer);
    }
    internal static uint StridedGathers(nint function, uint fragmentBits, int apply, out uint lanes, out uint loads)
    {
        if (!LlvmManaged.Active) return LlvmNative.StridedGathers(function, fragmentBits, apply, out lanes, out loads);
        lanes = default;
        loads = default;
        fixed (uint* lanesPointer = &lanes)
        fixed (uint* loadsPointer = &loads)
            return ((delegate*<nint, uint, int, nint, nint, uint>)LlvmManaged.llvmnet_strided_gathers)(function, fragmentBits, apply, (nint)lanesPointer, (nint)loadsPointer);
    }
    internal static void AddNativeThunk(nint shim, nint function, nint call, string name)
    {
        if (!LlvmManaged.Active) { LlvmNative.AddNativeThunk(shim, function, call, name); return; }
        fixed (byte* nameUtf8 = LlvmManaged.Utf8(name))
            ((delegate*<nint, nint, nint, nint, void>)LlvmManaged.llvmnet_add_native_thunk)(shim, function, call, (nint)nameUtf8);
    }
    internal static void AddNativeForwarder(nint shim, nint function, string name)
    {
        if (!LlvmManaged.Active) { LlvmNative.AddNativeForwarder(shim, function, name); return; }
        fixed (byte* nameUtf8 = LlvmManaged.Utf8(name))
            ((delegate*<nint, nint, nint, void>)LlvmManaged.llvmnet_add_native_forwarder)(shim, function, (nint)nameUtf8);
    }
    internal static int LLVMWriteBitcodeToFile(nint module, string path)
    {
        if (!LlvmManaged.Active) return LlvmNative.LLVMWriteBitcodeToFile(module, path);
        fixed (byte* pathUtf8 = LlvmManaged.Utf8(LlvmManaged.ProgramPath(path)))
            return ((delegate*<nint, nint, int>)LlvmManaged.LLVMWriteBitcodeToFile)(module, (nint)pathUtf8);
    }
    internal static nint LLVMGetTarget(nint module) => LlvmManaged.Active ? ((delegate*<nint, nint>)LlvmManaged.LLVMGetTarget)(module) : LlvmNative.LLVMGetTarget(module);
    private static void LLVMGetVersion(out uint major, out uint minor, out uint patch)
    {
        if (!LlvmManaged.Active) { LlvmNative.LLVMGetVersion(out major, out minor, out patch); return; }
        major = default;
        minor = default;
        patch = default;
        fixed (uint* majorPointer = &major)
        fixed (uint* minorPointer = &minor)
        fixed (uint* patchPointer = &patch)
            ((delegate*<nint, nint, nint, void>)LlvmManaged.LLVMGetVersion)((nint)majorPointer, (nint)minorPointer, (nint)patchPointer);
    }

    /// <summary>Version of the LLVM library loaded into the compiler process.</summary>
    internal static string Version
    {
        get
        {
            LLVMGetVersion(out uint major, out uint minor, out uint patch);
            return $"{major}.{minor}.{patch}";
        }
    }

    /// <summary>Records the ABI tag and, unless it is the baseline, the ISA level (--isa) in a compiled object.</summary>
    internal static void StampAbi(string path, string abi, string isa, bool textual)
    {
        if (StampFile(path, abi, isa == "baseline" ? "" : isa, textual ? 1 : 0, out nint message) == 0) return;
        string text = Marshal.PtrToStringUTF8(message) ?? "Cannot stamp LLVM ABI";
        LLVMDisposeMessage(message);
        throw new InvalidOperationException(text);
    }

    /// <summary>The ISA levels other than the baseline that a linked module's objects were compiled for, comma-separated.</summary>
    internal static string IsaLevels(nint module)
    {
        nint message = IsaLevelsOf(module);
        string levels = Marshal.PtrToStringUTF8(message) ?? "";
        LLVMDisposeMessage(message);
        return levels;
    }

    /// <summary>
    /// Links objects and archives (recognized by content) into one bitcode module: every object, and the archive
    /// members that resolve a referenced symbol (rescanning until none does) or every member of an archive marked
    /// whole-archive. While none of the <paramref name="entry"/> names is defined, a member defining one is linked too.
    /// With <paramref name="nativeMembers"/>, archives' native object members are left out, as a native kernel package
    /// provides them (SV-13); otherwise they are an error.
    /// </summary>
    internal static void Link(string[] paths, bool[] wholeArchive, string output, string abi, string[]? entry = null, string[]? required = null,
        bool nativeMembers = false)
    {
        entry ??= [];
        required ??= [];
        nint[] pointers = new nint[paths.Length + entry.Length + required.Length];
        int[] whole = wholeArchive.Select(flag => (flag ? 1 : 0) | (nativeMembers ? 2 : 0)).ToArray();
        try
        {
            for (int index = 0; index < pointers.Length; index++)
                pointers[index] = Marshal.StringToCoTaskMemUTF8(index < paths.Length ? LlvmManaged.ProgramPath(paths[index]) : index < paths.Length + entry.Length
                    ? entry[index - paths.Length] : required[index - paths.Length - entry.Length]);
            fixed (nint* pointer = pointers)
            fixed (int* flags = whole)
            {
                if (LinkFiles(pointer, flags, (uint)paths.Length, pointer + paths.Length, (uint)entry.Length, pointer + paths.Length + entry.Length,
                    (uint)required.Length, output, abi, out nint message) == 0)
                    return;
                string text = Marshal.PtrToStringUTF8(message) ?? "LLVM link failed";
                LLVMDisposeMessage(message);
                throw new InvalidOperationException(text);
            }
        }
        finally
        {
            foreach (nint pointer in pointers)
                Marshal.FreeCoTaskMem(pointer);
        }
    }

    /// <summary>
    /// The native object files of a link input (native/objects.cpp): the input itself when it is an ELF, COFF or Mach-O
    /// object, the native members of an archive, which are written into <paramref name="directory"/>, or none.
    /// </summary>
    internal static List<NativeObject> NativeObjects(string path, string directory)
    {
        if (ReadNativeObjects(path, directory, out nint listing, out nint message) != 0)
        {
            string text = Marshal.PtrToStringUTF8(message) ?? "cannot read native objects";
            LLVMDisposeMessage(message);
            throw new InvalidOperationException(text);
        }
        string lines = Marshal.PtrToStringUTF8(listing) ?? "";
        LLVMDisposeMessage(listing);
        List<NativeObject> objects = [];
        HashSet<string>? functions = null, data = null, undefined = null;
        foreach (string line in lines.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string name = line[2..];
            switch (line[0])
            {
                case 'F': objects.Add(new NativeObject(name, functions = [], data = [], undefined = [])); break;
                case 'T': functions!.Add(name); break;
                case 'D': data!.Add(name); break;
                default: undefined!.Add(name); break;
            }
        }
        return objects;
    }

    internal static byte[] Bits(nint value)
    {
        uint length = ConstantBits(value, null, 0);
        if (length == 0)
            throw new NotSupportedException($"Not a numeric constant: {Print(value)}");
        byte[] result = new byte[length];
        fixed (byte* pointer = result)
            ConstantBits(value, pointer, length);
        return result;
    }

    internal static nint LLVMContextCreate() => LlvmManaged.Active ? ((delegate*<nint>)LlvmManaged.LLVMContextCreate)() : LlvmNative.LLVMContextCreate();
    internal static void LLVMContextDispose(nint context)
    {
        if (LlvmManaged.Active) ((delegate*<nint, void>)LlvmManaged.LLVMContextDispose)(context);
        else LlvmNative.LLVMContextDispose(context);
    }
    internal static int LLVMCreateMemoryBufferWithContentsOfFile(string path, out nint buffer, out nint message)
    {
        if (!LlvmManaged.Active) return LlvmNative.LLVMCreateMemoryBufferWithContentsOfFile(path, out buffer, out message);
        buffer = default;
        message = default;
        fixed (byte* pathUtf8 = LlvmManaged.Utf8(LlvmManaged.ProgramPath(path)))
        fixed (nint* bufferPointer = &buffer)
        fixed (nint* messagePointer = &message)
            return ((delegate*<nint, nint, nint, int>)LlvmManaged.LLVMCreateMemoryBufferWithContentsOfFile)((nint)pathUtf8, (nint)bufferPointer, (nint)messagePointer);
    }
    internal static int LLVMParseIRInContext(nint context, nint buffer, out nint module, out nint message)
    {
        if (!LlvmManaged.Active) return LlvmNative.LLVMParseIRInContext(context, buffer, out module, out message);
        module = default;
        message = default;
        fixed (nint* modulePointer = &module)
        fixed (nint* messagePointer = &message)
            return ((delegate*<nint, nint, nint, nint, int>)LlvmManaged.LLVMParseIRInContext)(context, buffer, (nint)modulePointer, (nint)messagePointer);
    }
    internal static void LLVMDisposeModule(nint module)
    {
        if (LlvmManaged.Active) ((delegate*<nint, void>)LlvmManaged.LLVMDisposeModule)(module);
        else LlvmNative.LLVMDisposeModule(module);
    }
    internal static void LLVMDisposeMessage(nint message)
    {
        if (LlvmManaged.Active) ((delegate*<nint, void>)LlvmManaged.LLVMDisposeMessage)(message);
        else LlvmNative.LLVMDisposeMessage(message);
    }
    internal static nint LLVMGetFirstFunction(nint module) => LlvmManaged.Active ? ((delegate*<nint, nint>)LlvmManaged.LLVMGetFirstFunction)(module) : LlvmNative.LLVMGetFirstFunction(module);
    internal static nint LLVMGetNextFunction(nint function) => LlvmManaged.Active ? ((delegate*<nint, nint>)LlvmManaged.LLVMGetNextFunction)(function) : LlvmNative.LLVMGetNextFunction(function);
    internal static int LLVMIsDeclaration(nint value) => LlvmManaged.Active ? ((delegate*<nint, int>)LlvmManaged.LLVMIsDeclaration)(value) : LlvmNative.LLVMIsDeclaration(value);
    internal static nint LLVMGetValueName2(nint value, out nuint length)
    {
        if (!LlvmManaged.Active) return LlvmNative.LLVMGetValueName2(value, out length);
        length = default;
        fixed (nuint* lengthPointer = &length)
            return ((delegate*<nint, nint, nint>)LlvmManaged.LLVMGetValueName2)(value, (nint)lengthPointer);
    }
    internal static nint LLVMGlobalGetValueType(nint global) => LlvmManaged.Active ? ((delegate*<nint, nint>)LlvmManaged.LLVMGlobalGetValueType)(global) : LlvmNative.LLVMGlobalGetValueType(global);
    internal static nint LLVMGetReturnType(nint functionType) => LlvmManaged.Active ? ((delegate*<nint, nint>)LlvmManaged.LLVMGetReturnType)(functionType) : LlvmNative.LLVMGetReturnType(functionType);
    internal static uint LLVMCountParams(nint function) => LlvmManaged.Active ? ((delegate*<nint, uint>)LlvmManaged.LLVMCountParams)(function) : LlvmNative.LLVMCountParams(function);
    internal static nint LLVMGetParam(nint function, uint index) => LlvmManaged.Active ? ((delegate*<nint, uint, nint>)LlvmManaged.LLVMGetParam)(function, index) : LlvmNative.LLVMGetParam(function, index);
    internal static nint LLVMTypeOf(nint value) => LlvmManaged.Active ? ((delegate*<nint, nint>)LlvmManaged.LLVMTypeOf)(value) : LlvmNative.LLVMTypeOf(value);
    internal static int LLVMGetTypeKind(nint type) => LlvmManaged.Active ? ((delegate*<nint, int>)LlvmManaged.LLVMGetTypeKind)(type) : LlvmNative.LLVMGetTypeKind(type);
    internal static uint LLVMGetIntTypeWidth(nint type) => LlvmManaged.Active ? ((delegate*<nint, uint>)LlvmManaged.LLVMGetIntTypeWidth)(type) : LlvmNative.LLVMGetIntTypeWidth(type);
    internal static nint LLVMGetFirstBasicBlock(nint function) => LlvmManaged.Active ? ((delegate*<nint, nint>)LlvmManaged.LLVMGetFirstBasicBlock)(function) : LlvmNative.LLVMGetFirstBasicBlock(function);
    internal static nint LLVMGetNextBasicBlock(nint block) => LlvmManaged.Active ? ((delegate*<nint, nint>)LlvmManaged.LLVMGetNextBasicBlock)(block) : LlvmNative.LLVMGetNextBasicBlock(block);
    internal static nint LLVMGetFirstInstruction(nint block) => LlvmManaged.Active ? ((delegate*<nint, nint>)LlvmManaged.LLVMGetFirstInstruction)(block) : LlvmNative.LLVMGetFirstInstruction(block);
    internal static nint LLVMGetNextInstruction(nint instruction) => LlvmManaged.Active ? ((delegate*<nint, nint>)LlvmManaged.LLVMGetNextInstruction)(instruction) : LlvmNative.LLVMGetNextInstruction(instruction);
    internal static int LLVMGetInstructionOpcode(nint instruction) => LlvmManaged.Active ? ((delegate*<nint, int>)LlvmManaged.LLVMGetInstructionOpcode)(instruction) : LlvmNative.LLVMGetInstructionOpcode(instruction);
    internal static nint LLVMGetOperand(nint value, uint index) => LlvmManaged.Active ? ((delegate*<nint, uint, nint>)LlvmManaged.LLVMGetOperand)(value, index) : LlvmNative.LLVMGetOperand(value, index);
    internal static nint LLVMGetFirstUse(nint value) => LlvmManaged.Active ? ((delegate*<nint, nint>)LlvmManaged.LLVMGetFirstUse)(value) : LlvmNative.LLVMGetFirstUse(value);
    internal static nint LLVMGetNextUse(nint use) => LlvmManaged.Active ? ((delegate*<nint, nint>)LlvmManaged.LLVMGetNextUse)(use) : LlvmNative.LLVMGetNextUse(use);
    internal static nint LLVMGetUser(nint use) => LlvmManaged.Active ? ((delegate*<nint, nint>)LlvmManaged.LLVMGetUser)(use) : LlvmNative.LLVMGetUser(use);
    internal static nint LLVMIsAInstruction(nint value) => LlvmManaged.Active ? ((delegate*<nint, nint>)LlvmManaged.LLVMIsAInstruction)(value) : LlvmNative.LLVMIsAInstruction(value);
    internal static nint LLVMGetInstructionParent(nint instruction) => LlvmManaged.Active ? ((delegate*<nint, nint>)LlvmManaged.LLVMGetInstructionParent)(instruction) : LlvmNative.LLVMGetInstructionParent(instruction);
    internal static nint LLVMGetBasicBlockParent(nint block) => LlvmManaged.Active ? ((delegate*<nint, nint>)LlvmManaged.LLVMGetBasicBlockParent)(block) : LlvmNative.LLVMGetBasicBlockParent(block);
    internal static nint LLVMIsAConstantInt(nint value) => LlvmManaged.Active ? ((delegate*<nint, nint>)LlvmManaged.LLVMIsAConstantInt)(value) : LlvmNative.LLVMIsAConstantInt(value);
    internal static long LLVMConstIntGetSExtValue(nint value) => LlvmManaged.Active ? ((delegate*<nint, long>)LlvmManaged.LLVMConstIntGetSExtValue)(value) : LlvmNative.LLVMConstIntGetSExtValue(value);
    internal static nint LLVMPrintValueToString(nint value) => LlvmManaged.Active ? ((delegate*<nint, nint>)LlvmManaged.LLVMPrintValueToString)(value) : LlvmNative.LLVMPrintValueToString(value);
    internal static nint LLVMPrintTypeToString(nint type) => LlvmManaged.Active ? ((delegate*<nint, nint>)LlvmManaged.LLVMPrintTypeToString)(type) : LlvmNative.LLVMPrintTypeToString(type);
    internal static nint LLVMGetDataLayoutStr(nint module) => LlvmManaged.Active ? ((delegate*<nint, nint>)LlvmManaged.LLVMGetDataLayoutStr)(module) : LlvmNative.LLVMGetDataLayoutStr(module);
    internal static nint LLVMCreateTargetData(nint layout) => LlvmManaged.Active ? ((delegate*<nint, nint>)LlvmManaged.LLVMCreateTargetData)(layout) : LlvmNative.LLVMCreateTargetData(layout);
    internal static void LLVMDisposeTargetData(nint data)
    {
        if (LlvmManaged.Active) ((delegate*<nint, void>)LlvmManaged.LLVMDisposeTargetData)(data);
        else LlvmNative.LLVMDisposeTargetData(data);
    }
    internal static int LLVMByteOrder(nint data) => LlvmManaged.Active ? ((delegate*<nint, int>)LlvmManaged.LLVMByteOrder)(data) : LlvmNative.LLVMByteOrder(data);
    internal static uint LLVMPointerSize(nint data) => LlvmManaged.Active ? ((delegate*<nint, uint>)LlvmManaged.LLVMPointerSize)(data) : LlvmNative.LLVMPointerSize(data);
    internal static ulong LLVMABISizeOfType(nint data, nint type) => LlvmManaged.Active ? ((delegate*<nint, nint, ulong>)LlvmManaged.LLVMABISizeOfType)(data, type) : LlvmNative.LLVMABISizeOfType(data, type);
    internal static uint LLVMABIAlignmentOfType(nint data, nint type) => LlvmManaged.Active ? ((delegate*<nint, nint, uint>)LlvmManaged.LLVMABIAlignmentOfType)(data, type) : LlvmNative.LLVMABIAlignmentOfType(data, type);
    internal static ulong LLVMOffsetOfElement(nint data, nint type, uint index) => LlvmManaged.Active ? ((delegate*<nint, nint, uint, ulong>)LlvmManaged.LLVMOffsetOfElement)(data, type, index) : LlvmNative.LLVMOffsetOfElement(data, type, index);
    internal static nint LLVMGetElementType(nint type) => LlvmManaged.Active ? ((delegate*<nint, nint>)LlvmManaged.LLVMGetElementType)(type) : LlvmNative.LLVMGetElementType(type);
    internal static uint LLVMCountStructElementTypes(nint type) => LlvmManaged.Active ? ((delegate*<nint, uint>)LlvmManaged.LLVMCountStructElementTypes)(type) : LlvmNative.LLVMCountStructElementTypes(type);
    internal static nint LLVMStructGetTypeAtIndex(nint type, uint index) => LlvmManaged.Active ? ((delegate*<nint, uint, nint>)LlvmManaged.LLVMStructGetTypeAtIndex)(type, index) : LlvmNative.LLVMStructGetTypeAtIndex(type, index);
    internal static ulong LLVMGetArrayLength2(nint type) => LlvmManaged.Active ? ((delegate*<nint, ulong>)LlvmManaged.LLVMGetArrayLength2)(type) : LlvmNative.LLVMGetArrayLength2(type);
    internal static uint LLVMGetVectorSize(nint type) => LlvmManaged.Active ? ((delegate*<nint, uint>)LlvmManaged.LLVMGetVectorSize)(type) : LlvmNative.LLVMGetVectorSize(type);
    internal static ulong LLVMStoreSizeOfType(nint data, nint type) => LlvmManaged.Active ? ((delegate*<nint, nint, ulong>)LlvmManaged.LLVMStoreSizeOfType)(data, type) : LlvmNative.LLVMStoreSizeOfType(data, type);
    internal static uint LLVMGetNumMaskElements(nint shuffle) => LlvmManaged.Active ? ((delegate*<nint, uint>)LlvmManaged.LLVMGetNumMaskElements)(shuffle) : LlvmNative.LLVMGetNumMaskElements(shuffle);
    internal static int LLVMGetMaskValue(nint shuffle, uint element) => LlvmManaged.Active ? ((delegate*<nint, uint, int>)LlvmManaged.LLVMGetMaskValue)(shuffle, element) : LlvmNative.LLVMGetMaskValue(shuffle, element);
    internal static uint LLVMGetFastMathFlags(nint instruction) => LlvmManaged.Active ? ((delegate*<nint, uint>)LlvmManaged.LLVMGetFastMathFlags)(instruction) : LlvmNative.LLVMGetFastMathFlags(instruction);
    internal static uint LLVMCountParamTypes(nint type) => LlvmManaged.Active ? ((delegate*<nint, uint>)LlvmManaged.LLVMCountParamTypes)(type) : LlvmNative.LLVMCountParamTypes(type);
    internal static void LLVMGetParamTypes(nint type, nint* parameters)
    {
        if (LlvmManaged.Active) ((delegate*<nint, nint, void>)LlvmManaged.LLVMGetParamTypes)(type, (nint)parameters);
        else LlvmNative.LLVMGetParamTypes(type, parameters);
    }
    internal static int LLVMIsFunctionVarArg(nint type) => LlvmManaged.Active ? ((delegate*<nint, int>)LlvmManaged.LLVMIsFunctionVarArg)(type) : LlvmNative.LLVMIsFunctionVarArg(type);
    internal static nint LLVMGetFirstGlobal(nint module) => LlvmManaged.Active ? ((delegate*<nint, nint>)LlvmManaged.LLVMGetFirstGlobal)(module) : LlvmNative.LLVMGetFirstGlobal(module);
    internal static nint LLVMGetNamedGlobal(nint module, string name)
    {
        if (!LlvmManaged.Active) return LlvmNative.LLVMGetNamedGlobal(module, name);
        fixed (byte* nameUtf8 = LlvmManaged.Utf8(name))
            return ((delegate*<nint, nint, nint>)LlvmManaged.LLVMGetNamedGlobal)(module, (nint)nameUtf8);
    }
    internal static nint LLVMGetNextGlobal(nint global) => LlvmManaged.Active ? ((delegate*<nint, nint>)LlvmManaged.LLVMGetNextGlobal)(global) : LlvmNative.LLVMGetNextGlobal(global);
    internal static nint LLVMGetInitializer(nint global) => LlvmManaged.Active ? ((delegate*<nint, nint>)LlvmManaged.LLVMGetInitializer)(global) : LlvmNative.LLVMGetInitializer(global);
    internal static uint LLVMGetAlignment(nint value) => LlvmManaged.Active ? ((delegate*<nint, uint>)LlvmManaged.LLVMGetAlignment)(value) : LlvmNative.LLVMGetAlignment(value);
    internal static int LLVMIsThreadLocal(nint global) => LlvmManaged.Active ? ((delegate*<nint, int>)LlvmManaged.LLVMIsThreadLocal)(global) : LlvmNative.LLVMIsThreadLocal(global);
    internal static nint LLVMGetFirstGlobalAlias(nint module) => LlvmManaged.Active ? ((delegate*<nint, nint>)LlvmManaged.LLVMGetFirstGlobalAlias)(module) : LlvmNative.LLVMGetFirstGlobalAlias(module);
    internal static nint LLVMGetNextGlobalAlias(nint alias) => LlvmManaged.Active ? ((delegate*<nint, nint>)LlvmManaged.LLVMGetNextGlobalAlias)(alias) : LlvmNative.LLVMGetNextGlobalAlias(alias);
    internal static nint LLVMAliasGetAliasee(nint alias) => LlvmManaged.Active ? ((delegate*<nint, nint>)LlvmManaged.LLVMAliasGetAliasee)(alias) : LlvmNative.LLVMAliasGetAliasee(alias);
    internal static nint LLVMIsAGlobalAlias(nint value) => LlvmManaged.Active ? ((delegate*<nint, nint>)LlvmManaged.LLVMIsAGlobalAlias)(value) : LlvmNative.LLVMIsAGlobalAlias(value);
    internal static int LLVMGetNumOperands(nint value) => LlvmManaged.Active ? ((delegate*<nint, int>)LlvmManaged.LLVMGetNumOperands)(value) : LlvmNative.LLVMGetNumOperands(value);
    internal static int LLVMIsNull(nint value) => LlvmManaged.Active ? ((delegate*<nint, int>)LlvmManaged.LLVMIsNull)(value) : LlvmNative.LLVMIsNull(value);
    internal static int LLVMIsConstant(nint value) => LlvmManaged.Active ? ((delegate*<nint, int>)LlvmManaged.LLVMIsConstant)(value) : LlvmNative.LLVMIsConstant(value);
    internal static int LLVMIsUndef(nint value) => LlvmManaged.Active ? ((delegate*<nint, int>)LlvmManaged.LLVMIsUndef)(value) : LlvmNative.LLVMIsUndef(value);
    internal static int LLVMIsPoison(nint value) => LlvmManaged.Active ? ((delegate*<nint, int>)LlvmManaged.LLVMIsPoison)(value) : LlvmNative.LLVMIsPoison(value);
    internal static nint LLVMIsAConstantFP(nint value) => LlvmManaged.Active ? ((delegate*<nint, nint>)LlvmManaged.LLVMIsAConstantFP)(value) : LlvmNative.LLVMIsAConstantFP(value);
    internal static nint LLVMIsAConstantExpr(nint value) => LlvmManaged.Active ? ((delegate*<nint, nint>)LlvmManaged.LLVMIsAConstantExpr)(value) : LlvmNative.LLVMIsAConstantExpr(value);
    internal static nint LLVMIsAInlineAsm(nint value) => LlvmManaged.Active ? ((delegate*<nint, nint>)LlvmManaged.LLVMIsAInlineAsm)(value) : LlvmNative.LLVMIsAInlineAsm(value);
    internal static nint LLVMGetInlineAsmAsmString(nint value, out nuint length)
    {
        if (!LlvmManaged.Active) return LlvmNative.LLVMGetInlineAsmAsmString(value, out length);
        length = default;
        fixed (nuint* lengthPointer = &length)
            return ((delegate*<nint, nint, nint>)LlvmManaged.LLVMGetInlineAsmAsmString)(value, (nint)lengthPointer);
    }
    internal static nint LLVMGetInlineAsmConstraintString(nint value, out nuint length)
    {
        if (!LlvmManaged.Active) return LlvmNative.LLVMGetInlineAsmConstraintString(value, out length);
        length = default;
        fixed (nuint* lengthPointer = &length)
            return ((delegate*<nint, nint, nint>)LlvmManaged.LLVMGetInlineAsmConstraintString)(value, (nint)lengthPointer);
    }
    internal static nint LLVMIsAFunction(nint value) => LlvmManaged.Active ? ((delegate*<nint, nint>)LlvmManaged.LLVMIsAFunction)(value) : LlvmNative.LLVMIsAFunction(value);
    internal static int LLVMGetConstOpcode(nint value) => LlvmManaged.Active ? ((delegate*<nint, int>)LlvmManaged.LLVMGetConstOpcode)(value) : LlvmNative.LLVMGetConstOpcode(value);
    internal static double LLVMConstRealGetDouble(nint value, out int losesInfo)
    {
        if (!LlvmManaged.Active) return LlvmNative.LLVMConstRealGetDouble(value, out losesInfo);
        losesInfo = default;
        fixed (int* losesInfoPointer = &losesInfo)
            return ((delegate*<nint, nint, double>)LlvmManaged.LLVMConstRealGetDouble)(value, (nint)losesInfoPointer);
    }
    internal static nint LLVMGetAggregateElement(nint value, uint index) => LlvmManaged.Active ? ((delegate*<nint, uint, nint>)LlvmManaged.LLVMGetAggregateElement)(value, index) : LlvmNative.LLVMGetAggregateElement(value, index);
    internal static uint LLVMCountIncoming(nint phi) => LlvmManaged.Active ? ((delegate*<nint, uint>)LlvmManaged.LLVMCountIncoming)(phi) : LlvmNative.LLVMCountIncoming(phi);
    internal static nint LLVMGetIncomingValue(nint phi, uint index) => LlvmManaged.Active ? ((delegate*<nint, uint, nint>)LlvmManaged.LLVMGetIncomingValue)(phi, index) : LlvmNative.LLVMGetIncomingValue(phi, index);
    internal static nint LLVMGetIncomingBlock(nint phi, uint index) => LlvmManaged.Active ? ((delegate*<nint, uint, nint>)LlvmManaged.LLVMGetIncomingBlock)(phi, index) : LlvmNative.LLVMGetIncomingBlock(phi, index);
    internal static uint LLVMGetNumSuccessors(nint terminator) => LlvmManaged.Active ? ((delegate*<nint, uint>)LlvmManaged.LLVMGetNumSuccessors)(terminator) : LlvmNative.LLVMGetNumSuccessors(terminator);
    internal static nint LLVMGetSuccessor(nint terminator, uint index) => LlvmManaged.Active ? ((delegate*<nint, uint, nint>)LlvmManaged.LLVMGetSuccessor)(terminator, index) : LlvmNative.LLVMGetSuccessor(terminator, index);
    internal static nint LLVMGetSwitchCaseValue(nint instruction, uint successorIndex) => LlvmManaged.Active ? ((delegate*<nint, uint, nint>)LlvmManaged.LLVMGetSwitchCaseValue)(instruction, successorIndex) : LlvmNative.LLVMGetSwitchCaseValue(instruction, successorIndex);
    internal static nint LLVMGetCondition(nint branch) => LlvmManaged.Active ? ((delegate*<nint, nint>)LlvmManaged.LLVMGetCondition)(branch) : LlvmNative.LLVMGetCondition(branch);
    internal static nint LLVMGetAllocatedType(nint alloca) => LlvmManaged.Active ? ((delegate*<nint, nint>)LlvmManaged.LLVMGetAllocatedType)(alloca) : LlvmNative.LLVMGetAllocatedType(alloca);
    internal static int LLVMGetOrdering(nint instruction) => LlvmManaged.Active ? ((delegate*<nint, int>)LlvmManaged.LLVMGetOrdering)(instruction) : LlvmNative.LLVMGetOrdering(instruction);
    internal static int LLVMGetVolatile(nint instruction) => LlvmManaged.Active ? ((delegate*<nint, int>)LlvmManaged.LLVMGetVolatile)(instruction) : LlvmNative.LLVMGetVolatile(instruction);
    internal static int LLVMGetAtomicRMWBinOp(nint instruction) => LlvmManaged.Active ? ((delegate*<nint, int>)LlvmManaged.LLVMGetAtomicRMWBinOp)(instruction) : LlvmNative.LLVMGetAtomicRMWBinOp(instruction);
    internal static nint LLVMGetGEPSourceElementType(nint gep) => LlvmManaged.Active ? ((delegate*<nint, nint>)LlvmManaged.LLVMGetGEPSourceElementType)(gep) : LlvmNative.LLVMGetGEPSourceElementType(gep);
    internal static int LLVMGetICmpPredicate(nint instruction) => LlvmManaged.Active ? ((delegate*<nint, int>)LlvmManaged.LLVMGetICmpPredicate)(instruction) : LlvmNative.LLVMGetICmpPredicate(instruction);
    internal static int LLVMGetFCmpPredicate(nint instruction) => LlvmManaged.Active ? ((delegate*<nint, int>)LlvmManaged.LLVMGetFCmpPredicate)(instruction) : LlvmNative.LLVMGetFCmpPredicate(instruction);
    internal static uint LLVMGetNumArgOperands(nint instruction) => LlvmManaged.Active ? ((delegate*<nint, uint>)LlvmManaged.LLVMGetNumArgOperands)(instruction) : LlvmNative.LLVMGetNumArgOperands(instruction);
    internal static nint LLVMGetCalledValue(nint instruction) => LlvmManaged.Active ? ((delegate*<nint, nint>)LlvmManaged.LLVMGetCalledValue)(instruction) : LlvmNative.LLVMGetCalledValue(instruction);
    internal static nint LLVMGetCalledFunctionType(nint instruction) => LlvmManaged.Active ? ((delegate*<nint, nint>)LlvmManaged.LLVMGetCalledFunctionType)(instruction) : LlvmNative.LLVMGetCalledFunctionType(instruction);
    /// <summary>0 none, 1 tail, 2 musttail, 3 notail.</summary>
    internal static int LLVMGetTailCallKind(nint call) => LlvmManaged.Active ? ((delegate*<nint, int>)LlvmManaged.LLVMGetTailCallKind)(call) : LlvmNative.LLVMGetTailCallKind(call);
    internal static uint LLVMGetInstructionCallConv(nint instruction) => LlvmManaged.Active ? ((delegate*<nint, uint>)LlvmManaged.LLVMGetInstructionCallConv)(instruction) : LlvmNative.LLVMGetInstructionCallConv(instruction);
    internal static uint LLVMGetFunctionCallConv(nint function) => LlvmManaged.Active ? ((delegate*<nint, uint>)LlvmManaged.LLVMGetFunctionCallConv)(function) : LlvmNative.LLVMGetFunctionCallConv(function);
    internal static uint LLVMGetNumIndices(nint instruction) => LlvmManaged.Active ? ((delegate*<nint, uint>)LlvmManaged.LLVMGetNumIndices)(instruction) : LlvmNative.LLVMGetNumIndices(instruction);
    internal static uint LLVMGetNumClauses(nint instruction) => LlvmManaged.Active ? ((delegate*<nint, uint>)LlvmManaged.LLVMGetNumClauses)(instruction) : LlvmNative.LLVMGetNumClauses(instruction);
    internal static nint LLVMGetClause(nint instruction, uint index) => LlvmManaged.Active ? ((delegate*<nint, uint, nint>)LlvmManaged.LLVMGetClause)(instruction, index) : LlvmNative.LLVMGetClause(instruction, index);
    internal static uint LLVMGetEnumAttributeKindForName(string name, nuint length)
    {
        if (!LlvmManaged.Active) return LlvmNative.LLVMGetEnumAttributeKindForName(name, length);
        fixed (byte* nameUtf8 = LlvmManaged.Utf8(name))
            return ((delegate*<nint, nuint, uint>)LlvmManaged.LLVMGetEnumAttributeKindForName)((nint)nameUtf8, length);
    }
    internal static nint LLVMGetEnumAttributeAtIndex(nint function, uint index, uint kind) => LlvmManaged.Active ? ((delegate*<nint, uint, uint, nint>)LlvmManaged.LLVMGetEnumAttributeAtIndex)(function, index, kind) : LlvmNative.LLVMGetEnumAttributeAtIndex(function, index, kind);
    internal static nint LLVMGetCallSiteEnumAttribute(nint instruction, uint index, uint kind) => LlvmManaged.Active ? ((delegate*<nint, uint, uint, nint>)LlvmManaged.LLVMGetCallSiteEnumAttribute)(instruction, index, kind) : LlvmNative.LLVMGetCallSiteEnumAttribute(instruction, index, kind);
    internal static nint LLVMGetTypeAttributeValue(nint attribute) => LlvmManaged.Active ? ((delegate*<nint, nint>)LlvmManaged.LLVMGetTypeAttributeValue)(attribute) : LlvmNative.LLVMGetTypeAttributeValue(attribute);
    internal static ulong LLVMGetEnumAttributeValue(nint attribute) => LlvmManaged.Active ? ((delegate*<nint, ulong>)LlvmManaged.LLVMGetEnumAttributeValue)(attribute) : LlvmNative.LLVMGetEnumAttributeValue(attribute);

    internal static nint ParameterAttribute(nint function, uint index, string name) =>
        LLVMGetEnumAttributeAtIndex(function, index + 1, LLVMGetEnumAttributeKindForName(name, (nuint)name.Length));
    internal static uint* LLVMGetIndices(nint instruction) => LlvmManaged.Active ? (uint*)((delegate*<nint, nint>)LlvmManaged.LLVMGetIndices)(instruction) : LlvmNative.LLVMGetIndices(instruction);

    internal static string Name(nint value)
    {
        nint pointer = LLVMGetValueName2(value, out nuint length);
        return Marshal.PtrToStringUTF8(pointer, checked((int)length)) ?? "";
    }

    private static nint DemangleName(string name)
    {
        if (!LlvmManaged.Active) return LlvmNative.DemangleName(name);
        fixed (byte* nameUtf8 = LlvmManaged.Utf8(name))
            return ((delegate*<nint, nint>)LlvmManaged.llvmnet_demangle)((nint)nameUtf8);
    }
    private static nint LLVMGetDebugLocFilename(nint value, out uint length)
    {
        if (!LlvmManaged.Active) return LlvmNative.LLVMGetDebugLocFilename(value, out length);
        length = default;
        fixed (uint* lengthPointer = &length)
            return ((delegate*<nint, nint, nint>)LlvmManaged.LLVMGetDebugLocFilename)(value, (nint)lengthPointer);
    }
    private static nint LLVMGetDebugLocDirectory(nint value, out uint length)
    {
        if (!LlvmManaged.Active) return LlvmNative.LLVMGetDebugLocDirectory(value, out length);
        length = default;
        fixed (uint* lengthPointer = &length)
            return ((delegate*<nint, nint, nint>)LlvmManaged.LLVMGetDebugLocDirectory)(value, (nint)lengthPointer);
    }
    private static uint LLVMGetDebugLocLine(nint value) => LlvmManaged.Active ? ((delegate*<nint, uint>)LlvmManaged.LLVMGetDebugLocLine)(value) : LlvmNative.LLVMGetDebugLocLine(value);
    private static uint LLVMGetDebugLocColumn(nint value) => LlvmManaged.Active ? ((delegate*<nint, uint>)LlvmManaged.LLVMGetDebugLocColumn)(value) : LlvmNative.LLVMGetDebugLocColumn(value);
    internal static int LLVMGetLinkage(nint global) => LlvmManaged.Active ? ((delegate*<nint, int>)LlvmManaged.LLVMGetLinkage)(global) : LlvmNative.LLVMGetLinkage(global);
    internal static int LLVMGetVisibility(nint global) => LlvmManaged.Active ? ((delegate*<nint, int>)LlvmManaged.LLVMGetVisibility)(global) : LlvmNative.LLVMGetVisibility(global);
    internal static nint LLVMGetNamedFunction(nint module, string name)
    {
        if (!LlvmManaged.Active) return LlvmNative.LLVMGetNamedFunction(module, name);
        fixed (byte* nameUtf8 = LlvmManaged.Utf8(name))
            return ((delegate*<nint, nint, nint>)LlvmManaged.LLVMGetNamedFunction)(module, (nint)nameUtf8);
    }
    internal static nint LLVMAddFunction(nint module, string name, nint type)
    {
        if (!LlvmManaged.Active) return LlvmNative.LLVMAddFunction(module, name, type);
        fixed (byte* nameUtf8 = LlvmManaged.Utf8(name))
            return ((delegate*<nint, nint, nint, nint>)LlvmManaged.LLVMAddFunction)(module, (nint)nameUtf8, type);
    }
    internal static nint LLVMFunctionType(nint result, nint* parameters, uint count, int variadic) => LlvmManaged.Active ? ((delegate*<nint, nint, uint, int, nint>)LlvmManaged.LLVMFunctionType)(result, (nint)parameters, count, variadic) : LlvmNative.LLVMFunctionType(result, parameters, count, variadic);
    /// <summary>The type of a function without parameters.</summary>
    internal static nint FunctionType(nint result) => LLVMFunctionType(result, null, 0, 0);
    internal static nint LLVMPointerTypeInContext(nint context, uint addressSpace) => LlvmManaged.Active ? ((delegate*<nint, uint, nint>)LlvmManaged.LLVMPointerTypeInContext)(context, addressSpace) : LlvmNative.LLVMPointerTypeInContext(context, addressSpace);
    internal static nint LLVMGetModuleContext(nint module) => LlvmManaged.Active ? ((delegate*<nint, nint>)LlvmManaged.LLVMGetModuleContext)(module) : LlvmNative.LLVMGetModuleContext(module);
    internal static nint LLVMGetFirstGlobalIFunc(nint module) => LlvmManaged.Active ? ((delegate*<nint, nint>)LlvmManaged.LLVMGetFirstGlobalIFunc)(module) : LlvmNative.LLVMGetFirstGlobalIFunc(module);
    internal static nint LLVMGetNextGlobalIFunc(nint ifunc) => LlvmManaged.Active ? ((delegate*<nint, nint>)LlvmManaged.LLVMGetNextGlobalIFunc)(ifunc) : LlvmNative.LLVMGetNextGlobalIFunc(ifunc);
    internal static nint LLVMIsAGlobalIFunc(nint value) => LlvmManaged.Active ? ((delegate*<nint, nint>)LlvmManaged.LLVMIsAGlobalIFunc)(value) : LlvmNative.LLVMIsAGlobalIFunc(value);
    internal static nint LLVMIsAGlobalVariable(nint value) => LlvmManaged.Active ? ((delegate*<nint, nint>)LlvmManaged.LLVMIsAGlobalVariable)(value) : LlvmNative.LLVMIsAGlobalVariable(value);
    internal const int ExternalWeakLinkage = 12;
    internal const int InternalLinkage = 8, PrivateLinkage = 9;
    internal const int DefaultVisibility = 0;

    /// <summary>
    /// True for a definition a managed library exports: external linkage (not internal or private) and default
    /// visibility, so -fvisibility=hidden and visibility attributes select the exports as they do for a native library.
    /// </summary>
    internal static bool IsExported(nint global) => LLVMGetLinkage(global) is not (InternalLinkage or PrivateLinkage) && LLVMGetVisibility(global) == DefaultVisibility;

    /// <summary>The names of the module's definitions, functions and variables, that a library exports (<see cref="IsExported"/>).</summary>
    internal static HashSet<string> ExportedDefinitions(nint module)
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (nint function in Functions(module))
            if (LLVMIsDeclaration(function) == 0 && IsExported(function)) names.Add(Name(function));
        for (nint global = LLVMGetFirstGlobal(module); global != 0; global = LLVMGetNextGlobal(global))
            if (LLVMIsDeclaration(global) == 0 && IsExported(global)) names.Add(Name(global));
        for (nint alias = LLVMGetFirstGlobalAlias(module); alias != 0; alias = LLVMGetNextGlobalAlias(alias))
            if (IsExported(alias)) names.Add(Name(alias));
        return names;
    }

    /// <summary>Human-readable symbol name: demangled for C++ and Rust, unchanged otherwise.</summary>
    internal static string Demangle(string name)
    {
        if (!name.StartsWith("_Z", StringComparison.Ordinal) && !name.StartsWith("_R", StringComparison.Ordinal)) return name;
        nint message = DemangleName(name);
        string text = Marshal.PtrToStringUTF8(message) ?? name;
        LLVMDisposeMessage(message);
        return text;
    }

    /// <summary>"file:line:col" from an instruction's DILocation or a function's DISubprogram; null without debug info.</summary>
    internal static string? DebugLocation(nint value)
    {
        nint file = LLVMGetDebugLocFilename(value, out uint fileLength);
        if (file == 0 || fileLength == 0) return null;
        string name = Marshal.PtrToStringUTF8(file, checked((int)fileLength));
        nint directory = LLVMGetDebugLocDirectory(value, out uint directoryLength);
        if (!Path.IsPathRooted(name) && directory != 0 && directoryLength != 0)
            name = Path.Combine(Marshal.PtrToStringUTF8(directory, checked((int)directoryLength)), name);
        uint line = LLVMGetDebugLocLine(value);
        uint column = LLVMIsAFunction(value) != 0 ? 0 : LLVMGetDebugLocColumn(value);
        return line == 0 ? name : column == 0 ? $"{name}:{line}" : $"{name}:{line}:{column}";
    }

    internal static string Print(nint value)
    {
        nint message = LLVMPrintValueToString(value);
        string text = Marshal.PtrToStringUTF8(message) ?? "";
        LLVMDisposeMessage(message);
        return text;
    }

    internal static string PrintType(nint type)
    {
        nint message = LLVMPrintTypeToString(type);
        string text = Marshal.PtrToStringUTF8(message) ?? "";
        LLVMDisposeMessage(message);
        return text;
    }

    private static void LLVMRemoveStringAttributeAtIndex(nint function, uint index, string key, uint length)
    {
        if (!LlvmManaged.Active) { LlvmNative.LLVMRemoveStringAttributeAtIndex(function, index, key, length); return; }
        fixed (byte* keyUtf8 = LlvmManaged.Utf8(key))
            ((delegate*<nint, uint, nint, uint, void>)LlvmManaged.LLVMRemoveStringAttributeAtIndex)(function, index, (nint)keyUtf8, length);
    }

    /// <summary>
    /// Removes host CPU tuning (target-cpu, target-features, tune-cpu) from every function. It has no meaning for CIL,
    /// and leaving it would make legalization passes treat AVX masked memory operations or vector reductions as legal.
    /// </summary>
    internal static void RemoveTargetAttributes(nint module)
    {
        foreach (nint function in Functions(module))
            foreach (string key in new[] { "target-cpu", "target-features", "tune-cpu" })
                LLVMRemoveStringAttributeAtIndex(function, uint.MaxValue, key, (uint)key.Length);
    }

    internal static IEnumerable<nint> Functions(nint module)
    {
        for (nint function = LLVMGetFirstFunction(module); function != 0; function = LLVMGetNextFunction(function))
            yield return function;
    }

    internal static IEnumerable<nint> Blocks(nint function)
    {
        for (nint block = LLVMGetFirstBasicBlock(function); block != 0; block = LLVMGetNextBasicBlock(block))
            yield return block;
    }

    internal static IEnumerable<nint> Instructions(nint block)
    {
        for (nint instruction = LLVMGetFirstInstruction(block); instruction != 0; instruction = LLVMGetNextInstruction(instruction))
            yield return instruction;
    }
}

internal sealed class LlvmModule : IDisposable
{
    private readonly nint context = Llvm.LLVMContextCreate();
    internal nint Handle { get; }

    internal LlvmModule(string path)
    {
        if (Llvm.LLVMCreateMemoryBufferWithContentsOfFile(path, out nint buffer, out nint message) != 0)
            throw Error(message);
        if (Llvm.LLVMParseIRInContext(context, buffer, out nint module, out message) != 0)
            throw Error(message);
        Handle = module;
    }

    private static Exception Error(nint message)
    {
        string text = Marshal.PtrToStringUTF8(message) ?? "LLVM error";
        Llvm.LLVMDisposeMessage(message);
        return new InvalidOperationException(text);
    }

    public void Dispose()
    {
        Llvm.LLVMDisposeModule(Handle);
        Llvm.LLVMContextDispose(context);
    }
}
