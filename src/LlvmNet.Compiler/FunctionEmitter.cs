using System.Reflection;
using System.Reflection.Emit;
using LlvmNet.Runtime;

namespace LlvmNet;

internal sealed class FunctionEmitter : ValueEmitter
{
    private readonly nint function;
    private readonly MethodBuilder method;
    private readonly Dictionary<nint, LocalBuilder> locals = [];
    private readonly Dictionary<nint, (LocalBuilder Array, LocalBuilder Temporary, int Index)> storedScalars = [];
    private readonly Dictionary<nint, int> arguments = [];
    private readonly Dictionary<nint, Label> labels = [];
    private readonly VarArgEmitter varargs;
    private LocalBuilder? currentException;
    private readonly Dictionary<nint, (int Site, Label Resume)> jumpSites = [];
    private LocalBuilder? jumpFrame;
    private LocalBuilder? jumpResume;
    private LocalBuilder? jumpReturn;
    private Label jumpDispatch;
    private Label jumpExit;
    private LocalBuilder? stackMemory;
    private LocalBuilder? stackReturn;
    private Label stackExit;

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
        bool scopedStack = false;
        bool hasAllocations = false;
        int valueCount = 0;
        foreach (nint block in Llvm.Blocks(function))
        {
            labels[block] = Il.DefineLabel();
            foreach (nint instruction in Llvm.Instructions(block))
            {
                nint type = Llvm.LLVMTypeOf(instruction);
                if (TypeSystem.Kind(type) != 0)
                    valueCount++;
                hasAllocations |= Llvm.LLVMGetInstructionOpcode(instruction) == 26;
                if (Llvm.LLVMGetInstructionOpcode(instruction) == 45 && CilCompiler.IsSetJump(Llvm.LLVMGetCalledValue(instruction)))
                    jumpSites.Add(instruction, (jumpSites.Count + 1, Il.DefineLabel()));
                if (Llvm.LLVMGetInstructionOpcode(instruction) == 45)
                {
                    string name = Llvm.Name(Llvm.LLVMGetCalledValue(instruction));
                    scopedStack |= name.StartsWith("llvm.stacksave", StringComparison.Ordinal) || name.StartsWith("llvm.stackrestore", StringComparison.Ordinal);
                }
            }
        }
        AllocateFunctionLocals(valueCount > 32768 && jumpSites.Count == 0);
        if (scopedStack || hasAllocations && jumpSites.Count != 0)
        {
            stackMemory = Il.DeclareLocal(typeof(StackMemory));
            stackReturn = method.ReturnType == typeof(void) || jumpSites.Count != 0 ? null : Il.DeclareLocal(method.ReturnType);
            stackExit = Il.DefineLabel();
            Il.Emit(OpCodes.Newobj, typeof(StackMemory).GetConstructor(Type.EmptyTypes)!);
            Il.Emit(OpCodes.Stloc, stackMemory);
            Il.BeginExceptionBlock();
        }
        if (jumpSites.Count != 0)
        {
            jumpFrame = Il.DeclareLocal(typeof(long));
            jumpResume = Il.DeclareLocal(typeof(int));
            jumpReturn = method.ReturnType == typeof(void) ? null : Il.DeclareLocal(method.ReturnType);
            jumpDispatch = Il.DefineLabel();
            jumpExit = Il.DefineLabel();
            Il.Emit(OpCodes.Call, typeof(NonLocalJumps).GetMethod(nameof(NonLocalJumps.NewFrame))!);
            Il.Emit(OpCodes.Stloc, jumpFrame);
            Il.MarkLabel(jumpDispatch);
            Il.BeginExceptionBlock();
            Il.Emit(OpCodes.Ldloc, jumpResume);
            Il.Emit(OpCodes.Ldc_I4_1);
            Il.Emit(OpCodes.Sub);
            Il.Emit(OpCodes.Switch, jumpSites.Values.Select(site => site.Resume).ToArray());
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
        if (jumpFrame is not null)
        {
            Il.BeginCatchBlock(typeof(NonLocalJumpException));
            LocalBuilder exception = Il.DeclareLocal(typeof(NonLocalJumpException));
            Il.Emit(OpCodes.Stloc, exception);
            Label matched = Il.DefineLabel();
            Il.Emit(OpCodes.Ldloc, exception);
            Il.Emit(OpCodes.Callvirt, typeof(NonLocalJumpException).GetProperty(nameof(NonLocalJumpException.Frame))!.GetMethod!);
            Il.Emit(OpCodes.Ldloc, jumpFrame);
            Il.Emit(OpCodes.Beq, matched);
            Il.Emit(OpCodes.Rethrow);
            Il.MarkLabel(matched);
            if (stackMemory is not null)
            {
                Il.Emit(OpCodes.Ldloc, stackMemory);
                Il.Emit(OpCodes.Ldloc, exception);
                Il.Emit(OpCodes.Callvirt, typeof(NonLocalJumpException).GetProperty(nameof(NonLocalJumpException.StackPosition))!.GetMethod!);
                Il.Emit(OpCodes.Callvirt, typeof(StackMemory).GetMethod(nameof(StackMemory.Restore))!);
            }
            foreach ((nint instruction, (int site, _)) in jumpSites)
            {
                Label next = Il.DefineLabel();
                Il.Emit(OpCodes.Ldloc, exception);
                Il.Emit(OpCodes.Callvirt, typeof(NonLocalJumpException).GetProperty(nameof(NonLocalJumpException.Site))!.GetMethod!);
                Il.Emit(OpCodes.Ldc_I4, site);
                Il.Emit(OpCodes.Bne_Un, next);
                Il.Emit(OpCodes.Ldloc, exception);
                Il.Emit(OpCodes.Callvirt, typeof(NonLocalJumpException).GetProperty(nameof(NonLocalJumpException.Value))!.GetMethod!);
                Il.Emit(OpCodes.Stloc, locals[instruction]);
                Il.Emit(OpCodes.Ldc_I4, site);
                Il.Emit(OpCodes.Stloc, jumpResume!);
                Il.Emit(OpCodes.Leave, jumpDispatch);
                Il.MarkLabel(next);
            }
            Il.Emit(OpCodes.Rethrow);
            Il.EndExceptionBlock();
            Il.MarkLabel(jumpExit);
            if (stackMemory is not null)
                Il.Emit(OpCodes.Leave, stackExit);
            else
            {
                if (jumpReturn is not null) Il.Emit(OpCodes.Ldloc, jumpReturn);
                Il.Emit(OpCodes.Ret);
            }
        }
        if (stackMemory is not null)
        {
            Il.BeginFinallyBlock();
            Il.Emit(OpCodes.Ldloc, stackMemory);
            Il.Emit(OpCodes.Callvirt, typeof(StackMemory).GetMethod(nameof(StackMemory.Dispose))!);
            Il.EndExceptionBlock();
            Il.MarkLabel(stackExit);
            if ((jumpReturn ?? stackReturn) is LocalBuilder result) Il.Emit(OpCodes.Ldloc, result);
            Il.Emit(OpCodes.Ret);
        }
    }

