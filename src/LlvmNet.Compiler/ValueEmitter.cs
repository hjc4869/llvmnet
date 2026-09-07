using System.Reflection.Emit;
using System.Buffers.Binary;
using LlvmNet.Runtime;

namespace LlvmNet;

internal class ValueEmitter(CilCompiler compiler, ILGenerator il)
{
    protected readonly CilCompiler Compiler = compiler;
    protected readonly ILGenerator Il = il;
    protected TypeSystem Types => Compiler.Types;

    internal virtual void Load(nint value)
    {
        nint type = Llvm.LLVMTypeOf(value);
        if (Llvm.LLVMIsAGlobalAlias(value) != 0)
            Load(Llvm.LLVMAliasGetAliasee(value));
        else if (Compiler.Globals.TryGetValue(value, out FieldBuilder? global))
            Il.Emit(OpCodes.Ldsfld, global);
        else if (Compiler.Methods.TryGetValue(value, out System.Reflection.MethodInfo? method))
            FunctionPointer(value, method);
        else if (Llvm.LLVMIsAFunction(value) != 0)
        {
            System.Reflection.MethodInfo target = Compiler.ResolveFunction(value);
            FunctionPointer(value, target);
        }
        else if (Llvm.LLVMIsAConstantInt(value) != 0)
        {
            if (TypeSystem.Width(type) > 64)
            {
                if (TypeSystem.Width(type) > 128)
                    throw new NotSupportedException($"Unsupported integer: {Llvm.Print(value)}");
                Span<byte> bits = stackalloc byte[16];
                bits.Clear();
                Llvm.Bits(value).CopyTo(bits);
                Il.Emit(OpCodes.Ldc_I8, BinaryPrimitives.ReadInt64LittleEndian(bits));
                Il.Emit(OpCodes.Ldc_I8, BinaryPrimitives.ReadInt64LittleEndian(bits[8..]));
                Il.Emit(OpCodes.Call, typeof(WideInteger).GetMethod(nameof(WideInteger.FromBits))!);
                return;
            }
            long number = Llvm.LLVMConstIntGetSExtValue(value);
            if (Types.Map(type) == typeof(long))
                Il.Emit(OpCodes.Ldc_I8, number);
            else
                Il.Emit(OpCodes.Ldc_I4, unchecked((int)number));
            Normalize(type);
        }
        else if (Llvm.LLVMIsAConstantFP(value) != 0)
        {
            byte[] bits = Llvm.Bits(value);
            if (TypeSystem.Kind(type) == 2)
                Il.Emit(OpCodes.Ldc_R4, BinaryPrimitives.ReadSingleLittleEndian(bits));
            else if (TypeSystem.Kind(type) == 3)
                Il.Emit(OpCodes.Ldc_R8, BinaryPrimitives.ReadDoubleLittleEndian(bits));
            else if (TypeSystem.Kind(type) == 4)
            {
                Il.Emit(OpCodes.Ldc_I8, BinaryPrimitives.ReadInt64LittleEndian(bits));
                Il.Emit(OpCodes.Ldc_I4, (int)BinaryPrimitives.ReadUInt16LittleEndian(bits.AsSpan(8)));
                Il.Emit(OpCodes.Newobj, typeof(Float80).GetConstructor([typeof(long), typeof(int)])!);
            }
            else
                throw new NotSupportedException($"Unsupported floating constant: {Llvm.Print(value)}");
        }
        else if (Llvm.LLVMIsNull(value) != 0 || Llvm.LLVMIsUndef(value) != 0 || Llvm.LLVMIsPoison(value) != 0)
            Zero(type);
        else if (Llvm.LLVMIsAConstantExpr(value) != 0)
            Expression(value, Llvm.LLVMGetConstOpcode(value));
        else if (TypeSystem.IsAggregate(type))
        {
            Il.Emit(OpCodes.Ldsfld, Compiler.Constant(value));
            ReadMemory(type);
        }
        else
            throw new NotSupportedException($"Unresolved value: {Llvm.Print(value)}");
    }

