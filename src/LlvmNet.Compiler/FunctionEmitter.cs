using System.Reflection;
using System.Reflection.Emit;
using LlvmNet.Runtime;

namespace LlvmNet;

internal sealed class FunctionEmitter : ValueEmitter
{
    private readonly nint function;
    private readonly MethodBuilder method;
    private readonly Dictionary<nint, LocalBuilder> locals = [];
    private readonly Dictionary<nint, int> arguments = [];
    private readonly Dictionary<nint, Label> labels = [];
    private readonly VarArgEmitter varargs;
    private LocalBuilder? currentException;

    internal FunctionEmitter(CilCompiler compiler, nint function, MethodBuilder method) : base(compiler, method.GetILGenerator())
    {
        this.function = function;
        this.method = method;
        varargs = new VarArgEmitter(compiler, Il, this);
    }

    internal void Emit()
    {
        varargs.Allocate(function);
        for (uint index = 0; index < Llvm.LLVMCountParams(function); index++)
            arguments[Llvm.LLVMGetParam(function, index)] = (int)index;
        for (uint index = 0; index < Llvm.LLVMCountParams(function); index++)
        {
            nint byValue = Llvm.ParameterAttribute(function, index, "byval");
            if (byValue == 0) continue;
            nint type = Llvm.LLVMGetTypeAttributeValue(byValue);
            nint explicitAlignment = Llvm.ParameterAttribute(function, index, "align");
            int alignment = Math.Max(Types.Alignment(type), explicitAlignment == 0 ? 1 : checked((int)Llvm.LLVMGetEnumAttributeValue(explicitAlignment)));
            LocalBuilder copy = Il.DeclareLocal(typeof(nint));
            Il.Emit(OpCodes.Ldc_I8, checked(Types.Size(type) + alignment - 1));
            Il.Emit(OpCodes.Conv_U);
            Il.Emit(OpCodes.Localloc);
            Offset(alignment - 1);
            Il.Emit(OpCodes.Ldc_I8, -(long)alignment);
            Il.Emit(OpCodes.Conv_I);
            Il.Emit(OpCodes.And);
            Il.Emit(OpCodes.Stloc, copy);
            Il.Emit(OpCodes.Ldloc, copy);
            Il.Emit(OpCodes.Ldarg, checked((short)index));
            Il.Emit(OpCodes.Ldc_I8, Types.Size(type));
            Il.Emit(OpCodes.Call, typeof(Memory).GetMethod(nameof(Memory.Copy))!);
            Il.Emit(OpCodes.Pop);
            locals[Llvm.LLVMGetParam(function, index)] = copy;
        }
        foreach (nint block in Llvm.Blocks(function))
        {
            labels[block] = Il.DefineLabel();
            foreach (nint instruction in Llvm.Instructions(block))
            {
                nint type = Llvm.LLVMTypeOf(instruction);
                if (TypeSystem.Kind(type) != 0)
                    locals[instruction] = Il.DeclareLocal(Types.Map(type));
            }
        }
        foreach (nint block in Llvm.Blocks(function))
        {
            Il.MarkLabel(labels[block]);
            foreach (nint instruction in Llvm.Instructions(block))
            {
                try
                {
                    Instruction(block, instruction);
                }
                catch (NotSupportedException error)
                {
                    throw new NotSupportedException($"{Llvm.Name(function)}: {error.Message}\n  {Llvm.Print(instruction).Trim()}", error);
                }
            }
        }
    }

    internal override void Load(nint value)
    {
        if (locals.TryGetValue(value, out LocalBuilder? local))
            Il.Emit(OpCodes.Ldloc, local);
        else if (arguments.TryGetValue(value, out int index))
            Il.Emit(OpCodes.Ldarg, (short)index);
        else
            base.Load(value);
    }