    internal override void Load(nint value)
    {
        if (locals.TryGetValue(value, out LocalBuilder? local))
            Il.Emit(OpCodes.Ldloc, local);
        else if (storedScalars.TryGetValue(value, out var storage))
        {
            Il.Emit(OpCodes.Ldloc, storage.Array);
            Il.Emit(OpCodes.Ldc_I4, storage.Index);
            Il.Emit(OpCodes.Ldelem, storage.Temporary.LocalType);
        }
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
                if (jumpFrame is not null)
                {
                    if (jumpReturn is not null) Il.Emit(OpCodes.Stloc, jumpReturn);
                    Il.Emit(OpCodes.Leave, jumpExit);
                }
                else if (stackMemory is not null)
                {
                    if (stackReturn is not null) Il.Emit(OpCodes.Stloc, stackReturn);
                    Il.Emit(OpCodes.Leave, stackExit);
                }
                else
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
                if (stackMemory is not null) Il.Emit(OpCodes.Ldloc, stackMemory);
                Load(Llvm.LLVMGetOperand(value, 0));
                Il.Emit(OpCodes.Conv_U);
                Il.Emit(OpCodes.Ldc_I8, Types.Size(allocated));
                Il.Emit(OpCodes.Conv_U);
                Il.Emit(OpCodes.Mul_Ovf_Un);
                Offset(alignment - 1);
                if (stackMemory is not null)
                    Il.Emit(OpCodes.Callvirt, typeof(StackMemory).GetMethod(nameof(StackMemory.Allocate))!);
                else
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
            case 50:
            case 51:
                VectorElement(value, opcode == 51);
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
        StoreResult(value);
        if (jumpSites.TryGetValue(value, out var jump))
            Il.MarkLabel(jump.Resume);
    }

    private void VectorElement(nint value, bool insert)
    {
        nint vector = Llvm.LLVMGetOperand(value, 0);
        nint vectorType = Llvm.LLVMTypeOf(vector);
        nint element = Llvm.LLVMGetElementType(vectorType);
        int width = TypeSystem.Width(element);
        if (TypeSystem.Kind(element) is not (2 or 3 or 12) &&
            !(TypeSystem.Kind(element) == 8 && width is > 0 and <= 128))
            throw new NotSupportedException($"Unsupported vector lane layout: {Llvm.PrintType(element)}");
        LocalBuilder copy = Il.DeclareLocal(Types.Map(vectorType));
        Load(vector);
        Il.Emit(OpCodes.Stloc, copy);
        Il.Emit(OpCodes.Ldloca, copy);
        Il.Emit(OpCodes.Conv_I);
        Load(Llvm.LLVMGetOperand(value, insert ? 2u : 1u));
        if (width > 0 && width is not (8 or 16 or 32 or 64 or 128))
        {
            Il.Emit(OpCodes.Conv_I8);
            if (insert)
            {
                Load(Llvm.LLVMGetOperand(value, 1));
                if (width <= 64)
                {
                    Il.Emit(OpCodes.Conv_U8);
                    Il.Emit(OpCodes.Call, typeof(WideInteger).GetMethod(nameof(WideInteger.FromUnsigned))!);
                }
            }
            Il.Emit(OpCodes.Ldc_I4, width);
            Il.Emit(OpCodes.Call, typeof(WideInteger).GetMethod(insert ? nameof(WideInteger.WritePacked) : nameof(WideInteger.ReadPacked))!);
            if (insert)
                Il.Emit(OpCodes.Ldloc, copy);
            else if (width <= 64)
            {
                Il.Emit(OpCodes.Call, typeof(WideInteger).GetMethod(nameof(WideInteger.Low))!);
                if (width <= 32) Il.Emit(OpCodes.Conv_I4);
            }
            return;
        }
        Il.Emit(OpCodes.Conv_I);
        Il.Emit(OpCodes.Ldc_I8, Types.Size(element));
        Il.Emit(OpCodes.Conv_I);
        Il.Emit(OpCodes.Mul);
        Il.Emit(OpCodes.Add);
        if (insert)
        {
            Load(Llvm.LLVMGetOperand(value, 1));
            StoreMemory(element);
            Il.Emit(OpCodes.Ldloc, copy);
        }
        else
            ReadMemory(element);
    }

