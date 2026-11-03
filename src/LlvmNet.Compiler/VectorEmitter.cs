using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Intrinsics;
using LlvmNet.Runtime;

namespace LlvmNet;

/// <summary>
/// The typed lowering of LLVM vector code (ADR-16 Level 1, docs/plan/12-simd.md): vector values are fragments of
/// <see cref="Vector128{T}"/> (<see cref="VectorShape"/>), and each instruction becomes the Vector128 operations of its
/// fragments, which RyuJIT and ILC compile to the host's SIMD instructions. Lane-changing operations (casts, masks of
/// another lane width) go through Widen and Narrow, shuffles through constant Shuffle and select operations, and the
/// rare operations with no vector instruction (integer division, variable shifts, partial memory accesses) through
/// <see cref="Vectors"/>. A function whose vector code has no lowering here is scalarized by LLVM first
/// (<see cref="Legalize"/>), as all vector code was before.
/// </summary>
internal sealed class VectorEmitter(ValueEmitter values, ILGenerator il, TypeSystem types)
{
    private static readonly ConcurrentDictionary<(string Name, Type? Lane, string Parameters), MethodInfo> Methods = [];
    private readonly Dictionary<Type, Stack<LocalBuilder>> temporaries = [];

    /// <summary>
    /// Scalarizes, through LLVM's scalarizer, every function with vector code this lowering does not translate (lanes of
    /// no CIL vector type such as i24 or i128, vector getelementptr, atomics, vector intrinsics without a lowering), so
    /// that only typed vectors remain at its boundaries; and every function whose vector code moves more lanes than it
    /// computes on. Each lane inserted or extracted costs an instruction in Vector128 form and nothing as scalars, and
    /// each operation on N lanes saves N - 1 scalar operations, so a function stays vector code when its operations save
    /// at least as much as its lane traffic costs. Vectorized loops save far more; what fails the test is the x86-64
    /// ABI's two-float &lt;2 x float&gt; and SROA's small arrays, used as containers with an occasional two-lane
    /// operation InstCombine formed (Bullet ran 4% slower with those typed). Without <paramref name="cost"/> (the hidden
    /// --vector-lowering=typed) only what the lowering does not translate is scalarized. Returns each scalarized
    /// function's name with the reason, null for one scalarized by this cost.
    /// </summary>
    internal static unsafe List<(string Function, string? Reason)> Legalize(TypeSystem types, nint module, bool cost)
    {
        List<nint> functions = [];
        List<(string, string?)> report = [];
        foreach (nint function in Llvm.Functions(module))
        {
            if (Llvm.LLVMIsDeclaration(function) != 0) continue;
            string? reason = null;
            long saved = 0, traffic = 0;
            foreach (nint instruction in Llvm.Blocks(function).SelectMany(Llvm.Instructions))
            {
                reason ??= Unsupported(types, instruction);
                if (reason is not null) break;
                int opcode = Llvm.LLVMGetInstructionOpcode(instruction);
                if (opcode is 50 or 51 && types.Vector(Llvm.LLVMTypeOf(Llvm.LLVMGetOperand(instruction, 0))) is not null) traffic++;
                else saved += Saved(types, instruction, opcode);
            }
            if (reason is null && (!cost || traffic <= saved)) continue;
            functions.Add(function);
            report.Add((Llvm.Name(function), reason));
        }
        if (functions.Count == 0) return report;
        nint[] handles = [.. functions];
        fixed (nint* pointer = handles)
            Llvm.ScalarizeFunctions(pointer, (uint)handles.Length);
        return report;
    }

    /// <summary>The scalar operations a vector instruction saves: one fewer than the lanes it computes on (arithmetic,
    /// comparisons, conversions, vector selects, vector intrinsics); zero for moves, shuffles and everything else.</summary>
    private static long Saved(TypeSystem types, nint instruction, int opcode)
    {
        long Lanes(nint value) => types.Vector(Llvm.LLVMTypeOf(value)) is VectorShape shape ? shape.Lanes - 1 : 0;
        return opcode switch
        {
            >= 8 and <= 25 or 66 => Lanes(instruction),
            42 or 43 or >= 30 and <= 40 or 60 or 69 => Lanes(Llvm.LLVMGetOperand(instruction, 0)),
            46 => Lanes(Llvm.LLVMGetOperand(instruction, 0)),
            45 when Llvm.LLVMGetCalledValue(instruction) is nint target && Llvm.LLVMIsAFunction(target) != 0 &&
                Llvm.Name(target).StartsWith("llvm.", StringComparison.Ordinal) =>
                Math.Max(Lanes(instruction), Llvm.LLVMGetNumArgOperands(instruction) == 0 ? 0 : Lanes(Llvm.LLVMGetOperand(instruction, 0))),
            _ => 0
        };
    }

    /// <summary>Why the lowering does not translate the instruction, or null when it does or the instruction has no vectors.</summary>
    internal static string? Unsupported(TypeSystem types, nint instruction)
    {
        int opcode = Llvm.LLVMGetInstructionOpcode(instruction);
        bool vectors = false;
        nint result = Llvm.LLVMTypeOf(instruction);
        foreach (nint type in Enumerable.Range(0, Llvm.LLVMGetNumOperands(instruction)).Select(index => Llvm.LLVMTypeOf(Llvm.LLVMGetOperand(instruction, (uint)index))).Append(result))
        {
            if (TypeSystem.Kind(type) != 13) continue;
            if (types.Vector(type) is not VectorShape shape) return $"lanes of {Llvm.PrintType(type)}";
            // Masks are read, written and converted to integers as bits in one ulong.
            if (shape is { Mask: true, Lanes: > 64 }) return $"more than 64 i1 lanes in {Llvm.PrintType(type)}";
            vectors = true;
        }
        if (!vectors) return null;
        switch (opcode)
        {
            case 29: return "vector getelementptr";
            case 27 or 28 when Llvm.LLVMGetOrdering(instruction) != 0: return "atomic vector access";
            case 56 or 57: return "atomic vector access";
            case 49: return "va_arg of a vector";
            case 14 or 15 or 17 or 18 or 19 or 20 or 21 or 22 when types.Vector(result) is { Mask: true }: return "i1 vector division or shift";
            case 33 or 34 when types.Vector(result) is { Mask: true }: return "conversion of floating-point lanes to i1";
            case 35 or 36 when types.Vector(Llvm.LLVMTypeOf(Llvm.LLVMGetOperand(instruction, 0))) is { Mask: true }: return "conversion of i1 lanes to floating point";
            case 45:
                nint target = Llvm.LLVMGetCalledValue(instruction);
                string name = Llvm.LLVMIsAFunction(target) != 0 ? Llvm.Name(target) : "";
                if (name.StartsWith("llvm.", StringComparison.Ordinal) && !Intrinsics(name, result))
                    return name;
                return null;
        }
        return null;
    }

    /// <summary>The vector intrinsics <see cref="Intrinsic"/> translates; the floating-point reductions are lowered lane by
    /// lane by the function emitter.</summary>
    private static bool Intrinsics(string name, nint result)
    {
        string operation = name.Split('.')[1];
        if (name.StartsWith("llvm.vector.reduce.", StringComparison.Ordinal))
            return name.Split('.')[3] is "add" or "mul" or "and" or "or" or "xor" or "smax" or "smin" or "umax" or "umin" or
                "fadd" or "fmul" or "fmax" or "fmin" or "fmaximum" or "fminimum";
        bool floating = TypeSystem.Kind(result) == 13 && TypeSystem.Kind(Llvm.LLVMGetElementType(result)) is 2 or 3;
        return operation switch
        {
            "smin" or "smax" or "umin" or "umax" or "abs" => true,
            "fabs" or "sqrt" or "fma" or "fmuladd" or "minnum" or "maxnum" or "minimum" or "maximum" or "copysign" or
                "floor" or "ceil" or "trunc" or "roundeven" or "rint" or "nearbyint" or "round" => floating,
            "sadd" or "uadd" or "ssub" or "usub" => name.Contains(".sat.", StringComparison.Ordinal),
            _ => false
        };
    }

    // ---- Vector128 API ----

