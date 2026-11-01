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
    private bool usesLocalloc;
    private LocalBuilder? stackReturn;
    private Label stackExit;
    private bool tailCalled;
    private readonly List<(Label Site, nint Call)> tailSites = [];
    private readonly Dictionary<Type, LocalBuilder> scratch = [];

    internal FunctionEmitter(CilCompiler compiler, nint function, MethodBuilder method) : base(compiler, method.GetILGenerator())
    {
        this.function = function;
        this.method = method;
        varargs = new VarArgEmitter(compiler, Il, this);
    }

    internal void Emit()
    {
        for (uint index = 0; index < Llvm.LLVMCountParams(function); index++)
            arguments[Llvm.LLVMGetParam(function, index)] = (int)index;
        bool scopedStack = false;
        bool hasAllocations = false;
        bool variadicCalls = false;
        bool mustTail = false;
        int valueCount = 0;
        foreach (nint block in Llvm.Blocks(function))
        {
            labels[block] = Il.DefineLabel();
            foreach (nint instruction in Llvm.Instructions(block))
            {
                nint type = Llvm.LLVMTypeOf(instruction);
                if (TypeSystem.Kind(type) != 0)
                    valueCount++;
                int opcode = Llvm.LLVMGetInstructionOpcode(instruction);
                hasAllocations |= opcode == 26;
                variadicCalls |= opcode is 5 or 45 && Llvm.LLVMIsFunctionVarArg(Llvm.LLVMGetCalledFunctionType(instruction)) != 0;
                if (opcode == 45 && CilCompiler.IsSetJump(Llvm.LLVMGetCalledValue(instruction)))
                    jumpSites.Add(instruction, (jumpSites.Count + 1, Il.DefineLabel()));
                if (opcode == 45)
                {
                    string name = Llvm.Name(Llvm.LLVMGetCalledValue(instruction));
                    scopedStack |= name.StartsWith("llvm.stacksave", StringComparison.Ordinal) || name.StartsWith("llvm.stackrestore", StringComparison.Ordinal);
                    mustTail |= Llvm.LLVMGetTailCallKind(instruction) == 2;
                }
            }
        }
        // A function with a musttail call takes its stack memory from the arena, never from localloc (MustTailCall). It
        // has no byval parameters: those would be byval arguments of its musttail calls, which are rejected.
        bool arenaFrame = mustTail && (hasAllocations || variadicCalls);
        if (!arenaFrame)
            varargs.Allocate(function, arena: false);
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
            usesLocalloc = true;
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
        AllocateFunctionLocals(valueCount > 32768 && jumpSites.Count == 0);
        if (scopedStack || hasAllocations && jumpSites.Count != 0 || arenaFrame)
        {
            // The thread's alloca arena top on entry, restored on every exit (BE-17).
            stackMemory = Il.DeclareLocal(typeof(nint));
            stackReturn = method.ReturnType == typeof(void) || jumpSites.Count != 0 ? null : Il.DeclareLocal(method.ReturnType);
            stackExit = Il.DefineLabel();
            Il.Emit(OpCodes.Call, typeof(StackMemory).GetMethod(nameof(StackMemory.Save))!);
            Il.Emit(OpCodes.Stloc, stackMemory);
            Il.BeginExceptionBlock();
        }
        if (arenaFrame)
            varargs.Allocate(function, arena: true);
        if (jumpSites.Count != 0)
        {
            jumpFrame = Il.DeclareLocal(typeof(long));
            jumpResume = Il.DeclareLocal(typeof(int));
            jumpReturn = method.ReturnType == typeof(void) ? null : Il.DeclareLocal(method.ReturnType);
            jumpDispatch = Il.DefineLabel();
            jumpExit = Il.DefineLabel();
            Il.Emit(OpCodes.Call, typeof(NonLocalJumps).GetMethod(nameof(NonLocalJumps.NewFrame))!);
            Il.Emit(OpCodes.Stloc, jumpFrame);
            Il.Emit(OpCodes.Ldc_I4_0);
            Il.Emit(OpCodes.Stloc, jumpResume);
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
                catch (Exception error)
                {
                    throw CompilationError.InFunction(error, function, instruction);
                }
                // A musttail call returns itself; what follows it is its ret (and possibly a pointer bitcast).
                if (tailCalled) { tailCalled = false; break; }
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
                Il.Emit(OpCodes.Ldloc, exception);
                Il.Emit(OpCodes.Callvirt, typeof(NonLocalJumpException).GetProperty(nameof(NonLocalJumpException.StackPosition))!.GetMethod!);
                Il.Emit(OpCodes.Call, typeof(StackMemory).GetMethod(nameof(StackMemory.Restore))!);
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
            Il.Emit(OpCodes.Call, typeof(StackMemory).GetMethod(nameof(StackMemory.Restore))!);
            Il.EndExceptionBlock();
            Il.MarkLabel(stackExit);
            if ((jumpReturn ?? stackReturn) is LocalBuilder result) Il.Emit(OpCodes.Ldloc, result);
            Il.Emit(OpCodes.Ret);
        }
        // Tail calls that left the protected regions: the finally above has released the arena, and the operands are
        // still in their locals.
        foreach ((Label site, nint call) in tailSites)
        {
            Il.MarkLabel(site);
            TailCall(call);
        }
        // A method with localloc (allocas, byval copies, the variadic call area) does not zero its locals and stack
        // buffers on entry (C# SkipLocalsInit): an alloca's contents are undefined in LLVM, and zeroing large stack
        // buffers on every call cost H.264 decoding 30% (PF-04). The JIT cannot replace such a method on the stack
        // anyway. Other methods keep the flag: without it, the JIT's on-stack replacement code for a long-running loop
        // ran 2.6 times slower (lpbench). Locals whose first value matters are initialized explicitly.
        method.InitLocals = !(usesLocalloc || varargs.Allocated);
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
                Load(Llvm.LLVMGetOperand(value, 0));
                Il.Emit(OpCodes.Conv_U);
                Il.Emit(OpCodes.Ldc_I8, Types.Size(allocated));
                Il.Emit(OpCodes.Conv_U);
                Il.Emit(OpCodes.Mul_Ovf_Un);
                // One spare byte: a zero-size alloca must still yield a non-null address, as on a native stack (localloc 0
                // returns null, which flang-rt reads as an unallocated zero-length character variable).
                Offset(alignment);
                if (stackMemory is not null)
                    Il.Emit(OpCodes.Call, typeof(StackMemory).GetMethod(nameof(StackMemory.Allocate))!);
                else
                {
                    Il.Emit(OpCodes.Localloc);
                    usesLocalloc = true;
                }
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
                if (Llvm.LLVMGetTailCallKind(value) == 2 && MustTailCall(value))
                {
                    tailCalled = true;
                    return;
                }
                Call(value);
                break;
            case 46:
                Select(value);
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

    /// <summary>
    /// An integer or pointer select is computed without a branch, as false ^ ((true ^ false) &amp; -condition): RyuJIT
    /// turns a branch into cmov only outside loops (ifconversion.cpp), and an unpredictable branch in a loop costs far
    /// more than these four operations (PNG's Paeth filter ran 3.4-5.7 times native time). A boolean select is the and
    /// or the or it stands for. Floating-point, 128-bit and aggregate selects keep the branch, which is exact for every
    /// value.
    /// </summary>
    private void Select(nint value)
    {
        nint condition = Llvm.LLVMGetOperand(value, 0);
        nint whenTrue = Llvm.LLVMGetOperand(value, 1);
        nint whenFalse = Llvm.LLVMGetOperand(value, 2);
        nint type = Llvm.LLVMTypeOf(value);
        Type managed = Types.Map(type);
        if (managed == typeof(int) || managed == typeof(long) || managed == typeof(nint))
        {
            bool and = TypeSystem.Width(type) == 1 && IsBoolean(whenFalse, false);
            if (and || TypeSystem.Width(type) == 1 && IsBoolean(whenTrue, true))
            {
                Load(condition);
                Load(and ? whenTrue : whenFalse);
                Il.Emit(and ? OpCodes.And : OpCodes.Or);
                return;
            }
            (nint Less, nint Greater, bool Signed, bool Inverted)? order = Order(condition);
            if (order is { Inverted: true })
                (whenTrue, whenFalse) = (whenFalse, whenTrue);
            Load(whenFalse);
            Il.Emit(OpCodes.Dup);
            Load(whenTrue);
            Il.Emit(OpCodes.Xor);
            if (order is (nint less, nint greater, bool signed, _))
                LessMask(less, greater, signed, managed);
            else
            {
                Load(condition);
                Mask(managed);
            }
            Il.Emit(OpCodes.And);
            Il.Emit(OpCodes.Xor);
            return;
        }
        Label otherwise = Il.DefineLabel();
        Label end = Il.DefineLabel();
        Load(condition);
        Il.Emit(OpCodes.Brfalse, otherwise);
        Load(whenTrue);
        Il.Emit(OpCodes.Br, end);
        Il.MarkLabel(otherwise);
        Load(whenFalse);
        Il.MarkLabel(end);
    }

    private static bool IsBoolean(nint value, bool constant) =>
        Llvm.LLVMIsAConstantInt(value) != 0 && Llvm.LLVMConstIntGetSExtValue(value) == (constant ? -1 : 0);

    /// <summary>
    /// A condition that is an ordered comparison of integers of at most 32 bits, as the operands of a strict less-than:
    /// x &gt; y is y &lt; x, and x &gt;= y is the inverse of x &lt; y. Null for any other condition.
    /// </summary>
    private static (nint Less, nint Greater, bool Signed, bool Inverted)? Order(nint condition)
    {
        if (Llvm.LLVMGetInstructionOpcode(condition) != 42)
            return null;
        nint left = Llvm.LLVMGetOperand(condition, 0), right = Llvm.LLVMGetOperand(condition, 1);
        if (TypeSystem.Width(Llvm.LLVMTypeOf(left)) is 0 or > 32)
            return null;
        return Llvm.LLVMGetICmpPredicate(condition) switch
        {
            34 => (right, left, false, false),
            35 => (left, right, false, true),
            36 => (left, right, false, false),
            37 => (right, left, false, true),
            38 => (right, left, true, false),
            39 => (left, right, true, true),
            40 => (left, right, true, false),
            41 => (right, left, true, true),
            _ => null
        };
    }

    /// <summary>
    /// All one bits of <paramref name="type"/> when <paramref name="less"/> &lt; <paramref name="greater"/>, integers
    /// of at most 32 bits, else zero: the sign of their 64-bit difference, which takes two operations where a 0 or 1
    /// comparison result takes three more (setcc, zero extension, negation) and lengthens the dependency chain.
    /// </summary>
    private void LessMask(nint less, nint greater, bool signed, Type type)
    {
        foreach (nint operand in new[] { less, greater })
        {
            Load(operand);
            if (signed)
                SignExtend(Llvm.LLVMTypeOf(operand));
            Il.Emit(signed ? OpCodes.Conv_I8 : OpCodes.Conv_U8);
        }
        Il.Emit(OpCodes.Sub);
        Il.Emit(OpCodes.Ldc_I4, 63);
        Il.Emit(OpCodes.Shr);
        if (type == typeof(int))
            Il.Emit(OpCodes.Conv_I4);
        else if (type == typeof(nint))
            Il.Emit(OpCodes.Conv_I);
    }

    /// <summary>Turns the 0 or 1 of a condition on the stack into all zero or all one bits of <paramref name="type"/>.</summary>
    private void Mask(Type type)
    {
        Il.Emit(OpCodes.Neg);
        if (type == typeof(long))
            Il.Emit(OpCodes.Conv_I8);
        else if (type == typeof(nint))
            Il.Emit(OpCodes.Conv_I);
    }

    /// <summary>A local of the function for a value used only within one instruction's lowering.</summary>
    private LocalBuilder Scratch(Type type)
    {
        if (!scratch.TryGetValue(type, out LocalBuilder? local))
            scratch.Add(type, local = Il.DeclareLocal(type));
        return local;
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
            if (operation is not (0 or 11 or 12 or 13 or 14)) throw new NotSupportedException($"Floating atomic RMW operation {operation}");
            Load(Llvm.LLVMGetOperand(instruction, 0));
            Load(Llvm.LLVMGetOperand(instruction, 1));
            Il.Emit(OpCodes.Ldc_I4, operation);
            Il.Emit(OpCodes.Call, typeof(Atomics).GetMethod(TypeSystem.Kind(type) == 2 ? nameof(Atomics.ModifySingle) : nameof(Atomics.ModifyDouble))!);
            return;
        }
        int width = TypeSystem.Kind(type) == 12 ? 64 : TypeSystem.Width(type);
        if (width == 128 && TypeSystem.Kind(type) == 8)
        {
            Load(Llvm.LLVMGetOperand(instruction, opcode == 28 ? 1u : 0u));
            if (opcode == 56)
            {
                Load(Llvm.LLVMGetOperand(instruction, 2));
                Load(Llvm.LLVMGetOperand(instruction, 1));
            }
            else if (opcode != 27)
                Load(Llvm.LLVMGetOperand(instruction, opcode == 28 ? 0u : 1u));
            if (opcode == 57)
            {
                int operation = Llvm.LLVMGetAtomicRMWBinOp(instruction);
                if (operation > 10) throw new NotSupportedException($"Atomic RMW operation {operation}");
                Il.Emit(OpCodes.Ldc_I4, operation);
            }
            string wideHelper = opcode switch { 27 => nameof(Atomics.Load128), 28 => nameof(Atomics.Store128), 56 => nameof(Atomics.CompareExchange128), _ => nameof(Atomics.Modify128) };
            Il.Emit(OpCodes.Call, typeof(Atomics).GetMethod(wideHelper)!);
            if (opcode == 56)
            {
                LocalBuilder previous = Il.DeclareLocal(typeof(UInt128));
                LocalBuilder pair = Il.DeclareLocal(Types.Map(Llvm.LLVMTypeOf(instruction)));
                Il.Emit(OpCodes.Stloc, previous);
                Il.Emit(OpCodes.Ldloca, pair);
                Il.Emit(OpCodes.Ldloc, previous);
                StoreMemory(type);
                Il.Emit(OpCodes.Ldloca, pair);
                Il.Emit(OpCodes.Conv_I);
                Offset(Types.Offset(Llvm.LLVMTypeOf(instruction), 1));
                Il.Emit(OpCodes.Ldloc, previous);
                Load(Llvm.LLVMGetOperand(instruction, 1));
                Il.Emit(OpCodes.Ldc_I4, 32);
                Il.Emit(OpCodes.Ldc_I4, 128);
                Il.Emit(OpCodes.Call, typeof(WideInteger).GetMethod(nameof(WideInteger.Compare))!);
                Il.Emit(OpCodes.Stind_I1);
                Il.Emit(OpCodes.Ldloc, pair);
            }
            return;
        }
        if (width is not (8 or 16 or 32 or 64))
            throw new NotSupportedException($"Atomic type: {Llvm.PrintType(type)}");
        if (DirectAtomic(instruction, opcode, type, width))
            return;
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
            CompareExchangeResult(instruction, type);
    }

    /// <summary>The { previous, success } pair of a cmpxchg from the previous value on the stack.</summary>
    private void CompareExchangeResult(nint instruction, nint type)
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

    /// <summary>
    /// A 32- or 64-bit atomic whose IR alignment is natural, inline: Volatile.Read for loads and Interlocked for the
    /// rest, the instructions the runtime's aligned path (Atomics) executes, without its call, width switch and
    /// alignment test (PF-02). Under-aligned atomics and the other operations keep the runtime path, which locks
    /// misaligned locations.
    /// </summary>
    private bool DirectAtomic(nint instruction, int opcode, nint type, int width)
    {
        int operation = opcode == 57 ? Llvm.LLVMGetAtomicRMWBinOp(instruction) : -1;
        if (width is not (32 or 64) || Llvm.LLVMGetAlignment(instruction) < width / 8 || TypeSystem.Kind(type) is not (8 or 12) ||
            opcode == 57 && operation is not (0 or 1 or 2 or 3 or 5))
            return false;
        Type cell = width == 32 ? typeof(int) : typeof(long);
        Type[] exchange = [cell.MakeByRefType(), cell];
        OpCode convert = width == 32 ? OpCodes.Conv_I4 : OpCodes.Conv_I8;
        Load(Llvm.LLVMGetOperand(instruction, opcode == 28 ? 1u : 0u));
        switch (opcode)
        {
            case 27:
                Il.Emit(OpCodes.Volatile);
                Il.Emit(width == 32 ? OpCodes.Ldind_I4 : OpCodes.Ldind_I8);
                break;
            case 28:
                Load(Llvm.LLVMGetOperand(instruction, 0));
                Il.Emit(convert);
                Il.Emit(OpCodes.Call, typeof(Interlocked).GetMethod(nameof(Interlocked.Exchange), exchange)!);
                Il.Emit(OpCodes.Pop);
                return true;
            case 56:
                Load(Llvm.LLVMGetOperand(instruction, 2));
                Il.Emit(convert);
                Load(Llvm.LLVMGetOperand(instruction, 1));
                Il.Emit(convert);
                Il.Emit(OpCodes.Call, typeof(Interlocked).GetMethod(nameof(Interlocked.CompareExchange), [cell.MakeByRefType(), cell, cell])!);
                break;
            default:
                // xchg, add, sub, and, or. Interlocked.Add returns the new value, so the addend is taken away again.
                Load(Llvm.LLVMGetOperand(instruction, 1));
                Il.Emit(convert);
                LocalBuilder? addend = null;
                if (operation is 1 or 2)
                {
                    if (operation == 2) Il.Emit(OpCodes.Neg);
                    addend = Il.DeclareLocal(cell);
                    Il.Emit(OpCodes.Dup);
                    Il.Emit(OpCodes.Stloc, addend);
                }
                string name = operation switch { 0 => nameof(Interlocked.Exchange), 1 or 2 => nameof(Interlocked.Add), 3 => nameof(Interlocked.And), _ => nameof(Interlocked.Or) };
                Il.Emit(OpCodes.Call, typeof(Interlocked).GetMethod(name, exchange)!);
                if (addend is not null)
                {
                    Il.Emit(OpCodes.Ldloc, addend);
                    Il.Emit(OpCodes.Sub);
                }
                break;
        }
        if (TypeSystem.Kind(type) == 12) Il.Emit(OpCodes.Conv_I);
        if (opcode == 56) CompareExchangeResult(instruction, type);
        return true;
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

    /// <summary>
    /// The native thunk returns the declaration's type; a call through a K&amp;R or another contradicting prototype
    /// (<c>extern int free();</c> calling <c>void free(void *)</c>) expects its own. Reconcile them as direct calls to
    /// translated definitions do: a value the call does not use is dropped, a value the declaration does not return is
    /// only accepted when nothing uses it, pointers and 64-bit integers convert exactly, and anything else is rejected
    /// with both signatures, never left as invalid IL.
    /// </summary>
    private void NativeReturn(nint instruction, nint target, string name, Type returned)
    {
        Type site = Types.Map(Llvm.LLVMTypeOf(instruction));
        if (site == returned) return;
        if (site == typeof(void)) { Il.Emit(OpCodes.Pop); return; }
        if (returned == typeof(void) && Llvm.LLVMGetFirstUse(instruction) == 0)
        {
            locals.Remove(instruction);
            storedScalars.Remove(instruction);
            return;
        }
        if (site == typeof(nint) && returned == typeof(long) || site == typeof(long) && returned == typeof(nint))
        {
            Il.Emit(site == typeof(nint) ? OpCodes.Conv_I : OpCodes.Conv_I8);
            return;
        }
        throw new NotSupportedException($"Incompatible call signature for native function {name}: " +
            $"{Llvm.PrintType(Llvm.LLVMGetCalledFunctionType(instruction))} versus {Llvm.PrintType(Llvm.LLVMGlobalGetValueType(target))}");
    }

    /// <summary>
    /// The operand an empty inline-assembly template returns: its constraints name one direct output, and an input with
    /// the matching constraint (the output's number) of the same type. Also whether it clobbers memory. Null for any other
    /// form, including an untied output, whose value would be undefined.
    /// </summary>
    private static (nint Input, bool Memory)? TiedBarrierInput(nint instruction, nint target)
    {
        nint text = Llvm.LLVMGetInlineAsmConstraintString(target, out nuint length);
        string[] constraints = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(text, checked((int)length))!.Split(',');
        int output = -1, operand = 0;
        nint input = 0;
        bool memory = false;
        for (int index = 0; index < constraints.Length; index++)
        {
            string constraint = constraints[index];
            if (constraint.StartsWith('~')) { memory |= constraint == "~{memory}"; continue; }
            if (constraint.StartsWith('='))
            {
                if (constraint.Contains('*')) { operand++; continue; }
                if (output >= 0) return null;
                output = index;
                continue;
            }
            if (output >= 0 && constraint == output.ToString(System.Globalization.CultureInfo.InvariantCulture))
                input = Llvm.LLVMGetOperand(instruction, (uint)operand);
            operand++;
        }
        if (input == 0 || Llvm.LLVMTypeOf(input) != Llvm.LLVMTypeOf(instruction)) return null;
        return (input, memory);
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
        if (Llvm.LLVMIsAGlobalAlias(target) != 0 && Llvm.LLVMIsAFunction(CilCompiler.ResolveAlias(target)) != 0)
            target = CilCompiler.ResolveAlias(target);
        string name = Llvm.Name(target);
        if (Llvm.LLVMIsAFunction(target) != 0 && Compiler.IsUndefinedWeak(target))
        {
            // Calling an undefined weak function is undefined behavior; trap with its name instead.
            Il.Emit(OpCodes.Ldstr, $"Called undefined weak function {name}.");
            Il.Emit(OpCodes.Newobj, typeof(InvalidOperationException).GetConstructor([typeof(string)])!);
            Il.Emit(OpCodes.Throw);
            if (TypeSystem.Kind(Llvm.LLVMTypeOf(instruction)) != 0) Zero(Llvm.LLVMTypeOf(instruction));
            return;
        }
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
            if (commentOnly && TypeSystem.Kind(Llvm.LLVMTypeOf(instruction)) != 0 && TiedBarrierInput(instruction, target) is (nint input, bool memory))
            {
                // asm("" : "+r"(x)) and asm("" : "=r"(y) : "0"(x)): an empty template whose one register output is tied
                // to an input yields that input; code uses it to hide a value from the optimizer.
                if (memory) Il.Emit(OpCodes.Call, typeof(Thread).GetMethod(nameof(Thread.MemoryBarrier))!);
                Load(input);
                return;
            }
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
                Il.Emit(OpCodes.Call, typeof(StackMemory).GetMethod(nameof(StackMemory.Save))!);
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
            NativeReturn(instruction, target, name, native.ReturnType);
            return;
        }
        nint signature = Llvm.LLVMGetCalledFunctionType(instruction);
        bool discardReturn = false;
        bool truncateBooleanReturn = false;
        Type? convertReturn = null;
        (nint From, nint To)? byteReturn = null;
        if (Llvm.LLVMIsAFunction(target) != 0 && signature != Llvm.LLVMGlobalGetValueType(target))
        {
            nint definition = Llvm.LLVMGlobalGetValueType(target);
            uint arguments = Llvm.LLVMGetNumArgOperands(instruction);
            uint parameters = Llvm.LLVMCountParamTypes(definition);
            if (Compiler.TrapMissingArguments && parameters > arguments &&
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
            truncateBooleanReturn = Llvm.LLVMGetInstructionCallConv(instruction) == 0 && Llvm.LLVMGetFunctionCallConv(target) == 0 &&
                TypeSystem.Kind(Llvm.LLVMGetReturnType(signature)) == 8 && TypeSystem.Kind(Llvm.LLVMGetReturnType(definition)) == 8 &&
                (TypeSystem.Width(Llvm.LLVMGetReturnType(signature)) == 1 && TypeSystem.Width(Llvm.LLVMGetReturnType(definition)) == 8 ||
                    TypeSystem.Width(Llvm.LLVMGetReturnType(signature)) == 8 && TypeSystem.Width(Llvm.LLVMGetReturnType(definition)) == 1);
            // Pointers and i64 share one 64-bit representation in both ABIs (musl's pthread_t is unsigned long in C++
            // but a pointer in C), so a return of one may be received as the other with an exact conversion.
            nint siteReturn = Llvm.LLVMGetReturnType(signature), definedReturn = Llvm.LLVMGetReturnType(definition);
            bool pointerReturn = TypeSystem.Kind(siteReturn) == 12 && TypeSystem.Kind(definedReturn) == 8 && TypeSystem.Width(definedReturn) == 64 ||
                TypeSystem.Kind(siteReturn) == 8 && TypeSystem.Width(siteReturn) == 64 && TypeSystem.Kind(definedReturn) == 12;
            if (pointerReturn) convertReturn = Types.Map(siteReturn);
            if (Llvm.LLVMGetInstructionCallConv(instruction) == 0 && Llvm.LLVMGetFunctionCallConv(target) == 0 && OneByte(definedReturn, siteReturn))
                byteReturn = (definedReturn, siteReturn);
            if (Llvm.LLVMIsFunctionVarArg(definition) != 0 || parameters > arguments ||
                !discardReturn && !unusedVoidReturn && !truncateBooleanReturn && !pointerReturn && byteReturn is null && Llvm.LLVMGetReturnType(definition) != Llvm.LLVMGetReturnType(signature) ||
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
                if (OneByte(Llvm.LLVMTypeOf(argument), parameter))
                {
                    ConvertOneByte(Llvm.LLVMTypeOf(argument), parameter);
                    continue;
                }
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
        if (convertReturn is not null) Il.Emit(convertReturn == typeof(nint) ? OpCodes.Conv_I : OpCodes.Conv_I8);
        if (byteReturn is (nint from, nint to)) ConvertOneByte(from, to);
    }

    /// <summary>
    /// A musttail call ([[clang::musttail]]) must not grow the stack: the callee replaces this frame, so a chain of such
    /// calls runs in constant stack, as Skia's raster pipeline relies on. It becomes a CIL tail. call and ret. The JIT
    /// always honors that, with a fast tail call that reuses the frame or else through the runtime's tail-call helpers,
    /// but NativeAOT has no helpers and needs a fast tail call, which x64 refuses from a method that uses localloc. So a
    /// function with a musttail call takes its allocas and variadic call area from the thread's arena (StackMemory), and
    /// its tail calls first leave the try/finally that releases it: LLVM does not let the callee use the caller's
    /// allocas. What a fast tail call cannot carry is rejected: arguments and results of more than 16 bytes, which
    /// Windows x64 and ARM64 pass by reference to a copy in the caller's frame, byval arguments, whose copy is in the
    /// caller's frame, forwarded variadic arguments, and calls through a prototype other than the callee's. Windows x64
    /// also passes 16-byte values (vectors, long double, i128) by reference, so there NativeAOT makes a call with one an
    /// ordinary call; Skia's skcms passes &lt;4 x float&gt;. Returns false for the calls made as ordinary calls: an
    /// undefined weak callee, which traps, and in system mode a native callee.
    /// </summary>
    private bool MustTailCall(nint instruction)
    {
        nint target = Llvm.LLVMGetCalledValue(instruction);
        if (Llvm.LLVMIsAGlobalAlias(target) != 0 && Llvm.LLVMIsAFunction(CilCompiler.ResolveAlias(target)) != 0)
            target = CilCompiler.ResolveAlias(target);
        bool direct = Llvm.LLVMIsAFunction(target) != 0;
        if (direct && (Compiler.IsUndefinedWeak(target) || Compiler.Host is not null && Compiler.Host.IsSystem(target)))
            return false;
        string name = direct ? Llvm.Name(target) : "a function pointer";
        nint signature = Llvm.LLVMGetCalledFunctionType(instruction);
        if (Llvm.LLVMIsAInlineAsm(target) != 0 || name.StartsWith("llvm.", StringComparison.Ordinal) || jumpSites.ContainsKey(instruction))
            throw new NotSupportedException($"musttail call to {name}: only calls to functions can be tail calls.");
        if (Llvm.LLVMIsFunctionVarArg(signature) != 0)
            throw new NotSupportedException($"musttail call to {name}: calls that forward variadic arguments cannot be tail calls.");
        if (direct && signature != Llvm.LLVMGlobalGetValueType(target))
            throw new NotSupportedException($"musttail call to {name} through another prototype: {Llvm.PrintType(signature)} versus {Llvm.PrintType(Llvm.LLVMGlobalGetValueType(target))}");
        nint[] types = [Llvm.LLVMGetReturnType(signature), .. Enumerable.Range(0, checked((int)Llvm.LLVMCountParamTypes(signature)))
            .Select(index => Llvm.LLVMTypeOf(Llvm.LLVMGetOperand(instruction, (uint)index)))];
        if (Types.Map(types[0]) != method.ReturnType || types.Any(type => !CilCompiler.NativeScalar(Types.Map(type)) && Types.Size(type) > 16))
            throw new NotSupportedException($"musttail call to {name} with type {Llvm.PrintType(signature)}: arguments and results of more than 16 bytes " +
                "cannot be tail calls, because Windows x64 and ARM64 pass them by reference to a copy in the caller's frame.");
        uint byvalKind = Llvm.LLVMGetEnumAttributeKindForName("byval", 5);
        for (uint index = 0; index < Llvm.LLVMGetNumArgOperands(instruction); index++)
            if (Llvm.LLVMGetCallSiteEnumAttribute(instruction, index + 1, byvalKind) != 0)
                throw new NotSupportedException($"musttail call to {name}: a byval argument cannot be a tail call, because its copy is in the caller's frame.");
        if (stackMemory is null && jumpFrame is null)
            TailCall(instruction);
        else
        {
            Label site = Il.DefineLabel();
            Il.Emit(OpCodes.Leave, site);
            tailSites.Add((site, instruction));
        }
        return true;
    }

    /// <summary>The call of <see cref="MustTailCall"/>: its operands, a tail. call or calli, and ret.</summary>
    private void TailCall(nint instruction)
    {
        nint target = Llvm.LLVMGetCalledValue(instruction);
        if (Llvm.LLVMIsAGlobalAlias(target) != 0 && Llvm.LLVMIsAFunction(CilCompiler.ResolveAlias(target)) != 0)
            target = CilCompiler.ResolveAlias(target);
        nint signature = Llvm.LLVMGetCalledFunctionType(instruction);
        for (uint index = 0; index < Llvm.LLVMGetNumArgOperands(instruction); index++)
            Load(Llvm.LLVMGetOperand(instruction, index));
        Type returnType = Types.Map(Llvm.LLVMGetReturnType(signature));
        Type[] parameters = Types.Parameters(signature);
        if (Llvm.LLVMIsAFunction(target) != 0)
        {
            Il.Emit(OpCodes.Tailcall);
            Il.Emit(OpCodes.Call, Compiler.Methods.TryGetValue(target, out MethodInfo? callee) ? callee : Compiler.ResolveFunction(target));
        }
        else
        {
            Load(target);
            if (Compiler.Host is null)
                ManagedCalli(returnType, parameters, tail: true);
            else
            {
                // System mode: a pointer to a native function is called as Call calls it, without the guarantee.
                LocalBuilder managedTarget = Il.DeclareLocal(typeof(nint));
                Label native = Il.DefineLabel();
                Il.Emit(OpCodes.Dup);
                Il.Emit(OpCodes.Call, typeof(SystemAbi).GetMethod(nameof(SystemAbi.ResolveCallback))!);
                Il.Emit(OpCodes.Stloc, managedTarget);
                Il.Emit(OpCodes.Ldloc, managedTarget);
                Il.Emit(OpCodes.Brfalse, native);
                Il.Emit(OpCodes.Pop);
                Il.Emit(OpCodes.Ldloc, managedTarget);
                ManagedCalli(returnType, parameters, tail: true);
                Il.Emit(OpCodes.Ret);
                Il.MarkLabel(native);
                if (CilCompiler.NativeScalar(returnType) && parameters.All(CilCompiler.NativeScalar))
                    Il.EmitCalli(OpCodes.Calli, System.Runtime.InteropServices.CallingConvention.Cdecl, returnType, parameters);
                else
                {
                    for (int index = 0; index <= parameters.Length; index++) Il.Emit(OpCodes.Pop);
                    Il.Emit(OpCodes.Ldstr, "Indirect native aggregate calls are not supported; compile the callback to bitcode.");
                    Il.Emit(OpCodes.Newobj, typeof(NotSupportedException).GetConstructor([typeof(string)])!);
                    Il.Emit(OpCodes.Throw);
                    return;
                }
            }
        }
        Il.Emit(OpCodes.Ret);
    }

    private static bool ByteArray(nint type) => TypeSystem.Kind(type) == 11 && Llvm.LLVMGetArrayLength2(type) == 1 &&
        TypeSystem.Kind(Llvm.LLVMGetElementType(type)) == 8 && TypeSystem.Width(Llvm.LLVMGetElementType(type)) == 8;

    /// <summary>i8 and [1 x i8], which x86-64 passes and returns in the same register: Fortran passes character(1) by value
    /// as the array where C takes char.</summary>
    private static bool OneByte(nint first, nint second) =>
        ByteArray(first) && TypeSystem.Kind(second) == 8 && TypeSystem.Width(second) == 8 ||
        ByteArray(second) && TypeSystem.Kind(first) == 8 && TypeSystem.Width(first) == 8;

    /// <summary>Converts the value on the stack between i8 and [1 x i8] through a temporary of the one-byte aggregate.</summary>
    private void ConvertOneByte(nint from, nint to)
    {
        if (ByteArray(to))
        {
            LocalBuilder value = Il.DeclareLocal(typeof(int)), aggregate = Il.DeclareLocal(Types.Map(to));
            Il.Emit(OpCodes.Stloc, value);
            Il.Emit(OpCodes.Ldloca, aggregate);
            Il.Emit(OpCodes.Ldloc, value);
            Il.Emit(OpCodes.Stind_I1);
            Il.Emit(OpCodes.Ldloc, aggregate);
        }
        else
        {
            LocalBuilder aggregate = Il.DeclareLocal(Types.Map(from));
            Il.Emit(OpCodes.Stloc, aggregate);
            Il.Emit(OpCodes.Ldloca, aggregate);
            Il.Emit(OpCodes.Ldind_U1);
        }
    }

    private void ManagedCalli(Type returnType, Type[] parameters, bool tail = false)
    {
        Compiler.ManagedCalli(Il, method, returnType, parameters, tail);
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
        // Both ABIs follow x86-64's register-slot behavior for these mismatches: pointer/i64 interchange,
        // i64 arguments narrowed to their low 32 bits and i32 arguments zero-extended in the six register positions, and
        // i1/i8. Flang passes hidden character lengths as i64 to C functions declared with int, for example.
        nint returned = Llvm.LLVMGetReturnType(Llvm.LLVMGlobalGetValueType(target));
        if (Llvm.LLVMGetInstructionCallConv(instruction) != 0 || Llvm.LLVMGetFunctionCallConv(target) != 0 ||
            !(integerWidths || booleanWidths || pointerBits || OneByte(source, destination)) ||
            integerWidths && TypeSystem.Width(source) == 32 && index >= 6 || TypeSystem.Kind(returned) is 10 or 11 && !ByteArray(returned))
            return false;
        for (uint parameter = 0; parameter <= index; parameter++)
        {
            nint type = Llvm.LLVMTypeOf(Llvm.LLVMGetParam(target, parameter));
            if (TypeSystem.Kind(type) is not (2 or 3 or 12) && !(TypeSystem.Kind(type) == 8 && TypeSystem.Width(type) <= 64) && !ByteArray(type)) return false;
            foreach (string attribute in new[] { "byval", "sret", "inreg", "signext" })
            {
                // C's char parameter is signext; the one-byte conversion delivers the byte, which the callee extends itself.
                if (attribute == "signext" && OneByte(Llvm.LLVMTypeOf(Llvm.LLVMGetOperand(instruction, parameter)), type)) continue;
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
            Il.Emit(OpCodes.Call, typeof(MathIntrinsics).GetMethod(nameof(MathIntrinsics.GetRounding))!);
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
            bool restore = name.StartsWith("llvm.stackrestore", StringComparison.Ordinal);
            if (restore) Load(Llvm.LLVMGetOperand(instruction, 0));
            Il.Emit(OpCodes.Call, typeof(StackMemory).GetMethod(restore ? nameof(StackMemory.Restore) : nameof(StackMemory.Save))!);
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
        if (name == "llvm.readcyclecounter")
        {
            Il.Emit(OpCodes.Call, typeof(Numeric).GetMethod(nameof(Numeric.CycleCounter))!);
            return;
        }
        if (name is "llvm.trap" or "llvm.debugtrap" or "llvm.ubsantrap")
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
        if (width > 64 && operation is "fptosi" or "fptoui" && name.Contains(".sat.", StringComparison.Ordinal))
        {
            nint operand = Llvm.LLVMGetOperand(instruction, 0);
            if (TypeSystem.Kind(Llvm.LLVMTypeOf(operand)) is not (2 or 3))
                throw new NotSupportedException($"Saturated conversion type: {name}");
            Load(operand);
            Il.Emit(OpCodes.Conv_R8);
            Il.Emit(OpCodes.Ldc_I4, operation == "fptosi" ? 1 : 0);
            Il.Emit(OpCodes.Ldc_I4, width);
            Il.Emit(OpCodes.Call, typeof(WideInteger).GetMethod(nameof(WideInteger.FromFloatingSaturated))!);
            return;
        }
        int wideCode = width <= 64 ? -1 : operation switch
        {
            "abs" => 0,
            "smin" => 1,
            "smax" => 2,
            "umin" => 3,
            "umax" => 4,
            "bitreverse" => 5,
            "fshl" => 6,
            "fshr" => 7,
            "sadd" when name.Contains(".sat.", StringComparison.Ordinal) => 8,
            "uadd" when name.Contains(".sat.", StringComparison.Ordinal) => 9,
            "ssub" when name.Contains(".sat.", StringComparison.Ordinal) => 10,
            "usub" when name.Contains(".sat.", StringComparison.Ordinal) => 11,
            _ => -1
        };
        if (wideCode >= 0)
        {
            int operands = wideCode is 0 or 5 ? 1 : wideCode is 6 or 7 ? 3 : 2;
            for (uint index = 0; index < 3; index++)
            {
                if (index < operands) Load(Llvm.LLVMGetOperand(instruction, index));
                else Zero(Llvm.LLVMTypeOf(instruction));
            }
            Il.Emit(OpCodes.Ldc_I4, wideCode);
            Il.Emit(OpCodes.Ldc_I4, width);
            Il.Emit(OpCodes.Call, typeof(WideInteger).GetMethod(nameof(WideInteger.Operation))!);
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
            Il.Emit(OpCodes.Call, typeof(MathIntrinsics).GetMethod(frexp ? nameof(MathIntrinsics.Frexp) : nameof(MathIntrinsics.Modf))!);
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
            Il.Emit(OpCodes.Call, typeof(MathIntrinsics).GetMethod(Types.Map(Llvm.LLVMTypeOf(instruction)) == typeof(float) ? nameof(MathIntrinsics.LdexpF) : nameof(MathIntrinsics.Ldexp))!);
            return;
        }
        if (TypeSystem.Kind(Llvm.LLVMTypeOf(instruction)) == 4)
        {
            // llvm.fmuladd may fuse or not; x87 has no fused multiply-add, so native code rounds the product first.
            string helper = operation switch { "fmuladd" => nameof(Float80.MultiplyAdd), "fma" => nameof(Float80.FusedMultiplyAdd), "fabs" => nameof(Float80.Abs), "copysign" => nameof(Float80.CopySign), "floor" => nameof(Float80.Floor), "maxnum" => nameof(Float80.MaxNumber), "minnum" => nameof(Float80.MinNumber), _ => throw new NotSupportedException($"Unsupported extended floating intrinsic: {name}") };
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
        if (operation is "sshl" or "ushl" && name.Contains(".sat.", StringComparison.Ordinal))
        {
            nint type = Llvm.LLVMTypeOf(instruction);
            bool signed = operation[0] == 's';
            Il.Emit(OpCodes.Ldc_I4, signed ? 1 : 0);
            Il.Emit(OpCodes.Ldc_I4, width);
            Load(Llvm.LLVMGetOperand(instruction, 0));
            if (signed) SignExtend(type);
            Il.Emit(signed ? OpCodes.Conv_I8 : OpCodes.Conv_U8);
            Load(Llvm.LLVMGetOperand(instruction, 1));
            Il.Emit(OpCodes.Conv_U8);
            Il.Emit(OpCodes.Call, typeof(Numeric).GetMethod(nameof(Numeric.ShiftSaturate))!);
            if (width <= 32) Il.Emit(OpCodes.Conv_I4);
            Normalize(type);
            return;
        }
        if (operation == "canonicalize" && TypeSystem.Kind(Llvm.LLVMTypeOf(instruction)) is 2 or 3)
        {
            Load(Llvm.LLVMGetOperand(instruction, 0));
            Il.Emit(OpCodes.Call, typeof(Numeric).GetMethod(TypeSystem.Kind(Llvm.LLVMTypeOf(instruction)) == 2 ? nameof(Numeric.CanonicalizeSingle) : nameof(Numeric.Canonicalize))!);
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
        if (operation is "lrint" or "llrint" or "lround" or "llround" && TypeSystem.Kind(Llvm.LLVMTypeOf(Llvm.LLVMGetOperand(instruction, 0))) == 4)
        {
            Load(Llvm.LLVMGetOperand(instruction, 0));
            Il.Emit(OpCodes.Ldc_I4, operation.Contains("round", StringComparison.Ordinal) ? 1 : 0);
            Il.Emit(OpCodes.Ldc_I4, width);
            Il.Emit(OpCodes.Call, typeof(Float80).GetMethod(nameof(Float80.RoundToInteger))!);
            if (width <= 32) Il.Emit(OpCodes.Conv_I4);
            return;
        }
        if (operation is "lrint" or "llrint" or "lround" or "llround")
        {
            nint operand = Llvm.LLVMGetOperand(instruction, 0);
            bool single = Types.Map(Llvm.LLVMTypeOf(operand)) == typeof(float);
            bool away = operation.Contains("round", StringComparison.Ordinal);
            Load(operand);
            Il.Emit(OpCodes.Call, typeof(MathIntrinsics).GetMethod(away ? single ? nameof(MathIntrinsics.LroundF) : nameof(MathIntrinsics.Lround) : single ? nameof(MathIntrinsics.LrintF) : nameof(MathIntrinsics.Lrint))!);
            if (width <= 32)
                Il.Emit(OpCodes.Conv_I4);
            return;
        }
        if (name.StartsWith("llvm.vector.reduce.fmax.", StringComparison.Ordinal) || name.StartsWith("llvm.vector.reduce.fmin.", StringComparison.Ordinal) ||
            name.StartsWith("llvm.vector.reduce.fmaximum.", StringComparison.Ordinal) || name.StartsWith("llvm.vector.reduce.fminimum.", StringComparison.Ordinal))
        {
            // expand-reductions expands the floating-point minimum and maximum reductions only with nnan; the others fold
            // here lane by lane with the scalar operation's semantics (LLVM leaves the order unspecified): fmax and fmin
            // as maxnum and minnum, fmaximum and fminimum as maximum and minimum.
            nint vector = Llvm.LLVMGetOperand(instruction, 0);
            nint vectorType = Llvm.LLVMTypeOf(vector);
            nint element = Llvm.LLVMGetElementType(vectorType);
            Type scalar = Types.Map(element);
            if (scalar != typeof(float) && scalar != typeof(double))
                throw new NotSupportedException($"Unsupported intrinsic: {name}");
            string reduction = name["llvm.vector.reduce.".Length..name.IndexOf('.', "llvm.vector.reduce.".Length)];
            MethodInfo combine = scalar.GetMethod(reduction switch { "fmax" => "MaxNumber", "fmin" => "MinNumber", "fmaximum" => "Max", _ => "Min" }, [scalar, scalar])!;
            LocalBuilder copy = Il.DeclareLocal(Types.Map(vectorType));
            Load(vector);
            Il.Emit(OpCodes.Stloc, copy);
            uint lanes = Llvm.LLVMGetVectorSize(vectorType);
            for (uint lane = 0; lane < lanes; lane++)
            {
                Il.Emit(OpCodes.Ldloca, copy);
                Il.Emit(OpCodes.Conv_U);
                if (lane > 0)
                {
                    Il.Emit(OpCodes.Ldc_I4, checked((int)(lane * Types.Size(element))));
                    Il.Emit(OpCodes.Add);
                }
                Il.Emit(scalar == typeof(float) ? OpCodes.Ldind_R4 : OpCodes.Ldind_R8);
                if (lane > 0) Il.Emit(OpCodes.Call, combine);
            }
            return;
        }
        if (name.StartsWith("llvm.is.fpclass.", StringComparison.Ordinal))
        {
            nint operand = Llvm.LLVMGetOperand(instruction, 0);
            Load(operand);
            Load(Llvm.LLVMGetOperand(instruction, 1));
            Type classified = Types.Map(Llvm.LLVMTypeOf(operand));
            Il.Emit(OpCodes.Call, classified == typeof(Float80) ? typeof(Float80).GetMethod(nameof(Float80.Class))! :
                typeof(Numeric).GetMethod(classified == typeof(float) ? nameof(Numeric.FloatClass32) : nameof(Numeric.FloatClass64))!);
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
            Il.Emit(OpCodes.Call, typeof(MathIntrinsics).GetMethod(Types.Map(Llvm.LLVMTypeOf(instruction)) == typeof(float) ? nameof(MathIntrinsics.RoundF) : nameof(MathIntrinsics.Round))!);
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
        if (operation is "fshl" or "fshr" && width is 32 or 64)
        {
            // A rotate (both halves the same value) is BitOperations.RotateLeft/RotateRight, a JIT intrinsic (rol/ror);
            // other funnel shifts call the helper of their width. The width-generic Funnel divides by the width,
            // which only disappears where the call is inlined, and NativeAOT did not inline it (PF-06).
            nint high = Llvm.LLVMGetOperand(instruction, 0), low = Llvm.LLVMGetOperand(instruction, 1);
            Type word = width == 32 ? typeof(uint) : typeof(ulong);
            Load(high);
            if (high != low) Load(low);
            Load(Llvm.LLVMGetOperand(instruction, 2));
            if (high == low)
            {
                if (width == 64) Il.Emit(OpCodes.Conv_I4);
                Il.Emit(OpCodes.Call, typeof(System.Numerics.BitOperations).GetMethod(operation == "fshl" ? "RotateLeft" : "RotateRight", [word, typeof(int)])!);
            }
            else
                Il.Emit(OpCodes.Call, typeof(Numeric).GetMethod((operation == "fshl" ? "FunnelLeft" : "FunnelRight") + width)!);
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
        // Without branches, as Select explains: right ^ ((left ^ right) & -(left < right)) for smin, and
        // (x ^ s) - s with s = x >> (bits - 1) for abs, which wraps the minimum to itself.
        if (operation is "smin" or "smax" or "umin" or "umax")
        {
            nint left = Llvm.LLVMGetOperand(instruction, 0);
            nint right = Llvm.LLVMGetOperand(instruction, 1);
            nint type = Llvm.LLVMTypeOf(left);
            Load(right);
            Load(left);
            Load(right);
            Il.Emit(OpCodes.Xor);
            bool minimum = operation.EndsWith("min", StringComparison.Ordinal);
            if (TypeSystem.Width(type) <= 32)
                LessMask(minimum ? left : right, minimum ? right : left, operation[0] == 's', Types.Map(type));
            else
            {
                Load(left);
                if (operation[0] == 's')
                    SignExtend(type);
                Load(right);
                if (operation[0] == 's')
                    SignExtend(type);
                Il.Emit(operation switch { "smin" => OpCodes.Clt, "smax" => OpCodes.Cgt, "umin" => OpCodes.Clt_Un, _ => OpCodes.Cgt_Un });
                Mask(Types.Map(type));
            }
            Il.Emit(OpCodes.And);
            Il.Emit(OpCodes.Xor);
            return;
        }
        if (operation == "abs")
        {
            nint operand = Llvm.LLVMGetOperand(instruction, 0);
            nint type = Llvm.LLVMTypeOf(operand);
            Type managed = Types.Map(type);
            LocalBuilder extended = Scratch(managed);
            Load(operand);
            SignExtend(type);
            Il.Emit(OpCodes.Stloc, extended);
            Il.Emit(OpCodes.Ldloc, extended);
            Il.Emit(OpCodes.Ldloc, extended);
            Il.Emit(OpCodes.Ldc_I4, managed == typeof(long) ? 63 : 31);
            Il.Emit(OpCodes.Shr);
            Il.Emit(OpCodes.Xor);
            Il.Emit(OpCodes.Ldloc, extended);
            Il.Emit(OpCodes.Ldc_I4, managed == typeof(long) ? 63 : 31);
            Il.Emit(OpCodes.Shr);
            Il.Emit(OpCodes.Sub);
            Normalize(type);
            return;
        }
        string? mathName = operation switch
        {
            "fabs" => "Abs",
            "sqrt" => "Sqrt",
            "sin" => "Sin",
            "cos" => "Cos",
            "atan" => "Atan",
            "atan2" => "Atan2",
            "asin" => "Asin",
            "acos" => "Acos",
            "tan" => "Tan",
            "sinh" => "Sinh",
            "cosh" => "Cosh",
            "tanh" => "Tanh",
            "exp" => "Exp",
            "exp2" => "Exp2",
            "exp10" => "Exp10",
            "log" => "Log",
            "log2" => "Log2",
            "log10" => "Log10",
            "pow" => "Pow",
            "floor" => "Floor",
            "ceil" => "Ceiling",
            "trunc" => "Truncate",
            "roundeven" or "rint" or "nearbyint" => "Round",
            "fma" or "fmuladd" => "FusedMultiplyAdd",
            "copysign" => "CopySign",
            "minnum" => "MinNumber",
            "maxnum" => "MaxNumber",
            // IEEE 754-2019 minimumNumber/maximumNumber: NaN-avoiding with -0 ordered below +0, as .NET implements.
            "minimumnum" => "MinNumber",
            "maximumnum" => "MaxNumber",
            "minimum" => "Min",
            "maximum" => "Max",
            _ => null
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