    private void Edge(nint source, nint target)
    {
        List<nint> copies = [];
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
                copies.Add(phi);
                found = true;
                break;
            }
            if (!found)
                throw new InvalidOperationException("PHI has no incoming value for predecessor.");
        }
        for (int index = copies.Count - 1; index >= 0; index--)
            StoreResult(copies[index]);
        Il.Emit(OpCodes.Br, labels[target]);
    }

    private void Atomic(nint instruction, int opcode)
    {
        nint type = opcode == 27 ? Llvm.LLVMTypeOf(instruction) : Llvm.LLVMTypeOf(Llvm.LLVMGetOperand(instruction, opcode == 28 ? 0u : 1u));
        if (TypeSystem.Kind(type) is 2 or 3)
        {
            if (opcode != 57) throw new NotSupportedException($"Floating atomic opcode {opcode}");
            int operation = Llvm.LLVMGetAtomicRMWBinOp(instruction);
            if (operation is not (0 or 11 or 12)) throw new NotSupportedException($"Floating atomic RMW operation {operation}");
            Load(Llvm.LLVMGetOperand(instruction, 0));
            Load(Llvm.LLVMGetOperand(instruction, 1));
            Il.Emit(OpCodes.Ldc_I4, operation);
            Il.Emit(OpCodes.Call, typeof(Atomics).GetMethod(TypeSystem.Kind(type) == 2 ? nameof(Atomics.ModifySingle) : nameof(Atomics.ModifyDouble))!);
            return;
        }
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
        StoreResult(instruction);
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
        uint count = Llvm.LLVMGetNumClauses(instruction);
        nint[] clauses = Enumerable.Range(0, checked((int)count)).Select(index => Llvm.LLVMGetClause(instruction, (uint)index)).ToArray();
        nint[] catches = clauses.Where(clause => TypeSystem.Kind(Llvm.LLVMTypeOf(clause)) == 12).ToArray();
        Il.Emit(OpCodes.Ldloc, currentException);
        Il.Emit(OpCodes.Ldc_I4, catches.Length);
        Il.Emit(OpCodes.Newarr, typeof(nint));
        for (int index = 0; index < catches.Length; index++)
        {
            Il.Emit(OpCodes.Dup);
            Il.Emit(OpCodes.Ldc_I4, index);
            Load(catches[index]);
            Il.Emit(OpCodes.Stelem_I);
        }
        Il.Emit(OpCodes.Call, typeof(Cxx).GetMethod(nameof(Cxx.Selector))!);
        LocalBuilder selector = Il.DeclareLocal(typeof(int));
        Il.Emit(OpCodes.Stloc, selector);
        Label selected = Il.DefineLabel();
        foreach (nint clause in clauses.Where(clause => TypeSystem.Kind(Llvm.LLVMTypeOf(clause)) != 12))
        {
            nint filterType = Llvm.LLVMTypeOf(clause);
            if (TypeSystem.Kind(filterType) != 11 || TypeSystem.Kind(Llvm.LLVMGetElementType(filterType)) != 12)
                throw new NotSupportedException($"Unsupported C++ exception filter: {Llvm.Print(clause)}");
            Il.Emit(OpCodes.Ldloc, selector);
            Il.Emit(OpCodes.Brtrue, selected);
            Il.Emit(OpCodes.Ldloc, currentException);
            int length = checked((int)Llvm.LLVMGetArrayLength2(filterType));
            Il.Emit(OpCodes.Ldc_I4, length);
            Il.Emit(OpCodes.Newarr, typeof(nint));
            for (int index = 0; index < length; index++)
            {
                Il.Emit(OpCodes.Dup);
                Il.Emit(OpCodes.Ldc_I4, index);
                Load(Llvm.LLVMGetAggregateElement(clause, (uint)index));
                Il.Emit(OpCodes.Stelem_I);
            }
            Il.Emit(OpCodes.Call, typeof(Cxx).GetMethod(nameof(Cxx.Filter))!);
            Il.Emit(OpCodes.Stloc, selector);
        }
        Il.MarkLabel(selected);
        Il.Emit(OpCodes.Ldloc, selector);
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

    private void AllocateFunctionLocals(bool reuse)
    {
        var available = new Dictionary<Type, Stack<LocalBuilder>>();
        var arrays = new Dictionary<Type, (LocalBuilder Array, LocalBuilder Temporary, int Count)>();
        foreach (nint block in Llvm.Blocks(function))
        {
            nint[] instructions = Llvm.Instructions(block).ToArray();
            var positions = new Dictionary<nint, int>();
            if (reuse)
                for (int index = 0; index < instructions.Length; index++) positions.Add(instructions[index], index);
            var expired = new Dictionary<int, List<LocalBuilder>>();
            for (int index = 0; index < instructions.Length; index++)
            {
                if (expired.Remove(index, out List<LocalBuilder>? released))
                    foreach (LocalBuilder local in released) Release(local);
                nint instruction = instructions[index];
                nint type = Llvm.LLVMTypeOf(instruction);
                if (TypeSystem.Kind(type) == 0) continue;
                Type managed = Types.Map(type);
                int lastUse = -1;
                if (reuse && managed.IsPrimitive && Llvm.LLVMGetInstructionOpcode(instruction) != 44)
                {
                    lastUse = index;
                    for (nint use = Llvm.LLVMGetFirstUse(instruction); use != 0; use = Llvm.LLVMGetNextUse(use))
                    {
                        nint user = Llvm.LLVMGetUser(use);
                        if (!positions.TryGetValue(user, out int position) || position <= index || Llvm.LLVMGetInstructionOpcode(user) == 44)
                        {
                            lastUse = -1;
                            break;
                        }
                        lastUse = Math.Max(lastUse, position);
                    }
                }
                if (reuse && managed.IsPrimitive && lastUse < 0)
                {
                    if (!arrays.TryGetValue(managed, out var arrayStorage))
                        arrayStorage = (Il.DeclareLocal(managed.MakeArrayType()), Il.DeclareLocal(managed), 0);
                    storedScalars[instruction] = (arrayStorage.Array, arrayStorage.Temporary, arrayStorage.Count);
                    arrays[managed] = (arrayStorage.Array, arrayStorage.Temporary, arrayStorage.Count + 1);
                    continue;
                }
                LocalBuilder storage = lastUse >= 0 && available.TryGetValue(managed, out Stack<LocalBuilder>? pool) && pool.TryPop(out LocalBuilder? previous)
                    ? previous : Il.DeclareLocal(managed);
                if (storage.LocalIndex >= ushort.MaxValue)
                    throw new NotSupportedException($"Function {Llvm.Name(function)} exceeds the CIL local-variable limit.");
                locals[instruction] = storage;
                if (lastUse >= 0)
                {
                    if (!expired.TryGetValue(lastUse + 1, out List<LocalBuilder>? entries)) expired.Add(lastUse + 1, entries = []);
                    entries.Add(storage);
                }
            }
            foreach (List<LocalBuilder> entries in expired.Values)
                foreach (LocalBuilder local in entries) Release(local);
        }
        foreach (var (type, storage) in arrays)
        {
            Il.Emit(OpCodes.Ldc_I4, storage.Count);
            Il.Emit(OpCodes.Newarr, type);
            Il.Emit(OpCodes.Stloc, storage.Array);
        }

        void Release(LocalBuilder local)
        {
            if (!available.TryGetValue(local.LocalType, out Stack<LocalBuilder>? pool)) available.Add(local.LocalType, pool = new());
            pool.Push(local);
        }
    }

    private void StoreResult(nint value)
    {
        if (locals.TryGetValue(value, out LocalBuilder? local))
            Il.Emit(OpCodes.Stloc, local);
        else if (storedScalars.TryGetValue(value, out var storage))
        {
            Il.Emit(OpCodes.Stloc, storage.Temporary);
            Il.Emit(OpCodes.Ldloc, storage.Array);
            Il.Emit(OpCodes.Ldc_I4, storage.Index);
            Il.Emit(OpCodes.Ldloc, storage.Temporary);
            Il.Emit(OpCodes.Stelem, storage.Temporary.LocalType);
        }
    }

    private void Call(nint instruction)
    {
        nint target = Llvm.LLVMGetCalledValue(instruction);
        string name = Llvm.Name(target);
        if (Llvm.LLVMIsAInlineAsm(target) != 0)
        {
            nint text = Llvm.LLVMGetInlineAsmAsmString(target, out nuint length);
            string assembly = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(text, checked((int)length))!;
            if (Compiler.Host is not null && NativeCpuQuery(instruction, target, assembly))
            {
                for (uint index = 0; index < Llvm.LLVMGetNumArgOperands(instruction); index++)
                    Load(Llvm.LLVMGetOperand(instruction, index));
                Il.Emit(OpCodes.Call, Compiler.Host.Resolve(0, instruction));
                return;
            }
            if (Compiler.Host is not null && length == 4 && TypeSystem.Kind(Llvm.LLVMTypeOf(instruction)) == 0 && Llvm.LLVMGetNumArgOperands(instruction) == 0 &&
                assembly == "int3")
            {
                Il.Emit(OpCodes.Call, typeof(SystemAbi).GetMethod(nameof(SystemAbi.DebugTrap))!);
                return;
            }
            bool commentOnly = assembly.Split('\n').All(line =>
            {
                string content = line.Trim(' ', '\t', '\r');
                return content.Length == 0 || content.StartsWith('#');
            });
            if (!commentOnly || TypeSystem.Kind(Llvm.LLVMTypeOf(instruction)) != 0)
                throw new NotSupportedException("Inline assembly with instructions or outputs is not supported; use a pure C configuration.");
            Il.Emit(OpCodes.Call, typeof(Thread).GetMethod(nameof(Thread.MemoryBarrier))!);
            return;
        }
        if (jumpSites.TryGetValue(instruction, out var jump))
        {
            Load(Llvm.LLVMGetOperand(instruction, 0));
            Il.Emit(OpCodes.Ldloc, jumpFrame!);
            Il.Emit(OpCodes.Ldc_I4, jump.Site);
            if (name is "sigsetjmp" or "__sigsetjmp") Load(Llvm.LLVMGetOperand(instruction, 1));
            else Il.Emit(OpCodes.Ldc_I4_0);
            Il.Emit(OpCodes.Ldc_I4, Compiler.Host is null ? 0 : 1);
            if (stackMemory is not null)
            {
                Il.Emit(OpCodes.Ldloc, stackMemory);
                Il.Emit(OpCodes.Callvirt, typeof(StackMemory).GetMethod(nameof(StackMemory.Save))!);
            }
            else
            {
                Il.Emit(OpCodes.Ldc_I4_0);
                Il.Emit(OpCodes.Conv_I);
            }
            Il.Emit(OpCodes.Call, typeof(NonLocalJumps).GetMethod(nameof(NonLocalJumps.Save))!);
            return;
        }
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
        bool discardReturn = false;
        bool truncateBooleanReturn = false;
        if (Llvm.LLVMIsAFunction(target) != 0 && signature != Llvm.LLVMGlobalGetValueType(target))
        {
            nint definition = Llvm.LLVMGlobalGetValueType(target);
            uint arguments = Llvm.LLVMGetNumArgOperands(instruction);
            uint parameters = Llvm.LLVMCountParamTypes(definition);
            if (Compiler.Host is not null && Compiler.TrapMissingArguments && parameters > arguments &&
                Llvm.LLVMIsDeclaration(target) == 0 && Llvm.LLVMIsFunctionVarArg(definition) == 0 &&
                TypeSystem.Kind(Llvm.LLVMGetReturnType(signature)) == 0 && TypeSystem.Kind(Llvm.LLVMGetReturnType(definition)) == 0)
            {
                string diagnostic = $"Invalid direct call with missing arguments: {Llvm.Name(function)} -> {name}: {Llvm.PrintType(signature)} versus {Llvm.PrintType(definition)}";
                Console.Error.WriteLine($"llvmnet: emitting runtime trap: {diagnostic}");
                Il.Emit(OpCodes.Ldstr, diagnostic);
                Il.Emit(OpCodes.Newobj, typeof(InvalidProgramException).GetConstructor([typeof(string)])!);
                Il.Emit(OpCodes.Throw);
                return;
            }
            discardReturn = TypeSystem.Kind(Llvm.LLVMGetReturnType(signature)) == 0 && TypeSystem.Kind(Llvm.LLVMGetReturnType(definition)) != 0;
            bool unusedVoidReturn = TypeSystem.Kind(Llvm.LLVMGetReturnType(definition)) == 0 && Llvm.LLVMGetFirstUse(instruction) == 0;
            truncateBooleanReturn = Compiler.Host is not null && Llvm.LLVMGetInstructionCallConv(instruction) == 0 && Llvm.LLVMGetFunctionCallConv(target) == 0 &&
                TypeSystem.Kind(Llvm.LLVMGetReturnType(signature)) == 8 && TypeSystem.Kind(Llvm.LLVMGetReturnType(definition)) == 8 &&
                (TypeSystem.Width(Llvm.LLVMGetReturnType(signature)) == 1 && TypeSystem.Width(Llvm.LLVMGetReturnType(definition)) == 8 ||
                    TypeSystem.Width(Llvm.LLVMGetReturnType(signature)) == 8 && TypeSystem.Width(Llvm.LLVMGetReturnType(definition)) == 1);
            if (Llvm.LLVMIsFunctionVarArg(definition) != 0 || parameters > arguments ||
                !discardReturn && !unusedVoidReturn && !truncateBooleanReturn && Llvm.LLVMGetReturnType(definition) != Llvm.LLVMGetReturnType(signature) ||
                Enumerable.Range(0, checked((int)parameters)).Any(index => Llvm.LLVMTypeOf(Llvm.LLVMGetParam(target, (uint)index)) != Llvm.LLVMTypeOf(Llvm.LLVMGetOperand(instruction, (uint)index)) &&
                    !SystemIntegerArgumentCompatible(instruction, target, (uint)index)))
                throw new NotSupportedException($"Incompatible direct call signature for {name}: {Llvm.PrintType(signature)} versus {Llvm.PrintType(definition)}");
            if (unusedVoidReturn) { locals.Remove(instruction); storedScalars.Remove(instruction); }
            signature = definition;
        }
        bool variadic = Llvm.LLVMIsFunctionVarArg(signature) != 0;
        Label? legacyComplete = null;
        if (variadic && Llvm.LLVMIsAFunction(target) == 0)
        {
            LocalBuilder fixedTarget = Il.DeclareLocal(typeof(nint));
            Label variadicCall = Il.DefineLabel();
            legacyComplete = Il.DefineLabel();
            if (Compiler.Host is not null)
            {
                Label translated = Il.DefineLabel();
                Load(target);
                Il.Emit(OpCodes.Call, typeof(SystemAbi).GetMethod(nameof(SystemAbi.IsNativeVariadic))!);
                Il.Emit(OpCodes.Brfalse, translated);
                for (uint index = 0; index < Llvm.LLVMGetNumArgOperands(instruction); index++)
                    Load(Llvm.LLVMGetOperand(instruction, index));
                Load(target);
                Il.Emit(OpCodes.Call, Compiler.Host.Resolve(0, instruction));
                Il.Emit(OpCodes.Br, legacyComplete.Value);
                Il.MarkLabel(translated);
            }
            Load(target);
            Il.Emit(OpCodes.Call, typeof(SystemAbi).GetMethod(nameof(SystemAbi.ResolveCallback))!);
            Il.Emit(OpCodes.Stloc, fixedTarget);
            Il.Emit(OpCodes.Ldloc, fixedTarget);
            Il.Emit(OpCodes.Brfalse, variadicCall);
            uint argumentCount = Llvm.LLVMGetNumArgOperands(instruction);
            for (uint index = 0; index < argumentCount; index++)
                Load(Llvm.LLVMGetOperand(instruction, index));
            Il.Emit(OpCodes.Ldloc, fixedTarget);
            Type[] parameters = Enumerable.Range(0, checked((int)argumentCount))
                .Select(index => Types.Map(Llvm.LLVMTypeOf(Llvm.LLVMGetOperand(instruction, (uint)index)))).ToArray();
            ManagedCalli(Types.Map(Llvm.LLVMGetReturnType(signature)), parameters);
            Il.Emit(OpCodes.Br, legacyComplete.Value);
            Il.MarkLabel(variadicCall);
        }
        if (variadic)
            varargs.Pack(instruction);
        uint count = variadic || Llvm.LLVMIsAFunction(target) != 0 ? Llvm.LLVMCountParamTypes(signature) : Llvm.LLVMGetNumArgOperands(instruction);
        for (uint index = 0; index < count; index++)
        {
            nint argument = Llvm.LLVMGetOperand(instruction, index);
            Load(argument);
            if (!variadic && Llvm.LLVMIsAFunction(target) != 0 && Llvm.LLVMTypeOf(Llvm.LLVMGetParam(target, index)) != Llvm.LLVMTypeOf(argument))
            {
                nint parameter = Llvm.LLVMTypeOf(Llvm.LLVMGetParam(target, index));
                Il.Emit(TypeSystem.Kind(parameter) == 12 ? OpCodes.Conv_I : TypeSystem.Width(parameter) == 64 ? OpCodes.Conv_U8 : OpCodes.Conv_I4);
                Normalize(parameter);
            }
        }
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
            if (Compiler.Host is not null && variadic)
                Il.Emit(OpCodes.Call, typeof(SystemAbi).GetMethod(nameof(SystemAbi.ResolveVariadicCallback))!);
            if (Compiler.Host is null || variadic)
                ManagedCalli(Types.Map(Llvm.LLVMGetReturnType(signature)), Types.Parameters(signature));
            else
            {
                LocalBuilder managedTarget = Il.DeclareLocal(typeof(nint));
                Label native = Il.DefineLabel();
                Label complete = Il.DefineLabel();
                Il.Emit(OpCodes.Dup);
                Il.Emit(OpCodes.Call, typeof(SystemAbi).GetMethod(nameof(SystemAbi.ResolveCallback))!);
                Il.Emit(OpCodes.Stloc, managedTarget);
                Il.Emit(OpCodes.Ldloc, managedTarget);
                Il.Emit(OpCodes.Brfalse, native);
                Il.Emit(OpCodes.Pop);
                Il.Emit(OpCodes.Ldloc, managedTarget);
                ManagedCalli(Types.Map(Llvm.LLVMGetReturnType(signature)), Types.Parameters(signature));
                Il.Emit(OpCodes.Br, complete);
                Il.MarkLabel(native);
                Type returnType = Types.Map(Llvm.LLVMGetReturnType(signature));
                Type[] parameters = Types.Parameters(signature);
                bool scalar(Type type) => type == typeof(void) || type == typeof(int) || type == typeof(long) || type == typeof(nint) || type == typeof(float) || type == typeof(double);
                if (!scalar(returnType) || parameters.Any(type => !scalar(type)))
                {
                    for (int index = 0; index <= parameters.Length; index++) Il.Emit(OpCodes.Pop);
                    Il.Emit(OpCodes.Ldstr, "Indirect native aggregate calls are not supported; compile the callback to bitcode.");
                    Il.Emit(OpCodes.Newobj, typeof(NotSupportedException).GetConstructor([typeof(string)])!);
                    Il.Emit(OpCodes.Throw);
                }
                else
                    Il.EmitCalli(OpCodes.Calli, System.Runtime.InteropServices.CallingConvention.Cdecl, returnType, parameters);
                Il.MarkLabel(complete);
            }
        }
        if (legacyComplete is Label completeLegacy) Il.MarkLabel(completeLegacy);
        if (discardReturn) Il.Emit(OpCodes.Pop);
        if (truncateBooleanReturn) Normalize(Llvm.LLVMTypeOf(instruction));
    }

    private void ManagedCalli(Type returnType, Type[] parameters)
    {
        Compiler.ManagedCalli(Il, method, returnType, parameters);
    }

    private bool SystemIntegerArgumentCompatible(nint instruction, nint target, uint index)
    {
        nint source = Llvm.LLVMTypeOf(Llvm.LLVMGetOperand(instruction, index));
        nint destination = Llvm.LLVMTypeOf(Llvm.LLVMGetParam(target, index));
        bool integerWidths = TypeSystem.Kind(source) == 8 && TypeSystem.Kind(destination) == 8 &&
            (TypeSystem.Width(source) == 32 && TypeSystem.Width(destination) == 64 || TypeSystem.Width(source) == 64 && TypeSystem.Width(destination) == 32);
        bool booleanWidths = TypeSystem.Kind(source) == 8 && TypeSystem.Kind(destination) == 8 &&
            (TypeSystem.Width(source) == 1 && TypeSystem.Width(destination) == 8 || TypeSystem.Width(source) == 8 && TypeSystem.Width(destination) == 1);
        bool pointerBits = TypeSystem.Kind(source) == 12 && TypeSystem.Kind(destination) == 8 && TypeSystem.Width(destination) == 64 ||
            TypeSystem.Kind(source) == 8 && TypeSystem.Width(source) == 64 && TypeSystem.Kind(destination) == 12;
        if (Compiler.Host is null || Llvm.LLVMGetInstructionCallConv(instruction) != 0 || Llvm.LLVMGetFunctionCallConv(target) != 0 ||
            !(integerWidths || booleanWidths || pointerBits) || integerWidths && TypeSystem.Width(source) == 32 && index >= 6 ||
            TypeSystem.Kind(Llvm.LLVMGetReturnType(Llvm.LLVMGlobalGetValueType(target))) is 10 or 11)
            return false;
        for (uint parameter = 0; parameter <= index; parameter++)
        {
            nint type = Llvm.LLVMTypeOf(Llvm.LLVMGetParam(target, parameter));
            if (TypeSystem.Kind(type) is not (2 or 3 or 12) && !(TypeSystem.Kind(type) == 8 && TypeSystem.Width(type) <= 64)) return false;
            foreach (string attribute in new[] { "byval", "sret", "inreg", "signext" })
            {
                uint kind = Llvm.LLVMGetEnumAttributeKindForName(attribute, (nuint)attribute.Length);
                if (Llvm.LLVMGetEnumAttributeAtIndex(target, parameter + 1, kind) != 0 || Llvm.LLVMGetCallSiteEnumAttribute(instruction, parameter + 1, kind) != 0)
                    return false;
            }
        }
        return true;
    }

    private static bool NativeCpuQuery(nint instruction, nint target, string assembly)
    {
        nint text = Llvm.LLVMGetInlineAsmConstraintString(target, out nuint length);
        string constraints = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(text, checked((int)length))!;
        int registers = 0;
        int arguments = 0;
        if (assembly == "movq\t%rbx, %rsi\n\tcpuid\n\txchgq\t%rbx, %rsi\n\t")
        {
            registers = 4;
            arguments = constraints switch
            {
                "={ax},={si},={cx},={dx},{ax},~{dirflag},~{fpsr},~{flags}" => 1,
                "={ax},={si},={cx},={dx},{ax},{cx},~{dirflag},~{fpsr},~{flags}" => 2,
                _ => 0
            };
        }
        else if (assembly == "xchg$(q$)\t$(%$)rbx, ${1:q}; cpuid; xchg$(q$)\t$(%$)rbx, ${1:q}" &&
            constraints == "={ax},=&r,={cx},={dx},0,2,~{dirflag},~{fpsr},~{flags}")
        {
            registers = 4;
            arguments = 2;
        }
        else if (assembly == ".byte 0x0f, 0x01, 0xd0" && constraints == "={ax},={dx},{cx},~{dirflag},~{fpsr},~{flags}")
        {
            registers = 2;
            arguments = 1;
        }
        nint result = Llvm.LLVMTypeOf(instruction);
        return arguments != 0 && Llvm.LLVMGetNumArgOperands(instruction) == arguments && TypeSystem.Kind(result) == 10 &&
            Llvm.LLVMCountStructElementTypes(result) == registers &&
            Enumerable.Range(0, registers).All(index => TypeSystem.Width(Llvm.LLVMStructGetTypeAtIndex(result, (uint)index)) == 32) &&
            Enumerable.Range(0, arguments).All(index => TypeSystem.Width(Llvm.LLVMTypeOf(Llvm.LLVMGetOperand(instruction, (uint)index))) == 32);
    }

    private void Intrinsic(nint instruction, string name)
    {
        if (name == "llvm.get.rounding")
        {
            Il.Emit(OpCodes.Ldc_I4, Compiler.Host is null ? 0 : 1);
            Il.Emit(OpCodes.Call, typeof(CMath).GetMethod(nameof(CMath.GetRounding))!);
            return;
        }
        if (name.StartsWith("llvm.threadlocal.address.", StringComparison.Ordinal))
        {
            Load(Llvm.LLVMGetOperand(instruction, 0));
            return;
        }
        if (name.StartsWith("llvm.invariant.start.", StringComparison.Ordinal))
        {
            Zero(Llvm.LLVMTypeOf(instruction));
            return;
        }
        if (name.StartsWith("llvm.invariant.end.", StringComparison.Ordinal))
            return;
        if (name.StartsWith("llvm.launder.invariant.group.", StringComparison.Ordinal) || name.StartsWith("llvm.strip.invariant.group.", StringComparison.Ordinal))
        {
            Load(Llvm.LLVMGetOperand(instruction, 0));
            return;
        }
        if (name.StartsWith("llvm.stacksave", StringComparison.Ordinal) || name.StartsWith("llvm.stackrestore", StringComparison.Ordinal))
        {
            if (stackMemory is null) throw new NotSupportedException("Stack intrinsics require a scoped allocation frame.");
            Il.Emit(OpCodes.Ldloc, stackMemory);
            bool restore = name.StartsWith("llvm.stackrestore", StringComparison.Ordinal);
            if (restore) Load(Llvm.LLVMGetOperand(instruction, 0));
            Il.Emit(OpCodes.Callvirt, typeof(StackMemory).GetMethod(restore ? nameof(StackMemory.Restore) : nameof(StackMemory.Save))!);
            return;
        }
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
        if (name.StartsWith("llvm.lifetime.", StringComparison.Ordinal) || name.StartsWith("llvm.dbg.", StringComparison.Ordinal) || name.StartsWith("llvm.prefetch.", StringComparison.Ordinal) || name is "llvm.assume" or "llvm.experimental.noalias.scope.decl")
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
                if (operation == "bswap" && width > 64 && width <= 128 && width % 16 == 0)
                {
                    Load(Llvm.LLVMGetOperand(instruction, 0));
                    Il.Emit(OpCodes.Ldc_I4, width);
                    Il.Emit(OpCodes.Call, typeof(WideInteger).GetMethod(nameof(WideInteger.ByteSwap))!);
                    return;
                }
        if (width > 64 && operation is "ctpop" or "ctlz" or "cttz")
        {
            Load(Llvm.LLVMGetOperand(instruction, 0));
            Il.Emit(OpCodes.Ldc_I4, width);
            Il.Emit(OpCodes.Ldc_I4, operation == "ctpop" ? 0 : operation == "ctlz" ? 1 : 2);
            Il.Emit(OpCodes.Call, typeof(WideInteger).GetMethod(nameof(WideInteger.CountBits))!);
            return;
        }
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
        if (operation is "frexp" or "modf")
        {
            nint resultType = Llvm.LLVMTypeOf(instruction);
            (nint fractionType, long fractionOffset) = Types.Element(resultType, 0);
            (nint integralType, long integralOffset) = Types.Element(resultType, 1);
            bool frexp = operation == "frexp";
            if (TypeSystem.Kind(fractionType) is not (2 or 3) ||
                (frexp ? TypeSystem.Width(integralType) != 32 : integralType != fractionType))
                throw new NotSupportedException($"Unsupported {operation} result: {Llvm.PrintType(resultType)}");
            LocalBuilder result = Il.DeclareLocal(Types.Map(resultType));
            LocalBuilder integral = Il.DeclareLocal(frexp ? typeof(int) : typeof(double));
            Il.Emit(OpCodes.Ldloca, result);
            Il.Emit(OpCodes.Initobj, Types.Map(resultType));
            Il.Emit(OpCodes.Ldloca, result);
            Offset(fractionOffset);
            Load(Llvm.LLVMGetOperand(instruction, 0));
            Il.Emit(OpCodes.Conv_R8);
            Il.Emit(OpCodes.Ldloca, integral);
            Il.Emit(OpCodes.Conv_I);
            Il.Emit(OpCodes.Call, typeof(CMath).GetMethod(frexp ? nameof(CMath.Frexp) : nameof(CMath.Modf))!);
            if (TypeSystem.Kind(fractionType) == 2) Il.Emit(OpCodes.Conv_R4);
            StoreMemory(fractionType);
            Il.Emit(OpCodes.Ldloca, result);
            Offset(integralOffset);
            Il.Emit(OpCodes.Ldloc, integral);
            if (!frexp && TypeSystem.Kind(integralType) == 2) Il.Emit(OpCodes.Conv_R4);
            StoreMemory(integralType);
            Il.Emit(OpCodes.Ldloc, result);
            return;
        }
        if (operation == "powi")
        {
            nint type = Llvm.LLVMTypeOf(instruction);
            if (TypeSystem.Kind(type) is not (2 or 3) || TypeSystem.Width(Llvm.LLVMTypeOf(Llvm.LLVMGetOperand(instruction, 1))) != 32)
                throw new NotSupportedException($"Unsupported integer-power intrinsic: {name}");
            Load(Llvm.LLVMGetOperand(instruction, 0));
            Load(Llvm.LLVMGetOperand(instruction, 1));
            Il.Emit(OpCodes.Call, typeof(Numeric).GetMethod(TypeSystem.Kind(type) == 2 ? nameof(Numeric.PowInteger32) : nameof(Numeric.PowInteger64))!);
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
            string helper = operation switch { "fmuladd" or "fma" => nameof(Float80.FusedMultiplyAdd), "fabs" => nameof(Float80.Abs), "floor" => nameof(Float80.Floor), _ => throw new NotSupportedException($"Unsupported extended floating intrinsic: {name}") };
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
            MethodInfo? helper = typeof(Numeric).GetMethod("ByteSwap" + width);
            if (width == 48)
            {
                Load(Llvm.LLVMGetOperand(instruction, 0));
                Il.Emit(OpCodes.Call, typeof(Numeric).GetMethod(nameof(Numeric.ByteSwap64))!);
                Il.Emit(OpCodes.Ldc_I4, 16);
                Il.Emit(OpCodes.Shr_Un);
                return;
            }
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
            "asin" => "Asin", "acos" => "Acos", "tan" => "Tan",
            "sinh" => "Sinh", "cosh" => "Cosh", "tanh" => "Tanh",
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