    /// <summary>The Vector128 method with these parameter types: a non-generic overload, or the generic method instantiated
    /// with <paramref name="lane"/>.</summary>
    private static MethodInfo Api(string name, Type? lane, params Type[] parameters) =>
        Methods.GetOrAdd((name, lane, string.Join(",", parameters.Select(parameter => parameter.FullName))), _ =>
        {
            MethodInfo[] candidates = typeof(Vector128).GetMethods(BindingFlags.Public | BindingFlags.Static).Where(method => method.Name == name)
                .OrderBy(method => method.IsGenericMethodDefinition).ToArray();
            foreach (MethodInfo method in candidates)
            {
                MethodInfo candidate = method;
                if (method.IsGenericMethodDefinition)
                {
                    if (lane is null || method.GetGenericArguments().Length != 1) continue;
                    try { candidate = method.MakeGenericMethod(lane); }
                    catch (ArgumentException) { continue; }
                }
                if (candidate.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(parameters))
                    return candidate;
            }
            throw new MissingMethodException($"Vector128.{name}({string.Join(", ", parameters.Select(parameter => parameter.Name))})");
        });

    private static Type V(Type lane) => typeof(Vector128<>).MakeGenericType(lane);
    private static Type V64(Type lane) => typeof(Vector64<>).MakeGenericType(lane);

    /// <summary>A generic Vector128 operation on vectors of <paramref name="lane"/>, such as Add or ConditionalSelect.</summary>
    private void Call(string name, Type lane, int arity) => il.Emit(OpCodes.Call, Api(name, lane, Enumerable.Repeat(V(lane), arity).ToArray()));

    private void As(Type from, Type to)
    {
        if (from == to) return;
        il.Emit(OpCodes.Call, Methods.GetOrAdd(("As", from, to.FullName!), _ =>
            typeof(Vector128).GetMethods().Single(method => method.Name == "As" && method.GetGenericArguments().Length == 2).MakeGenericMethod(from, to)));
    }

    private void Zero(Type lane) => il.Emit(OpCodes.Call, V(lane).GetProperty("Zero")!.GetMethod!);
    private void AllBitsSet(Type lane) => il.Emit(OpCodes.Call, V(lane).GetProperty("AllBitsSet")!.GetMethod!);

    private static Type Unsigned(Type lane) =>
        lane == typeof(sbyte) ? typeof(byte) : lane == typeof(short) ? typeof(ushort) : lane == typeof(int) ? typeof(uint) : lane == typeof(long) ? typeof(ulong) : lane;

    private static Type Signed(int bits) => bits switch { 8 => typeof(sbyte), 16 => typeof(short), 32 => typeof(int), _ => typeof(long) };

    /// <summary>The integer lane of a mask that compares lanes of this type.</summary>
    private static Type IntegerLane(Type lane) => lane == typeof(float) ? typeof(int) : lane == typeof(double) ? typeof(long) : lane;

    private LocalBuilder Rent(Type type)
    {
        if (temporaries.TryGetValue(type, out Stack<LocalBuilder>? pool) && pool.TryPop(out LocalBuilder? local)) return local;
        return il.DeclareLocal(type);
    }

    private void Release(LocalBuilder local)
    {
        if (!temporaries.TryGetValue(local.LocalType, out Stack<LocalBuilder>? pool)) temporaries.Add(local.LocalType, pool = new());
        pool.Push(local);
    }

    /// <summary>Pushes a value of the shape built fragment by fragment: the fragment itself, or the fragments stored
    /// into a temporary struct that is then loaded.</summary>
    private void Build(VectorShape shape, Action<int> fragment)
    {
        if (shape.Fragments == 1)
        {
            fragment(0);
            return;
        }
        LocalBuilder temporary = Rent(shape.Cil);
        for (int index = 0; index < shape.Fragments; index++)
        {
            il.Emit(OpCodes.Ldloca, temporary);
            fragment(index);
            il.Emit(OpCodes.Stfld, shape.Fields[index]);
        }
        il.Emit(OpCodes.Ldloc, temporary);
        Release(temporary);
    }

    private VectorShape Shape(nint value) => types.Vector(Llvm.LLVMTypeOf(value)) ?? throw new NotSupportedException($"Unsupported vector type: {Llvm.PrintType(Llvm.LLVMTypeOf(value))}");

    private static bool IsConstant(nint value) => Llvm.LLVMIsConstant(value) != 0 && Llvm.LLVMIsAConstantExpr(value) == 0 &&
        Llvm.LLVMIsAGlobalVariable(value) == 0 && Llvm.LLVMIsAFunction(value) == 0 && Llvm.LLVMIsAGlobalAlias(value) == 0;

    /// <summary>Pushes fragment <paramref name="index"/> of a vector value in its own shape.</summary>
    private void Fragment(nint value, int index)
    {
        VectorShape shape = Shape(value);
        if (IsConstant(value))
        {
            ConstantFragment(value, shape, index);
            return;
        }
        values.Load(value);
        if (shape.Fragments > 1) il.Emit(OpCodes.Ldfld, shape.Fields[index]);
    }

    // ---- Constants ----

    internal void Constant(nint value, VectorShape shape) => Build(shape, index => ConstantFragment(value, shape, index));

    /// <summary>Fragment <paramref name="index"/> of a constant vector, as Vector128.Create of its lanes, which RyuJIT folds
    /// into a constant; lanes that are constant expressions (addresses) are computed. Padding and undefined lanes are zero.</summary>
    private void ConstantFragment(nint value, VectorShape shape, int index)
    {
        int first = index * shape.PerFragment;
        if (Llvm.LLVMIsNull(value) != 0 || Llvm.LLVMIsUndef(value) != 0 || Llvm.LLVMIsPoison(value) != 0)
        {
            Zero(shape.Lane);
            return;
        }
        nint[] lanes = Enumerable.Range(first, Math.Min(shape.PerFragment, shape.Lanes - first))
            .Select(lane => Llvm.LLVMGetAggregateElement(value, (uint)lane)).ToArray();
        bool Undefined(nint lane) => lane == 0 || Llvm.LLVMIsUndef(lane) != 0 || Llvm.LLVMIsPoison(lane) != 0;
        bool Literal(nint lane) => Undefined(lane) || Llvm.LLVMIsAConstantInt(lane) != 0 || Llvm.LLVMIsAConstantFP(lane) != 0 || Llvm.LLVMIsNull(lane) != 0;
        long Bits(nint lane)
        {
            if (Undefined(lane)) return 0;
            if (Llvm.LLVMIsAConstantFP(lane) != 0)
            {
                byte[] bits = Llvm.Bits(lane);
                return shape.Lane == typeof(float) ? BitConverter.ToInt32(bits) : BitConverter.ToInt64(bits);
            }
            if (Llvm.LLVMIsAConstantInt(lane) == 0) return 0;
            long number = Llvm.LLVMConstIntGetSExtValue(lane);
            return shape.Mask ? -(number & 1) : number;
        }
        if (lanes.All(Literal))
        {
            long[] bits = lanes.Select(Bits).ToArray();
            if (bits.All(lane => lane == 0))
            {
                Zero(shape.Lane);
                return;
            }
            if (bits.All(lane => lane == bits[0]))
            {
                Scalar(shape.Lane, bits[0]);
                il.Emit(OpCodes.Call, Api("Create", null, shape.Lane));
                return;
            }
            for (int lane = 0; lane < shape.PerFragment; lane++)
                Scalar(shape.Lane, lane < bits.Length ? bits[lane] : 0);
        }
        else
        {
            for (int lane = 0; lane < shape.PerFragment; lane++)
            {
                if (lane >= lanes.Length || Literal(lanes[lane]))
                {
                    Scalar(shape.Lane, lane < lanes.Length ? Bits(lanes[lane]) : 0);
                    continue;
                }
                values.Load(lanes[lane]);
                if (shape.Lane == typeof(long)) il.Emit(OpCodes.Conv_I8);
            }
        }
        il.Emit(OpCodes.Call, Api("Create", null, Enumerable.Repeat(shape.Lane, shape.PerFragment).ToArray()));
    }

