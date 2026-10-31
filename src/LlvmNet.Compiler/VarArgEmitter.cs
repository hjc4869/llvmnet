using System.Reflection.Emit;
using LlvmNet.Runtime;

namespace LlvmNet;

/// <summary>
/// Builds the software va_list area of a variadic call: x86-64's register save area (six 8-byte general slots, then
/// eight 16-byte floating-point slots) and overflow area, preceded by the __va_list_tag the callee's va_start copies.
/// Clang lowers the callee's va_arg to the x86-64 rules, so arguments are placed as the System V ABI classifies them:
/// integers and pointers in general slots, float and double in floating-point slots, long double and aggregates passed
/// by value in memory in the overflow area, and a small aggregate, which Clang passes as its eightbytes
/// (`{ i64, double }`), in one slot per eightbyte when all of them fit, otherwise in the overflow area.
/// </summary>
internal sealed class VarArgEmitter(CilCompiler compiler, ILGenerator il, FunctionEmitter emitter)
{
    private LocalBuilder? storage;
    private static readonly uint byvalKind = Llvm.LLVMGetEnumAttributeKindForName("byval", 5);

    /// <summary>The aggregate type a byval argument points to, or 0 for any other argument.</summary>
    private static nint ByValueType(nint instruction, uint index)
    {
        nint attribute = Llvm.LLVMGetCallSiteEnumAttribute(instruction, index + 1, byvalKind);
        return attribute == 0 ? 0 : Llvm.LLVMGetTypeAttributeValue(attribute);
    }

    internal void Allocate(nint function)
    {
        int size = 0;
        foreach (nint instruction in Llvm.Blocks(function).SelectMany(Llvm.Instructions))
        {
            if (Llvm.LLVMGetInstructionOpcode(instruction) is not (5 or 45) || Llvm.LLVMIsFunctionVarArg(Llvm.LLVMGetCalledFunctionType(instruction)) == 0)
                continue;
            int callSize = 256;
            for (uint index = 0; index < Llvm.LLVMGetNumArgOperands(instruction); index++)
            {
                nint type = ByValueType(instruction, index);
                if (type == 0) type = Llvm.LLVMTypeOf(Llvm.LLVMGetOperand(instruction, index));
                callSize = checked(callSize + (int)Math.Max(16, compiler.Types.Size(type)) + 16);
            }
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
            nint byValue = ByValueType(instruction, index);
            if (byValue != 0)
            {
                // A byval argument points at an aggregate the ABI passes in memory, so it takes no register. A fixed one
                // reaches the callee as its pointer parameter; a variadic one is copied into the overflow area, where
                // the callee's va_arg reads it.
                if (index < fixedCount) continue;
                int alignment = Math.Max(8, compiler.Types.Alignment(byValue));
                overflow = (overflow + alignment - 1) & -(long)alignment;
                il.Emit(OpCodes.Ldloc, storage);
                emitter.Offset(208 + overflow);
                emitter.Load(operand);
                il.Emit(OpCodes.Ldc_I8, compiler.Types.Size(byValue));
                il.Emit(OpCodes.Call, typeof(Memory).GetMethod(nameof(Memory.Copy))!);
                il.Emit(OpCodes.Pop);
                overflow += (compiler.Types.Size(byValue) + 7) & -8L;
                continue;
            }
            if (TypeSystem.IsAggregate(type))
            {
                // Clang passes a small aggregate as the eightbytes x86-64 loads into registers, one element each.
                List<(long Offset, long Size, bool Floating)>? eightbytes = Eightbytes(type);
                int generalNeeded = eightbytes?.Count(e => !e.Floating) ?? 0, floatingNeeded = eightbytes?.Count(e => e.Floating) ?? 0;
                if (eightbytes is not null && general + generalNeeded <= 6 && floating + floatingNeeded <= 8)
                {
                    if (index < fixedCount) { general += generalNeeded; floating += floatingNeeded; continue; }
                    LocalBuilder copy = il.DeclareLocal(compiler.Types.Map(type));
                    emitter.Load(operand);
                    il.Emit(OpCodes.Stloc, copy);
                    foreach ((long elementOffset, long elementSize, bool isFloating) in eightbytes)
                    {
                        il.Emit(OpCodes.Ldloc, storage);
                        emitter.Offset(isFloating ? 32 + 48 + floating++ * 16 : 32 + general++ * 8);
                        il.Emit(OpCodes.Ldloca, copy);
                        il.Emit(OpCodes.Conv_U);
                        emitter.Offset(elementOffset);
                        il.Emit(OpCodes.Ldc_I8, elementSize);
                        il.Emit(OpCodes.Call, typeof(Memory).GetMethod(nameof(Memory.Copy))!);
                        il.Emit(OpCodes.Pop);
                    }
                    continue;
                }
                if (index < fixedCount) continue;
                int alignment = Math.Max(8, compiler.Types.Alignment(type));
                overflow = (overflow + alignment - 1) & -(long)alignment;
                il.Emit(OpCodes.Ldloc, storage);
                emitter.Offset(208 + overflow);
                emitter.Load(operand);
                emitter.StoreMemory(type);
                overflow += (compiler.Types.Size(type) + 7) & -8L;
                continue;
            }
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

    /// <summary>
    /// The eightbytes of an aggregate Clang coerced for register passing: each element is an integer, pointer, float,
    /// double or small vector that fills at most one eightbyte. Null when the aggregate is of another shape, which
    /// x86-64 passes in memory.
    /// </summary>
    private List<(long Offset, long Size, bool Floating)>? Eightbytes(nint type)
    {
        if (compiler.Types.Size(type) > 16) return null;
        List<(long, long, bool)> result = [];
        int kind = TypeSystem.Kind(type);
        if (kind == 13)
        {
            nint element = Llvm.LLVMGetElementType(type);
            if (TypeSystem.Kind(element) is not (2 or 3 or 8)) return null;
            // A vector up to 8 bytes is one eightbyte; a 16-byte vector is two.
            for (long offset = 0; offset < compiler.Types.Size(type); offset += 8)
                result.Add((offset, Math.Min(8, compiler.Types.Size(type) - offset), TypeSystem.Kind(element) is 2 or 3));
            return result;
        }
        uint count = kind == 10 ? Llvm.LLVMCountStructElementTypes(type) : checked((uint)Llvm.LLVMGetArrayLength2(type));
        for (uint index = 0; index < count; index++)
        {
            nint element = kind == 10 ? Llvm.LLVMStructGetTypeAtIndex(type, index) : Llvm.LLVMGetElementType(type);
            long offset = kind == 10 ? compiler.Types.Offset(type, index) : index * compiler.Types.Size(element);
            long size = compiler.Types.Size(element);
            int elementKind = TypeSystem.Kind(element);
            bool floating = elementKind is 2 or 3 || elementKind == 13 && TypeSystem.Kind(Llvm.LLVMGetElementType(element)) is 2 or 3;
            if (size > 8 || offset / 8 != (offset + size - 1) / 8 || elementKind is not (2 or 3 or 8 or 12 or 13)) return null;
            // Two elements in one eightbyte (`{ float, float }`) share its slot; integers make it general.
            if (result.Count > 0 && result[^1].Item1 / 8 == offset / 8)
                result[^1] = (result[^1].Item1, offset + size - result[^1].Item1, result[^1].Item3 && floating);
            else
                result.Add((offset, size, floating));
        }
        return result;
    }

    internal void Load() => il.Emit(OpCodes.Ldloc, storage ?? throw new InvalidOperationException("No variadic storage."));
}