    private void Instruction(nint block, nint value)
    {
        int opcode = Llvm.LLVMGetInstructionOpcode(value);
        switch (opcode)
        {
            case 1:
                if (method.ReturnType != typeof(void))
                    Load(Llvm.LLVMGetOperand(value, 0));
                Il.Emit(OpCodes.Ret);
                return;
            case 2:
                if (Llvm.LLVMGetNumSuccessors(value) == 1)
                    Edge(block, Llvm.LLVMGetSuccessor(value, 0));
                else
                {
                    Label falseEdge = Il.DefineLabel();
                    Load(Llvm.LLVMGetCondition(value));
                    Il.Emit(OpCodes.Brfalse, falseEdge);
                    Edge(block, Llvm.LLVMGetSuccessor(value, 0));
                    Il.MarkLabel(falseEdge);
                    Edge(block, Llvm.LLVMGetSuccessor(value, 1));
                }
                return;
            case 3:
                for (uint index = 1; index < Llvm.LLVMGetNumSuccessors(value); index++)
                {
                    Label next = Il.DefineLabel();
                    nint condition = Llvm.LLVMGetOperand(value, 0);
                    Load(condition);
                    Load(Llvm.LLVMGetSwitchCaseValue(value, index));
                    if (TypeSystem.Width(Llvm.LLVMTypeOf(condition)) > 64)
                    {
                        Il.Emit(OpCodes.Ldc_I4, 32);
                        Il.Emit(OpCodes.Ldc_I4, TypeSystem.Width(Llvm.LLVMTypeOf(condition)));
                        Il.Emit(OpCodes.Call, typeof(WideInteger).GetMethod(nameof(WideInteger.Compare))!);
                        Il.Emit(OpCodes.Brfalse, next);
                    }
                    else
                        Il.Emit(OpCodes.Bne_Un, next);
                    Edge(block, Llvm.LLVMGetSuccessor(value, index));
                    Il.MarkLabel(next);
                }
                Edge(block, Llvm.LLVMGetSuccessor(value, 0));
                return;
            case 5:
                Invoke(block, value);
                return;
            case 7:
                Il.Emit(OpCodes.Call, typeof(Memory).GetMethod(nameof(Memory.Unreachable))!);
                Il.Emit(OpCodes.Ldnull);
                Il.Emit(OpCodes.Throw);
                return;
            case >= 8 and <= 25:
                Binary(value, opcode);
                break;
            case 26:
                nint allocated = Llvm.LLVMGetAllocatedType(value);
                if (TypeSystem.Width(Llvm.LLVMTypeOf(Llvm.LLVMGetOperand(value, 0))) > 64)
                    throw new NotSupportedException("Alloca element counts wider than the address space are not supported.");
                int alignment = Math.Max(Types.Alignment(allocated), checked((int)Llvm.LLVMGetAlignment(value)));
                Load(Llvm.LLVMGetOperand(value, 0));
                Il.Emit(OpCodes.Conv_U);
                Il.Emit(OpCodes.Ldc_I8, Types.Size(allocated));
                Il.Emit(OpCodes.Conv_U);
                Il.Emit(OpCodes.Mul_Ovf_Un);
                Offset(alignment - 1);
                Il.Emit(OpCodes.Localloc);
                Offset(alignment - 1);
                Il.Emit(OpCodes.Ldc_I8, -(long)alignment);
                Il.Emit(OpCodes.Conv_I);
                Il.Emit(OpCodes.And);
                break;
            case 27:
                if (Llvm.LLVMGetOrdering(value) != 0)
                    Atomic(value, opcode);
                else
                {
                    Load(Llvm.LLVMGetOperand(value, 0));
                    ReadMemory(Llvm.LLVMTypeOf(value), Llvm.LLVMGetVolatile(value) != 0);
                }
                break;
            case 28:
                if (Llvm.LLVMGetOrdering(value) != 0)
                    Atomic(value, opcode);
                else
                {
                    Load(Llvm.LLVMGetOperand(value, 1));
                    Load(Llvm.LLVMGetOperand(value, 0));
                    StoreMemory(Llvm.LLVMTypeOf(Llvm.LLVMGetOperand(value, 0)), Llvm.LLVMGetVolatile(value) != 0);
                }
                return;
            case 29:
                Gep(value);
                break;
            case >= 30 and <= 41:
            case 60:
            case 69:
                Cast(value, opcode);
                break;
            case 42:
                CompareInteger(value);
                break;
            case 43:
                CompareFloat(value);
                break;
            case 44:
                return;
            case 45:
                Call(value);
                break;
            case 46:
                Label otherwise = Il.DefineLabel();
                Label end = Il.DefineLabel();
                Load(Llvm.LLVMGetOperand(value, 0));
                Il.Emit(OpCodes.Brfalse, otherwise);
                Load(Llvm.LLVMGetOperand(value, 1));
                Il.Emit(OpCodes.Br, end);
                Il.MarkLabel(otherwise);
                Load(Llvm.LLVMGetOperand(value, 2));
                Il.MarkLabel(end);
                break;
            case 53:
            case 54:
                Aggregate(value, opcode == 54);
                break;
            case 55:
                Il.Emit(OpCodes.Call, typeof(Thread).GetMethod(nameof(Thread.MemoryBarrier))!);
                return;
            case 56:
            case 57:
                Atomic(value, opcode);
                break;
            case 58:
                nint resumed = Llvm.LLVMGetOperand(value, 0);
                LocalBuilder landing = Il.DeclareLocal(Types.Map(Llvm.LLVMTypeOf(resumed)));
                Load(resumed);
                Il.Emit(OpCodes.Stloc, landing);
                Il.Emit(OpCodes.Ldloca, landing);
                Il.Emit(OpCodes.Ldind_I);
                Il.Emit(OpCodes.Call, typeof(Cxx).GetMethod(nameof(Cxx.Resume))!);
                Il.Emit(OpCodes.Ldnull);
                Il.Emit(OpCodes.Throw);
                return;
            case 59:
                LandingPad(value);
                break;
            case 66:
                Load(Llvm.LLVMGetOperand(value, 0));
                if (TypeSystem.Kind(Llvm.LLVMTypeOf(value)) == 4)
                    Il.Emit(OpCodes.Call, typeof(Float80).GetMethod(nameof(Float80.Negate))!);
                else
                    Il.Emit(OpCodes.Neg);
                break;
            case 68:
                Load(Llvm.LLVMGetOperand(value, 0));
                break;
            default:
                throw new NotSupportedException($"Unsupported LLVM opcode {opcode}");
        }
        if (locals.TryGetValue(value, out LocalBuilder? local))
            Il.Emit(OpCodes.Stloc, local);
    }