    /// <summary>Pushes a lane constant from its bits. Float NaNs go through their bits, because RyuJIT holds float constants
    /// as doubles and would quiet a signaling NaN.</summary>
    private void Scalar(Type lane, long bits)
    {
        if (lane == typeof(float))
        {
            float number = BitConverter.Int32BitsToSingle((int)bits);
            if (float.IsNaN(number))
            {
                il.Emit(OpCodes.Ldc_I4, (int)bits);
                il.Emit(OpCodes.Call, typeof(MathIntrinsics).GetMethod(nameof(MathIntrinsics.SingleFromBits))!);
            }
            else
                il.Emit(OpCodes.Ldc_R4, number);
        }
        else if (lane == typeof(double))
            il.Emit(OpCodes.Ldc_R8, BitConverter.Int64BitsToDouble(bits));
        else if (lane == typeof(long))
            il.Emit(OpCodes.Ldc_I8, bits);
        else
            il.Emit(OpCodes.Ldc_I4, unchecked((int)bits));
    }

    // ---- Memory ----

    /// <summary>Reads a vector from the address on the stack, touching exactly its store size (8 bytes for a 64-bit
    /// vector, 12 for three 32-bit lanes); an i1 vector is read as LLVM stores it, one bit per lane.</summary>
    internal void Read(nint type, VectorShape shape, bool isVolatile)
    {
        long size = types.StoreSize(type);
        LocalBuilder address = Rent(typeof(nint));
        il.Emit(OpCodes.Stloc, address);
        if (shape.Mask)
        {
            LocalBuilder bits = Rent(typeof(ulong));
            il.Emit(OpCodes.Ldloc, address);
            il.Emit(OpCodes.Ldc_I4, checked((int)size));
            il.Emit(OpCodes.Call, typeof(Vectors).GetMethod(nameof(Vectors.LoadBits))!);
            if (isVolatile) il.Emit(OpCodes.Call, typeof(Thread).GetMethod(nameof(Thread.MemoryBarrier))!);
            il.Emit(OpCodes.Stloc, bits);
            Build(shape, index =>
            {
                il.Emit(OpCodes.Ldloc, bits);
                MaskFromBits(shape, index);
            });
            Release(bits);
        }
        else
            Build(shape, index =>
            {
                il.Emit(OpCodes.Ldloc, address);
                values.Offset(16L * index);
                int bytes = (int)Math.Min(16, size - 16L * index);
                if (bytes == 16)
                {
                    if (isVolatile) il.Emit(OpCodes.Volatile);
                    il.Emit(OpCodes.Unaligned, (byte)1);
                    il.Emit(OpCodes.Ldobj, shape.Fragment);
                    return;
                }
                il.Emit(OpCodes.Ldc_I4, bytes);
                il.Emit(OpCodes.Call, typeof(Vectors).GetMethod(nameof(Vectors.Load))!);
                if (isVolatile) il.Emit(OpCodes.Call, typeof(Thread).GetMethod(nameof(Thread.MemoryBarrier))!);
                As(typeof(byte), shape.Lane);
            });
        Release(address);
    }

    /// <summary>Writes the vector on the stack to the address below it, exactly its store size.</summary>
    internal void Write(nint type, VectorShape shape, bool isVolatile)
    {
        long size = types.StoreSize(type);
        LocalBuilder value = Rent(shape.Cil), address = Rent(typeof(nint));
        il.Emit(OpCodes.Stloc, value);
        il.Emit(OpCodes.Stloc, address);
        if (shape.Mask)
        {
            il.Emit(OpCodes.Ldloc, address);
            MaskBits(shape, index =>
            {
                il.Emit(OpCodes.Ldloc, value);
                if (shape.Fragments > 1) il.Emit(OpCodes.Ldfld, shape.Fields[index]);
            });
            il.Emit(OpCodes.Ldc_I4, checked((int)size));
            if (isVolatile) il.Emit(OpCodes.Call, typeof(Thread).GetMethod(nameof(Thread.MemoryBarrier))!);
            il.Emit(OpCodes.Call, typeof(Vectors).GetMethod(nameof(Vectors.StoreBits))!);
        }
        else
            for (int index = 0; index < shape.Fragments; index++)
            {
                il.Emit(OpCodes.Ldloc, address);
                values.Offset(16L * index);
                il.Emit(OpCodes.Ldloc, value);
                if (shape.Fragments > 1) il.Emit(OpCodes.Ldfld, shape.Fields[index]);
                int bytes = (int)Math.Min(16, size - 16L * index);
                if (bytes == 16)
                {
                    if (isVolatile) il.Emit(OpCodes.Volatile);
                    il.Emit(OpCodes.Unaligned, (byte)1);
                    il.Emit(OpCodes.Stobj, shape.Fragment);
                    continue;
                }
                As(shape.Lane, typeof(byte));
                il.Emit(OpCodes.Ldc_I4, bytes);
                if (isVolatile) il.Emit(OpCodes.Call, typeof(Thread).GetMethod(nameof(Thread.MemoryBarrier))!);
                il.Emit(OpCodes.Call, typeof(Vectors).GetMethod(nameof(Vectors.Store))!);
            }
        Release(value);
        Release(address);
    }

    /// <summary>Turns the typed vector on the stack into the bytes a function returns it as (<see cref="TypeSystem.Return"/>).</summary>
    internal void ToBoundary(nint type)
    {
        VectorShape shape = types.Vector(type)!;
        LocalBuilder value = Rent(shape.Cil), bytes = Rent(types.Return(type));
        il.Emit(OpCodes.Stloc, value);
        il.Emit(OpCodes.Ldloca, bytes);
        il.Emit(OpCodes.Initobj, bytes.LocalType);
        il.Emit(OpCodes.Ldloca, bytes);
        il.Emit(OpCodes.Conv_U);
        il.Emit(OpCodes.Ldloc, value);
        Write(type, shape, isVolatile: false);
        il.Emit(OpCodes.Ldloc, bytes);
        Release(value);
        Release(bytes);
    }

    /// <summary>Turns the returned bytes of a vector on the stack into its typed value.</summary>
    internal void FromBoundary(nint type)
    {
        LocalBuilder bytes = Rent(types.Return(type));
        il.Emit(OpCodes.Stloc, bytes);
        il.Emit(OpCodes.Ldloca, bytes);
        il.Emit(OpCodes.Conv_U);
        Read(type, types.Vector(type)!, isVolatile: false);
        Release(bytes);
    }

    /// <summary>Turns the lane bits (bit i for lane i, as a ulong on the stack) into fragment <paramref name="index"/> of a mask.</summary>
    private void MaskFromBits(VectorShape shape, int index)
    {
        if (index > 0)
        {
            il.Emit(OpCodes.Ldc_I4, index * shape.PerFragment);
            il.Emit(OpCodes.Shr_Un);
        }
        il.Emit(OpCodes.Call, typeof(Vectors).GetMethod(shape.LaneBits switch { 8 => nameof(Vectors.Mask8), 16 => nameof(Vectors.Mask16), 32 => nameof(Vectors.Mask32), _ => nameof(Vectors.Mask64) })!);
    }

    /// <summary>Pushes the lanes of a mask as bits of a ulong, bit i for lane i and zero above the last lane.</summary>
    private void MaskBits(VectorShape shape, Action<int> fragment)
    {
        for (int index = 0; index < shape.Fragments; index++)
        {
            fragment(index);
            il.Emit(OpCodes.Call, Api("ExtractMostSignificantBits", shape.Lane, V(shape.Lane)));
            il.Emit(OpCodes.Conv_U8);
            if (index > 0)
            {
                il.Emit(OpCodes.Ldc_I4, index * shape.PerFragment);
                il.Emit(OpCodes.Shl);
                il.Emit(OpCodes.Or);
            }
        }
        if (shape.Lanes < 64)
        {
            il.Emit(OpCodes.Ldc_I8, (1L << shape.Lanes) - 1);
            il.Emit(OpCodes.And);
        }
    }

    // ---- Masks ----

    /// <summary>The shape a comparison produces: lanes of the compared width, all ones or zero.</summary>
    private VectorShape NaturalMask(nint compare)
    {
        VectorShape operand = Shape(Llvm.LLVMGetOperand(compare, 0));
        return types.Shape(operand.Lanes, IntegerLane(operand.Lane), mask: true);
    }

    private static bool IsCompare(nint value) => Llvm.LLVMIsAInstruction(value) != 0 && Llvm.LLVMGetInstructionOpcode(value) is 42 or 43;

