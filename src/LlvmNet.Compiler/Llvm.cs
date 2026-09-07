using System.Runtime.InteropServices;

namespace LlvmNet;

internal static unsafe class Llvm
{
    private const string Library = "libLLVM-22.so";
    [DllImport("llvmnet-llvm", EntryPoint = "llvmnet_constant_bits")]
    private static extern uint ConstantBits(nint value, byte* destination, uint capacity);
    [DllImport("llvmnet-llvm", EntryPoint = "llvmnet_link")]
    private static extern int LinkFiles(nint* paths, uint count, [MarshalAs(UnmanagedType.LPUTF8Str)] string output, [MarshalAs(UnmanagedType.LPUTF8Str)] string abi, out nint message);
    [DllImport("llvmnet-llvm", EntryPoint = "llvmnet_stamp_abi")]
    private static extern int StampFile([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string abi, int textual, out nint message);
    [DllImport("llvmnet-llvm", EntryPoint = "llvmnet_create_native_shim")]
    internal static extern nint CreateNativeShim(nint source);
    [DllImport("llvmnet-llvm", EntryPoint = "llvmnet_add_native_thunk")]
    internal static extern void AddNativeThunk(nint shim, nint function, nint call, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Library)] internal static extern int LLVMWriteBitcodeToFile(nint module, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [DllImport(Library)] internal static extern nint LLVMGetTarget(nint module);

    internal static void StampAbi(string path, string abi, bool textual)
    {
        if (StampFile(path, abi, textual ? 1 : 0, out nint message) == 0) return;
        string text = Marshal.PtrToStringUTF8(message) ?? "Cannot stamp LLVM ABI";
        LLVMDisposeMessage(message);
        throw new InvalidOperationException(text);
    }

    internal static void Link(string[] paths, string output, string abi = "managed-host-v1")
    {
        nint[] pointers = new nint[paths.Length];
        try
        {
            for (int index = 0; index < paths.Length; index++)
                pointers[index] = Marshal.StringToCoTaskMemUTF8(paths[index]);
            fixed (nint* pointer = pointers)
            {
                if (LinkFiles(pointer, (uint)paths.Length, output, abi, out nint message) == 0)
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

    [DllImport(Library)] internal static extern nint LLVMContextCreate();
    [DllImport(Library)] internal static extern void LLVMContextDispose(nint context);
    [DllImport(Library)] internal static extern int LLVMCreateMemoryBufferWithContentsOfFile([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out nint buffer, out nint message);
    [DllImport(Library)] internal static extern int LLVMParseIRInContext(nint context, nint buffer, out nint module, out nint message);
    [DllImport(Library)] internal static extern void LLVMDisposeModule(nint module);
    [DllImport(Library)] internal static extern void LLVMDisposeMessage(nint message);
    [DllImport(Library)] internal static extern nint LLVMGetFirstFunction(nint module);
    [DllImport(Library)] internal static extern nint LLVMGetNextFunction(nint function);
    [DllImport(Library)] internal static extern int LLVMIsDeclaration(nint value);
    [DllImport(Library)] internal static extern nint LLVMGetValueName2(nint value, out nuint length);
    [DllImport(Library)] internal static extern nint LLVMGlobalGetValueType(nint global);
    [DllImport(Library)] internal static extern nint LLVMGetReturnType(nint functionType);
    [DllImport(Library)] internal static extern uint LLVMCountParams(nint function);
    [DllImport(Library)] internal static extern nint LLVMGetParam(nint function, uint index);
    [DllImport(Library)] internal static extern nint LLVMTypeOf(nint value);
    [DllImport(Library)] internal static extern int LLVMGetTypeKind(nint type);
    [DllImport(Library)] internal static extern uint LLVMGetIntTypeWidth(nint type);
    [DllImport(Library)] internal static extern nint LLVMGetFirstBasicBlock(nint function);
    [DllImport(Library)] internal static extern nint LLVMGetNextBasicBlock(nint block);
    [DllImport(Library)] internal static extern nint LLVMGetFirstInstruction(nint block);
    [DllImport(Library)] internal static extern nint LLVMGetNextInstruction(nint instruction);
    [DllImport(Library)] internal static extern int LLVMGetInstructionOpcode(nint instruction);
    [DllImport(Library)] internal static extern nint LLVMGetOperand(nint value, uint index);
    [DllImport(Library)] internal static extern nint LLVMIsAConstantInt(nint value);
    [DllImport(Library)] internal static extern long LLVMConstIntGetSExtValue(nint value);
    [DllImport(Library)] internal static extern nint LLVMPrintValueToString(nint value);
    [DllImport(Library)] internal static extern nint LLVMPrintTypeToString(nint type);
    [DllImport(Library)] internal static extern nint LLVMGetDataLayoutStr(nint module);
    [DllImport(Library)] internal static extern nint LLVMCreateTargetData(nint layout);
    [DllImport(Library)] internal static extern void LLVMDisposeTargetData(nint data);
    [DllImport(Library)] internal static extern int LLVMByteOrder(nint data);
    [DllImport(Library)] internal static extern uint LLVMPointerSize(nint data);
    [DllImport(Library)] internal static extern ulong LLVMABISizeOfType(nint data, nint type);
    [DllImport(Library)] internal static extern uint LLVMABIAlignmentOfType(nint data, nint type);
    [DllImport(Library)] internal static extern ulong LLVMOffsetOfElement(nint data, nint type, uint index);
    [DllImport(Library)] internal static extern nint LLVMGetElementType(nint type);
    [DllImport(Library)] internal static extern uint LLVMCountStructElementTypes(nint type);
    [DllImport(Library)] internal static extern nint LLVMStructGetTypeAtIndex(nint type, uint index);
    [DllImport(Library)] internal static extern ulong LLVMGetArrayLength2(nint type);
    [DllImport(Library)] internal static extern uint LLVMGetVectorSize(nint type);
    [DllImport(Library)] internal static extern uint LLVMCountParamTypes(nint type);
    [DllImport(Library)] internal static extern void LLVMGetParamTypes(nint type, nint* parameters);
    [DllImport(Library)] internal static extern int LLVMIsFunctionVarArg(nint type);
    [DllImport(Library)] internal static extern nint LLVMGetFirstGlobal(nint module);
    [DllImport(Library)] internal static extern nint LLVMGetNamedGlobal(nint module, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Library)] internal static extern nint LLVMGetNextGlobal(nint global);
    [DllImport(Library)] internal static extern nint LLVMGetInitializer(nint global);
    [DllImport(Library)] internal static extern uint LLVMGetAlignment(nint value);
    [DllImport(Library)] internal static extern int LLVMIsThreadLocal(nint global);
    [DllImport(Library)] internal static extern nint LLVMGetFirstGlobalAlias(nint module);
    [DllImport(Library)] internal static extern nint LLVMGetNextGlobalAlias(nint alias);
    [DllImport(Library)] internal static extern nint LLVMAliasGetAliasee(nint alias);
    [DllImport(Library)] internal static extern nint LLVMIsAGlobalAlias(nint value);
    [DllImport(Library)] internal static extern int LLVMGetNumOperands(nint value);
    [DllImport(Library)] internal static extern int LLVMIsNull(nint value);
    [DllImport(Library)] internal static extern int LLVMIsConstant(nint value);
    [DllImport(Library)] internal static extern int LLVMIsUndef(nint value);
    [DllImport(Library)] internal static extern int LLVMIsPoison(nint value);
    [DllImport(Library)] internal static extern nint LLVMIsAConstantFP(nint value);
    [DllImport(Library)] internal static extern nint LLVMIsAConstantExpr(nint value);
    [DllImport(Library)] internal static extern nint LLVMIsAInlineAsm(nint value);
    [DllImport(Library)] internal static extern nint LLVMIsAFunction(nint value);
    [DllImport(Library)] internal static extern int LLVMGetConstOpcode(nint value);
    [DllImport(Library)] internal static extern double LLVMConstRealGetDouble(nint value, out int losesInfo);
    [DllImport(Library)] internal static extern nint LLVMGetAggregateElement(nint value, uint index);
    [DllImport(Library)] internal static extern uint LLVMCountIncoming(nint phi);
    [DllImport(Library)] internal static extern nint LLVMGetIncomingValue(nint phi, uint index);
    [DllImport(Library)] internal static extern nint LLVMGetIncomingBlock(nint phi, uint index);
    [DllImport(Library)] internal static extern uint LLVMGetNumSuccessors(nint terminator);
    [DllImport(Library)] internal static extern nint LLVMGetSuccessor(nint terminator, uint index);
    [DllImport(Library)] internal static extern nint LLVMGetSwitchCaseValue(nint instruction, uint successorIndex);
    [DllImport(Library)] internal static extern nint LLVMGetCondition(nint branch);
    [DllImport(Library)] internal static extern nint LLVMGetAllocatedType(nint alloca);
    [DllImport(Library)] internal static extern int LLVMGetOrdering(nint instruction);
    [DllImport(Library)] internal static extern int LLVMGetVolatile(nint instruction);
    [DllImport(Library)] internal static extern int LLVMGetAtomicRMWBinOp(nint instruction);
    [DllImport(Library)] internal static extern nint LLVMGetGEPSourceElementType(nint gep);
    [DllImport(Library)] internal static extern int LLVMGetICmpPredicate(nint instruction);
    [DllImport(Library)] internal static extern int LLVMGetFCmpPredicate(nint instruction);
    [DllImport(Library)] internal static extern uint LLVMGetNumArgOperands(nint instruction);
    [DllImport(Library)] internal static extern nint LLVMGetCalledValue(nint instruction);
    [DllImport(Library)] internal static extern nint LLVMGetCalledFunctionType(nint instruction);
    [DllImport(Library)] internal static extern uint LLVMGetNumIndices(nint instruction);
    [DllImport(Library)] internal static extern uint LLVMGetNumClauses(nint instruction);
    [DllImport(Library)] internal static extern nint LLVMGetClause(nint instruction, uint index);
    [DllImport(Library)] internal static extern uint LLVMGetEnumAttributeKindForName([MarshalAs(UnmanagedType.LPUTF8Str)] string name, nuint length);
    [DllImport(Library)] internal static extern nint LLVMGetEnumAttributeAtIndex(nint function, uint index, uint kind);
    [DllImport(Library)] internal static extern nint LLVMGetCallSiteEnumAttribute(nint instruction, uint index, uint kind);
    [DllImport(Library)] internal static extern nint LLVMGetTypeAttributeValue(nint attribute);
    [DllImport(Library)] internal static extern ulong LLVMGetEnumAttributeValue(nint attribute);

    internal static nint ParameterAttribute(nint function, uint index, string name) =>
        LLVMGetEnumAttributeAtIndex(function, index + 1, LLVMGetEnumAttributeKindForName(name, (nuint)name.Length));
    [DllImport(Library)] internal static extern uint* LLVMGetIndices(nint instruction);

    internal static string Name(nint value)
    {
        nint pointer = LLVMGetValueName2(value, out nuint length);
        return Marshal.PtrToStringUTF8(pointer, checked((int)length)) ?? "";
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