    private void Edge(nint source, nint target)
    {
        List<LocalBuilder> copies = [];
        foreach (nint phi in Llvm.Instructions(target))
        {
            if (Llvm.LLVMGetInstructionOpcode(phi) != 44)
                break;
            bool found = false;
            for (uint index = 0; index < Llvm.LLVMCountIncoming(phi); index++)
            {
                if (Llvm.LLVMGetIncomingBlock(phi, index) != source)
                    continue;
                Load(Llvm.LLVMGetIncomingValue(phi, index));
                copies.Add(locals[phi]);
                found = true;
                break;
            }
            if (!found)
                throw new InvalidOperationException("PHI has no incoming value for predecessor.");
        }
        for (int index = copies.Count - 1; index >= 0; index--)
            Il.Emit(OpCodes.Stloc, copies[index]);
        Il.Emit(OpCodes.Br, labels[target]);
    }

    private void Atomic(nint instruction, int opcode)
    {
        nint type = opcode == 27 ? Llvm.LLVMTypeOf(instruction) : Llvm.LLVMTypeOf(Llvm.LLVMGetOperand(instruction, opcode == 28 ? 0u : 1u));
        int width = TypeSystem.Kind(type) == 12 ? 64 : TypeSystem.Width(type);
        if (width is not (8 or 16 or 32 or 64))
            throw new NotSupportedException($"Atomic type: {Llvm.PrintType(type)}");
        Load(Llvm.LLVMGetOperand(instruction, opcode == 28 ? 1u : 0u));
        if (opcode != 27)
        {
            Load(Llvm.LLVMGetOperand(instruction, opcode switch { 28 => 0u, 56 => 2u, _ => 1u }));
            Il.Emit(OpCodes.Conv_U8);
        }
        if (opcode == 56)
        {
            Load(Llvm.LLVMGetOperand(instruction, 1));
            Il.Emit(OpCodes.Conv_U8);
        }
        if (opcode == 57)
        {
            int operation = Llvm.LLVMGetAtomicRMWBinOp(instruction);
            if (operation > 10)
                throw new NotSupportedException($"Atomic RMW operation {operation}");
            Il.Emit(OpCodes.Ldc_I4, operation);
        }
        Il.Emit(OpCodes.Ldc_I4, width);
        string helper = opcode switch { 27 => nameof(Atomics.Load), 28 => nameof(Atomics.Store), 56 => nameof(Atomics.CompareExchange), _ => nameof(Atomics.Modify) };
        Il.Emit(OpCodes.Call, typeof(Atomics).GetMethod(helper)!);
        if (opcode == 28)
            return;
        if (TypeSystem.Kind(type) == 12)
            Il.Emit(OpCodes.Conv_I);
        else if (width <= 32)
            Il.Emit(OpCodes.Conv_I4);
        Normalize(type);
        if (opcode == 56)
        {
            LocalBuilder previous = Il.DeclareLocal(Types.Map(type));
            LocalBuilder result = Il.DeclareLocal(Types.Map(Llvm.LLVMTypeOf(instruction)));
            Il.Emit(OpCodes.Stloc, previous);
            Il.Emit(OpCodes.Ldloca, result);
            Il.Emit(OpCodes.Ldloc, previous);
            StoreMemory(type);
            Il.Emit(OpCodes.Ldloca, result);
            Il.Emit(OpCodes.Conv_I);
            Offset(Types.Offset(Llvm.LLVMTypeOf(instruction), 1));
            Il.Emit(OpCodes.Ldloc, previous);
            Load(Llvm.LLVMGetOperand(instruction, 1));
            Il.Emit(OpCodes.Ceq);
            Il.Emit(OpCodes.Stind_I1);
            Il.Emit(OpCodes.Ldloc, result);
        }
    }

    private void Invoke(nint block, nint instruction)
    {
        currentException ??= Il.DeclareLocal(typeof(CxxException));
        LocalBuilder failed = Il.DeclareLocal(typeof(bool));
        Il.Emit(OpCodes.Ldc_I4_0);
        Il.Emit(OpCodes.Stloc, failed);
        Il.BeginExceptionBlock();
        Call(instruction);
        if (locals.TryGetValue(instruction, out LocalBuilder? result))
            Il.Emit(OpCodes.Stloc, result);
        Il.BeginCatchBlock(typeof(CxxException));
        Il.Emit(OpCodes.Stloc, currentException);
        Il.Emit(OpCodes.Ldc_I4_1);
        Il.Emit(OpCodes.Stloc, failed);
        Il.EndExceptionBlock();
        Label unwind = Il.DefineLabel();
        Il.Emit(OpCodes.Ldloc, failed);
        Il.Emit(OpCodes.Brtrue, unwind);
        Edge(block, Llvm.LLVMGetSuccessor(instruction, 0));
        Il.MarkLabel(unwind);
        Edge(block, Llvm.LLVMGetSuccessor(instruction, 1));
    }