    /// <summary>
    /// Pushes fragment <paramref name="index"/> of the mask <paramref name="mask"/> in shape <paramref name="want"/> (the same
    /// lanes, another lane width). A comparison whose own lanes have that width is computed again from its operands rather
    /// than converted from its stored form: comparisons are pure and their operands are locals, so a vector compare and
    /// select of 256 bits stay two fragments of the compared width.
    /// </summary>
    private void LoadMask(nint mask, VectorShape want, int index)
    {
        VectorShape stored = Shape(mask);
        if (IsConstant(mask))
        {
            ConstantFragment(mask, want with { Mask = true }, index);
            return;
        }
        if (stored.LaneBits == want.LaneBits)
        {
            Fragment(mask, index);
            return;
        }
        if (IsCompare(mask) && NaturalMask(mask).LaneBits == want.LaneBits)
        {
            CompareFragment(mask, index);
            return;
        }
        Resize(stored, want, fragment => Fragment(mask, fragment), index, signed: true);
    }

    // ---- Lane width changes ----

    /// <summary>
    /// Pushes fragment <paramref name="index"/> of shape <paramref name="to"/> from the fragments <paramref name="source"/>
    /// pushes in shape <paramref name="from"/>, which has the same lanes at another width: integers are truncated or
    /// extended (with their sign when <paramref name="signed"/>), floats widened to doubles or doubles narrowed to floats.
    /// </summary>
    private void Resize(VectorShape from, VectorShape to, Action<int> source, int index, bool signed)
    {
        bool floating = from.Lane == typeof(float) || from.Lane == typeof(double);
        if (from.LaneBits == to.LaneBits)
        {
            source(index);
            As(from.Lane, to.Lane);
            return;
        }
        Type Lane(int bits) => floating ? bits == 32 ? typeof(float) : typeof(double) : signed ? Signed(bits) : Unsigned(Signed(bits));
        if (to.LaneBits < from.LaneBits)
        {
            int levels = System.Numerics.BitOperations.Log2((uint)(from.LaneBits / to.LaneBits));
            Narrow(levels, index);
            As(Lane(to.LaneBits), to.Lane);

            void Narrow(int level, int fragment)
            {
                if (level == 0)
                {
                    if (fragment < from.Fragments)
                    {
                        source(fragment);
                        As(from.Lane, Lane(from.LaneBits));
                    }
                    else
                        Zero(Lane(from.LaneBits));
                    return;
                }
                Type lane = Lane(from.LaneBits >> (level - 1));
                Narrow(level - 1, 2 * fragment);
                Narrow(level - 1, 2 * fragment + 1);
                il.Emit(OpCodes.Call, Api("Narrow", null, V(lane), V(lane)));
            }
        }
        else
        {
            int levels = System.Numerics.BitOperations.Log2((uint)(to.LaneBits / from.LaneBits));
            Widen(levels, index);
            As(Lane(to.LaneBits), to.Lane);

            void Widen(int level, int fragment)
            {
                if (level == 0)
                {
                    source(fragment);
                    As(from.Lane, Lane(from.LaneBits));
                    return;
                }
                Widen(level - 1, fragment / 2);
                Type lane = Lane(from.LaneBits << (level - 1));
                il.Emit(OpCodes.Call, Api(fragment % 2 == 0 ? "WidenLower" : "WidenUpper", null, V(lane)));
            }
        }
    }

    // ---- Instructions ----

    internal void Binary(nint value, int opcode)
    {
        nint left = Llvm.LLVMGetOperand(value, 0), right = Llvm.LLVMGetOperand(value, 1);
        VectorShape shape = Shape(value);
        if (shape.Mask)
        {
            string logic = opcode switch { 23 or 12 => "BitwiseAnd", 24 => "BitwiseOr", 25 or 8 or 10 => "Xor", _ => throw new NotSupportedException($"i1 vector operation: {Llvm.Print(value)}") };
            Build(shape, index =>
            {
                Fragment(left, index);
                Fragment(right, index);
                Call(logic, shape.Lane, 2);
            });
            return;
        }
        if (opcode is 14 or 15 or 17 or 18 or 19)
        {
            // No SIMD instruction divides integers, and a padding lane must not divide by zero: lane by lane, as scalars.
            Type lane = opcode is 14 or 17 ? Unsigned(shape.Lane) : shape.Lane;
            MethodInfo helper = typeof(Vectors).GetMethod(opcode is 14 or 15 ? nameof(Vectors.Divide) : nameof(Vectors.Remainder))!.MakeGenericMethod(lane);
            Build(shape, index =>
            {
                Fragment(left, index);
                As(shape.Lane, lane);
                Fragment(right, index);
                As(shape.Lane, lane);
                il.Emit(OpCodes.Ldc_I4, Math.Min(shape.PerFragment, shape.Lanes - index * shape.PerFragment));
                il.Emit(OpCodes.Call, helper);
                As(lane, shape.Lane);
            });
            return;
        }
        if (opcode is 20 or 21 or 22)
        {
            Type lane = opcode == 21 ? Unsigned(shape.Lane) : shape.Lane;
            string name = opcode switch { 20 => "ShiftLeft", 21 => "ShiftRightLogical", _ => "ShiftRightArithmetic" };
            if (SplatScalar(right) is nint count)
            {
                LocalBuilder amount = Rent(typeof(int));
                LoadLaneScalar(count);
                if (shape.LaneBits == 64) il.Emit(OpCodes.Conv_I4);
                il.Emit(OpCodes.Stloc, amount);
                Build(shape, index =>
                {
                    Fragment(left, index);
                    As(shape.Lane, lane);
                    il.Emit(OpCodes.Ldloc, amount);
                    il.Emit(OpCodes.Call, Api(name, null, V(lane), typeof(int)));
                    As(lane, shape.Lane);
                });
                Release(amount);
                return;
            }
            MethodInfo helper = typeof(Vectors).GetMethod(name)!.MakeGenericMethod(lane);
            Build(shape, index =>
            {
                Fragment(left, index);
                As(shape.Lane, lane);
                Fragment(right, index);
                As(shape.Lane, lane);
                il.Emit(OpCodes.Call, helper);
                As(lane, shape.Lane);
            });
            return;
        }
        string operation = opcode switch
        {
            8 or 9 => "Add",
            10 or 11 => "Subtract",
            12 or 13 => "Multiply",
            16 => "Divide",
            23 => "BitwiseAnd",
            24 => "BitwiseOr",
            25 => "Xor",
            _ => throw new NotSupportedException($"Vector operation: {Llvm.Print(value)}")
        };
        Build(shape, index =>
        {
            Fragment(left, index);
            Fragment(right, index);
            Call(operation, shape.Lane, 2);
        });
    }

    /// <summary>The scalar every lane of a splat holds: a constant vector with equal lanes, or the insertelement and
    /// zero-mask shufflevector LLVM writes for a splat of a variable. Null for other vectors.</summary>
    private static nint? SplatScalar(nint vector)
    {
        if (IsConstant(vector))
        {
            // A zero vector stands for its own zero lane, which LoadLaneScalar loads as 0.
            if (Llvm.LLVMIsNull(vector) != 0) return vector;
            uint lanes = Llvm.LLVMGetVectorSize(Llvm.LLVMTypeOf(vector));
            nint first = Llvm.LLVMGetAggregateElement(vector, 0);
            if (first == 0 || Llvm.LLVMIsAConstantInt(first) == 0) return null;
            for (uint lane = 1; lane < lanes; lane++)
            {
                nint element = Llvm.LLVMGetAggregateElement(vector, lane);
                if (element == 0 || Llvm.LLVMIsAConstantInt(element) == 0 || Llvm.LLVMConstIntGetSExtValue(element) != Llvm.LLVMConstIntGetSExtValue(first))
                    return null;
            }
            return first;
        }
        if (Llvm.LLVMIsAInstruction(vector) == 0 || Llvm.LLVMGetInstructionOpcode(vector) != 52) return null;
        uint count = Llvm.LLVMGetNumMaskElements(vector);
        int selected = Llvm.LLVMGetMaskValue(vector, 0);
        for (uint lane = 0; lane < count; lane++)
            if (Llvm.LLVMGetMaskValue(vector, lane) != selected) return null;
        nint source = Llvm.LLVMGetOperand(vector, 0);
        if (selected < 0 || Llvm.LLVMIsAInstruction(source) == 0 || Llvm.LLVMGetInstructionOpcode(source) != 51) return null;
        nint position = Llvm.LLVMGetOperand(source, 2);
        return Llvm.LLVMIsAConstantInt(position) != 0 && Llvm.LLVMConstIntGetSExtValue(position) == selected ? Llvm.LLVMGetOperand(source, 1) : null;
    }