    private void FunctionPointer(nint function, System.Reflection.MethodInfo target)
    {
        Il.Emit(OpCodes.Ldftn, Compiler.Host is null ? target : Compiler.Host.Callback(function, target));
        if (Compiler.Host is not null && Llvm.LLVMIsFunctionVarArg(Llvm.LLVMGlobalGetValueType(function)) != 0)
        {
            Il.Emit(OpCodes.Ldftn, target);
            Il.Emit(OpCodes.Call, typeof(SystemAbi).GetMethod(nameof(SystemAbi.RegisterVariadicCallback))!);
        }
    }

    internal void Zero(nint type)
    {
        Type managedType = Types.Map(type);
        if (managedType == typeof(float))
            Il.Emit(OpCodes.Ldc_R4, 0f);
        else if (managedType == typeof(double))
            Il.Emit(OpCodes.Ldc_R8, 0d);
        else if (TypeSystem.IsAggregate(type) || managedType == typeof(Float80) || managedType == typeof(UInt128))
        {
            LocalBuilder local = Il.DeclareLocal(managedType);
            Il.Emit(OpCodes.Ldloca, local);
            Il.Emit(OpCodes.Initobj, managedType);
            Il.Emit(OpCodes.Ldloc, local);
        }
        else
        {
            Il.Emit(OpCodes.Ldc_I4_0);
            if (managedType == typeof(long))
                Il.Emit(OpCodes.Conv_I8);
            else if (managedType == typeof(nint))
                Il.Emit(OpCodes.Conv_I);
        }
    }

    internal void Offset(long offset)
    {
        if (offset == 0)
            return;
        Il.Emit(OpCodes.Ldc_I8, offset);
        Il.Emit(OpCodes.Conv_I);
        Il.Emit(OpCodes.Add);
    }

    internal void Normalize(nint type)
    {
        int width = TypeSystem.Width(type);
        if (width is > 64 and < 128)
        {
            Il.Emit(OpCodes.Ldc_I4, width);
            Il.Emit(OpCodes.Call, typeof(WideInteger).GetMethod(nameof(WideInteger.Mask))!);
            return;
        }
        if (width is > 0 and < 32)
        {
            Il.Emit(OpCodes.Ldc_I4, (1 << width) - 1);
            Il.Emit(OpCodes.And);
        }
        else if (width is > 32 and < 64)
        {
            Il.Emit(OpCodes.Ldc_I8, (1L << width) - 1);
            Il.Emit(OpCodes.And);
        }
    }

    protected void SignExtend(nint type)
    {
        int width = TypeSystem.Width(type);
        if (width > 64)
        {
            Il.Emit(OpCodes.Ldc_I4, width);
            Il.Emit(OpCodes.Call, typeof(WideInteger).GetMethod(nameof(WideInteger.ExtendSign))!);
            return;
        }
        int storage = width <= 32 ? 32 : 64;
        if (width == 0 || width == storage)
            return;
        Il.Emit(OpCodes.Ldc_I4, storage - width);
        Il.Emit(OpCodes.Shl);
        Il.Emit(OpCodes.Ldc_I4, storage - width);
        Il.Emit(OpCodes.Shr);
    }

    internal void ReadMemory(nint type, bool isVolatile = false)
    {
        int width = TypeSystem.Width(type);
        if (width > 64 || width > 0 && (width + 7) / 8 is not (1 or 2 or 4 or 8))
        {
            Il.Emit(OpCodes.Ldc_I4, width);
            Il.Emit(OpCodes.Call, typeof(WideInteger).GetMethod(width > 64 ? nameof(WideInteger.Read) : nameof(WideInteger.ReadNarrow))!);
            if (width <= 32) Il.Emit(OpCodes.Conv_I4);
            if (isVolatile) Il.Emit(OpCodes.Call, typeof(Thread).GetMethod(nameof(Thread.MemoryBarrier))!);
            Normalize(type);
            return;
        }
        if (isVolatile) Il.Emit(OpCodes.Volatile);
        Il.Emit(OpCodes.Unaligned, (byte)1);
        OpCode opcode = TypeSystem.Kind(type) switch
        {
            2 => OpCodes.Ldind_R4,
            3 => OpCodes.Ldind_R8,
            12 => OpCodes.Ldind_I,
            8 when TypeSystem.Width(type) <= 8 => OpCodes.Ldind_U1,
            8 when TypeSystem.Width(type) <= 16 => OpCodes.Ldind_U2,
            8 when TypeSystem.Width(type) <= 32 => OpCodes.Ldind_I4,
            8 when TypeSystem.Width(type) <= 64 => OpCodes.Ldind_I8,
            _ => OpCodes.Ldobj
        };
        if (opcode == OpCodes.Ldobj)
            Il.Emit(opcode, Types.Map(type));
        else
            Il.Emit(opcode);
        Normalize(type);
    }