    private void LandingPad(nint instruction)
    {
        currentException ??= Il.DeclareLocal(typeof(CxxException));
        nint type = Llvm.LLVMTypeOf(instruction);
        LocalBuilder result = Il.DeclareLocal(Types.Map(type));
        Il.Emit(OpCodes.Ldloca, result);
        Il.Emit(OpCodes.Ldloc, currentException);
        Il.Emit(OpCodes.Callvirt, typeof(CxxException).GetProperty(nameof(CxxException.Handle))!.GetMethod!);
        Il.Emit(OpCodes.Stind_I);
        Il.Emit(OpCodes.Ldloca, result);
        Il.Emit(OpCodes.Conv_I);
        Offset(Types.Offset(type, 1));
        Il.Emit(OpCodes.Ldloc, currentException);
        uint count = Llvm.LLVMGetNumClauses(instruction);
        Il.Emit(OpCodes.Ldc_I4, checked((int)count));
        Il.Emit(OpCodes.Newarr, typeof(nint));
        for (uint index = 0; index < count; index++)
        {
            nint clause = Llvm.LLVMGetClause(instruction, index);
            if (TypeSystem.Kind(Llvm.LLVMTypeOf(clause)) != 12)
                throw new NotSupportedException("C++ exception filters are not supported; use catch clauses.");
            Il.Emit(OpCodes.Dup);
            Il.Emit(OpCodes.Ldc_I4, (int)index);
            Load(clause);
            Il.Emit(OpCodes.Stelem_I);
        }
        Il.Emit(OpCodes.Call, typeof(Cxx).GetMethod(nameof(Cxx.Selector))!);
        Il.Emit(OpCodes.Stind_I4);
        Il.Emit(OpCodes.Ldloc, result);
    }

    private void CompareInteger(nint value)
    {
        int predicate = Llvm.LLVMGetICmpPredicate(value);
        nint type = Llvm.LLVMTypeOf(Llvm.LLVMGetOperand(value, 0));
        if (TypeSystem.Width(type) > 64)
        {
            Load(Llvm.LLVMGetOperand(value, 0));
            Load(Llvm.LLVMGetOperand(value, 1));
            Il.Emit(OpCodes.Ldc_I4, predicate);
            Il.Emit(OpCodes.Ldc_I4, TypeSystem.Width(type));
            Il.Emit(OpCodes.Call, typeof(WideInteger).GetMethod(nameof(WideInteger.Compare))!);
            return;
        }
        for (uint index = 0; index < 2; index++)
        {
            nint operand = Llvm.LLVMGetOperand(value, index);
            Load(operand);
            if (predicate >= 38)
                SignExtend(Llvm.LLVMTypeOf(operand));
        }
        Il.Emit(predicate switch
        {
            32 or 33 => OpCodes.Ceq,
            34 or 37 => OpCodes.Cgt_Un,
            35 or 36 => OpCodes.Clt_Un,
            38 or 41 => OpCodes.Cgt,
            39 or 40 => OpCodes.Clt,
            _ => throw new NotSupportedException($"Integer predicate {predicate}")
        });
        if (predicate is 33 or 35 or 37 or 39 or 41)
            Not();
    }

    private void CompareFloat(nint value)
    {
        int predicate = Llvm.LLVMGetFCmpPredicate(value);
        nint left = Llvm.LLVMGetOperand(value, 0);
        nint right = Llvm.LLVMGetOperand(value, 1);
        if (TypeSystem.Kind(Llvm.LLVMTypeOf(left)) == 4)
        {
            Load(left);
            Load(right);
            Il.Emit(OpCodes.Ldc_I4, predicate);
            Il.Emit(OpCodes.Call, typeof(Float80).GetMethod(nameof(Float80.Compare))!);
            return;
        }
        if (predicate is 0 or 15)
        {
            Il.Emit(OpCodes.Ldc_I4, predicate == 15 ? 1 : 0);
            return;
        }
        if (predicate is 7 or 8)
        {
            Ordered();
            if (predicate == 8)
                Not();
            return;
        }
        Load(left);
        Load(right);
        Il.Emit(predicate switch
        {
            1 or 6 or 9 or 14 => OpCodes.Ceq,
            2 or 13 => OpCodes.Cgt,
            3 or 12 => OpCodes.Clt_Un,
            4 or 11 => OpCodes.Clt,
            5 or 10 => OpCodes.Cgt_Un,
            _ => throw new NotSupportedException($"Float predicate {predicate}")
        });
        if (predicate is 3 or 5 or 6 or 11 or 13 or 14)
            Not();
        if (predicate is 6 or 9)
        {
            Ordered();
            if (predicate == 9)
                Not();
            Il.Emit(predicate == 6 ? OpCodes.And : OpCodes.Or);
        }

        void Ordered()
        {
            Load(left);
            Load(left);
            Il.Emit(OpCodes.Ceq);
            Load(right);
            Load(right);
            Il.Emit(OpCodes.Ceq);
            Il.Emit(OpCodes.And);
        }
    }

    private void Not()
    {
        Il.Emit(OpCodes.Ldc_I4_0);
        Il.Emit(OpCodes.Ceq);
    }