    /// <summary>Pushes a lane scalar (a constant vector's element or a splatted variable) as its CIL value.</summary>
    private void LoadLaneScalar(nint scalar)
    {
        if (Llvm.LLVMTypeOf(scalar) is nint type && TypeSystem.Kind(type) == 13)
        {
            Scalar(typeof(int), 0);
            return;
        }
        values.Load(scalar);
    }

    internal void Negate(nint value)
    {
        VectorShape shape = Shape(value);
        Build(shape, index =>
        {
            Fragment(Llvm.LLVMGetOperand(value, 0), index);
            Scalar(shape.Lane, shape.Lane == typeof(float) ? int.MinValue : long.MinValue);
            il.Emit(OpCodes.Call, Api("Create", null, shape.Lane));
            Call("Xor", shape.Lane, 2);
        });
    }

    internal void Compare(nint value)
    {
        VectorShape result = Shape(value), natural = NaturalMask(value);
        Build(result, index =>
        {
            if (natural.LaneBits == result.LaneBits) CompareFragment(value, index);
            else Resize(natural, result, fragment => CompareFragment(value, fragment), index, signed: true);
        });
    }

    /// <summary>Pushes fragment <paramref name="index"/> of a vector comparison in its natural shape (<see cref="NaturalMask"/>).</summary>
    private void CompareFragment(nint value, int index)
    {
        nint left = Llvm.LLVMGetOperand(value, 0), right = Llvm.LLVMGetOperand(value, 1);
        VectorShape operand = Shape(left);
        Type mask = IntegerLane(operand.Lane);
        if (Llvm.LLVMGetInstructionOpcode(value) == 42)
        {
            int predicate = Llvm.LLVMGetICmpPredicate(value);
            Type lane = predicate is >= 34 and <= 37 ? Unsigned(operand.Lane) : operand.Lane;
            Fragment(left, index);
            As(operand.Lane, lane);
            Fragment(right, index);
            As(operand.Lane, lane);
            Call(predicate switch
            {
                32 or 33 => "Equals",
                34 or 38 => "GreaterThan",
                35 or 39 => "GreaterThanOrEqual",
                36 or 40 => "LessThan",
                _ => "LessThanOrEqual"
            }, lane, 2);
            if (predicate == 33) Call("OnesComplement", lane, 1);
            As(lane, mask);
            return;
        }
        int floating = Llvm.LLVMGetFCmpPredicate(value);
        Type type = operand.Lane;
        switch (floating)
        {
            case 0:
                Zero(mask);
                return;
            case 15:
                AllBitsSet(mask);
                return;
            case 7 or 8:
                // ord: each operand equals itself; uno is its complement.
                Fragment(left, index);
                Fragment(left, index);
                Call("Equals", type, 2);
                Fragment(right, index);
                Fragment(right, index);
                Call("Equals", type, 2);
                Call("BitwiseAnd", type, 2);
                break;
            case 6 or 9:
                // one: less or greater, which is false for NaN; ueq is its complement.
                Fragment(left, index);
                Fragment(right, index);
                Call("LessThan", type, 2);
                Fragment(left, index);
                Fragment(right, index);
                Call("GreaterThan", type, 2);
                Call("BitwiseOr", type, 2);
                break;
            default:
                // The ordered predicates are the IEEE comparisons, false when either operand is NaN; each unordered one
                // is the complement of the ordered opposite (ugt = !ole, une = !oeq).
                Fragment(left, index);
                Fragment(right, index);
                Call(floating switch
                {
                    1 or 14 => "Equals",
                    2 or 13 => "GreaterThan",
                    3 or 12 => "GreaterThanOrEqual",
                    4 or 11 => "LessThan",
                    _ => "LessThanOrEqual"
                }, type, 2);
                break;
        }
        if (floating is 8 or 9 or >= 10 and <= 14) Call("OnesComplement", type, 1);
        As(type, mask);
    }

    /// <summary>A select on a vector condition: ConditionalSelect with the mask at the selected lanes' width.</summary>
    internal void Select(nint value)
    {
        nint condition = Llvm.LLVMGetOperand(value, 0), whenTrue = Llvm.LLVMGetOperand(value, 1), whenFalse = Llvm.LLVMGetOperand(value, 2);
        VectorShape shape = Shape(value);
        VectorShape mask = shape.Mask ? shape : types.Shape(shape.Lanes, IntegerLane(shape.Lane), mask: true);
        Build(shape, index =>
        {
            LoadMask(condition, mask, index);
            As(mask.Lane, shape.Lane);
            Fragment(whenTrue, index);
            Fragment(whenFalse, index);
            Call("ConditionalSelect", shape.Lane, 3);
        });
    }

    internal void Cast(nint value, int opcode)
    {
        nint source = Llvm.LLVMGetOperand(value, 0);
        nint sourceType = Llvm.LLVMTypeOf(source), destinationType = Llvm.LLVMTypeOf(value);
        VectorShape? from = types.Vector(sourceType), to = types.Vector(destinationType);
        if (opcode == 41)
        {
            Bitcast(source, from, to, sourceType, destinationType);
            return;
        }
        if (from is null || to is null) throw new NotSupportedException($"Vector cast: {Llvm.Print(value)}");
        switch (opcode)
        {
            case 30 when to.Mask:
                // trunc to i1: the low bit of each lane.
                VectorShape bit = types.Shape(from.Lanes, from.Lane, mask: true);
                Build(to, index => Resize(bit, to, fragment =>
                {
                    Fragment(source, fragment);
                    Scalar(from.Lane, 1);
                    il.Emit(OpCodes.Call, Api("Create", null, from.Lane));
                    Call("BitwiseAnd", from.Lane, 2);
                    Scalar(from.Lane, 1);
                    il.Emit(OpCodes.Call, Api("Create", null, from.Lane));
                    Call("Equals", from.Lane, 2);
                }, index, signed: true));
                return;
            case 31 or 32 when from.Mask:
                Build(to, index =>
                {
                    LoadMask(source, types.Shape(to.Lanes, to.Lane, mask: true), index);
                    if (opcode == 31)
                    {
                        Scalar(to.Lane, 1);
                        il.Emit(OpCodes.Call, Api("Create", null, to.Lane));
                        Call("BitwiseAnd", to.Lane, 2);
                    }
                });
                return;
            case 30 or 31 or 32 or 39 or 40 or 60 or 69:
                Build(to, index => Resize(from, to, fragment => Fragment(source, fragment), index, signed: opcode == 32));
                return;
            case 37 or 38:
                Build(to, index => Resize(from, to, fragment => Fragment(source, fragment), index, signed: true));
                return;
            case 33 or 34:
                FloatToInteger(source, from, to, signed: opcode == 34);
                return;
            case 35 or 36:
                IntegerToFloat(source, from, to, signed: opcode == 36);
                return;
        }
        throw new NotSupportedException($"Vector cast: {Llvm.Print(value)}");
    }

    /// <summary>fptosi and fptoui: converted at the source's width (Vector128's conversions saturate, and an out-of-range
    /// input is poison in LLVM, as for scalars, BE-12), float widened to double first for 64-bit results, then truncated
    /// or extended to the result's width.</summary>
    private void FloatToInteger(nint source, VectorShape from, VectorShape to, bool signed)
    {
        VectorShape wide = to.LaneBits == 64 && from.Lane == typeof(float) ? types.Shape(from.Lanes, typeof(double), mask: false) : from;
        Type converted = wide.Lane == typeof(float) ? signed || to.LaneBits < 32 ? typeof(int) : typeof(uint) : signed || to.LaneBits < 64 ? typeof(long) : typeof(ulong);
        string conversion = converted == typeof(int) ? "ConvertToInt32" : converted == typeof(uint) ? "ConvertToUInt32" : converted == typeof(long) ? "ConvertToInt64" : "ConvertToUInt64";
        VectorShape integers = types.Shape(from.Lanes, Signed(wide.LaneBits), mask: false);
        Build(to, index => Resize(integers, to, fragment =>
        {
            Resize(from, wide, part => Fragment(source, part), fragment, signed: true);
            il.Emit(OpCodes.Call, Api(conversion, null, V(wide.Lane)));
            As(converted, integers.Lane);
        }, index, signed));
    }

