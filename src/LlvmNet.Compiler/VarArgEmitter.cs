using System.Reflection.Emit;
using LlvmNet.Runtime;

namespace LlvmNet;

internal sealed class VarArgEmitter(CilCompiler compiler, ILGenerator il, FunctionEmitter emitter)
{
    private LocalBuilder? storage;

    internal void Allocate(nint function)
    {
        int size = 0;
        foreach (nint instruction in Llvm.Blocks(function).SelectMany(Llvm.Instructions))
        {
            if (Llvm.LLVMGetInstructionOpcode(instruction) is not (5 or 45) || Llvm.LLVMIsFunctionVarArg(Llvm.LLVMGetCalledFunctionType(instruction)) == 0)
                continue;
            int callSize = 256;
            for (uint index = 0; index < Llvm.LLVMGetNumArgOperands(instruction); index++)
                callSize = checked(callSize + (int)Math.Max(16, compiler.Types.Size(Llvm.LLVMTypeOf(Llvm.LLVMGetOperand(instruction, index)))) + 16);
            size = Math.Max(size, callSize);
        }
        if (size == 0)
            return;
        storage = il.DeclareLocal(typeof(nint));
        il.Emit(OpCodes.Ldc_I4, size);
        il.Emit(OpCodes.Conv_U);
        il.Emit(OpCodes.Localloc);
        il.Emit(OpCodes.Stloc, storage);
    }

    internal void Pack(nint instruction)
    {
        if (storage is null)
            throw new InvalidOperationException("Variadic frame was not allocated.");
        uint fixedCount = Llvm.LLVMCountParamTypes(Llvm.LLVMGetCalledFunctionType(instruction));
        int general = 0;
        int floating = 0;
        long overflow = 0;
        int firstGeneral = 0;
        int firstFloating = 48;
        long firstOverflow = 0;
        for (uint index = 0; index < Llvm.LLVMGetNumArgOperands(instruction); index++)
        {
            if (index == fixedCount)
            {
                firstGeneral = general * 8;
                firstFloating = 48 + floating * 16;
                firstOverflow = overflow;
            }
            nint operand = Llvm.LLVMGetOperand(instruction, index);
            nint type = Llvm.LLVMTypeOf(operand);
            uint byvalKind = Llvm.LLVMGetEnumAttributeKindForName("byval", 5);
            if (Llvm.LLVMGetCallSiteEnumAttribute(instruction, index + 1, byvalKind) != 0 || TypeSystem.IsAggregate(type))
                throw new NotSupportedException("Variadic aggregate ABI classification is not implemented; use scalar arguments.");
            long offset;
            if (TypeSystem.Kind(type) is 2 or 3 && floating < 8)
                offset = 32 + 48 + floating++ * 16;
            else if (TypeSystem.Kind(type) is 8 or 12 && general + (TypeSystem.Width(type) > 64 ? 2 : 1) <= 6)
            {
                offset = 32 + general * 8;
                general += TypeSystem.Width(type) > 64 ? 2 : 1;
            }
            else
            {
                int alignment = Math.Max(8, compiler.Types.Alignment(type));
                overflow = (overflow + alignment - 1) & -(long)alignment;
                offset = 208 + overflow;
                overflow += (compiler.Types.Size(type) + 7) & -8L;
            }
            il.Emit(OpCodes.Ldloc, storage);
            emitter.Offset(offset);
            emitter.Load(operand);
            if (TypeSystem.Kind(type) == 8 && TypeSystem.Width(type) <= 32)
            {
                il.Emit(OpCodes.Conv_U8);
                il.Emit(OpCodes.Stind_I8);
            }
            else
                emitter.StoreMemory(type);
        }
        if (Llvm.LLVMGetNumArgOperands(instruction) == fixedCount)
        {
            firstGeneral = general * 8;
            firstFloating = 48 + floating * 16;
            firstOverflow = overflow;
        }
        il.Emit(OpCodes.Ldloc, storage);
        il.Emit(OpCodes.Ldc_I4, firstGeneral);
        il.Emit(OpCodes.Stind_I4);
        il.Emit(OpCodes.Ldloc, storage);
        emitter.Offset(4);
        il.Emit(OpCodes.Ldc_I4, firstFloating);
        il.Emit(OpCodes.Stind_I4);
        il.Emit(OpCodes.Ldloc, storage);
        emitter.Offset(8);
        il.Emit(OpCodes.Ldloc, storage);
        emitter.Offset(208 + firstOverflow);
        il.Emit(OpCodes.Stind_I);
        il.Emit(OpCodes.Ldloc, storage);
        emitter.Offset(16);
        il.Emit(OpCodes.Ldloc, storage);
        emitter.Offset(32);
        il.Emit(OpCodes.Stind_I);
    }

    internal void Load() => il.Emit(OpCodes.Ldloc, storage ?? throw new InvalidOperationException("No variadic storage."));
}