    internal void StoreMemory(nint type, bool isVolatile = false)
    {
        int width = TypeSystem.Width(type);
        if (width > 64 || width > 0 && (width + 7) / 8 is not (1 or 2 or 4 or 8))
        {
            if (isVolatile) Il.Emit(OpCodes.Call, typeof(Thread).GetMethod(nameof(Thread.MemoryBarrier))!);
            if (width <= 64) Il.Emit(OpCodes.Conv_U8);
            Il.Emit(OpCodes.Ldc_I4, width);
            Il.Emit(OpCodes.Call, typeof(WideInteger).GetMethod(width > 64 ? nameof(WideInteger.Write) : nameof(WideInteger.WriteNarrow))!);
            return;
        }
        if (isVolatile) Il.Emit(OpCodes.Volatile);
        Il.Emit(OpCodes.Unaligned, (byte)1);
        OpCode opcode = TypeSystem.Kind(type) switch
        {
            2 => OpCodes.Stind_R4,
            3 => OpCodes.Stind_R8,
            12 => OpCodes.Stind_I,
            8 when TypeSystem.Width(type) <= 8 => OpCodes.Stind_I1,
            8 when TypeSystem.Width(type) <= 16 => OpCodes.Stind_I2,
            8 when TypeSystem.Width(type) <= 32 => OpCodes.Stind_I4,
            8 when TypeSystem.Width(type) <= 64 => OpCodes.Stind_I8,
            _ => OpCodes.Stobj
        };
        if (opcode == OpCodes.Stobj)
            Il.Emit(opcode, Types.Map(type));
        else
            Il.Emit(opcode);
    }

    protected void Expression(nint value, int opcode)
    {
        if (opcode == 29)
        {
            Gep(value);
            return;
        }
        if (opcode is >= 30 and <= 41 or 60 or 69)
        {
            Cast(value, opcode);
            return;
        }
        if (opcode is >= 8 and <= 25)
        {
            Binary(value, opcode);
            return;
        }
        throw new NotSupportedException($"Unsupported expression: {Llvm.Print(value)}");
    }

    protected void Binary(nint value, int opcode)
    {
        nint left = Llvm.LLVMGetOperand(value, 0);
        nint right = Llvm.LLVMGetOperand(value, 1);
        nint type = Llvm.LLVMTypeOf(left);
        if (TypeSystem.Width(type) > 64)
        {
            Load(left);
            Load(right);
            Il.Emit(OpCodes.Ldc_I4, opcode);
            Il.Emit(OpCodes.Ldc_I4, TypeSystem.Width(type));
            Il.Emit(OpCodes.Call, typeof(WideInteger).GetMethod(nameof(WideInteger.Binary))!);
            return;
        }
        if (TypeSystem.Kind(type) == 4)
        {
            Load(left);
            Load(right);
            string helper = opcode switch { 9 => nameof(Float80.Add), 11 => nameof(Float80.Subtract), 13 => nameof(Float80.Multiply), 16 => nameof(Float80.Divide), 19 => nameof(Float80.Remainder), _ => throw new NotSupportedException($"Extended floating operation {opcode}") };
            Il.Emit(OpCodes.Call, typeof(Float80).GetMethod(helper)!);
            return;
        }
        if (TypeSystem.IsAggregate(type))
            throw new NotSupportedException($"Vector arithmetic: {Llvm.Print(value)}");
        Load(left);
        if (opcode is 15 or 18 or 22)
            SignExtend(type);
        Load(right);
        if (opcode is 15 or 18)
            SignExtend(type);
        if (opcode is 20 or 21 or 22)
            Il.Emit(OpCodes.Conv_I4);
        Il.Emit(opcode switch
        {
            8 or 9 => OpCodes.Add,
            10 or 11 => OpCodes.Sub,
            12 or 13 => OpCodes.Mul,
            14 => OpCodes.Div_Un,
            15 or 16 => OpCodes.Div,
            17 => OpCodes.Rem_Un,
            18 or 19 => OpCodes.Rem,
            20 => OpCodes.Shl,
            21 => OpCodes.Shr_Un,
            22 => OpCodes.Shr,
            23 => OpCodes.And,
            24 => OpCodes.Or,
            25 => OpCodes.Xor,
            _ => throw new NotSupportedException($"Binary opcode {opcode}")
        });
        Normalize(Llvm.LLVMTypeOf(value));
    }