    /// <summary>sitofp and uitofp: integers extended to the result's width and converted, which is exact; 64-bit integers
    /// to float lane by lane, because converting through double would round twice.</summary>
    private void IntegerToFloat(nint source, VectorShape from, VectorShape to, bool signed)
    {
        if (from.LaneBits == 64 && to.Lane == typeof(float))
        {
            MethodInfo helper = typeof(Vectors).GetMethod(signed ? nameof(Vectors.SignedToSingle) : nameof(Vectors.UnsignedToSingle))!;
            Build(to, index =>
            {
                // Two 64-bit fragments give one fragment of four floats.
                for (int part = 0; part < 2; part++)
                {
                    if (2 * index + part < from.Fragments) Fragment(source, 2 * index + part);
                    else Zero(typeof(long));
                }
                il.Emit(OpCodes.Call, helper);
            });
            return;
        }
        VectorShape integers = types.Shape(from.Lanes, Signed(to.LaneBits), mask: false);
        Type lane = signed ? integers.Lane : Unsigned(integers.Lane);
        Build(to, index =>
        {
            Resize(from, integers, fragment => Fragment(source, fragment), index, signed);
            As(integers.Lane, lane);
            il.Emit(OpCodes.Call, Api(to.Lane == typeof(float) ? "ConvertToSingle" : "ConvertToDouble", null, V(lane)));
        });
    }

    private void Bitcast(nint source, VectorShape? from, VectorShape? to, nint sourceType, nint destinationType)
    {
        if (from is { Mask: false } && to is { Mask: false } && from.Fragments == to.Fragments && from.Lanes * from.LaneBits == to.Lanes * to.LaneBits)
        {
            Build(to, index =>
            {
                Fragment(source, index);
                As(from.Lane, to.Lane);
            });
            return;
        }
        if (from is { Mask: true } && TypeSystem.Kind(destinationType) == 8)
        {
            MaskBits(from, index => Fragment(source, index));
            if (TypeSystem.Width(destinationType) <= 32) il.Emit(OpCodes.Conv_I4);
            return;
        }
        if (to is { Mask: true } && TypeSystem.Kind(sourceType) == 8)
        {
            LocalBuilder bits = Rent(typeof(ulong));
            values.Load(source);
            il.Emit(OpCodes.Conv_U8);
            il.Emit(OpCodes.Stloc, bits);
            Build(to, index =>
            {
                il.Emit(OpCodes.Ldloc, bits);
                MaskFromBits(to, index);
            });
            Release(bits);
            return;
        }
        // Between a vector and a scalar of the same size, or vectors fragmented differently: through memory, where both
        // have LLVM's layout.
        long size = Math.Max(types.Size(sourceType), types.Size(destinationType));
        VectorShape buffer = types.Shape(checked((int)((size + 15) / 16 * 16)), typeof(sbyte), mask: false);
        LocalBuilder memory = Rent(buffer.Cil);
        il.Emit(OpCodes.Ldloca, memory);
        il.Emit(OpCodes.Conv_U);
        values.Load(source);
        values.StoreMemory(sourceType);
        il.Emit(OpCodes.Ldloca, memory);
        il.Emit(OpCodes.Conv_U);
        values.ReadMemory(destinationType);
        Release(memory);
    }

    internal void Extract(nint value)
    {
        nint vector = Llvm.LLVMGetOperand(value, 0), position = Llvm.LLVMGetOperand(value, 1);
        VectorShape shape = Shape(vector);
        Type lane = shape.Mask ? shape.Lane : shape.LaneBits < 32 ? Unsigned(shape.Lane) : shape.Lane;
        if (Llvm.LLVMIsAConstantInt(position) != 0)
        {
            long index = Llvm.LLVMConstIntGetSExtValue(position);
            if ((ulong)index >= (ulong)shape.Lanes)
            {
                values.Zero(Llvm.LLVMTypeOf(value));
                return;
            }
            Fragment(vector, (int)(index / shape.PerFragment));
            As(shape.Lane, lane);
            il.Emit(OpCodes.Ldc_I4, (int)(index % shape.PerFragment));
            il.Emit(OpCodes.Call, Api("GetElement", lane, V(lane), typeof(int)));
        }
        else
        {
            LocalBuilder copy = Rent(shape.Cil);
            values.Load(vector);
            il.Emit(OpCodes.Stloc, copy);
            il.Emit(OpCodes.Ldloca, copy);
            il.Emit(OpCodes.Conv_U);
            LaneOffset(position, shape);
            il.Emit(OpCodes.Ldobj, lane);
            Release(copy);
        }
        if (shape.Mask)
        {
            il.Emit(OpCodes.Ldc_I4_1);
            if (shape.LaneBits == 64) il.Emit(OpCodes.Conv_I8);
            il.Emit(OpCodes.And);
            if (shape.LaneBits == 64) il.Emit(OpCodes.Conv_I4);
        }
        else if (TypeSystem.Kind(Llvm.LLVMTypeOf(value)) == 12)
            il.Emit(OpCodes.Conv_I);
    }

    internal void Insert(nint value)
    {
        nint vector = Llvm.LLVMGetOperand(value, 0), element = Llvm.LLVMGetOperand(value, 1), position = Llvm.LLVMGetOperand(value, 2);
        VectorShape shape = Shape(value);
        if (Llvm.LLVMIsAConstantInt(position) != 0)
        {
            long index = Llvm.LLVMConstIntGetSExtValue(position);
            Build(shape, fragment =>
            {
                Fragment(vector, fragment);
                if ((ulong)index >= (ulong)shape.Lanes || index / shape.PerFragment != fragment) return;
                il.Emit(OpCodes.Ldc_I4, (int)(index % shape.PerFragment));
                LaneValue(element, shape);
                il.Emit(OpCodes.Call, Api("WithElement", shape.Lane, V(shape.Lane), typeof(int), shape.Lane));
            });
            return;
        }
        LocalBuilder copy = Rent(shape.Cil);
        values.Load(vector);
        il.Emit(OpCodes.Stloc, copy);
        il.Emit(OpCodes.Ldloca, copy);
        il.Emit(OpCodes.Conv_U);
        LaneOffset(position, shape);
        LaneValue(element, shape);
        il.Emit(OpCodes.Stobj, shape.Lane);
        il.Emit(OpCodes.Ldloc, copy);
        Release(copy);
    }

    /// <summary>Adds the byte offset of a variable lane index to the address on the stack. An index past the last lane
    /// yields poison in LLVM; it reads lane 0 here instead of memory beyond the vector.</summary>
    private void LaneOffset(nint position, VectorShape shape)
    {
        values.Load(position);
        if (TypeSystem.Width(Llvm.LLVMTypeOf(position)) > 64) il.Emit(OpCodes.Call, typeof(WideInteger).GetMethod(nameof(WideInteger.Low))!);
        il.Emit(OpCodes.Conv_U8);
        LocalBuilder index = Rent(typeof(ulong));
        il.Emit(OpCodes.Stloc, index);
        il.Emit(OpCodes.Ldloc, index);
        il.Emit(OpCodes.Ldloc, index);
        il.Emit(OpCodes.Ldc_I8, (long)shape.Lanes);
        il.Emit(OpCodes.Clt_Un);
        il.Emit(OpCodes.Conv_U8);
        il.Emit(OpCodes.Neg);
        il.Emit(OpCodes.And);
        Release(index);
        il.Emit(OpCodes.Ldc_I8, (long)shape.LaneBits / 8);
        il.Emit(OpCodes.Mul);
        il.Emit(OpCodes.Conv_U);
        il.Emit(OpCodes.Add);
    }

