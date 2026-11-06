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
    // The IL locals once the values have theirs (for ManagedCalli).
    private int localCount;
    private readonly List<(Label Site, nint Call)> tailSites = [];
    private readonly Dictionary<Type, LocalBuilder> scratch = [];
    // Static allocas in frame slots (FrameSlots): the slot and the alignment its address is rounded to.
    private readonly Dictionary<nint, (LocalBuilder Slot, int Alignment)> frameSlots = [];
    // Values computed where their one use loads them (ChooseUseSiteValues), and the one being computed there.
    private readonly HashSet<nint> atUse = [];
    private nint computing;

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
                if (opcode == 45 && CilCompiler.IsSetJump(Llvm.LLVMGetCalledValue(instruction)) &&
                    Llvm.LLVMGetNumArgOperands(instruction) != 0)
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
        bool byValueParameters = Enumerable.Range(0, (int)Llvm.LLVMCountParams(function)).Any(index => Llvm.ParameterAttribute(function, (uint)index, "byval") != 0);
        // The variadic call area, static allocas and byval copies take frame slots instead of localloc, except under the
        // JIT in a method with a loop: localloc keeps such a method out of on-stack replacement (the JIT compiles it
        // optimized at once), whose code runs slower in a method without localsinit (lpbench, PF-04); NativeAOT has no
        // on-stack replacement.
        bool slots = !arenaFrame && (hasAllocations || variadicCalls || byValueParameters) && (Compiler.NativeAot || !HasLoop());
        if (!arenaFrame)
            varargs.Allocate(function, arena: false, slot: slots);
        if (slots && hasAllocations)
            FrameSlots();
        for (uint index = 0; index < Llvm.LLVMCountParams(function); index++)
        {
            nint byValue = Llvm.ParameterAttribute(function, index, "byval");
            if (byValue == 0) continue;
            nint type = Llvm.LLVMGetTypeAttributeValue(byValue);
            nint explicitAlignment = Llvm.ParameterAttribute(function, index, "align");
            int alignment = Math.Max(Types.Alignment(type), explicitAlignment == 0 ? 1 : checked((int)Llvm.LLVMGetEnumAttributeValue(explicitAlignment)));
            if (slots && Types.Size(type) <= FrameSlotLimit)
            {
                // The copy in a frame slot, whose address each use takes, as for a static alloca.
                var frame = (Il.DeclareLocal(Types.Block(checked((int)Math.Max(1, Types.Size(type)) + (alignment > 8 ? alignment - 1 : 0)))), alignment);
                frameSlots[Llvm.LLVMGetParam(function, index)] = frame;
                SlotAddress(frame);
            }
            else
            {
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
                locals[Llvm.LLVMGetParam(function, index)] = copy;
            }
            Il.Emit(OpCodes.Ldarg, checked((short)index));
            Il.Emit(OpCodes.Ldc_I8, Types.Size(type));
            Il.Emit(OpCodes.Call, typeof(Memory).GetMethod(nameof(Memory.Copy))!);
            Il.Emit(OpCodes.Pop);
        }
        if (jumpSites.Count == 0 && valueCount <= 32768) ChooseUseSiteValues();
        if (jumpSites.Count == 0 && valueCount is > 512 and <= 32768) ShareFunctionLocals();
        else AllocateFunctionLocals(valueCount > 32768 && jumpSites.Count == 0);
        localCount = locals.Values.Concat(storedScalars.Values.Select(storage => storage.Array)).Select(local => local.LocalIndex + 1).DefaultIfEmpty(0).Max();
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
            varargs.Allocate(function, arena: true, slot: false);
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
        method.InitLocals = !(usesLocalloc || varargs.Allocated || frameSlots.Count != 0);
    }

    /// <summary>The address of a frame slot, rounded up to its alignment where that is above RyuJIT's 8 bytes.</summary>
    private void SlotAddress((LocalBuilder Slot, int Alignment) frame)
    {
        Il.Emit(OpCodes.Ldloca, frame.Slot);
        Il.Emit(OpCodes.Conv_U);
        if (frame.Alignment <= 8) return;
        Offset(frame.Alignment - 1);
        Il.Emit(OpCodes.Ldc_I8, -(long)frame.Alignment);
        Il.Emit(OpCodes.Conv_I);
        Il.Emit(OpCodes.And);
    }

    /// <summary>The bytes a frame slot of a static alloca or byval copy holds at most; a larger one keeps localloc.</summary>
    private const long FrameSlotLimit = 65536;

    /// <summary>
    /// Gives each static alloca (in the entry block, of a constant size) a frame slot: a local of a value type of its
    /// size, whose address each use takes (ldloca), as LLVM gives static allocas a fixed place in the frame. With localloc,
    /// RyuJIT gives the method a frame pointer, a stack probe and NativeAOT's stack cookie, and turns a constant block of
    /// up to 32 bytes into a local that keeps the cookie (an unsafe buffer); the address also took a register for the
    /// whole method (PF-24). A slot needs no zeroing: an alloca's contents are undefined.
    /// </summary>
    private void FrameSlots()
    {
        foreach (nint instruction in Llvm.Instructions(Llvm.Blocks(function).First()))
        {
            if (Llvm.LLVMGetInstructionOpcode(instruction) != 26) continue;
            nint count = Llvm.LLVMGetOperand(instruction, 0);
            if (Llvm.LLVMIsAConstantInt(count) == 0 || TypeSystem.Width(Llvm.LLVMTypeOf(count)) > 64) continue;
            nint allocated = Llvm.LLVMGetAllocatedType(instruction);
            long elements = Llvm.LLVMConstIntGetSExtValue(count), size = Types.Size(allocated);
            if (elements < 0 || size != 0 && elements > FrameSlotLimit / size || elements * size > FrameSlotLimit) continue;
            int alignment = Math.Max(Types.Alignment(allocated), checked((int)Llvm.LLVMGetAlignment(instruction)));
            // RyuJIT aligns a local of 8 bytes or more to 8 on 64-bit targets; a stricter alignment rounds the address up
            // (SlotAddress).
            int spare = alignment > 8 ? alignment - 1 : 0;
            frameSlots[instruction] = (Il.DeclareLocal(Types.Block(checked((int)Math.Max(1, elements * size) + spare))), alignment);
            atUse.Add(instruction);
        }
    }

    /// <summary>Whether a branch of the function goes back to its own block or an earlier one: a loop in the IL, whose
    /// blocks are in the function's order.</summary>
    private bool HasLoop()
    {
        var order = new Dictionary<nint, int>();
        foreach (nint block in Llvm.Blocks(function))
            order[block] = order.Count;
        foreach ((nint block, int index) in order)
        {
            nint terminator = Llvm.Instructions(block).Last();
            for (uint successor = 0; successor < Llvm.LLVMGetNumSuccessors(terminator); successor++)
                if (order[Llvm.LLVMGetSuccessor(terminator, successor)] <= index)
                    return true;
        }
        return false;
    }

    internal override void Load(nint value)
    {
        if (atUse.Contains(value))
        {
            nint outer = computing;
            computing = value;
            Instruction(Llvm.LLVMGetInstructionParent(value), value);
            computing = outer;
            return;
        }
        if (frameSlots.TryGetValue(value, out var frame))
            SlotAddress(frame);
        else if (locals.TryGetValue(value, out LocalBuilder? local))
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
        if (value != computing && atUse.Contains(value))
            return;
        int opcode = Llvm.LLVMGetInstructionOpcode(value);
        switch (opcode)
        {
            case 1:
                if (method.ReturnType != typeof(void))
                {
                    Load(Llvm.LLVMGetOperand(value, 0));
                    if (Types.ReturnsBytes(Llvm.LLVMTypeOf(Llvm.LLVMGetOperand(value, 0)))) Vectors.ToBoundary(Llvm.LLVMTypeOf(Llvm.LLVMGetOperand(value, 0)));
                }
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
                if (TypeSystem.Width(Llvm.LLVMTypeOf(Llvm.LLVMGetOperand(value, 0))) <= 64)
                {
                    Switch(block, value);
                    return;
                }
                for (uint index = 1; index < Llvm.LLVMGetNumSuccessors(value); index++)
                {
                    Label next = Il.DefineLabel();
                    nint condition = Llvm.LLVMGetOperand(value, 0);
                    Load(condition);
                    Load(Llvm.LLVMGetSwitchCaseValue(value, index));
                    Il.Emit(OpCodes.Ldc_I4, 32);
                    Il.Emit(OpCodes.Ldc_I4, TypeSystem.Width(Llvm.LLVMTypeOf(condition)));
                    Il.Emit(OpCodes.Call, typeof(WideInteger).GetMethod(nameof(WideInteger.Compare))!);
                    Il.Emit(OpCodes.Brfalse, next);
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
            // The shift and the and that a multiplication's lowering computes from their operand (VectorEmitter.HalfSigns).
            case 21 or 23 when Types.Vector(Llvm.LLVMTypeOf(value)) is not null && Vectors.Fused(value):
                return;
            case >= 8 and <= 25:
                Binary(value, opcode);
                break;
            case 26:
                if (frameSlots.TryGetValue(value, out var frame))
                {
                    SlotAddress(frame);
                    break;
                }
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
            case 42 when Types.Vector(Llvm.LLVMTypeOf(value)) is not null:
            case 43 when Types.Vector(Llvm.LLVMTypeOf(value)) is not null:
                Vectors.Compare(value);
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
            case 50 when Types.Vector(Llvm.LLVMTypeOf(Llvm.LLVMGetOperand(value, 0))) is not null:
                Vectors.Extract(value);
                break;
            // A chain of insertelements is built at its last insert, by the typed lowering only: without typed vectors
            // (--vector-lowering=scalar) each insert writes its lane.
            case 51 when Types.Vector(Llvm.LLVMTypeOf(value)) is not null && VectorEmitter.Chained(value):
                return;
            case 51 when Types.Vector(Llvm.LLVMTypeOf(value)) is not null:
                Vectors.Insert(value);
                break;
            case 50:
            case 51:
                VectorElement(value, opcode == 51);
                break;
            case 52:
                if (Types.Vector(Llvm.LLVMTypeOf(value)) is null)
                    throw new NotSupportedException($"Unsupported vector shuffle: {Llvm.Print(value)}");
                Vectors.Shuffle(value);
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
            case 66 when Types.Vector(Llvm.LLVMTypeOf(value)) is not null:
                Vectors.Negate(value);
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
    /// value, and so does a select on a loop-carried chain (<see cref="LoopCarried"/>). Integer min/max against a
    /// constant keep theirs too (Intrinsic explains why).
    /// </summary>
    private void Select(nint value)
    {
        nint condition = Llvm.LLVMGetOperand(value, 0);
        nint whenTrue = Llvm.LLVMGetOperand(value, 1);
        nint whenFalse = Llvm.LLVMGetOperand(value, 2);
        nint type = Llvm.LLVMTypeOf(value);
        if (Types.Vector(Llvm.LLVMTypeOf(condition)) is not null)
        {
            Vectors.Select(value);
            return;
        }
        Type managed = Types.Map(type);
        if (managed == typeof(int) || managed == typeof(long) || managed == typeof(nint))
        {
            bool and = TypeSystem.Width(type) == 1 && IsConstant(whenFalse, 0);
            if (and || TypeSystem.Width(type) == 1 && IsConstant(whenTrue, -1))
            {
                Load(condition);
                Load(and ? whenTrue : whenFalse);
                Il.Emit(and ? OpCodes.And : OpCodes.Or);
                return;
            }
            if (LoopCarried(value) && Running(condition))
            {
                Carry(() => Load(condition), whenTrue, whenFalse, managed);
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
        Branch(() => Load(condition), whenTrue, whenFalse);
    }

    /// <summary>A select as a branch on the condition <paramref name="condition"/> pushes (nonzero for <paramref name="whenTrue"/>).</summary>
    private void Branch(Action condition, nint whenTrue, nint whenFalse)
    {
        Label otherwise = Il.DefineLabel();
        Label end = Il.DefineLabel();
        condition();
        Il.Emit(OpCodes.Brfalse, otherwise);
        Load(whenTrue);
        Il.Emit(OpCodes.Br, end);
        Il.MarkLabel(otherwise);
        Load(whenFalse);
        Il.MarkLabel(end);
    }

    /// <summary>
    /// A select on a loop-carried chain (<see cref="LoopCarried"/>) as a branch around its running value, which it almost
    /// always keeps: the other value is stored first, and the running one replaces it unless the condition chose the
    /// other. The usual case is then a branch not taken and a move, as native code lays out a running maximum (cmp, ja,
    /// mov), where a branch to either arm made RyuJIT jump over the empty arm of the running value at every element.
    /// With both or neither arm running it is <see cref="Branch"/>.
    /// </summary>
    private void Carry(Action condition, nint whenTrue, nint whenFalse, Type managed)
    {
        bool trueRuns = LoopCarried(whenTrue);
        if (trueRuns == LoopCarried(whenFalse))
        {
            Branch(condition, whenTrue, whenFalse);
            return;
        }
        LocalBuilder result = Scratch(managed);
        Label kept = Il.DefineLabel();
        Load(trueRuns ? whenFalse : whenTrue);
        Il.Emit(OpCodes.Stloc, result);
        condition();
        Il.Emit(trueRuns ? OpCodes.Brfalse : OpCodes.Brtrue, kept);
        Load(trueRuns ? whenTrue : whenFalse);
        Il.Emit(OpCodes.Stloc, result);
        Il.MarkLabel(kept);
        Il.Emit(OpCodes.Ldloc, result);
    }

    /// <summary>
    /// Whether a scalar select or integer minimum or maximum is on a loop-carried chain: its value comes back to it
    /// through selects, minimums, maximums and phis alone, as a running maximum does (m = a[i] &gt; m ? a[i] : m, which
    /// LLVM writes as llvm.umax, a chain of them in an unrolled loop). Without a branch each iteration waits for the
    /// previous one's comparison and mask, about five cycles, where native code's branch, which a running minimum or
    /// maximum almost always takes the same way, lets iterations overlap (PF-18: a byte maximum ran 7.2 times native);
    /// LLVM's x86 backend turns a cmov on a loop-carried dependency into a branch for the same reason
    /// (X86CmovConversion). A select keeps the branch only when its condition compares such a running value
    /// (<see cref="Running"/>), as the index of a running minimum does; selects whose values come from arithmetic, as
    /// Paeth's, are not on such a chain.
    /// </summary>
    private static bool LoopCarried(nint instruction)
    {
        HashSet<nint> seen = [];
        Stack<nint> work = new(Carried(instruction));
        while (work.TryPop(out nint value))
        {
            if (value == instruction) return true;
            if (seen.Count < 64 && seen.Add(value))
                foreach (nint operand in Carried(value)) work.Push(operand);
        }
        return false;
    }

    /// <summary>Whether a select's condition is an integer comparison of a running value: a phi, select, minimum or
    /// maximum on a loop-carried chain (<see cref="LoopCarried"/>). A running extreme changes rarely, so the comparison
    /// almost always goes the same way; a condition on the loop's data, as in x = c ? f(x) : x, may not.</summary>
    private static bool Running(nint condition) =>
        Llvm.LLVMIsAInstruction(condition) != 0 && Llvm.LLVMGetInstructionOpcode(condition) == 42 &&
        (LoopCarried(Llvm.LLVMGetOperand(condition, 0)) || LoopCarried(Llvm.LLVMGetOperand(condition, 1)));

    /// <summary>The values a phi, a select or an integer minimum or maximum passes on: the phi's incoming values, the
    /// select's arms, the others' operands; none for any other value.</summary>
    private static nint[] Carried(nint value)
    {
        if (Llvm.LLVMIsAInstruction(value) == 0 || TypeSystem.Kind(Llvm.LLVMTypeOf(value)) == 13) return [];
        switch (Llvm.LLVMGetInstructionOpcode(value))
        {
            case 44:
                return [.. Enumerable.Range(0, (int)Llvm.LLVMCountIncoming(value)).Select(index => Llvm.LLVMGetIncomingValue(value, (uint)index))];
            case 46:
                return [Llvm.LLVMGetOperand(value, 1), Llvm.LLVMGetOperand(value, 2)];
            case 45 when Llvm.LLVMGetCalledValue(value) is nint target && Llvm.LLVMIsAFunction(target) != 0 &&
                Llvm.Name(target).Split('.') is ["llvm", "smin" or "smax" or "umin" or "umax", ..]:
                return [Llvm.LLVMGetOperand(value, 0), Llvm.LLVMGetOperand(value, 1)];
        }
        return [];
    }

    /// <summary>Whether the value is the integer constant <paramref name="number"/>, sign-extended (an i1 true is -1).</summary>
    private static bool IsConstant(nint value, long number) =>
        Llvm.LLVMIsAConstantInt(value) != 0 && TypeSystem.Width(Llvm.LLVMTypeOf(value)) <= 64 && Llvm.LLVMConstIntGetSExtValue(value) == number;

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

    /// <summary>
    /// The fewest clusters a CIL switch is built from, and how sparse it may be (its range at most this many times its
    /// case count). LLVM's x86 minimum is 4, but RyuJIT's table of four made the 4-case switches in musl's sin, cos and
    /// atan2 slower than two compares each (almabench 1.8% under the JIT).
    /// </summary>
    internal const int SwitchTableClusters = 5, SwitchTableDensity = 4;

    /// <summary>
    /// A switch on at most 64 bits, lowered as LLVM's SelectionDAG lowers one (JP-03, SwitchLoweringUtils.cpp). Cases with
    /// consecutive values and one target form ranges. Runs of at least <see cref="SwitchTableClusters"/> clusters dense
    /// enough for a table become a CIL switch on the condition minus the run's lowest value, which RyuJIT compiles to a
    /// jump table or a bit test; it rebuilds a switch from a compare chain only for equality tests with one shared target
    /// (switchrecognition.cpp). The clusters are then found by a binary search, with at most three compared in a row.
    /// Values compare as the condition's CIL value: zero-extended below 32 bits and between 33 and 63, signed at 32 and
    /// 64. Each target's phi copies are emitted once, at a stub every case of that target branches to.
    /// </summary>
    private void Switch(nint block, nint value)
    {
        nint condition = Llvm.LLVMGetOperand(value, 0);
        int width = TypeSystem.Width(Llvm.LLVMTypeOf(condition));
        bool wide = width > 32;
        var stubs = new Dictionary<nint, Label>();
        Label Stub(nint target) => stubs.TryGetValue(target, out Label label) ? label : stubs[target] = Il.DefineLabel();
        nint defaultTarget = Llvm.LLVMGetSuccessor(value, 0);
        Label defaultStub = Stub(defaultTarget);
        // A default that is unreachable needs no test of the last cluster that could match.
        bool unreachableDefault = Llvm.LLVMGetInstructionOpcode(Llvm.LLVMGetFirstInstruction(defaultTarget)) == 7;
        var cases = new List<(long Key, nint Target)>();
        for (uint index = 1; index < Llvm.LLVMGetNumSuccessors(value); index++)
        {
            long key = Llvm.LLVMConstIntGetSExtValue(Llvm.LLVMGetSwitchCaseValue(value, index));
            cases.Add((width is 32 or 64 ? key : key & ((1L << width) - 1), Llvm.LLVMGetSuccessor(value, index)));
        }
        cases.Sort((left, right) => left.Key.CompareTo(right.Key));
        var ranges = new List<(long Low, long High, nint Target)>();
        foreach ((long key, nint target) in cases)
        {
            if (ranges.Count > 0 && ranges[^1].Target == target && ranges[^1].High + 1 == key)
                ranges[^1] = (ranges[^1].Low, key, target);
            else
                ranges.Add((key, key, target));
        }
        // The fewest partitions of the ranges into tables and single ranges, found backwards as LLVM's findJumpTables
        // does; between equal partitionings, the one with more tables.
        int count = ranges.Count;
        var totals = new long[count + 1];
        for (int index = 0; index < count; index++)
            totals[index + 1] = totals[index] + (ranges[index].High - ranges[index].Low + 1);
        var partitions = new int[count + 1];
        var tables = new int[count + 1];
        var last = new int[count];
        for (int first = count - 1; first >= 0; first--)
        {
            partitions[first] = partitions[first + 1] + 1;
            tables[first] = tables[first + 1];
            last[first] = first;
            for (int end = first + SwitchTableClusters - 1; end < count; end++)
            {
                ulong span = unchecked((ulong)ranges[end].High - (ulong)ranges[first].Low);
                if (span >= (ulong)(SwitchTableDensity * (totals[end + 1] - totals[first])))
                    continue;
                if (partitions[end + 1] + 1 < partitions[first] || partitions[end + 1] + 1 == partitions[first] && tables[end + 1] + 1 > tables[first])
                {
                    partitions[first] = partitions[end + 1] + 1;
                    tables[first] = tables[end + 1] + 1;
                    last[first] = end;
                }
            }
        }
        var clusters = new List<(long Low, long High, nint Target, Label[]? Table, long Weight)>();
        for (int first = 0; first < count; first = last[first] + 1)
        {
            if (last[first] == first)
            {
                clusters.Add((ranges[first].Low, ranges[first].High, ranges[first].Target, null, totals[first + 1] - totals[first]));
                continue;
            }
            long low = ranges[first].Low;
            var table = new Label[checked((int)(ranges[last[first]].High - low + 1))];
            Array.Fill(table, defaultStub);
            for (int index = first; index <= last[first]; index++)
                for (long entry = ranges[index].Low - low; entry <= ranges[index].High - low; entry++)
                    table[entry] = Stub(ranges[index].Target);
            clusters.Add((low, ranges[last[first]].High, 0, table, totals[last[first] + 1] - totals[first]));
        }

        void Constant(long number)
        {
            if (wide) Il.Emit(OpCodes.Ldc_I8, number);
            else Il.Emit(OpCodes.Ldc_I4, unchecked((int)number));
        }
        void Offset(long low)
        {
            Load(condition);
            if (low == 0) return;
            Constant(low);
            Il.Emit(OpCodes.Sub);
        }
        void Search(int first, int end)
        {
            if (end - first < 3)
            {
                for (int index = first; index <= end; index++)
                {
                    (long low, long high, nint target, Label[]? table, _) = clusters[index];
                    if (table is null && index == end && unreachableDefault)
                    {
                        Il.Emit(OpCodes.Br, Stub(target));
                        return;
                    }
                    if (table is not null)
                    {
                        Label outside = Il.DefineLabel();
                        if (wide)
                        {
                            // A CIL switch takes a 32-bit index.
                            Offset(low);
                            Constant(table.Length - 1);
                            Il.Emit(OpCodes.Bgt_Un, outside);
                        }
                        Offset(low);
                        if (wide) Il.Emit(OpCodes.Conv_I4);
                        Il.Emit(OpCodes.Switch, table);
                        Il.MarkLabel(outside);
                    }
                    else if (low == high)
                    {
                        Load(condition);
                        Constant(low);
                        Il.Emit(OpCodes.Beq, Stub(target));
                    }
                    else
                    {
                        Offset(low);
                        Constant(high - low);
                        Il.Emit(OpCodes.Ble_Un, Stub(target));
                    }
                }
                Il.Emit(OpCodes.Br, defaultStub);
                return;
            }
            // Split where the cases on either side are closest to half of them, as LLVM balances the clusters'
            // probabilities, which are proportional to their cases without profile data.
            long total = 0, below = 0;
            for (int index = first; index <= end; index++)
                total += clusters[index].Weight;
            int middle = first + 1;
            for (below = clusters[first].Weight; middle < end && 2 * (below + clusters[middle].Weight) <= total; middle++)
                below += clusters[middle].Weight;
            Label lower = Il.DefineLabel();
            Load(condition);
            Constant(clusters[middle].Low);
            Il.Emit(OpCodes.Blt, lower);
            Search(middle, end);
            Il.MarkLabel(lower);
            Search(first, middle - 1);
        }

        if (clusters.Count == 0)
            Il.Emit(OpCodes.Br, defaultStub);
        else
            Search(0, clusters.Count - 1);
        foreach ((nint target, Label label) in stubs)
        {
            Il.MarkLabel(label);
            Edge(block, target);
        }
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
                found = true;
                nint incoming = Llvm.LLVMGetIncomingValue(phi, index);
                // A phi that shares the copied value's local (ShareFunctionLocals) already holds it.
                if (locals.TryGetValue(phi, out LocalBuilder? destination) && locals.TryGetValue(incoming, out LocalBuilder? copied) && destination == copied)
                    break;
                Load(incoming);
                copies.Add(phi);
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

    /// <summary>
    /// Chooses the values computed where their one use loads them, instead of stored in a local and loaded back (JP-05),
    /// as LLVM's WebAssembly backend keeps such a value on the stack (WebAssemblyRegStackify). Each saves a stloc and an
    /// ldloc: two of the 8,000 local references and two of the 20,000 instructions past which RyuJIT gives up optimizing
    /// a method, and a local of the 1,024 it tracks; RyuJIT also sees the computation as one tree. A value qualifies when
    /// its one use is a later instruction of its block whose lowering loads each operand once (a binary operation, a
    /// cast, a GEP, a load's address, a store or a return), and its own lowering only computes on the evaluation stack:
    /// scalar arithmetic other than division and remainder (whose traps would move), casts, GEPs, and plain loads with
    /// nothing between them and where they end up computed that may write memory or have another effect (stores, calls,
    /// atomics, allocas). A value computed in its use's place is computed where that one is, so the rules apply to the
    /// whole chain, which stops at UseSiteDepth values: an unrolled reduction of thousands of additions would otherwise
    /// be one recursive computation for the emitter (and one tree for RyuJIT).
    /// </summary>
    private const int UseSiteDepth = 16;

    private void ChooseUseSiteValues()
    {
        foreach (nint block in Llvm.Blocks(function))
        {
            nint[] instructions = [.. Llvm.Instructions(block)];
            var position = new Dictionary<nint, int>();
            var computedAt = new Dictionary<nint, int>();
            var depth = new Dictionary<nint, int>();
            // effects[i]: how many of the first i instructions may write memory or have another effect.
            int[] effects = new int[instructions.Length + 1];
            for (int index = 0; index < instructions.Length; index++)
            {
                position.Add(instructions[index], index);
                effects[index + 1] = effects[index] + (Pure(instructions[index]) ? 0 : 1);
            }
            for (int index = instructions.Length - 1; index >= 0; index--)
            {
                nint value = instructions[index];
                computedAt[value] = index;
                depth[value] = 0;
                nint use = Llvm.LLVMGetFirstUse(value);
                if (use == 0 || Llvm.LLVMGetNextUse(use) != 0 || !UseSiteValue(value)) continue;
                nint user = Llvm.LLVMGetUser(use);
                if (!position.ContainsKey(user) || !UseSiteUser(user) || depth[user] == UseSiteDepth) continue;
                int at = computedAt[user];
                if (Llvm.LLVMGetInstructionOpcode(value) == 27 && effects[at] != effects[index + 1]) continue;
                atUse.Add(value);
                computedAt[value] = at;
                depth[value] = depth[user] + 1;
            }
        }
        // A GEP of constant offsets from an argument or a value with a local is computed at each of its uses (PF-23),
        // where RyuJIT folds it into the address of a load or store as native code does: LLVM hoists a structure's
        // field addresses into a loop head for the blocks below it, and a local for each was stored on every pass. The
        // base is then live where the GEP is used (ShareFunctionLocals counts a value computed where it is used as a
        // use of its operands there). A GEP whose base is such a GEP keeps its local.
        var offsets = new HashSet<nint>();
        foreach (nint block in Llvm.Blocks(function))
            foreach (nint value in Llvm.Instructions(block))
                if (Llvm.LLVMGetInstructionOpcode(value) == 29 && !atUse.Contains(value) && ConstantOffsets(value))
                    offsets.Add(value);
        foreach (nint value in offsets)
        {
            nint pointer = Llvm.LLVMGetOperand(value, 0);
            if (arguments.ContainsKey(pointer) || Llvm.LLVMIsAInstruction(pointer) != 0 && !atUse.Contains(pointer) && !offsets.Contains(pointer))
                atUse.Add(value);
        }

        static bool Scalar(nint type) => TypeSystem.Kind(type) is 2 or 3 or 12 || TypeSystem.Kind(type) == 8 && TypeSystem.Width(type) <= 64;
        bool Operands(nint instruction)
        {
            for (uint index = 0; index < Llvm.LLVMGetNumOperands(instruction); index++)
                if (!Scalar(Llvm.LLVMTypeOf(Llvm.LLVMGetOperand(instruction, index)))) return false;
            return true;
        }
        static bool ConstantOffsets(nint gep)
        {
            if (TypeSystem.Kind(Llvm.LLVMTypeOf(gep)) != 12 || TypeSystem.Kind(Llvm.LLVMTypeOf(Llvm.LLVMGetOperand(gep, 0))) != 12) return false;
            for (uint index = 1; index < Llvm.LLVMGetNumOperands(gep); index++)
                if (Llvm.LLVMIsAConstantInt(Llvm.LLVMGetOperand(gep, index)) == 0 || TypeSystem.Width(Llvm.LLVMTypeOf(Llvm.LLVMGetOperand(gep, index))) > 64)
                    return false;
            return true;
        }
        bool PlainMemory(nint instruction, nint type) => Llvm.LLVMGetOrdering(instruction) == 0 && Scalar(type) &&
            (TypeSystem.Kind(type) != 8 || (TypeSystem.Width(type) + 7) / 8 is 1 or 2 or 4 or 8);
        bool UseSiteValue(nint value)
        {
            int opcode = Llvm.LLVMGetInstructionOpcode(value);
            if (!Scalar(Llvm.LLVMTypeOf(value))) return false;
            return opcode switch
            {
                8 or 9 or 10 or 11 or 12 or 13 or 16 or >= 20 and <= 25 => Operands(value),
                29 => Operands(value),
                >= 30 and <= 40 or 60 or 69 => Operands(value),
                41 => Operands(value) && Types.Map(Llvm.LLVMTypeOf(Llvm.LLVMGetOperand(value, 0))) == Types.Map(Llvm.LLVMTypeOf(value)),
                27 => Llvm.LLVMGetVolatile(value) == 0 && PlainMemory(value, Llvm.LLVMTypeOf(value)),
                _ => false
            };
        }
        bool UseSiteUser(nint user)
        {
            int opcode = Llvm.LLVMGetInstructionOpcode(user);
            return opcode switch
            {
                >= 8 and <= 25 or 29 or >= 30 and <= 41 or 60 or 69 => Scalar(Llvm.LLVMTypeOf(user)) && Operands(user),
                27 => PlainMemory(user, Llvm.LLVMTypeOf(user)),
                28 => PlainMemory(user, Llvm.LLVMTypeOf(Llvm.LLVMGetOperand(user, 0))),
                1 => Llvm.LLVMGetNumOperands(user) == 1 && Scalar(Llvm.LLVMTypeOf(Llvm.LLVMGetOperand(user, 0))) && !Types.ReturnsBytes(Llvm.LLVMTypeOf(Llvm.LLVMGetOperand(user, 0))),
                _ => false
            };
        }
        // Instructions that neither write memory nor have another effect a load must not move past.
        static bool Pure(nint instruction) => Llvm.LLVMGetInstructionOpcode(instruction) switch
        {
            >= 8 and <= 25 or 29 or >= 30 and <= 44 or 46 or >= 50 and <= 54 or 60 or 66 or 68 or 69 => true,
            27 => Llvm.LLVMGetOrdering(instruction) == 0 && Llvm.LLVMGetVolatile(instruction) == 0,
            _ => false
        };
    }

    /// <summary>
    /// Gives each instruction value a local, shared by values of one CIL type whose live ranges do not meet. RyuJIT
    /// keeps at most 1024 locals of a method in registers (JitMaxLocalsToTrack) and stops inlining from about 920
    /// (90% of that), so a local for each value made functions of more than a thousand instructions several times
    /// slower, vectorized ones the most (SV-05). A live range is the span, in emission order, of the value's
    /// definition, its uses and the blocks it is live into or out of (SSA liveness); a phi's starts at the edges
    /// that store it. Ranges that share a position conflict, so an instruction's result never shares a local with its
    /// operands, as an instruction may store part of its result before it has read all of them. The emitter reads the
    /// operands of a comparison, a shuffle or an insertelement where those are used (Order, LoadMask, SplatScalar, a
    /// chain of insertelements built at its end), so they count as used there too, as do the operands of a value
    /// computed where it is used (ChooseUseSiteValues), which gets no local, and the values a vector lowering reads
    /// besides its operands (VectorEmitter.Reads: a widening add's narrow operands). Only functions of more than 512
    /// values share: RyuJIT allocates registers per local, so values sharing one share an allocation, which added
    /// moves to small hot loops (heapsort and lpbench 10 to 15% slower). A function with setjmp keeps a local for
    /// each value, as the jump dispatch re-enters the body at its call sites.
    /// </summary>
    private void ShareFunctionLocals()
    {
        nint[] blocks = [.. Llvm.Blocks(function)];
        var blockIndex = new Dictionary<nint, int>();
        var valueIndex = new Dictionary<nint, int>();
        var values = new List<nint>();
        var home = new List<int>();
        var low = new List<int>();
        var high = new List<int>();
        var phis = new List<int>();
        int[] start = new int[blocks.Length], end = new int[blocks.Length];
        int position = 0;
        for (int block = 0; block < blocks.Length; block++)
        {
            blockIndex.Add(blocks[block], block);
            start[block] = position;
            foreach (nint instruction in Llvm.Instructions(blocks[block]))
            {
                if (TypeSystem.Kind(Llvm.LLVMTypeOf(instruction)) != 0 && !atUse.Contains(instruction))
                {
                    if (Llvm.LLVMGetInstructionOpcode(instruction) == 44) phis.Add(values.Count);
                    valueIndex.Add(instruction, values.Count);
                    values.Add(instruction);
                    home.Add(block);
                    low.Add(position);
                    high.Add(position);
                }
                position++;
            }
            end[block] = position - 1;
        }
        // Phis and the values they copy, which may join one local; their last use in each block decides that.
        bool[] coalescing = new bool[values.Count];
        int[] definition = [.. low];
        foreach (int phi in phis)
        {
            coalescing[phi] = true;
            definition[phi] = start[home[phi]];
            for (uint index = 0; index < Llvm.LLVMCountIncoming(values[phi]); index++)
                if (valueIndex.TryGetValue(Llvm.LLVMGetIncomingValue(values[phi], index), out int incoming)) coalescing[incoming] = true;
        }
        var lastUse = new Dictionary<(int Value, int Block), int>();
        void Extend(int value, int at)
        {
            low[value] = Math.Min(low[value], at);
            high[value] = Math.Max(high[value], at);
        }
        // Uses in another block than the definition's are the ones liveness carries between blocks.
        var uses = new List<(int Value, int Block)>();
        var seen = new HashSet<nint>();
        void Use(nint operand, int block, int at)
        {
            if (atUse.Contains(operand))
            {
                for (int index = 0; index < Llvm.LLVMGetNumOperands(operand); index++)
                    Use(Llvm.LLVMGetOperand(operand, (uint)index), block, at);
                return;
            }
            if (!valueIndex.TryGetValue(operand, out int value)) return;
            Extend(value, at);
            if (block != home[value]) uses.Add((value, block));
            if (coalescing[value])
                lastUse[(value, block)] = lastUse.TryGetValue((value, block), out int last) ? Math.Max(last, at) : at;
            if (Llvm.LLVMGetInstructionOpcode(operand) is 42 or 43 or 51 or 52 && seen.Add(operand))
                for (int index = 0; index < Llvm.LLVMGetNumOperands(operand); index++)
                    Use(Llvm.LLVMGetOperand(operand, (uint)index), block, at);
        }
        int[][] successors = new int[blocks.Length][];
        for (int block = 0; block < blocks.Length; block++)
        {
            int at = start[block];
            nint terminator = 0;
            foreach (nint instruction in Llvm.Instructions(blocks[block]))
            {
                terminator = instruction;
                if (Llvm.LLVMGetInstructionOpcode(instruction) == 44)
                {
                    int phi = valueIndex[instruction];
                    for (uint index = 0; index < Llvm.LLVMCountIncoming(instruction); index++)
                    {
                        // The predecessor's edge loads the incoming value and stores the phi's local.
                        int from = blockIndex[Llvm.LLVMGetIncomingBlock(instruction, index)];
                        Extend(phi, end[from]);
                        seen.Clear();
                        Use(Llvm.LLVMGetIncomingValue(instruction, index), from, end[from]);
                    }
                }
                else if (!atUse.Contains(instruction))
                {
                    seen.Clear();
                    for (int index = 0; index < Llvm.LLVMGetNumOperands(instruction); index++)
                        Use(Llvm.LLVMGetOperand(instruction, (uint)index), block, at);
                    if (Types.Vector(Llvm.LLVMTypeOf(instruction)) is not null)
                        foreach (nint read in Vectors.Reads(instruction)) Use(read, block, at);
                }
                at++;
            }
            successors[block] = [.. Enumerable.Range(0, (int)Llvm.LLVMGetNumSuccessors(terminator)).Select(index => blockIndex[Llvm.LLVMGetSuccessor(terminator, (uint)index)])];
        }
        // Liveness of those values, as bit sets over them, from the blocks that use them back to their definitions.
        int[] global = [.. uses.Select(use => use.Value).Distinct()];
        var bit = new Dictionary<int, int>();
        for (int index = 0; index < global.Length; index++) bit.Add(global[index], index);
        int words = (global.Length + 63) / 64;
        ulong[][] used = new ulong[blocks.Length][], defined = new ulong[blocks.Length][], liveIn = new ulong[blocks.Length][], liveOut = new ulong[blocks.Length][];
        for (int block = 0; block < blocks.Length; block++)
        {
            used[block] = new ulong[words];
            defined[block] = new ulong[words];
            liveIn[block] = new ulong[words];
            liveOut[block] = new ulong[words];
        }
        foreach ((int value, int block) in uses) used[block][bit[value] >> 6] |= 1UL << bit[value];
        foreach (int value in global) defined[home[value]][bit[value] >> 6] |= 1UL << bit[value];
        for (bool changed = true; changed;)
        {
            changed = false;
            for (int block = blocks.Length - 1; block >= 0; block--)
            {
                ulong[] output = liveOut[block], input = liveIn[block];
                foreach (int successor in successors[block])
                    for (int word = 0; word < words; word++) output[word] |= liveIn[successor][word];
                for (int word = 0; word < words; word++)
                {
                    ulong next = (used[block][word] | output[word]) & ~defined[block][word];
                    if (next == input[word]) continue;
                    input[word] = next;
                    changed = true;
                }
            }
        }
        for (int block = 0; block < blocks.Length; block++)
            for (int index = 0; index < global.Length; index++)
            {
                if ((liveIn[block][index >> 6] & 1UL << index) != 0) Extend(global[index], start[block]);
                if ((liveOut[block][index >> 6] & 1UL << index) != 0) Extend(global[index], end[block]);
            }
        // Where each phi and copied value is live: in each block it is defined in or live into, from its definition
        // (a phi's at the block's start) or the block's start to its last use there or the block's end, as sorted
        // [From, To] position segments, disjoint because blocks are.
        var segments = new List<(int From, int To)>?[values.Count];
        for (int value = 0; value < values.Count; value++)
            if (coalescing[value]) segments[value] = [];
        int segmentIndex = 0;
        for (int block = 0; block < blocks.Length; block++)
        {
            for (; segmentIndex < values.Count && home[segmentIndex] == block; segmentIndex++)
            {
                if (!coalescing[segmentIndex]) continue;
                bool liveAfter = bit.TryGetValue(segmentIndex, out int index) && (liveOut[block][index >> 6] & 1UL << index) != 0;
                int through = lastUse.TryGetValue((segmentIndex, block), out int at) ? Math.Max(at, definition[segmentIndex]) : definition[segmentIndex];
                segments[segmentIndex]!.Add((definition[segmentIndex], liveAfter ? end[block] : through));
            }
            for (int word = 0; word < words; word++)
                for (ulong bits = liveIn[block][word]; bits != 0; bits &= bits - 1)
                {
                    int index = word * 64 + System.Numerics.BitOperations.TrailingZeroCount(bits);
                    int value = global[index];
                    if (!coalescing[value]) continue;
                    bool liveAfter = (liveOut[block][index >> 6] & 1UL << index) != 0;
                    segments[value]!.Add((start[block], liveAfter || !lastUse.TryGetValue((value, block), out int at) ? end[block] : at));
                }
        }
        // A phi joins the class of a value it copies, as LLVM's register coalescer joins a copy's intervals, when no
        // member of one class is live where a member of the other is (SSA interference); a class then has one local
        // and its edge copies are not emitted. An edge loads every value it copies before it stores any phi, so a
        // copied value whose last use is the edge does not conflict with the phi; one still live after it, or a phi
        // live where the other is defined, does. The phis of one block conflict at its start, and an instruction
        // reads its operands where its result is written, so it never joins them.
        int[] parent = [.. Enumerable.Range(0, values.Count)];
        int Find(int value)
        {
            while (parent[value] != value) value = parent[value] = parent[parent[value]];
            return value;
        }
        // Copies of phis go first: a dispatch loop forwards its values through chains of phis, which one class can
        // take whole, and a value computed from a phi of the chain (an induction variable's next value), joined first,
        // would keep the chain apart.
        bool[] isPhi = new bool[values.Count];
        foreach (int phi in phis) isPhi[phi] = true;
        foreach (bool forwarding in (bool[])[true, false])
            foreach (int phi in phis)
                for (uint index = 0; index < Llvm.LLVMCountIncoming(values[phi]); index++)
                {
                    if (!valueIndex.TryGetValue(Llvm.LLVMGetIncomingValue(values[phi], index), out int incoming) || isPhi[incoming] != forwarding)
                        continue;
                    int left = Find(phi), right = Find(incoming);
                    if (left == right || Overlap(segments[left]!, segments[right]!)) continue;
                    parent[right] = left;
                    segments[left] = Merge(segments[left]!, segments[right]!);
                    segments[right] = null;
                }
        // Linear scan: a class takes a local of its type that the classes before it no longer use, over the span of
        // its members' ranges.
        int[] first = [.. low], last = [.. high];
        for (int value = 0; value < values.Count; value++)
        {
            int root = Find(value);
            first[root] = Math.Min(first[root], low[value]);
            last[root] = Math.Max(last[root], high[value]);
        }
        var assigned = new LocalBuilder?[values.Count];
        var available = new Dictionary<Type, Stack<LocalBuilder>>();
        var active = new PriorityQueue<int, int>();
        foreach (int root in Enumerable.Range(0, values.Count).Where(value => parent[value] == value).OrderBy(value => first[value]))
        {
            while (active.TryPeek(out int expired, out int until) && until < first[root])
            {
                active.Dequeue();
                LocalBuilder released = assigned[expired]!;
                if (!available.TryGetValue(released.LocalType, out Stack<LocalBuilder>? free)) available.Add(released.LocalType, free = new());
                free.Push(released);
            }
            Type managed = Types.Map(Llvm.LLVMTypeOf(values[root]));
            LocalBuilder local = available.TryGetValue(managed, out Stack<LocalBuilder>? pool) && pool.TryPop(out LocalBuilder? previous) ? previous : Il.DeclareLocal(managed);
            if (local.LocalIndex >= ushort.MaxValue)
                throw new NotSupportedException($"Function {Llvm.Name(function)} exceeds the CIL local-variable limit.");
            assigned[root] = local;
            active.Enqueue(root, last[root]);
        }
        for (int value = 0; value < values.Count; value++)
            locals[values[value]] = assigned[Find(value)]!;

        static bool Overlap(List<(int From, int To)> left, List<(int From, int To)> right)
        {
            for (int i = 0, j = 0; i < left.Count && j < right.Count;)
            {
                if (left[i].To < right[j].From) i++;
                else if (right[j].To < left[i].From) j++;
                else return true;
            }
            return false;
        }
        static List<(int From, int To)> Merge(List<(int From, int To)> left, List<(int From, int To)> right)
        {
            var merged = new List<(int From, int To)>(left.Count + right.Count);
            int i = 0, j = 0;
            while (i < left.Count && j < right.Count) merged.Add(left[i].From < right[j].From ? left[i++] : right[j++]);
            for (; i < left.Count; i++) merged.Add(left[i]);
            for (; j < right.Count; j++) merged.Add(right[j]);
            return merged;
        }
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
                if (TypeSystem.Kind(type) == 0 || atUse.Contains(instruction)) continue;
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
        if (Types.ReturnsBytes(Llvm.LLVMTypeOf(instruction)) && returned == Types.Return(Llvm.LLVMTypeOf(instruction)))
        {
            Vectors.FromBoundary(Llvm.LLVMTypeOf(instruction));
            return;
        }
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
        if (value == computing)
            return;
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
            if (CpuQuery(instruction, target, assembly) is int registers and not 0)
            {
                if (Compiler.Host is not null)
                {
                    for (uint index = 0; index < Llvm.LLVMGetNumArgOperands(instruction); index++)
                        Load(Llvm.LLVMGetOperand(instruction, index));
                    Il.Emit(OpCodes.Call, Compiler.Host.Resolve(0, instruction));
                    return;
                }
                // Portable code asks the runtime, which answers for the host from .NET's view (SV-12): the registers are
                // written into the result's explicit-layout structure, EAX first.
                LocalBuilder result = Il.DeclareLocal(Types.Map(Llvm.LLVMTypeOf(instruction)));
                Il.Emit(OpCodes.Ldloca, result);
                Il.Emit(OpCodes.Initobj, result.LocalType);
                Il.Emit(OpCodes.Ldloca, result);
                Il.Emit(OpCodes.Conv_U);
                Load(Llvm.LLVMGetOperand(instruction, 0));
                if (registers == 4)
                {
                    if (Llvm.LLVMGetNumArgOperands(instruction) == 2) Load(Llvm.LLVMGetOperand(instruction, 1));
                    else Il.Emit(OpCodes.Ldc_I4_0);
                }
                Il.Emit(OpCodes.Call, typeof(CpuFeatures).GetMethod(registers == 4 ? nameof(CpuFeatures.CpuId) : nameof(CpuFeatures.XGetBv))!);
                Il.Emit(OpCodes.Ldloc, result);
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
                throw new NotSupportedException("Inline assembly with instructions or outputs is not supported; " + InlineAssemblyAdvice);
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
        if (Llvm.LLVMGetNumArgOperands(instruction) == 0 && Llvm.LLVMIsAFunction(target) != 0 && Llvm.LLVMIsDeclaration(target) != 0 &&
            Compiler.ManagedContradiction(target, Llvm.LLVMGetCalledFunctionType(instruction)) is string implementation)
        {
            // The configure probe described below, against a function the runtime implements in managed code.
            TrapCall(instruction, $"Invalid direct call through a prototype without parameters: {Llvm.Name(function)} -> {name}: {Llvm.PrintType(Llvm.LLVMGetCalledFunctionType(instruction))} versus {implementation}");
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
                TrapCall(instruction, $"Invalid direct call with missing arguments: {Llvm.Name(function)} -> {name}: {Llvm.PrintType(signature)} versus {Llvm.PrintType(definition)}");
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
            {
                // A call that passes no arguments through a prototype without parameters ("char f(void);" or an
                // unprototyped "int f();") is how autoconf, Meson and ffmpeg's configure check that a function links.
                // Natively it links whatever the definition is and only executing it is undefined, so it compiles to a
                // trap: the probe gets the native answer and a program that executes the call still stops (TC-07).
                if (arguments == 0 && Llvm.LLVMIsDeclaration(target) == 0)
                {
                    TrapCall(instruction, $"Invalid direct call through a prototype without parameters: {Llvm.Name(function)} -> {name}: {Llvm.PrintType(signature)} versus {Llvm.PrintType(definition)}");
                    return;
                }
                throw new NotSupportedException($"Incompatible direct call signature for {name}: {Llvm.PrintType(signature)} versus {Llvm.PrintType(definition)}");
            }
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
            ManagedCalli(Types.Return(Llvm.LLVMGetReturnType(signature)), parameters);
            Il.Emit(OpCodes.Br, legacyComplete.Value);
            Il.MarkLabel(variadicCall);
        }
        if (variadic)
            varargs.Pack(instruction);
        // A runtime method (a SIMD128 helper) may take vectors of other lanes than the call passes: reinterpreted.
        MethodInfo? runtime = !variadic && Llvm.LLVMIsAFunction(target) != 0 &&
            (Compiler.Methods.TryGetValue(target, out MethodInfo? resolved) ? resolved : Compiler.ResolveFunction(target)) is MethodInfo direct &&
            direct is not MethodBuilder ? direct : null;
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
            if (runtime is not null && index < runtime.GetParameters().Length)
                Vectors.Reinterpret(Types.Map(Llvm.LLVMTypeOf(argument)), runtime.GetParameters()[index].ParameterType);
        }
        if (variadic)
            varargs.Load();
        if (Compiler.Methods.TryGetValue(target, out MethodInfo? callee))
            Il.Emit(OpCodes.Call, callee);
        else if (Llvm.LLVMIsAFunction(target) != 0)
            Il.Emit(OpCodes.Call, Compiler.ResolveFunction(target));
        else if (Llvm.LLVMIsAInlineAsm(target) != 0)
            throw new NotSupportedException("Inline assembly is not supported; " + InlineAssemblyAdvice);
        else
        {
            Load(target);
            if (Compiler.Host is not null && variadic)
                Il.Emit(OpCodes.Call, typeof(SystemAbi).GetMethod(nameof(SystemAbi.ResolveVariadicCallback))!);
            if (Compiler.Host is null || variadic)
                ManagedCalli(Types.Return(Llvm.LLVMGetReturnType(signature)), Types.Parameters(signature));
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
                ManagedCalli(Types.Return(Llvm.LLVMGetReturnType(signature)), Types.Parameters(signature));
                Il.Emit(OpCodes.Br, complete);
                Il.MarkLabel(native);
                Type returnType = Types.Return(Llvm.LLVMGetReturnType(signature));
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
                {
                    // Native code expects a narrow integer argument extended to 32 bits as its signext or zeroext attribute
                    // says, and leaves the upper bits of a narrow result undefined, while llvmnet keeps narrow integers
                    // zero-extended: the arguments are loaded again, extended, and the result is masked.
                    if (Enumerable.Range(0, parameters.Length).Any(index => NarrowInteger(Llvm.LLVMTypeOf(Llvm.LLVMGetOperand(instruction, (uint)index)))))
                    {
                        for (int index = 0; index <= parameters.Length; index++) Il.Emit(OpCodes.Pop);
                        uint signext = Llvm.LLVMGetEnumAttributeKindForName("signext", 7);
                        for (uint index = 0; index < parameters.Length; index++)
                        {
                            nint argument = Llvm.LLVMGetOperand(instruction, index);
                            Load(argument);
                            if (!NarrowInteger(Llvm.LLVMTypeOf(argument))) continue;
                            if (Llvm.LLVMGetCallSiteEnumAttribute(instruction, index + 1, signext) != 0) SignExtend(Llvm.LLVMTypeOf(argument));
                            else Normalize(Llvm.LLVMTypeOf(argument));
                        }
                        Load(target);
                    }
                    Il.EmitCalli(OpCodes.Calli, System.Runtime.InteropServices.CallingConvention.Cdecl, returnType, parameters);
                    if (NarrowInteger(Llvm.LLVMGetReturnType(signature))) Normalize(Llvm.LLVMGetReturnType(signature));
                }
                Il.MarkLabel(complete);
            }
        }
        if (legacyComplete is Label completeLegacy) Il.MarkLabel(completeLegacy);
        if (discardReturn) Il.Emit(OpCodes.Pop);
        else if (runtime is not null) Vectors.Reinterpret(runtime.ReturnType, Types.Map(Llvm.LLVMGetReturnType(signature)));
        else if (Types.ReturnsBytes(Llvm.LLVMGetReturnType(signature))) Vectors.FromBoundary(Llvm.LLVMGetReturnType(signature));
        if (truncateBooleanReturn) Normalize(Llvm.LLVMTypeOf(instruction));
        if (convertReturn is not null) Il.Emit(convertReturn == typeof(nint) ? OpCodes.Conv_I : OpCodes.Conv_I8);
        if (byteReturn is (nint from, nint to)) ConvertOneByte(from, to);
    }

    /// <summary>Compiles a direct call that contradicts its definition to an InvalidProgramException with the diagnostic,
    /// and reports it as a warning while translating.</summary>
    private void TrapCall(nint instruction, string diagnostic)
    {
        Compiler.Warning($"emitting runtime trap: {diagnostic}");
        Il.Emit(OpCodes.Ldstr, diagnostic);
        Il.Emit(OpCodes.Newobj, typeof(InvalidProgramException).GetConstructor([typeof(string)])!);
        Il.Emit(OpCodes.Throw);
        if (TypeSystem.Kind(Llvm.LLVMTypeOf(instruction)) != 0) Zero(Llvm.LLVMTypeOf(instruction));
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
        if (Types.Return(types[0]) != method.ReturnType || types.Any(type => !CilCompiler.NativeScalar(Types.Map(type)) && Types.Size(type) > 16))
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
        Type returnType = Types.Return(Llvm.LLVMGetReturnType(signature));
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
    /// <summary>An integer type narrower than the 32-bit CIL value that holds it (i1, i8, i16).</summary>
    private static bool NarrowInteger(nint type) => TypeSystem.Kind(type) == 8 && TypeSystem.Width(type) < 32;

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

    /// <summary>The IL locals past which a stub of an indirect call is not used (CilCompiler.ManagedCalli): RyuJIT stops
    /// inlining into a method of about 920 locals, its temporaries included, and a stub it does not inline is a call more.</summary>
    private const int StubLocals = 512;

    private void ManagedCalli(Type returnType, Type[] parameters, bool tail = false)
    {
        Compiler.ManagedCalli(Il, method, returnType, parameters, tail, stub: localCount <= StubLocals);
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

    /// <summary>
    /// What the inline assembly rejection advises (BE-24, SV-15): the projects' switches that leave it out, and where
    /// assembly goes instead.
    /// </summary>
    internal const string InlineAssemblyAdvice = "build the project without it (ffmpeg: --disable-inline-asm; OpenSSL: no-asm; " +
        "libjpeg-turbo: -DWITH_SIMD=0) and keep assembly in .S or .asm files, which system mode links as native objects and " +
        "portable mode calls through a native kernel package (docs/user/native-kernels.md).";

    /// <summary>
    /// The registers a CPUID (4) or XGETBV (2) inline-assembly form returns, or 0 for other assembly. CPUID is "cpuid"
    /// with its four outputs, or "cpuid" between a mov or xchg that keeps RBX in another register (LLVM's and Eigen's host
    /// detection, ffmpeg's, Clang's &lt;cpuid.h&gt;); its inputs are the leaf, in EAX or tied to it, and optionally the
    /// subleaf, in ECX or tied to it. XGETBV is "xgetbv" or its encoding, with the register's index in ECX. The text is
    /// compared without white space or operand-size suffixes, with LLVM's spelling of Clang's {att|intel} alternatives,
    /// $(att$|intel$), taken as its AT&amp;T part. System mode runs them in native thunks, portable mode asks
    /// <see cref="CpuFeatures"/>.
    /// </summary>
    internal static int CpuQuery(nint instruction, nint target, string assembly)
    {
        nint text = Llvm.LLVMGetInlineAsmConstraintString(target, out nuint length);
        string[] constraints = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(text, checked((int)length))!.Split(',')
            .Where(constraint => !constraint.StartsWith('~')).ToArray();
        string[] outputs = constraints.Where(constraint => constraint.StartsWith('=')).ToArray();
        string[] inputs = constraints.Where(constraint => !constraint.StartsWith('=')).ToArray();
        string attSyntax = System.Text.RegularExpressions.Regex.Replace(assembly, @"\$\(([^$|]*)(\$\|[^$]*)?\$\)", "$1");
        string[] instructions = attSyntax.Split('\n', ';').Select(line => System.Text.RegularExpressions.Regex.Replace(line, @"\s+", "").ToLowerInvariant())
            .Where(line => line.Length != 0).ToArray();
        int registers = 0;
        if (instructions is ["cpuid"] && outputs is ["={ax}", "={bx}", "={cx}", "={dx}"])
            registers = 4;
        else if (instructions is [string save, "cpuid", string restore] && outputs is ["={ax}", string kept, "={cx}", "={dx}"] &&
            System.Text.RegularExpressions.Regex.Match(save, @"^(movq?|xchgq?)%rbx,(%r[sd]i|\$\{1:q\})$") is { Success: true } match &&
            System.Text.RegularExpressions.Regex.IsMatch(restore, @"^xchgq?%rbx," + System.Text.RegularExpressions.Regex.Escape(match.Groups[2].Value) + "$") &&
            (match.Groups[2].Value.StartsWith('%') ? kept == "={" + match.Groups[2].Value[2..] + "}" : kept is "=r" or "=&r"))
            registers = 4;
        else if (instructions is [".byte0x0f,0x01,0xd0"] or ["xgetbv"] && outputs is ["={ax}", "={dx}"] && inputs is ["{cx}"])
            registers = 2;
        int arguments = registers == 2 ? 1 : inputs switch
        {
            ["0" or "{ax}"] => 1,
            ["0" or "{ax}", "2" or "{cx}"] => 2,
            _ => 0
        };
        nint result = Llvm.LLVMTypeOf(instruction);
        return registers != 0 && arguments != 0 && Llvm.LLVMGetNumArgOperands(instruction) == arguments && TypeSystem.Kind(result) == 10 &&
            Llvm.LLVMCountStructElementTypes(result) == registers &&
            Enumerable.Range(0, registers).All(index => TypeSystem.Width(Llvm.LLVMStructGetTypeAtIndex(result, (uint)index)) == 32) &&
            Enumerable.Range(0, arguments).All(index => TypeSystem.Width(Llvm.LLVMTypeOf(Llvm.LLVMGetOperand(instruction, (uint)index))) == 32)
            ? registers : 0;
    }

    private void Intrinsic(nint instruction, string name)
    {
        if (TargetIntrinsics.Helper(name) is MethodInfo target)
        {
            TargetIntrinsic(instruction, name, target);
            return;
        }
        if (Vectors.Intrinsic(instruction, name))
            return;
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
        if (name == "llvm.x86.sse2.pause")
        {
            Il.Emit(OpCodes.Ldc_I4_1);
            Il.Emit(OpCodes.Call, typeof(Thread).GetMethod(nameof(Thread.SpinWait))!);
            return;
        }
        if (name is "llvm.x86.sse2.mfence" or "llvm.x86.sse2.lfence" or "llvm.x86.sse.sfence")
        {
            Il.Emit(OpCodes.Call, typeof(Thread).GetMethod(nameof(Thread.MemoryBarrier))!);
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
        // Without branches, as Select explains, except against a constant bound and on a loop-carried chain
        // (LoopCarried, Carry). A constant bound is a clamp (FFmpeg's 10-bit pixels are smax(x, 0) then umin(x, 1023))
        // whose comparison almost always goes the same way, and a predicted branch costs less than a mask: with masks,
        // HEVC decoding took 12% longer, and 5% with only smax(x, 0) as x & ~(x >> 31). Two variable operands are a
        // data-dependent choice, as Paeth's umin(pb, pa), where a mispredicted branch costs far more. Up to 32 bits,
        // with d = left - right in 64 bits and s = d >> 63 (all ones when left < right): minimum = right + (d & s),
        // maximum = left - (d & s); wider,
        // right ^ ((left ^ right) & -(left < right)). abs is (x ^ s) - s with s = x >> (bits - 1), which wraps the
        // minimum to itself.
        if (operation is "smin" or "smax" or "umin" or "umax")
        {
            nint left = Llvm.LLVMGetOperand(instruction, 0);
            nint right = Llvm.LLVMGetOperand(instruction, 1);
            nint type = Llvm.LLVMTypeOf(left);
            bool signed = operation[0] == 's', minimum = operation.EndsWith("min", StringComparison.Ordinal);
            if (Llvm.LLVMIsAConstantInt(left) == 0 && Llvm.LLVMIsAConstantInt(right) == 0 && LoopCarried(instruction))
            {
                Carry(() =>
                {
                    Load(left);
                    if (signed)
                        SignExtend(type);
                    Load(right);
                    if (signed)
                        SignExtend(type);
                    Il.Emit(operation switch { "smin" => OpCodes.Clt, "smax" => OpCodes.Cgt, "umin" => OpCodes.Clt_Un, _ => OpCodes.Cgt_Un });
                }, left, right, Types.Map(type));
                return;
            }
            if (Llvm.LLVMIsAConstantInt(left) != 0 || Llvm.LLVMIsAConstantInt(right) != 0)
            {
                Load(left);
                if (signed)
                    SignExtend(type);
                Load(right);
                if (signed)
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
            if (TypeSystem.Width(type) <= 32)
            {
                LocalBuilder difference = Scratch(typeof(long));
                foreach (nint operand in new[] { left, right })
                {
                    Load(operand);
                    if (signed)
                        SignExtend(type);
                    Il.Emit(signed ? OpCodes.Conv_I8 : OpCodes.Conv_U8);
                }
                Il.Emit(OpCodes.Sub);
                Il.Emit(OpCodes.Stloc, difference);
                Load(minimum ? right : left);
                Il.Emit(OpCodes.Ldloc, difference);
                Il.Emit(OpCodes.Ldloc, difference);
                Il.Emit(OpCodes.Ldc_I4, 63);
                Il.Emit(OpCodes.Shr);
                Il.Emit(OpCodes.And);
                Il.Emit(OpCodes.Conv_I4);
                Il.Emit(minimum ? OpCodes.Add : OpCodes.Sub);
                Normalize(type);
                return;
            }
            Load(right);
            Load(left);
            Load(right);
            Il.Emit(OpCodes.Xor);
            Load(left);
            if (signed)
                SignExtend(type);
            Load(right);
            if (signed)
                SignExtend(type);
            Il.Emit(operation switch { "smin" => OpCodes.Clt, "smax" => OpCodes.Cgt, "umin" => OpCodes.Clt_Un, _ => OpCodes.Cgt_Un });
            Mask(Types.Map(type));
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
        if (name.StartsWith("llvm.x86.", StringComparison.Ordinal))
            throw new NotSupportedException(X86IntrinsicMessage(name, Compiler.Host is not null));
        throw new NotSupportedException($"Unsupported intrinsic: {name}");
    }

    /// <summary>
    /// A call of a target intrinsic the runtime implements (<see cref="TargetIntrinsics"/>): the operands, vectors
    /// converted to the helper's lanes, then the helper, whose vector result is converted back. Scalar operands pass as
    /// they are: an i8, i16 or i32 as the helper's int, an i64 as its long, a pointer as its nint.
    /// </summary>
    private void TargetIntrinsic(nint instruction, string name, MethodInfo helper)
    {
        static bool IsVector(Type type) => type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(System.Runtime.Intrinsics.Vector128<>) ||
            type.GetGenericTypeDefinition() == typeof(System.Runtime.Intrinsics.Vector256<>));
        ParameterInfo[] parameters = helper.GetParameters();
        if (parameters.Length != Llvm.LLVMGetNumArgOperands(instruction))
            throw new NotSupportedException($"{name} has {Llvm.LLVMGetNumArgOperands(instruction)} operands where its runtime helper takes {parameters.Length}.");
        for (uint index = 0; index < parameters.Length; index++)
        {
            nint operand = Llvm.LLVMGetOperand(instruction, index);
            nint type = Llvm.LLVMTypeOf(operand);
            Type expected = parameters[index].ParameterType, actual = Types.Map(type);
            if (IsVector(expected) && !IsVector(actual) && VectorEmitter.Halves(actual) is null)
                throw new NotSupportedException($"{name} needs the typed vector lowering, which --vector-lowering=scalar turns off.");
            if (!IsVector(expected) && (expected == typeof(int) ? TypeSystem.Kind(type) != 8 || TypeSystem.Width(type) > 32 : expected != actual))
                throw new NotSupportedException($"{name}: operand {index} is {Llvm.PrintType(type)} where its runtime helper takes {expected.Name}.");
            Load(operand);
            if (IsVector(expected)) Vectors.Convert(actual, expected);
        }
        Il.Emit(OpCodes.Call, helper);
        if (IsVector(helper.ReturnType)) Vectors.Convert(helper.ReturnType, Types.Map(Llvm.LLVMTypeOf(instruction)));
    }

    /// <summary>
    /// The x86 target intrinsics the backend translates: the spin-wait hint, which libstdc++'s atomic waits use on
    /// x86-64, the SSE fences, and the SSE to AVX2, FMA, F16C and BMI intrinsics the runtime implements
    /// (<see cref="TargetIntrinsics"/>). Every other llvm.x86 intrinsic (AVX-512 and later, the MXCSR write) is rejected
    /// when a module is translated, after a warning when its unit was compiled (Driver).
    /// </summary>
    internal static bool TranslatesX86Intrinsic(string name) =>
        name is "llvm.x86.sse2.pause" or "llvm.x86.sse2.mfence" or "llvm.x86.sse2.lfence" or "llvm.x86.sse.sfence" || TargetIntrinsics.Helper(name) is not null;

    internal static string X86IntrinsicMessage(string name, bool system, bool compiling = false) =>
        $"x86 target intrinsic {name} is not translated{(compiling ? ", so linking a program that reaches it fails" : "")}; " + (name == "llvm.x86.sse.ldmxcsr"
            ? "translated code keeps the default MXCSR (round to nearest, no flush to zero), so _mm_setcsr and the _MM_SET macros cannot change it"
            : "llvmnet translates the SSE to AVX2 intrinsics with FMA, F16C, BMI and PCLMULQDQ (x86-64-v3), so build the code's AVX2, SSE or C path instead" + (system
                ? " (system mode takes the instruction sets of -m and -march as native code does: try -march=x86-64-v3 or the library's own SIMD option)"
                : " (--isa=x86-64-v3 selects AVX2 paths, x86-64-v2 SSE4.2 ones, the baseline a library's C paths)"));

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
