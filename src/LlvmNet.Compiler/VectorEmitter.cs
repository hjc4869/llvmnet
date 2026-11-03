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
    private static readonly ConcurrentDictionary<(Type Owner, string Name, Type? Lane, string Parameters), MethodInfo> Methods = [];
    private readonly Dictionary<Type, Stack<LocalBuilder>> temporaries = [];
    /// <summary>Vector128 or Vector256, whose static methods operate on this module's fragments (--vector-width).</summary>
    private readonly Type owner = types.FragmentBits == 256 ? typeof(Vector256) : typeof(Vector128);
    private readonly int fragmentBytes = types.FragmentBits / 8;
    private bool Wide => types.FragmentBits == 256;

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
                Llvm.Name(target).StartsWith("llvm.", StringComparison.Ordinal) &&
                Llvm.Name(target).Split('.') is not [_, "vector", "reverse" or "insert" or "extract", ..] =>
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
        int bits = TypeSystem.Kind(result) == 13 ? TypeSystem.Width(Llvm.LLVMGetElementType(result)) : 0;
        return operation switch
        {
            "smin" or "smax" or "umin" or "umax" or "abs" => true,
            "ctpop" or "ctlz" or "cttz" or "bitreverse" or "fshl" or "fshr" => bits >= 8,
            "bswap" => bits >= 16,
            "fptosi" or "fptoui" => bits >= 8 && name.Contains(".sat.", StringComparison.Ordinal),
            "vector" => name.Split('.')[2] is "reverse" or "insert" or "extract",
            "fabs" or "sqrt" or "fma" or "fmuladd" or "minnum" or "maxnum" or "minimum" or "maximum" or "copysign" or
                "floor" or "ceil" or "trunc" or "roundeven" or "rint" or "nearbyint" or "round" => floating,
            "sadd" or "uadd" or "ssub" or "usub" => name.Contains(".sat.", StringComparison.Ordinal),
            _ => false
        };
    }

    // ---- Vector128 API ----

    /// <summary>The Vector128 (or Vector256) method with these parameter types: a non-generic overload, or the generic
    /// method instantiated with <paramref name="lane"/>.</summary>
    private MethodInfo Api(string name, Type? lane, params Type[] parameters) => Lookup(owner, name, lane, parameters);

    /// <summary>A helper of <see cref="Vectors"/> with these parameter types, generic ones instantiated with the lane.</summary>
    private static MethodInfo Helper(string name, Type? lane, params Type[] parameters) => Lookup(typeof(Vectors), name, lane, parameters);

    /// <summary>
    /// The operators of Vector128&lt;T&gt; and Vector256&lt;T&gt; for the element-wise methods of the same meaning. RyuJIT
    /// expands the operators itself, while .NET 10 implements Add, Xor, ShiftLeft and the others with them and relies on
    /// inlining, which RyuJIT stops in a method of about 920 locals (90% of JitMaxLocalsToTrack); a large translated
    /// function would call them.
    /// </summary>
    private static readonly Dictionary<string, string> Operators = new()
    {
        ["Add"] = "op_Addition", ["Subtract"] = "op_Subtraction", ["Multiply"] = "op_Multiply", ["Divide"] = "op_Division",
        ["BitwiseAnd"] = "op_BitwiseAnd", ["BitwiseOr"] = "op_BitwiseOr", ["Xor"] = "op_ExclusiveOr",
        ["OnesComplement"] = "op_OnesComplement", ["Negate"] = "op_UnaryNegation", ["ShiftLeft"] = "op_LeftShift",
        ["ShiftRightArithmetic"] = "op_RightShift", ["ShiftRightLogical"] = "op_UnsignedRightShift",
    };

    private static MethodInfo Lookup(Type type, string name, Type? lane, Type[] parameters) =>
        Methods.GetOrAdd((type, name, lane, string.Join(",", parameters.Select(parameter => parameter.FullName))), _ =>
        {
            (Type declaring, string member) = (type, name);
            if ((type == typeof(Vector128) || type == typeof(Vector256)) && Operators.TryGetValue(name, out string? op) &&
                parameters[0].IsGenericType && parameters[0].GetGenericTypeDefinition() == (type == typeof(Vector128) ? typeof(Vector128<>) : typeof(Vector256<>)))
                (declaring, member) = (parameters[0], op);
            MethodInfo[] candidates = declaring.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(method => method.Name == member)
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
            throw new MissingMethodException($"{type.Name}.{name}({string.Join(", ", parameters.Select(parameter => parameter.Name))})");
        });

    /// <summary>A fragment of <paramref name="lane"/>: Vector128 or Vector256.</summary>
    private Type V(Type lane) => (Wide ? typeof(Vector256<>) : typeof(Vector128<>)).MakeGenericType(lane);
    /// <summary>Half a fragment: Vector64 or Vector128.</summary>
    private Type Half(Type lane) => (Wide ? typeof(Vector128<>) : typeof(Vector64<>)).MakeGenericType(lane);

    /// <summary>A generic Vector128 operation on vectors of <paramref name="lane"/>, such as Add or ConditionalSelect.</summary>
    private void Call(string name, Type lane, int arity) => il.Emit(OpCodes.Call, Api(name, lane, Enumerable.Repeat(V(lane), arity).ToArray()));

    private void As(Type from, Type to)
    {
        if (from == to) return;
        il.Emit(OpCodes.Call, Methods.GetOrAdd((owner, "As", from, to.FullName!), _ =>
            owner.GetMethods().Single(method => method.Name == "As" && method.GetGenericArguments().Length == 2).MakeGenericMethod(from, to)));
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
                values.Offset((long)fragmentBytes * index);
                int bytes = (int)Math.Min(fragmentBytes, size - (long)fragmentBytes * index);
                if (bytes == fragmentBytes)
                {
                    if (isVolatile) il.Emit(OpCodes.Volatile);
                    il.Emit(OpCodes.Unaligned, (byte)1);
                    il.Emit(OpCodes.Ldobj, shape.Fragment);
                    return;
                }
                il.Emit(OpCodes.Ldc_I4, bytes);
                il.Emit(OpCodes.Call, typeof(Vectors).GetMethod(Wide ? nameof(Vectors.LoadWide) : nameof(Vectors.Load))!);
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
                values.Offset((long)fragmentBytes * index);
                il.Emit(OpCodes.Ldloc, value);
                if (shape.Fragments > 1) il.Emit(OpCodes.Ldfld, shape.Fields[index]);
                int bytes = (int)Math.Min(fragmentBytes, size - (long)fragmentBytes * index);
                if (bytes == fragmentBytes)
                {
                    if (isVolatile) il.Emit(OpCodes.Volatile);
                    il.Emit(OpCodes.Unaligned, (byte)1);
                    il.Emit(OpCodes.Stobj, shape.Fragment);
                    continue;
                }
                As(shape.Lane, typeof(byte));
                il.Emit(OpCodes.Ldc_I4, bytes);
                if (isVolatile) il.Emit(OpCodes.Call, typeof(Thread).GetMethod(nameof(Thread.MemoryBarrier))!);
                il.Emit(OpCodes.Call, typeof(Vectors).GetMethod(Wide ? nameof(Vectors.StoreWide) : nameof(Vectors.Store))!);
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
        il.Emit(OpCodes.Call, typeof(Vectors).GetMethod(shape.LaneBits switch { 8 => nameof(Vectors.Mask8), 16 => nameof(Vectors.Mask16), 32 => nameof(Vectors.Mask32), _ => nameof(Vectors.Mask64) } + (Wide ? "Wide" : ""))!);
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
    /// With <paramref name="saturate"/>, integers narrow to the nearest value of the narrower type, signed or unsigned.
    /// </summary>
    private void Resize(VectorShape from, VectorShape to, Action<int> source, int index, bool signed, bool saturate = false)
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
                il.Emit(OpCodes.Call, Api(saturate ? "NarrowWithSaturation" : "Narrow", null, V(lane), V(lane)));
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
            MethodInfo helper = Helper(opcode is 14 or 15 ? nameof(Vectors.Divide) : nameof(Vectors.Remainder), lane, V(lane), V(lane), typeof(int));
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
            MethodInfo helper = Helper(name, lane, V(lane), V(lane));
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
    private void FloatToInteger(nint source, VectorShape from, VectorShape to, bool signed, bool saturate = false)
    {
        // llvm.fptosi.sat and fptoui.sat saturate at the result's width: the conversion saturates at its own width (NaN
        // to zero, as LLVM defines), the narrowing at the result's, both unsigned for fptoui.sat.
        VectorShape wide = to.LaneBits == 64 && from.Lane == typeof(float) ? types.Shape(from.Lanes, typeof(double), mask: false) : from;
        bool unsignedConversion = !signed && (saturate || to.LaneBits >= wide.LaneBits);
        Type converted = wide.Lane == typeof(float) ? unsignedConversion ? typeof(uint) : typeof(int) : unsignedConversion ? typeof(ulong) : typeof(long);
        string conversion = converted == typeof(int) ? "ConvertToInt32" : converted == typeof(uint) ? "ConvertToUInt32" : converted == typeof(long) ? "ConvertToInt64" : "ConvertToUInt64";
        VectorShape integers = types.Shape(from.Lanes, Signed(wide.LaneBits), mask: false);
        Build(to, index => Resize(integers, to, fragment =>
        {
            Resize(from, wide, part => Fragment(source, part), fragment, signed: true);
            il.Emit(OpCodes.Call, Api(conversion, null, V(wide.Lane)));
            As(converted, integers.Lane);
        }, index, signed, saturate));
    }

    /// <summary>sitofp and uitofp: integers extended to the result's width and converted, which is exact; 64-bit integers
    /// to float lane by lane, because converting through double would round twice.</summary>
    private void IntegerToFloat(nint source, VectorShape from, VectorShape to, bool signed)
    {
        if (from.LaneBits == 64 && to.Lane == typeof(float))
        {
            MethodInfo helper = typeof(Vectors).GetMethod((signed ? nameof(Vectors.SignedToSingle) : nameof(Vectors.UnsignedToSingle)) + (Wide ? "Wide" : ""))!;
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
        VectorShape buffer = types.Shape(checked((int)((size + fragmentBytes - 1) / fragmentBytes * fragmentBytes)), typeof(sbyte), mask: false);
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

    /// <summary>
    /// Whether an insertelement at a constant lane only feeds the next one of the same block, which builds the vector
    /// with the lanes of the whole chain (<see cref="Insert"/>); it emits nothing itself. Without SSE4.1, as baseline
    /// NativeAOT on x64 compiles, WithElement of a 32-bit lane goes through memory, and a chain of them stalls each
    /// store's forwarding (perlin's vectorized loop as NativeAOT: 2.9 s, 0.83 s with x86-64-v3), while Create of the
    /// lanes is a few register instructions on every host.
    /// </summary>
    internal static bool Chained(nint value)
    {
        if (Llvm.LLVMIsAConstantInt(Llvm.LLVMGetOperand(value, 2)) == 0) return false;
        nint use = Llvm.LLVMGetFirstUse(value);
        if (use == 0 || Llvm.LLVMGetNextUse(use) != 0) return false;
        nint user = Llvm.LLVMGetUser(use);
        return Llvm.LLVMIsAInstruction(user) != 0 && Llvm.LLVMGetInstructionOpcode(user) == 51 && Llvm.LLVMGetOperand(user, 0) == value &&
            Llvm.LLVMIsAConstantInt(Llvm.LLVMGetOperand(user, 2)) != 0 && Llvm.LLVMGetInstructionParent(user) == Llvm.LLVMGetInstructionParent(value);
    }

    internal void Insert(nint value)
    {
        nint vector = Llvm.LLVMGetOperand(value, 0), element = Llvm.LLVMGetOperand(value, 1), position = Llvm.LLVMGetOperand(value, 2);
        VectorShape shape = Shape(value);
        if (Llvm.LLVMIsAConstantInt(position) != 0)
        {
            // The lanes of the chain this insertelement ends, the last insert of a lane winning, and the vector it starts from.
            var lanes = new nint[shape.Lanes];
            for (nint current = value; ; current = vector)
            {
                long index = Llvm.LLVMConstIntGetSExtValue(Llvm.LLVMGetOperand(current, 2));
                if ((ulong)index < (ulong)shape.Lanes && lanes[index] == 0) lanes[index] = Llvm.LLVMGetOperand(current, 1);
                vector = Llvm.LLVMGetOperand(current, 0);
                if (Llvm.LLVMIsAInstruction(vector) == 0 || Llvm.LLVMGetInstructionOpcode(vector) != 51 || !Chained(vector)) break;
            }
            bool fresh = Llvm.LLVMIsUndef(vector) != 0 || Llvm.LLVMIsPoison(vector) != 0 || Llvm.LLVMIsNull(vector) != 0;
            Build(shape, fragment =>
            {
                int first = fragment * shape.PerFragment;
                if (fresh)
                {
                    for (int lane = first; lane < first + shape.PerFragment; lane++)
                        if (lane < shape.Lanes && lanes[lane] != 0) LaneValue(lanes[lane], shape);
                        else Scalar(shape.Lane, 0);
                    il.Emit(OpCodes.Call, Api("Create", null, Enumerable.Repeat(shape.Lane, shape.PerFragment).ToArray()));
                    return;
                }
                Fragment(vector, fragment);
                for (int lane = first; lane < Math.Min(first + shape.PerFragment, shape.Lanes); lane++)
                {
                    if (lanes[lane] == 0) continue;
                    il.Emit(OpCodes.Ldc_I4, lane - first);
                    LaneValue(lanes[lane], shape);
                    il.Emit(OpCodes.Call, Api("WithElement", shape.Lane, V(shape.Lane), typeof(int), shape.Lane));
                }
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
        int lanes = Shape(first).Lanes;
        Gather(Shape(value), [first, second], Enumerable.Range(0, Shape(value).Lanes).Select(lane =>
            Llvm.LLVMGetMaskValue(value, (uint)lane) is int element and >= 0 ? element < lanes ? (0, element) : (1, element - lanes) : ((int, int)?)null).ToArray());
    }

    /// <summary>
    /// Builds <paramref name="result"/> from lanes of <paramref name="operands"/>, <paramref name="map"/> naming the
    /// operand and lane of each result lane (null where it is undefined), for shufflevector and llvm.vector.reverse,
    /// insert and extract.
    /// </summary>
    private void Gather(VectorShape result, nint[] operands, (int Operand, int Lane)?[] map)
    {
        VectorShape[] inputs = operands.Select(operand => result.Mask ? types.Shape(Shape(operand).Lanes, result.Lane, mask: true) : Shape(operand)).ToArray();
        int per = result.PerFragment;
        void Source(int operand, int fragment)
        {
            if (result.Mask) LoadMask(operands[operand], inputs[operand], fragment);
            else Fragment(operands[operand], fragment);
        }
        Build(result, index =>
        {
            // (operand, fragment) -> the lane each result lane takes from it, -1 elsewhere.
            var sources = new Dictionary<(int Operand, int Fragment), int[]>();
            for (int lane = 0; lane < per; lane++)
            {
                int global = index * per + lane;
                if (global >= result.Lanes || map[global] is not (int operand, int element)) continue;
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
                if (Wide)
                {
                    SourceHalf(lower);
                    SourceHalf(upper);
                    il.Emit(OpCodes.Call, Api("Create", result.Lane, Half(result.Lane), Half(result.Lane)));
                }
                else
                {
                    // The halves of a Vector128 as the lanes of a Vector128<double>: unpcklpd and unpckhpd, or ins on
                    // ARM64. Vector64 has no hardware acceleration on x64, where Create of two goes through memory.
                    SourceElement(lower);
                    SourceElement(upper);
                    il.Emit(OpCodes.Call, Api("Create", null, typeof(double), typeof(double)));
                    As(typeof(double), result.Lane);
                }
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

        void SourceHalf((int Operand, int Fragment, bool Upper) half)
        {
            Source(half.Operand, half.Fragment);
            il.Emit(OpCodes.Call, Api(half.Upper ? "GetUpper" : "GetLower", result.Lane, V(result.Lane)));
        }

        void SourceElement((int Operand, int Fragment, bool Upper) half)
        {
            Source(half.Operand, half.Fragment);
            As(result.Lane, typeof(double));
            il.Emit(half.Upper ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Call, Api("GetElement", typeof(double), V(typeof(double)), typeof(int)));
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
    /// llvm.vector.reduce.*, which the backend lowers itself (expand-reductions runs only on the functions the
    /// scalarizer takes): lanes of a power-of-two vector combine pairwise as expand-reductions and LLVM's backends do,
    /// an integer sum through Vector128.Sum, other vectors lane by lane; fadd and fmul are ordered from the start value
    /// unless they may reassociate; an i1 vector is reduced as its bits.
    /// </summary>
    private void Reduce(nint instruction, string kind)
    {
        bool ordered = kind is "fadd" or "fmul";
        nint vector = Llvm.LLVMGetOperand(instruction, ordered ? 1u : 0u);
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
        if (ordered)
        {
            ReduceFloating(instruction, kind, vector, shape);
            return;
        }
        bool floating = shape.Lane == typeof(float) || shape.Lane == typeof(double);
        Type lane = !floating && (kind is "umax" or "umin" || shape.LaneBits < 32 && kind is not ("smax" or "smin")) ? Unsigned(shape.Lane) : shape.Lane;
        string operation = kind switch
        {
            "add" => "Add",
            "mul" => "Multiply",
            "and" => "BitwiseAnd",
            "or" => "BitwiseOr",
            "xor" => "Xor",
            "smax" or "umax" or "fmaximum" => "Max",
            "smin" or "umin" or "fminimum" => "Min",
            "fmax" => "MaxNumber",
            _ => "MinNumber"
        };
        if (kind == "add" && shape.Lanes == shape.PerFragment * shape.Fragments && System.Numerics.BitOperations.IsPow2(shape.Fragments))
        {
            Fold(vector, shape, lane, operation);
            il.Emit(OpCodes.Call, Api("Sum", lane, V(lane)));
        }
        else if (System.Numerics.BitOperations.IsPow2(shape.Lanes))
            ReduceTree(vector, shape, lane, operation);
        else
        {
            MethodInfo? combine = floating ? shape.Lane.GetMethod(operation, [shape.Lane, shape.Lane]) : null;
            for (int element = 0; element < shape.Lanes; element++)
            {
                Fragment(vector, element / shape.PerFragment);
                As(shape.Lane, lane);
                il.Emit(OpCodes.Ldc_I4, element % shape.PerFragment);
                il.Emit(OpCodes.Call, Api("GetElement", lane, V(lane), typeof(int)));
                if (element == 0) continue;
                if (combine is not null) il.Emit(OpCodes.Call, combine);
                else
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
                            il.Emit(OpCodes.Call, typeof(Math).GetMethod(operation, [compared, compared])!);
                            break;
                    }
            }
        }
        if (!floating) values.Normalize(type);
    }

    /// <summary>Pushes the fragments of a vector combined lane by lane, fragment j with fragment j + count / 2 while there
    /// are several, as LLVM halves a vector; the fragment count is a power of two.</summary>
    private void Fold(nint vector, VectorShape shape, Type lane, string operation)
    {
        void Pair(int count, int index)
        {
            if (count == shape.Fragments)
            {
                Fragment(vector, index);
                As(shape.Lane, lane);
                return;
            }
            Pair(count * 2, index);
            Pair(count * 2, index + count);
            Call(operation, lane, 2);
        }
        Pair(1, 0);
    }

    /// <summary>Pushes lane 0 of a power-of-two vector after combining its upper half with its lower half until one lane
    /// is left: the fragments first, then the lanes of the last one by shuffles. A 256-bit fragment first combines its
    /// 128-bit halves, which is the same step and avoids shuffling a Vector256, which .NET does lane by lane where the
    /// host has no AVX.</summary>
    private void ReduceTree(nint vector, VectorShape shape, Type lane, string operation)
    {
        Fold(vector, shape, lane, operation);
        int lanes = Math.Min(shape.Lanes, shape.PerFragment), per = shape.PerFragment;
        Type owner = this.owner;
        Type Fragment(Type type) => (owner == typeof(Vector256) ? typeof(Vector256<>) : typeof(Vector128<>)).MakeGenericType(type);
        if (Wide)
        {
            LocalBuilder whole = Rent(V(lane));
            il.Emit(OpCodes.Stloc, whole);
            il.Emit(OpCodes.Ldloc, whole);
            il.Emit(OpCodes.Call, Api("GetLower", lane, V(lane)));
            if (lanes == per)
            {
                il.Emit(OpCodes.Ldloc, whole);
                il.Emit(OpCodes.Call, Api("GetUpper", lane, V(lane)));
                il.Emit(OpCodes.Call, Lookup(typeof(Vector128), operation, lane, [Half(lane), Half(lane)]));
                lanes /= 2;
            }
            Release(whole);
            owner = typeof(Vector128);
            per /= 2;
        }
        Type fragment = Fragment(lane), indices = IntegerLane(lane);
        LocalBuilder folded = Rent(fragment);
        il.Emit(OpCodes.Stloc, folded);
        for (int width = lanes / 2; width >= 1; width /= 2)
        {
            il.Emit(OpCodes.Ldloc, folded);
            il.Emit(OpCodes.Ldloc, folded);
            for (int element = 0; element < per; element++) Scalar(indices, element < width ? element + width : element);
            il.Emit(OpCodes.Call, Lookup(owner, "Create", null, Enumerable.Repeat(indices, per).ToArray()));
            il.Emit(OpCodes.Call, Lookup(owner, "Shuffle", null, [fragment, Fragment(indices)]));
            il.Emit(OpCodes.Call, Lookup(owner, operation, lane, [fragment, fragment]));
            il.Emit(OpCodes.Stloc, folded);
        }
        il.Emit(OpCodes.Ldloc, folded);
        il.Emit(OpCodes.Call, Lookup(owner, "ToScalar", lane, [fragment]));
        Release(folded);
    }

    private void ReduceFloating(nint instruction, string kind, nint vector, VectorShape shape)
    {
        OpCode operation = kind == "fadd" ? OpCodes.Add : OpCodes.Mul;
        OpCode round = shape.Lane == typeof(float) ? OpCodes.Conv_R4 : OpCodes.Conv_R8;
        values.Load(Llvm.LLVMGetOperand(instruction, 0));
        if ((Llvm.LLVMGetFastMathFlags(instruction) & 1) != 0 && System.Numerics.BitOperations.IsPow2(shape.Lanes))
        {
            ReduceTree(vector, shape, shape.Lane, kind == "fadd" ? "Add" : "Multiply");
            il.Emit(operation);
            il.Emit(round);
            return;
        }
        for (int element = 0; element < shape.Lanes; element++)
        {
            Fragment(vector, element / shape.PerFragment);
            il.Emit(OpCodes.Ldc_I4, element % shape.PerFragment);
            il.Emit(OpCodes.Call, Api("GetElement", shape.Lane, V(shape.Lane), typeof(int)));
            il.Emit(operation);
            il.Emit(round);
        }
    }

    // ---- Intrinsics ----

    /// <summary>
    /// llvm.fshl and llvm.fshr: with one count for every lane, s = count mod width and fshl = high &lt;&lt; s | (low
    /// &gt;&gt;&gt; 1) &gt;&gt;&gt; (width - 1 - s), which never shifts by the whole width, and fshr its mirror; a
    /// count per lane goes lane by lane.
    /// </summary>
    private void FunnelShift(VectorShape shape, nint high, nint low, nint count, bool right)
    {
        Type lane = Unsigned(shape.Lane);
        if (SplatScalar(count) is not nint scalar)
        {
            MethodInfo helper = Helper(nameof(Vectors.FunnelShift), lane, V(lane), V(lane), V(lane), typeof(bool));
            Build(shape, index =>
            {
                foreach (nint operand in new[] { high, low, count })
                {
                    Fragment(operand, index);
                    As(shape.Lane, lane);
                }
                il.Emit(right ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Call, helper);
                As(lane, shape.Lane);
            });
            return;
        }
        LocalBuilder amount = Rent(typeof(int));
        LoadLaneScalar(scalar);
        if (shape.LaneBits == 64) il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Ldc_I4, shape.LaneBits - 1);
        il.Emit(OpCodes.And);
        il.Emit(OpCodes.Stloc, amount);
        MethodInfo near = Api(right ? "ShiftRightLogical" : "ShiftLeft", null, V(lane), typeof(int));
        MethodInfo far = Api(right ? "ShiftLeft" : "ShiftRightLogical", null, V(lane), typeof(int));
        Build(shape, index =>
        {
            Fragment(right ? low : high, index);
            As(shape.Lane, lane);
            il.Emit(OpCodes.Ldloc, amount);
            il.Emit(OpCodes.Call, near);
            Fragment(right ? high : low, index);
            As(shape.Lane, lane);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Call, far);
            il.Emit(OpCodes.Ldc_I4, shape.LaneBits - 1);
            il.Emit(OpCodes.Ldloc, amount);
            il.Emit(OpCodes.Sub);
            il.Emit(OpCodes.Call, far);
            Call("BitwiseOr", lane, 2);
            As(lane, shape.Lane);
        });
        Release(amount);
    }

    /// <summary>Translates a generic vector intrinsic (<see cref="Intrinsics"/>); false for any other call.</summary>
    internal bool Intrinsic(nint instruction, string name)
    {
        nint type = Llvm.LLVMTypeOf(instruction);
        if (name.StartsWith("llvm.vector.reduce.", StringComparison.Ordinal) && Intrinsics(name, type))
        {
            Reduce(instruction, name.Split('.')[3]);
            return true;
        }
        if (types.Vector(type) is not VectorShape shape || !Intrinsics(name, type)) return false;
        string operation = name.Split('.')[1];
        nint Operand(uint index) => Llvm.LLVMGetOperand(instruction, index);
        if (operation == "vector")
        {
            // reverse, extract (a subvector at a constant lane) and insert (one at a constant lane).
            nint vector = Operand(0);
            int count = Shape(vector).Lanes;
            string kind = name.Split('.')[2];
            long position = kind == "reverse" ? 0 : Llvm.LLVMConstIntGetSExtValue(Operand(kind == "insert" ? 2u : 1u));
            int inserted = kind == "insert" ? Shape(Operand(1)).Lanes : 0;
            Gather(shape, kind == "insert" ? [vector, Operand(1)] : [vector], Enumerable.Range(0, shape.Lanes).Select(lane => kind switch
            {
                "reverse" => (0, count - 1 - lane),
                "extract" => (0, (int)position + lane),
                _ => lane >= position && lane < position + inserted ? (1, lane - (int)position) : ((int, int)?)(0, lane)
            }).ToArray());
            return true;
        }
        if (operation is "fptosi" or "fptoui")
        {
            FloatToInteger(Operand(0), Shape(Operand(0)), shape, signed: operation == "fptosi", saturate: true);
            return true;
        }
        if (operation is "ctpop" or "ctlz" or "cttz" or "bswap" or "bitreverse")
        {
            Type lane = Unsigned(shape.Lane);
            MethodInfo helper = typeof(Vectors).GetMethod(operation switch
            {
                "ctpop" => nameof(Vectors.PopCount),
                "ctlz" => nameof(Vectors.LeadingZeroCount),
                "cttz" => nameof(Vectors.TrailingZeroCount),
                "bswap" => nameof(Vectors.ByteSwap),
                _ => nameof(Vectors.ReverseBits)
            }, [V(lane)])!;
            Build(shape, index =>
            {
                Fragment(Operand(0), index);
                As(shape.Lane, lane);
                il.Emit(OpCodes.Call, helper);
                As(lane, shape.Lane);
            });
            return true;
        }
        if (operation is "fshl" or "fshr")
        {
            FunnelShift(shape, Operand(0), Operand(1), Operand(2), operation == "fshr");
            return true;
        }
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
        if (operation == "copysign")
        {
            // The sign bit of the second operand and the other bits of the first; Vector128.CopySign is a call in a
            // method too large for RyuJIT to inline (see Operators).
            Build(shape, index =>
            {
                if (shape.Lane == typeof(float)) il.Emit(OpCodes.Ldc_R4, -0.0f);
                else il.Emit(OpCodes.Ldc_R8, -0.0);
                il.Emit(OpCodes.Call, Api("Create", null, shape.Lane));
                Fragment(Operand(1), index);
                Fragment(Operand(0), index);
                Call("ConditionalSelect", shape.Lane, 3);
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