    protected void Cast(nint value, int opcode)
    {
        nint source = Llvm.LLVMGetOperand(value, 0);
        nint sourceType = Llvm.LLVMTypeOf(source);
        nint destinationType = Llvm.LLVMTypeOf(value);
        Type destination = Types.Map(destinationType);
        Load(source);
        if (opcode == 41)
        {
            if (Types.Map(sourceType) == destination)
                return;
            if (Types.Size(sourceType) != Types.Size(destinationType))
                throw new NotSupportedException($"Invalid bitcast: {Llvm.Print(value)}");
            LocalBuilder temporary = Il.DeclareLocal(Types.Map(sourceType));
            Il.Emit(OpCodes.Stloc, temporary);
            Il.Emit(OpCodes.Ldloca, temporary);
            ReadMemory(destinationType);
            return;
        }
        if (Types.Map(sourceType) == typeof(UInt128) || destination == typeof(UInt128))
        {
            WideCast(sourceType, destinationType, opcode);
            return;
        }
        if (TypeSystem.Kind(sourceType) == 4 || TypeSystem.Kind(destinationType) == 4)
        {
            if (TypeSystem.Kind(sourceType) == 4)
            {
                string helper = destination == typeof(float) ? nameof(Float80.ToSingle) : destination == typeof(double) ? nameof(Float80.ToDouble) : opcode == 33 ? nameof(Float80.ToUnsigned) : nameof(Float80.ToSigned);
                Il.Emit(OpCodes.Call, typeof(Float80).GetMethod(helper)!);
                if (destination == typeof(int))
                    Il.Emit(OpCodes.Conv_I4);
                Normalize(destinationType);
            }
            else
            {
                Type sourceManaged = Types.Map(sourceType);
                string helper;
                if (sourceManaged == typeof(float))
                    helper = nameof(Float80.FromSingle);
                else if (sourceManaged == typeof(double))
                    helper = nameof(Float80.FromDouble);
                else
                {
                    if (opcode == 36)
                        SignExtend(sourceType);
                    Il.Emit(opcode == 35 ? OpCodes.Conv_U8 : OpCodes.Conv_I8);
                    helper = opcode == 35 ? nameof(Float80.FromUnsigned) : nameof(Float80.FromSigned);
                }
                Il.Emit(OpCodes.Call, typeof(Float80).GetMethod(helper)!);
            }
            return;
        }
        if (opcode is 32 or 36)
            SignExtend(sourceType);
        if (opcode == 35)
            Il.Emit(OpCodes.Conv_R_Un);
        if (destination == typeof(float))
            Il.Emit(OpCodes.Conv_R4);
        else if (destination == typeof(double))
            Il.Emit(OpCodes.Conv_R8);
        else if (destination == typeof(nint))
        {
            if (Types.Map(sourceType) == typeof(int))
                Il.Emit(OpCodes.Conv_U8);
            Il.Emit(OpCodes.Conv_U);
        }
        else if (destination == typeof(long))
            Il.Emit(opcode is 31 or 33 ? OpCodes.Conv_U8 : OpCodes.Conv_I8);
        else if (destination == typeof(int))
            Il.Emit(opcode == 33 ? OpCodes.Conv_U4 : OpCodes.Conv_I4);
        else
            throw new NotSupportedException($"Unsupported cast: {Llvm.Print(value)}");
        Normalize(destinationType);
    }