    private void Call(nint instruction)
    {
        nint target = Llvm.LLVMGetCalledValue(instruction);
        string name = Llvm.Name(target);
        if (name.StartsWith("llvm.", StringComparison.Ordinal))
        {
            Intrinsic(instruction, name);
            return;
        }
        if (Compiler.Host is not null && Compiler.Host.IsSystem(target))
        {
            MethodInfo native = Compiler.Host.Resolve(target, instruction);
            for (uint index = 0; index < Llvm.LLVMGetNumArgOperands(instruction); index++)
                Load(Llvm.LLVMGetOperand(instruction, index));
            Il.Emit(OpCodes.Call, native);
            return;
        }
        nint signature = Llvm.LLVMGetCalledFunctionType(instruction);
        bool variadic = Llvm.LLVMIsFunctionVarArg(signature) != 0;
        if (variadic)
            varargs.Pack(instruction);
        uint count = variadic ? Llvm.LLVMCountParamTypes(signature) : Llvm.LLVMGetNumArgOperands(instruction);
        for (uint index = 0; index < count; index++)
            Load(Llvm.LLVMGetOperand(instruction, index));
        if (variadic)
            varargs.Load();
        if (Compiler.Methods.TryGetValue(target, out MethodInfo? callee))
            Il.Emit(OpCodes.Call, callee);
        else if (Llvm.LLVMIsAFunction(target) != 0)
            Il.Emit(OpCodes.Call, Compiler.ResolveFunction(target));
        else if (Llvm.LLVMIsAInlineAsm(target) != 0)
            throw new NotSupportedException("Inline assembly is not supported; use a pure C configuration.");
        else
        {
            Load(target);
            if (Compiler.Host is null)
                Il.EmitCalli(OpCodes.Calli, CallingConventions.Standard, Types.Map(Llvm.LLVMGetReturnType(signature)), Types.Parameters(signature), null);
            else
                Il.EmitCalli(OpCodes.Calli, System.Runtime.InteropServices.CallingConvention.Cdecl, Types.Map(Llvm.LLVMGetReturnType(signature)), Types.Parameters(signature));
        }
    }