    /// <summary>Pushes a scalar as a lane of the shape: pointers as 64-bit integers, i1 as all ones or zero.</summary>
    private void LaneValue(nint element, VectorShape shape)
    {
        values.Load(element);
        if (shape.Mask)
        {
            il.Emit(OpCodes.Neg);
            if (shape.LaneBits == 64) il.Emit(OpCodes.Conv_I8);
        }
        else if (TypeSystem.Kind(Llvm.LLVMTypeOf(element)) == 12)
            il.Emit(OpCodes.Conv_I8);
    }

    /// <summary>
    /// A shufflevector: each result fragment is classified by the source fragments its lanes come from. Lanes from one
    /// fragment are that fragment (in order) or a Shuffle with constant indices, which RyuJIT compiles to one instruction
    /// where the host has it (pshufd, shufps, pshufb, tbl); lanes from two fragments that are halves of each are Create
    /// of the halves (movlhps, unpckhpd), and any other mix is a ConditionalSelect of such shuffles. An i1 vector is
    /// shuffled as a mask at the result's lane width.
    /// </summary>
    internal void Shuffle(nint value)
    {
        nint first = Llvm.LLVMGetOperand(value, 0), second = Llvm.LLVMGetOperand(value, 1);
        VectorShape result = Shape(value), input = Shape(first);
        if (result.Mask)
            input = types.Shape(input.Lanes, result.Lane, mask: true);
        int sourceLanes = input.Lanes, per = result.PerFragment;
        int[] mask = Enumerable.Range(0, result.Lanes).Select(lane => Llvm.LLVMGetMaskValue(value, (uint)lane)).ToArray();
        void Source(int operand, int fragment)
        {
            nint vector = operand == 0 ? first : second;
            if (result.Mask) LoadMask(vector, input, fragment);
            else Fragment(vector, fragment);
        }
        Build(result, index =>
        {
            // (operand, fragment) -> the lane each result lane takes from it, -1 elsewhere.
            var sources = new Dictionary<(int Operand, int Fragment), int[]>();
            for (int lane = 0; lane < per; lane++)
            {
                int global = index * per + lane;
                if (global >= result.Lanes || mask[global] < 0) continue;
                int operand = mask[global] < sourceLanes ? 0 : 1, element = mask[global] % sourceLanes;
                var key = (operand, element / per);
                if (!sources.TryGetValue(key, out int[]? pattern)) sources.Add(key, pattern = Enumerable.Repeat(-1, per).ToArray());
                pattern[lane] = element % per;
            }
            if (sources.Count == 0)
            {
                Zero(result.Lane);
                return;
            }
            if (sources.Count == 2 && Halves(sources) is var (lower, upper))
            {
                Half(lower);
                Half(upper);
                il.Emit(OpCodes.Call, Api("Create", result.Lane, V64(result.Lane), V64(result.Lane)));
                return;
            }
            bool firstSource = true;
            foreach (((int operand, int fragment), int[] pattern) in sources)
            {
                if (!firstSource)
                {
                    // The lanes this source supplies, from the shuffled source, else the lanes gathered so far.
                    LocalBuilder gathered = Rent(result.Fragment);
                    il.Emit(OpCodes.Stloc, gathered);
                    for (int lane = 0; lane < per; lane++) Scalar(IntegerLane(result.Lane), pattern[lane] >= 0 ? -1 : 0);
                    il.Emit(OpCodes.Call, Api("Create", null, Enumerable.Repeat(IntegerLane(result.Lane), per).ToArray()));
                    As(IntegerLane(result.Lane), result.Lane);
                    Permute(operand, fragment, pattern);
                    il.Emit(OpCodes.Ldloc, gathered);
                    Call("ConditionalSelect", result.Lane, 3);
                    Release(gathered);
                }
                else
                    Permute(operand, fragment, pattern);
                firstSource = false;
            }
        });

        void Half((int Operand, int Fragment, bool Upper) half)
        {
            Source(half.Operand, half.Fragment);
            il.Emit(OpCodes.Call, Api(half.Upper ? "GetUpper" : "GetLower", result.Lane, V(result.Lane)));
        }

        void Permute(int operand, int fragment, int[] pattern)
        {
            Source(operand, fragment);
            if (pattern.Select((element, lane) => element < 0 || element == lane).All(identity => identity)) return;
            // Unused lanes repeat a used index, which keeps a broadcast a broadcast.
            int fill = pattern.First(element => element >= 0);
            Type indices = IntegerLane(result.Lane);
            for (int lane = 0; lane < per; lane++) Scalar(indices, pattern[lane] >= 0 ? pattern[lane] : pattern.All(element => element < 0 || element == fill) ? fill : lane);
            il.Emit(OpCodes.Call, Api("Create", null, Enumerable.Repeat(indices, per).ToArray()));
            il.Emit(OpCodes.Call, Api("Shuffle", null, V(result.Lane), V(indices)));
        }

        // Two sources, one filling the result's lower half and the other its upper half, each with one of its own
        // halves lane for lane.
        ((int, int, bool), (int, int, bool))? Halves(Dictionary<(int Operand, int Fragment), int[]> sources)
        {
            if (per < 2) return null;
            int half = per / 2;
            (int, int, bool)? lower = null, upper = null;
            foreach (((int operand, int fragment), int[] pattern) in sources)
            {
                bool inLower = pattern.Take(half).Any(element => element >= 0), inUpper = pattern.Skip(half).Any(element => element >= 0);
                if (inLower == inUpper) return null;
                int offset = inLower ? 0 : half;
                int? start = null;
                for (int lane = 0; lane < half; lane++)
                {
                    int element = pattern[offset + lane];
                    if (element < 0) continue;
                    if (start is null) start = element - lane;
                    else if (element - lane != start) return null;
                }
                if (start != 0 && start != half) return null;
                if (inLower) lower = (operand, fragment, start == half);
                else upper = (operand, fragment, start == half);
            }
            return lower is { } low && upper is { } high ? (low, high) : null;
        }
    }

    // ---- Reductions ----