    private void WideCast(nint source, nint destination, int opcode)
    {
        Type sourceManaged = Types.Map(source);
        Type destinationManaged = Types.Map(destination);
        if (sourceManaged == typeof(UInt128))
        {
            if (destinationManaged == typeof(UInt128))
            {
                if (opcode == 32) SignExtend(source);
                Normalize(destination);
                return;
            }
            if (destinationManaged == typeof(float) || destinationManaged == typeof(double))
            {
                if (opcode == 36) SignExtend(source);
                Il.Emit(OpCodes.Ldc_I4, opcode == 36 ? 1 : 0);
                Il.Emit(OpCodes.Call, typeof(WideInteger).GetMethod(destinationManaged == typeof(float) ? nameof(WideInteger.ToSingle) : nameof(WideInteger.ToDouble))!);
            }
            else if (destinationManaged == typeof(Float80))
            {
                if (opcode == 36) SignExtend(source);
                Il.Emit(OpCodes.Call, typeof(Float80).GetMethod(opcode == 36 ? nameof(Float80.FromWideSigned) : nameof(Float80.FromWideUnsigned))!);
            }
            else
            {
                Il.Emit(OpCodes.Call, typeof(WideInteger).GetMethod(nameof(WideInteger.Low))!);
                if (destinationManaged == typeof(int)) Il.Emit(OpCodes.Conv_I4);
                else if (destinationManaged == typeof(nint)) Il.Emit(OpCodes.Conv_I);
                Normalize(destination);
            }
        }
        else
        {
            if (sourceManaged == typeof(float) || sourceManaged == typeof(double))
            {
                Il.Emit(OpCodes.Conv_R8);
                Il.Emit(OpCodes.Ldc_I4, opcode == 34 ? 1 : 0);
                Il.Emit(OpCodes.Call, typeof(WideInteger).GetMethod(nameof(WideInteger.FromFloating))!);
            }
            else if (sourceManaged == typeof(Float80))
                Il.Emit(OpCodes.Call, typeof(Float80).GetMethod(opcode == 34 ? nameof(Float80.ToWideSigned) : nameof(Float80.ToWideUnsigned))!);
            else
            {
                if (opcode == 32) SignExtend(source);
                Il.Emit(opcode == 32 ? OpCodes.Conv_I8 : OpCodes.Conv_U8);
                Il.Emit(OpCodes.Call, typeof(WideInteger).GetMethod(opcode == 32 ? nameof(WideInteger.FromSigned) : nameof(WideInteger.FromUnsigned))!);
            }
            Normalize(destination);
        }
    }

    protected void Gep(nint value)
    {
        Load(Llvm.LLVMGetOperand(value, 0));
        nint type = Llvm.LLVMGetGEPSourceElementType(value);
        for (uint index = 1; index < Llvm.LLVMGetNumOperands(value); index++)
        {
            nint operand = Llvm.LLVMGetOperand(value, index);
            if (index > 1 && TypeSystem.Kind(type) == 10)
            {
                uint fieldIndex = checked((uint)Llvm.LLVMConstIntGetSExtValue(operand));
                (nint fieldType, long fieldOffset) = Types.Element(type, fieldIndex);
                Offset(fieldOffset);
                type = fieldType;
            }
            else
            {
                if (index > 1)
                    type = Llvm.LLVMGetElementType(type);
                Load(operand);
                if (TypeSystem.Width(Llvm.LLVMTypeOf(operand)) > 64)
                    Il.Emit(OpCodes.Call, typeof(WideInteger).GetMethod(nameof(WideInteger.Low))!);
                else
                    SignExtend(Llvm.LLVMTypeOf(operand));
                Il.Emit(OpCodes.Conv_I8);
                Il.Emit(OpCodes.Ldc_I8, Types.Size(type));
                Il.Emit(OpCodes.Mul);
                Il.Emit(OpCodes.Conv_I);
                Il.Emit(OpCodes.Add);
            }
        }
    }
}