    private void Intrinsic(nint instruction, string name)
    {
        if (name.StartsWith("llvm.ptrmask.", StringComparison.Ordinal))
        {
            Load(Llvm.LLVMGetOperand(instruction, 0));
            Load(Llvm.LLVMGetOperand(instruction, 1));
            Il.Emit(OpCodes.Conv_I);
            Il.Emit(OpCodes.And);
            return;
        }
        if (name.StartsWith("llvm.is.constant.", StringComparison.Ordinal))
        {
            nint operand = Llvm.LLVMGetOperand(instruction, 0);
            Il.Emit(OpCodes.Ldc_I4, Llvm.LLVMIsConstant(operand) != 0 && Llvm.LLVMIsUndef(operand) == 0 && Llvm.LLVMIsPoison(operand) == 0 ? 1 : 0);
            return;
        }
        if (name == "llvm.eh.typeid.for" || name.StartsWith("llvm.eh.typeid.for.", StringComparison.Ordinal))
        {
            Load(Llvm.LLVMGetOperand(instruction, 0));
            Il.Emit(OpCodes.Call, typeof(Cxx).GetMethod(nameof(Cxx.TypeId))!);
            return;
        }
        if (name.StartsWith("llvm.va_start", StringComparison.Ordinal))
        {
            if (Llvm.LLVMIsFunctionVarArg(Llvm.LLVMGlobalGetValueType(function)) == 0)
                throw new NotSupportedException("va_start in a non-variadic function");
            Load(Llvm.LLVMGetOperand(instruction, 0));
            Il.Emit(OpCodes.Ldarg, checked((short)Llvm.LLVMCountParams(function)));
            Il.Emit(OpCodes.Ldc_I8, 24L);
            Il.Emit(OpCodes.Call, typeof(Memory).GetMethod(nameof(Memory.Copy))!);
            Il.Emit(OpCodes.Pop);
            return;
        }
        if (name.StartsWith("llvm.va_copy", StringComparison.Ordinal))
        {
            Load(Llvm.LLVMGetOperand(instruction, 0));
            Load(Llvm.LLVMGetOperand(instruction, 1));
            Il.Emit(OpCodes.Ldc_I8, 24L);
            Il.Emit(OpCodes.Call, typeof(Memory).GetMethod(nameof(Memory.Copy))!);
            Il.Emit(OpCodes.Pop);
            return;
        }
        if (name.StartsWith("llvm.va_end", StringComparison.Ordinal))
            return;
        if (name.StartsWith("llvm.lifetime.", StringComparison.Ordinal) || name.StartsWith("llvm.dbg.", StringComparison.Ordinal) || name is "llvm.assume" or "llvm.experimental.noalias.scope.decl")
            return;
        if (name is "llvm.trap" or "llvm.debugtrap")
        {
            Il.Emit(OpCodes.Call, typeof(Memory).GetMethod(nameof(Memory.Unreachable))!);
            return;
        }
        if (name.StartsWith("llvm.expect.", StringComparison.Ordinal))
        {
            Load(Llvm.LLVMGetOperand(instruction, 0));
            return;
        }
        if (name.StartsWith("llvm.memcpy.", StringComparison.Ordinal) || name.StartsWith("llvm.memmove.", StringComparison.Ordinal) || name.StartsWith("llvm.memset.", StringComparison.Ordinal))
        {
            for (uint index = 0; index < 3; index++)
            {
                Load(Llvm.LLVMGetOperand(instruction, index));
                if (index == 2)
                    Il.Emit(OpCodes.Conv_U8);
            }
            Il.Emit(OpCodes.Call, typeof(Memory).GetMethod(name.StartsWith("llvm.memset.", StringComparison.Ordinal) ? nameof(Memory.Set) : nameof(Memory.Copy))!);
            Il.Emit(OpCodes.Pop);
            return;
        }
        string operation = name.Split('.')[1];
        int width = TypeSystem.Width(Llvm.LLVMTypeOf(instruction));
        if (width > 64)
            throw new NotSupportedException($"Wide integer intrinsic not implemented: {name}");
        if (operation == "fmuladd")
        {
            nint type = Llvm.LLVMTypeOf(instruction);
            Load(Llvm.LLVMGetOperand(instruction, 0));
            Load(Llvm.LLVMGetOperand(instruction, 1));
            if (TypeSystem.Kind(type) == 4)
                Il.Emit(OpCodes.Call, typeof(Float80).GetMethod(nameof(Float80.Multiply))!);
            else
            {
                Il.Emit(OpCodes.Mul);
                Il.Emit(TypeSystem.Kind(type) == 2 ? OpCodes.Conv_R4 : OpCodes.Conv_R8);
            }
            Load(Llvm.LLVMGetOperand(instruction, 2));
            if (TypeSystem.Kind(type) == 4)
                Il.Emit(OpCodes.Call, typeof(Float80).GetMethod(nameof(Float80.Add))!);
            else
            {
                Il.Emit(OpCodes.Add);
                Il.Emit(TypeSystem.Kind(type) == 2 ? OpCodes.Conv_R4 : OpCodes.Conv_R8);
            }
            return;
        }
        if (operation == "ldexp")
        {
            Load(Llvm.LLVMGetOperand(instruction, 0));
            Load(Llvm.LLVMGetOperand(instruction, 1));
            Il.Emit(OpCodes.Conv_I4);
            Il.Emit(OpCodes.Call, typeof(CMath).GetMethod(Types.Map(Llvm.LLVMTypeOf(instruction)) == typeof(float) ? nameof(CMath.LdexpF) : nameof(CMath.Ldexp))!);
            return;
        }
        if (TypeSystem.Kind(Llvm.LLVMTypeOf(instruction)) == 4)
        {
            string helper = operation switch { "fmuladd" or "fma" => nameof(Float80.FusedMultiplyAdd), "fabs" => nameof(Float80.Abs), _ => throw new NotSupportedException($"Unsupported extended floating intrinsic: {name}") };
            for (uint index = 0; index < Llvm.LLVMGetNumArgOperands(instruction); index++)
                Load(Llvm.LLVMGetOperand(instruction, index));
            Il.Emit(OpCodes.Call, typeof(Float80).GetMethod(helper)!);
            return;
        }
        if (operation == "load" && name.StartsWith("llvm.load.relative.", StringComparison.Ordinal))
        {
            Load(Llvm.LLVMGetOperand(instruction, 0));
            Il.Emit(OpCodes.Dup);
            Load(Llvm.LLVMGetOperand(instruction, 1));
            Il.Emit(OpCodes.Conv_I);
            Il.Emit(OpCodes.Add);
            Il.Emit(OpCodes.Unaligned, (byte)1);
            Il.Emit(OpCodes.Ldind_I4);
            Il.Emit(OpCodes.Conv_I);
            Il.Emit(OpCodes.Add);
            return;
        }
        if (operation is "fptosi" or "fptoui" && name.Contains(".sat.", StringComparison.Ordinal))
        {
            nint operand = Llvm.LLVMGetOperand(instruction, 0);
            if (TypeSystem.Kind(Llvm.LLVMTypeOf(operand)) is not (2 or 3) || width > 64)
                throw new NotSupportedException($"Saturated conversion type: {name}");
            Load(operand);
            Il.Emit(OpCodes.Conv_R8);
            Il.Emit(OpCodes.Ldc_I4, width);
            Il.Emit(OpCodes.Ldc_I4, operation == "fptosi" ? 1 : 0);
            Il.Emit(OpCodes.Call, typeof(Numeric).GetMethod(nameof(Numeric.FloatToIntegerSaturated))!);
            if (width <= 32) Il.Emit(OpCodes.Conv_I4);
            Normalize(Llvm.LLVMTypeOf(instruction));
            return;
        }
        if (operation is "sadd" or "uadd" or "ssub" or "usub" && name.Contains(".sat.", StringComparison.Ordinal))
        {
            nint type = Llvm.LLVMTypeOf(instruction);
            bool signed = operation.StartsWith('s');
            Il.Emit(OpCodes.Ldc_I4, operation.EndsWith("sub", StringComparison.Ordinal) ? 1 : 0);
            Il.Emit(OpCodes.Ldc_I4, signed ? 1 : 0);
            Il.Emit(OpCodes.Ldc_I4, width);
            for (uint index = 0; index < 2; index++)
            {
                Load(Llvm.LLVMGetOperand(instruction, index));
                if (signed)
                    SignExtend(type);
                Il.Emit(signed ? OpCodes.Conv_I8 : OpCodes.Conv_U8);
            }
            Il.Emit(OpCodes.Call, typeof(Numeric).GetMethod(nameof(Numeric.Saturate))!);
            if (width <= 32)
                Il.Emit(OpCodes.Conv_I4);
            Normalize(type);
            return;
        }
        if (operation is "lrint" or "llrint" or "lround" or "llround")
        {
            nint operand = Llvm.LLVMGetOperand(instruction, 0);
            bool single = Types.Map(Llvm.LLVMTypeOf(operand)) == typeof(float);
            bool away = operation.Contains("round", StringComparison.Ordinal);
            Load(operand);
            Il.Emit(OpCodes.Call, typeof(CMath).GetMethod(away ? single ? nameof(CMath.LroundF) : nameof(CMath.Lround) : single ? nameof(CMath.LrintF) : nameof(CMath.Lrint))!);
            if (width <= 32)
                Il.Emit(OpCodes.Conv_I4);
            return;
        }
        if (name.StartsWith("llvm.is.fpclass.", StringComparison.Ordinal))
        {
            nint operand = Llvm.LLVMGetOperand(instruction, 0);
            Load(operand);
            Load(Llvm.LLVMGetOperand(instruction, 1));
            Il.Emit(OpCodes.Call, typeof(Numeric).GetMethod(Types.Map(Llvm.LLVMTypeOf(operand)) == typeof(float) ? nameof(Numeric.FloatClass32) : nameof(Numeric.FloatClass64))!);
            return;
        }
        if (operation is "scmp" or "ucmp")
        {
            bool signed = operation == "scmp";
            nint operandType = Llvm.LLVMTypeOf(Llvm.LLVMGetOperand(instruction, 0));
            bool wide = TypeSystem.Width(operandType) > 64;
            for (int comparison = 0; comparison < 2; comparison++)
            {
                for (uint index = 0; index < 2; index++)
                {
                    nint operand = Llvm.LLVMGetOperand(instruction, index);
                    Load(operand);
                    if (signed && !wide)
                        SignExtend(Llvm.LLVMTypeOf(operand));
                }
                if (wide)
                {
                    Il.Emit(OpCodes.Ldc_I4, comparison == 0 ? signed ? 38 : 34 : signed ? 40 : 36);
                    Il.Emit(OpCodes.Ldc_I4, TypeSystem.Width(operandType));
                    Il.Emit(OpCodes.Call, typeof(WideInteger).GetMethod(nameof(WideInteger.Compare))!);
                }
                else
                    Il.Emit(comparison == 0 ? signed ? OpCodes.Cgt : OpCodes.Cgt_Un : signed ? OpCodes.Clt : OpCodes.Clt_Un);
            }
            Il.Emit(OpCodes.Sub);
            Normalize(Llvm.LLVMTypeOf(instruction));
            return;
        }
        if (operation == "round")
        {
            Load(Llvm.LLVMGetOperand(instruction, 0));
            Il.Emit(OpCodes.Call, typeof(CMath).GetMethod(Types.Map(Llvm.LLVMTypeOf(instruction)) == typeof(float) ? nameof(CMath.RoundF) : nameof(CMath.Round))!);
            return;
        }
        if (operation is "ctpop" or "ctlz" or "cttz")
        {
            Load(Llvm.LLVMGetOperand(instruction, 0));
            Il.Emit(OpCodes.Conv_U8);
            Il.Emit(OpCodes.Ldc_I4, width);
            Il.Emit(OpCodes.Ldc_I4, operation == "ctpop" ? 0 : operation == "ctlz" ? 1 : 2);
            Il.Emit(OpCodes.Call, typeof(Numeric).GetMethod(nameof(Numeric.CountBits))!);
            if (width <= 32)
                Il.Emit(OpCodes.Conv_I4);
            Normalize(Llvm.LLVMTypeOf(instruction));
            return;
        }
        if (operation == "bitreverse")
        {
            Load(Llvm.LLVMGetOperand(instruction, 0));
            Il.Emit(OpCodes.Conv_U8);
            Il.Emit(OpCodes.Ldc_I4, width);
            Il.Emit(OpCodes.Call, typeof(Numeric).GetMethod(nameof(Numeric.ReverseBits))!);
            if (width <= 32)
                Il.Emit(OpCodes.Conv_I4);
            Normalize(Llvm.LLVMTypeOf(instruction));
            return;
        }
        if (operation is "fshl" or "fshr")
        {
            for (uint index = 0; index < 3; index++)
            {
                Load(Llvm.LLVMGetOperand(instruction, index));
                Il.Emit(OpCodes.Conv_U8);
            }
            Il.Emit(OpCodes.Ldc_I4, width);
            Il.Emit(OpCodes.Ldc_I4, operation == "fshr" ? 1 : 0);
            Il.Emit(OpCodes.Call, typeof(Numeric).GetMethod(nameof(Numeric.Funnel))!);
            if (width <= 32)
                Il.Emit(OpCodes.Conv_I4);
            Normalize(Llvm.LLVMTypeOf(instruction));
            return;
        }
        if (operation == "bswap")
        {
            string prefix = operation switch
            {
                "bswap" => "ByteSwap",
                "ctpop" => "PopCount",
                "ctlz" => "LeadingZeros",
                "cttz" => "TrailingZeros",
                "fshl" => "FunnelLeft",
                _ => "FunnelRight"
            };
            MethodInfo? helper = typeof(Numeric).GetMethod(prefix + width);
            if (helper is null)
                throw new NotSupportedException($"Unsupported intrinsic width: {name}");
            for (uint index = 0; index < helper.GetParameters().Length; index++)
                Load(Llvm.LLVMGetOperand(instruction, index));
            Il.Emit(OpCodes.Call, helper);
            return;
        }
        if (name.Contains(".with.overflow.", StringComparison.Ordinal))
        {
            nint left = Llvm.LLVMGetOperand(instruction, 0);
            nint operandType = Llvm.LLVMTypeOf(left);
            nint returnType = Llvm.LLVMTypeOf(instruction);
            LocalBuilder result = Il.DeclareLocal(Types.Map(returnType));
            Il.Emit(OpCodes.Ldc_I4, operation[1..] switch { "add" => 0, "sub" => 1, "mul" => 2, _ => throw new NotSupportedException(name) });
            bool signed = operation[0] == 's';
            Il.Emit(OpCodes.Ldc_I4, signed ? 1 : 0);
            Il.Emit(OpCodes.Ldc_I4, TypeSystem.Width(operandType));
            bool wide = TypeSystem.Width(operandType) > 64;
            for (uint index = 0; index < 2; index++)
            {
                Load(Llvm.LLVMGetOperand(instruction, index));
                if (!wide)
                {
                    if (signed)
                        SignExtend(operandType);
                    Il.Emit(signed ? OpCodes.Conv_I8 : OpCodes.Conv_U8);
                }
            }
            Il.Emit(OpCodes.Ldloca, result);
            Il.Emit(OpCodes.Conv_I);
            Il.Emit(OpCodes.Ldc_I4, checked((int)Types.Offset(returnType, 1)));
            Il.Emit(OpCodes.Call, wide ? typeof(WideInteger).GetMethod(nameof(WideInteger.Overflow))! : typeof(Numeric).GetMethod(nameof(Numeric.Overflow))!);
            Il.Emit(OpCodes.Ldloc, result);
            return;
        }
        if (operation is "smin" or "smax" or "umin" or "umax")
        {
            nint left = Llvm.LLVMGetOperand(instruction, 0);
            nint right = Llvm.LLVMGetOperand(instruction, 1);
            nint type = Llvm.LLVMTypeOf(left);
            Load(left);
            if (operation[0] == 's')
                SignExtend(type);
            Load(right);
            if (operation[0] == 's')
                SignExtend(type);
            Il.Emit(operation switch { "smin" => OpCodes.Clt, "smax" => OpCodes.Cgt, "umin" => OpCodes.Clt_Un, _ => OpCodes.Cgt_Un });
            Label useRight = Il.DefineLabel();
            Label done = Il.DefineLabel();
            Il.Emit(OpCodes.Brfalse, useRight);
            Load(left);
            Il.Emit(OpCodes.Br, done);
            Il.MarkLabel(useRight);
            Load(right);
            Il.MarkLabel(done);
            return;
        }
        if (operation == "abs")
        {
            nint operand = Llvm.LLVMGetOperand(instruction, 0);
            nint type = Llvm.LLVMTypeOf(operand);
            Load(operand);
            SignExtend(type);
            Il.Emit(OpCodes.Dup);
            Zero(type);
            Il.Emit(OpCodes.Clt);
            Label done = Il.DefineLabel();
            Il.Emit(OpCodes.Brfalse, done);
            Il.Emit(OpCodes.Neg);
            Il.MarkLabel(done);
            Normalize(type);
            return;
        }
        string? mathName = operation switch
        {
            "fabs" => "Abs", "sqrt" => "Sqrt", "sin" => "Sin", "cos" => "Cos", "atan" => "Atan", "atan2" => "Atan2",
            "exp" => "Exp", "exp2" => "Exp2", "log" => "Log", "log2" => "Log2", "log10" => "Log10",
            "pow" => "Pow", "floor" => "Floor", "ceil" => "Ceiling", "trunc" => "Truncate",
            "roundeven" or "rint" or "nearbyint" => "Round", "fma" or "fmuladd" => "FusedMultiplyAdd",
            "copysign" => "CopySign", "minnum" => "MinNumber", "maxnum" => "MaxNumber",
            "minimum" => "Min", "maximum" => "Max", _ => null
        };
        if (mathName is not null)
        {
            Type scalar = Types.Map(Llvm.LLVMTypeOf(instruction));
            Type owner = scalar == typeof(float) ? typeof(float) : typeof(double);
            int count = checked((int)Llvm.LLVMGetNumArgOperands(instruction));
            MethodInfo? helper = owner.GetMethod(mathName, Enumerable.Repeat(scalar, count).ToArray());
            if (helper is null)
                throw new NotSupportedException($"No scalar math lowering: {name}");
            for (uint index = 0; index < count; index++)
                Load(Llvm.LLVMGetOperand(instruction, index));
            Il.Emit(OpCodes.Call, helper);
            return;
        }
        throw new NotSupportedException($"Unsupported intrinsic: {name}");
    }

    private unsafe void Aggregate(nint instruction, bool insert)
    {
        nint aggregate = Llvm.LLVMGetOperand(instruction, 0);
        nint type = Llvm.LLVMTypeOf(aggregate);
        LocalBuilder temporary = Il.DeclareLocal(Types.Map(type));
        Load(aggregate);
        Il.Emit(OpCodes.Stloc, temporary);
        Il.Emit(OpCodes.Ldloca, temporary);
        Il.Emit(OpCodes.Conv_I);
        long offset = 0;
        uint* indices = Llvm.LLVMGetIndices(instruction);
        for (uint index = 0; index < Llvm.LLVMGetNumIndices(instruction); index++)
        {
            (nint elementType, long elementOffset) = Types.Element(type, indices[index]);
            type = elementType;
            offset += elementOffset;
        }
        Offset(offset);
        if (insert)
        {
            Load(Llvm.LLVMGetOperand(instruction, 1));
            StoreMemory(type);
            Il.Emit(OpCodes.Ldloc, temporary);
        }
        else
            ReadMemory(type);
    }
}