    /// <summary>
    /// llvm.vector.reduce.*, which expand-reductions leaves in optnone (-O0) functions: integer reductions fold whole
    /// fragments first and then the lanes; fadd and fmul are ordered from the start value unless they may reassociate,
    /// in which case they halve the vector as expand-reductions and LLVM's backends do, so the result is the same; an i1
    /// vector is reduced as its bits.
    /// </summary>
    private void Reduce(nint instruction, string kind)
    {
        bool floating = kind is "fadd" or "fmul";
        nint vector = Llvm.LLVMGetOperand(instruction, floating ? 1u : 0u);
        nint type = Llvm.LLVMTypeOf(instruction);
        VectorShape shape = Shape(vector);
        if (shape.Mask)
        {
            MaskBits(shape, index => Fragment(vector, index));
            // i1 lanes: add and xor are parity, mul, and, smax and umin are "all", or, umax and smin are "any".
            if (kind is "add" or "xor")
                il.Emit(OpCodes.Call, typeof(System.Numerics.BitOperations).GetMethod(nameof(System.Numerics.BitOperations.PopCount), [typeof(ulong)])!);
            else if (kind is "mul" or "and" or "smax" or "umin")
            {
                il.Emit(OpCodes.Ldc_I8, shape.Lanes == 64 ? -1L : (1L << shape.Lanes) - 1);
                il.Emit(OpCodes.Ceq);
            }
            else
            {
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Conv_U8);
                il.Emit(OpCodes.Cgt_Un);
            }
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.And);
            return;
        }
        if (floating)
        {
            ReduceFloating(instruction, kind, vector, shape);
            return;
        }
        Type lane = kind is "umax" or "umin" || shape.LaneBits < 32 && kind is not ("smax" or "smin") ? Unsigned(shape.Lane) : shape.Lane;
        string vectorOperation = kind switch
        {
            "add" => "Add",
            "mul" => "Multiply",
            "and" => "BitwiseAnd",
            "or" => "BitwiseOr",
            "xor" => "Xor",
            "smax" or "umax" => "Max",
            _ => "Min"
        };
        void Combine()
        {
            switch (kind)
            {
                case "add": il.Emit(OpCodes.Add); break;
                case "mul": il.Emit(OpCodes.Mul); break;
                case "and": il.Emit(OpCodes.And); break;
                case "or": il.Emit(OpCodes.Or); break;
                case "xor": il.Emit(OpCodes.Xor); break;
                default:
                    // Narrow lanes are extended by GetElement as their view says, so int comparisons order them.
                    Type compared = shape.LaneBits < 32 ? typeof(int) : lane;
                    il.Emit(OpCodes.Call, typeof(Math).GetMethod(kind.EndsWith("max", StringComparison.Ordinal) ? "Max" : "Min", [compared, compared])!);
                    break;
            }
        }
        int lanes = shape.Lanes;
        LocalBuilder? folded = null;
        if (shape.Fragments > 1 && lanes == shape.PerFragment * shape.Fragments)
        {
            // Whole fragments combine lane for lane; only the lanes of one fragment remain.
            Fragment(vector, 0);
            As(shape.Lane, lane);
            for (int index = 1; index < shape.Fragments; index++)
            {
                Fragment(vector, index);
                As(shape.Lane, lane);
                Call(vectorOperation, lane, 2);
            }
            folded = Rent(V(lane));
            il.Emit(OpCodes.Stloc, folded);
            lanes = shape.PerFragment;
        }
        if (kind == "add" && lanes == shape.PerFragment)
        {
            if (folded is not null) il.Emit(OpCodes.Ldloc, folded);
            else
            {
                Fragment(vector, 0);
                As(shape.Lane, lane);
            }
            il.Emit(OpCodes.Call, Api("Sum", lane, V(lane)));
        }
        else
            for (int element = 0; element < lanes; element++)
            {
                if (folded is not null) il.Emit(OpCodes.Ldloc, folded);
                else
                {
                    Fragment(vector, element / shape.PerFragment);
                    As(shape.Lane, lane);
                }
                il.Emit(OpCodes.Ldc_I4, element % shape.PerFragment);
                il.Emit(OpCodes.Call, Api("GetElement", lane, V(lane), typeof(int)));
                if (element > 0) Combine();
            }
        if (folded is not null) Release(folded);
        values.Normalize(type);
    }

    private void ReduceFloating(nint instruction, string kind, nint vector, VectorShape shape)
    {
        OpCode operation = kind == "fadd" ? OpCodes.Add : OpCodes.Mul;
        OpCode round = shape.Lane == typeof(float) ? OpCodes.Conv_R4 : OpCodes.Conv_R8;
        values.Load(Llvm.LLVMGetOperand(instruction, 0));
        bool reassociate = (Llvm.LLVMGetFastMathFlags(instruction) & 1) != 0 && System.Numerics.BitOperations.IsPow2(shape.Lanes);
        if (!reassociate)
        {
            for (int element = 0; element < shape.Lanes; element++)
            {
                Fragment(vector, element / shape.PerFragment);
                il.Emit(OpCodes.Ldc_I4, element % shape.PerFragment);
                il.Emit(OpCodes.Call, Api("GetElement", shape.Lane, V(shape.Lane), typeof(int)));
                il.Emit(operation);
                il.Emit(round);
            }
            return;
        }
        // The upper half combined with the lower half until one lane is left: fragment j with fragment j + count / 2
        // while there are several, then the lanes of the last one by shuffles.
        string combine = kind == "fadd" ? "Add" : "Multiply";
        void Fold(int count, int index)
        {
            if (count == shape.Fragments)
            {
                Fragment(vector, index);
                return;
            }
            Fold(count * 2, index);
            Fold(count * 2, index + count);
            Call(combine, shape.Lane, 2);
        }
        Fold(1, 0);
        LocalBuilder folded = Rent(shape.Fragment);
        il.Emit(OpCodes.Stloc, folded);
        Type indices = IntegerLane(shape.Lane);
        for (int width = Math.Min(shape.Lanes, shape.PerFragment) / 2; width >= 1; width /= 2)
        {
            il.Emit(OpCodes.Ldloc, folded);
            il.Emit(OpCodes.Ldloc, folded);
            for (int lane = 0; lane < shape.PerFragment; lane++) Scalar(indices, lane < width ? lane + width : lane);
            il.Emit(OpCodes.Call, Api("Create", null, Enumerable.Repeat(indices, shape.PerFragment).ToArray()));
            il.Emit(OpCodes.Call, Api("Shuffle", null, V(shape.Lane), V(indices)));
            Call(combine, shape.Lane, 2);
            il.Emit(OpCodes.Stloc, folded);
        }
        il.Emit(OpCodes.Ldloc, folded);
        il.Emit(OpCodes.Call, Api("ToScalar", shape.Lane, V(shape.Lane)));
        Release(folded);
        il.Emit(operation);
        il.Emit(round);
    }

    // ---- Intrinsics ----

    /// <summary>Translates a generic vector intrinsic (<see cref="Intrinsics"/>); false for any other call.</summary>
    internal bool Intrinsic(nint instruction, string name)
    {
        nint type = Llvm.LLVMTypeOf(instruction);
        if (name.StartsWith("llvm.vector.reduce.", StringComparison.Ordinal) && name.Split('.')[3] is string kind &&
            kind is not ("fmax" or "fmin" or "fmaximum" or "fminimum") && Intrinsics(name, type))
        {
            Reduce(instruction, kind);
            return true;
        }
        if (types.Vector(type) is not VectorShape shape || !Intrinsics(name, type)) return false;
        string operation = name.Split('.')[1];
        nint Operand(uint index) => Llvm.LLVMGetOperand(instruction, index);
        if (operation is "smin" or "smax" or "umin" or "umax")
        {
            Type lane = operation[0] == 'u' ? Unsigned(shape.Lane) : shape.Lane;
            Build(shape, index =>
            {
                Fragment(Operand(0), index);
                As(shape.Lane, lane);
                Fragment(Operand(1), index);
                As(shape.Lane, lane);
                Call(operation.EndsWith("min", StringComparison.Ordinal) ? "Min" : "Max", lane, 2);
                As(lane, shape.Lane);
            });
            return true;
        }
        if (operation is "sadd" or "uadd" or "ssub" or "usub")
        {
            Type lane = operation[0] == 'u' ? Unsigned(shape.Lane) : shape.Lane;
            Build(shape, index =>
            {
                Fragment(Operand(0), index);
                As(shape.Lane, lane);
                Fragment(Operand(1), index);
                As(shape.Lane, lane);
                Call(operation.EndsWith("add", StringComparison.Ordinal) ? "AddSaturate" : "SubtractSaturate", lane, 2);
                As(lane, shape.Lane);
            });
            return true;
        }
        if (operation == "fmuladd")
        {
            // May round twice, as the scalar lowering does (architecture.md): the product, then the sum.
            Build(shape, index =>
            {
                Fragment(Operand(0), index);
                Fragment(Operand(1), index);
                Call("Multiply", shape.Lane, 2);
                Fragment(Operand(2), index);
                Call("Add", shape.Lane, 2);
            });
            return true;
        }
        if (operation == "round")
        {
            Build(shape, index =>
            {
                Fragment(Operand(0), index);
                il.Emit(OpCodes.Ldc_I4, (int)MidpointRounding.AwayFromZero);
                il.Emit(OpCodes.Call, Api("Round", null, V(shape.Lane), typeof(MidpointRounding)));
            });
            return true;
        }
        (string method, int arity, bool generic) = operation switch
        {
            "abs" => ("Abs", 1, true),
            "fabs" => ("Abs", 1, true),
            "sqrt" => ("Sqrt", 1, true),
            "fma" => ("FusedMultiplyAdd", 3, false),
            "minnum" => ("MinNumber", 2, true),
            "maxnum" => ("MaxNumber", 2, true),
            "minimum" => ("Min", 2, true),
            "maximum" => ("Max", 2, true),
            "copysign" => ("CopySign", 2, true),
            "floor" => ("Floor", 1, false),
            "ceil" => ("Ceiling", 1, false),
            "trunc" => ("Truncate", 1, false),
            _ => ("Round", 1, false)
        };
        Build(shape, index =>
        {
            for (uint operand = 0; operand < arity; operand++) Fragment(Operand(operand), index);
            il.Emit(OpCodes.Call, Api(method, generic ? shape.Lane : null, Enumerable.Repeat(V(shape.Lane), arity).ToArray()));
        });
        return true;
    }
}
