using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace LlvmNet.Runtime;

/// <summary>
/// Helpers of the typed vector lowering (ADR-16 Level 1) for what Vector128 has no single operation for: memory accesses
/// of fewer than 16 bytes, i1 vectors as bits, and the lane-by-lane operations no SIMD instruction performs.
/// </summary>
public static unsafe class Vectors
{
    // The immediates of the permutes and lane inserts (ExtractVector128, InsertSelectedScalar) are constants once the helpers
    // are inlined at the lowering's call sites, which pass constant forms and halves (X86.cs explains the same pragma).
#pragma warning disable CA1857
    /// <summary>Reads <paramref name="size"/> bytes (1 to 15) into the low lanes of a vector, zero above.</summary>
    /// <remarks>On ARM64 8 and 4 bytes are one ldr d or ldr s, which zeroes the rest of the register (as the 12 bytes'
    /// ldr d does before ld1 fills lane 2); CreateScalar of an integer was movi, ldr x and ins (SV-34). ToVector128Unsafe
    /// and CreateScalarUnsafe keep that register as it is, where ToVector128 would add a mov.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> Load(nint address, int size)
    {
        if (AdvSimd.IsSupported)
            switch (size)
            {
                case 8: return AdvSimd.LoadVector64((byte*)address).ToVector128Unsafe();
                case 4: return Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<float>((void*)address)).AsByte();
                case 12: return AdvSimd.LoadAndInsertScalar(AdvSimd.LoadVector64((uint*)address).ToVector128Unsafe(), 2, (uint*)(address + 8)).AsByte();
            }
        switch (size)
        {
            case 8: return Vector128.CreateScalar(Unsafe.ReadUnaligned<ulong>((void*)address)).AsByte();
            case 4: return Vector128.CreateScalar(Unsafe.ReadUnaligned<uint>((void*)address)).AsByte();
            case 12: return Vector128.Create(Unsafe.ReadUnaligned<ulong>((void*)address), Unsafe.ReadUnaligned<uint>((void*)(address + 8))).AsByte();
            case 2: return Vector128.CreateScalar(Unsafe.ReadUnaligned<ushort>((void*)address)).AsByte();
            case 1: return Vector128.CreateScalar(*(byte*)address);
        }
        return LoadBytes(address, size);
    }

    private static Vector128<byte> LoadBytes(nint address, int size)
    {
        Vector128<byte> result = Vector128<byte>.Zero;
        Buffer.MemoryCopy((void*)address, &result, 16, size);
        return result;
    }

    /// <summary>Writes the low <paramref name="size"/> bytes (1 to 15) of a vector.</summary>
    /// <remarks>On ARM64 8 and 4 bytes are one str d or str s, where ToScalar of an integer moved the lanes to a general
    /// register first (umov).</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Store(nint address, Vector128<byte> value, int size)
    {
        if (AdvSimd.IsSupported)
            switch (size)
            {
                case 8: AdvSimd.Store((byte*)address, value.GetLower()); return;
                case 4: Unsafe.WriteUnaligned((void*)address, value.AsSingle().ToScalar()); return;
                case 12:
                    AdvSimd.Store((byte*)address, value.GetLower());
                    AdvSimd.StoreSelectedScalar((uint*)(address + 8), value.AsUInt32(), 2);
                    return;
            }
        switch (size)
        {
            case 8: Unsafe.WriteUnaligned((void*)address, value.AsUInt64().ToScalar()); return;
            case 4: Unsafe.WriteUnaligned((void*)address, value.AsUInt32().ToScalar()); return;
            case 12:
                Unsafe.WriteUnaligned((void*)address, value.AsUInt64().ToScalar());
                Unsafe.WriteUnaligned((void*)(address + 8), value.AsUInt32().GetElement(2));
                return;
            case 2: Unsafe.WriteUnaligned((void*)address, value.AsUInt16().ToScalar()); return;
            case 1: *(byte*)address = value.ToScalar(); return;
        }
        StoreBytes(address, value, size);
    }

    private static void StoreBytes(nint address, Vector128<byte> value, int size) => Buffer.MemoryCopy(&value, (void*)address, size, size);

    /// <summary>The bits of an i1 vector in memory (one per lane, <paramref name="size"/> bytes, at most 8).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong LoadBits(nint address, int size)
    {
        ulong bits = 0;
        Buffer.MemoryCopy((void*)address, &bits, 8, size);
        return bits;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void StoreBits(nint address, ulong bits, int size) => Buffer.MemoryCopy(&bits, (void*)address, size, size);

    /// <summary>A mask of 16 byte lanes from the low 16 bits: lane i is all ones when bit i is set.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<sbyte> Mask8(ulong bits)
    {
        Vector128<byte> spread = Vector128.Create(Vector64.Create((byte)bits), Vector64.Create((byte)(bits >> 8)));
        Vector128<byte> selected = Vector128.Create((byte)1, 2, 4, 8, 16, 32, 64, 128, 1, 2, 4, 8, 16, 32, 64, 128);
        return Vector128.Equals(spread & selected, selected).AsSByte();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<short> Mask16(ulong bits)
    {
        Vector128<ushort> selected = Vector128.Create((ushort)1, 2, 4, 8, 16, 32, 64, 128);
        return Vector128.Equals(Vector128.Create((ushort)bits) & selected, selected).AsInt16();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> Mask32(ulong bits)
    {
        Vector128<uint> selected = Vector128.Create(1u, 2, 4, 8);
        return Vector128.Equals(Vector128.Create((uint)bits) & selected, selected).AsInt32();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<long> Mask64(ulong bits)
    {
        Vector128<ulong> selected = Vector128.Create(1ul, 2);
        return Vector128.Equals(Vector128.Create(bits) & selected, selected).AsInt64();
    }

    /// <summary>Integer division of the first <paramref name="lanes"/> lanes; the others are zero. A zero divisor throws,
    /// as the scalar division does (it is undefined behavior in LLVM).</summary>
    public static Vector128<T> Divide<T>(Vector128<T> left, Vector128<T> right, int lanes) where T : unmanaged, INumber<T>
    {
        Vector128<T> result = Vector128<T>.Zero;
        for (int lane = 0; lane < lanes; lane++)
            result = result.WithElement(lane, left.GetElement(lane) / right.GetElement(lane));
        return result;
    }

    /// <summary>Remainder of the first <paramref name="lanes"/> lanes: integers as CIL rem does, floats as fmod.</summary>
    public static Vector128<T> Remainder<T>(Vector128<T> left, Vector128<T> right, int lanes) where T : unmanaged, INumber<T>
    {
        Vector128<T> result = Vector128<T>.Zero;
        for (int lane = 0; lane < lanes; lane++)
            result = result.WithElement(lane, left.GetElement(lane) % right.GetElement(lane));
        return result;
    }

    // Shifts of each lane by the count in the same lane. A count at or above the lane width is poison in LLVM; it is taken
    // modulo the width, as C# shifts do, on every path. AVX2 shifts 32- and 64-bit lanes by vector counts (vpsllvd,
    // vpsrlvq, vpsravd; 16-bit lanes through 32), AdvSimd every width (ushl, sshl, with negative counts shifting right);
    // elsewhere the lanes go one by one through memory, out of line, where an inlined WithElement chain makes every lane
    // wait for the store before it (sub4x4_dct ran 14 times native that way).

    /// <summary>A shift left of each lane by the count in the same lane.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<T> ShiftLeft<T>(Vector128<T> value, Vector128<T> count) where T : unmanaged, IBinaryInteger<T>
    {
        if (Avx2.IsSupported)
        {
            if (sizeof(T) == 4) return Avx2.ShiftLeftLogicalVariable(value.AsUInt32(), count.AsUInt32() & Vector128.Create(31u)).As<uint, T>();
            if (sizeof(T) == 8) return Avx2.ShiftLeftLogicalVariable(value.AsUInt64(), count.AsUInt64() & Vector128.Create(63ul)).As<ulong, T>();
            if (sizeof(T) == 2) return Narrow(Avx2.ShiftLeftLogicalVariable(Avx2.ConvertToVector256Int32(value.AsUInt16()).AsUInt32(), Counts16(count))).As<ushort, T>();
        }
        if (AdvSimd.IsSupported) return ShiftLogical(value, Modulo(count));
        return ShiftLeftLanes(value, count);
    }

    /// <summary>A shift right of each lane by the count in the same lane, arithmetic: the lanes are signed.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<T> ShiftRightArithmetic<T>(Vector128<T> value, Vector128<T> count) where T : unmanaged, IBinaryInteger<T>
    {
        if (Avx2.IsSupported)
        {
            if (sizeof(T) == 4) return Avx2.ShiftRightArithmeticVariable(value.AsInt32(), count.AsUInt32() & Vector128.Create(31u)).As<int, T>();
            if (sizeof(T) == 2) return Narrow(Avx2.ShiftRightArithmeticVariable(Avx2.ConvertToVector256Int32(value.AsInt16()), Counts16(count)).AsUInt32()).As<ushort, T>();
        }
        if (AdvSimd.IsSupported) return ShiftArithmetic(value, Vector128<T>.Zero - Modulo(count));
        return ShiftRightArithmeticLanes(value, count);
    }

    /// <summary>A shift right of each lane by the count in the same lane, logical: the lanes are unsigned.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<T> ShiftRightLogical<T>(Vector128<T> value, Vector128<T> count) where T : unmanaged, IBinaryInteger<T>
    {
        if (Avx2.IsSupported)
        {
            if (sizeof(T) == 4) return Avx2.ShiftRightLogicalVariable(value.AsUInt32(), count.AsUInt32() & Vector128.Create(31u)).As<uint, T>();
            if (sizeof(T) == 8) return Avx2.ShiftRightLogicalVariable(value.AsUInt64(), count.AsUInt64() & Vector128.Create(63ul)).As<ulong, T>();
            if (sizeof(T) == 2) return Narrow(Avx2.ShiftRightLogicalVariable(Avx2.ConvertToVector256Int32(value.AsUInt16()).AsUInt32(), Counts16(count))).As<ushort, T>();
        }
        if (AdvSimd.IsSupported) return ShiftLogical(value, Vector128<T>.Zero - Modulo(count));
        return ShiftRightLogicalLanes(value, count);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<T> Modulo<T>(Vector128<T> count) where T : unmanaged, IBinaryInteger<T> =>
        count & Vector128.Create(T.CreateTruncating(sizeof(T) * 8 - 1));

    /// <summary>The 16-bit counts as 32-bit lanes, modulo 16.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<uint> Counts16<T>(Vector128<T> count) where T : unmanaged =>
        Avx2.ConvertToVector256Int32(count.AsUInt16()).AsUInt32() & Vector256.Create(15u);

    /// <summary>The low 16 bits of each 32-bit lane.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> Narrow(Vector256<uint> lanes) => Vector128.Narrow(lanes.GetLower(), lanes.GetUpper());

    // AdvSimd's ushl and sshl, which take a signed count per lane, at the lane width of T.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<T> ShiftLogical<T>(Vector128<T> value, Vector128<T> count) where T : unmanaged => sizeof(T) switch
    {
        1 => AdvSimd.ShiftLogical(value.AsSByte(), count.AsSByte()).As<sbyte, T>(),
        2 => AdvSimd.ShiftLogical(value.AsInt16(), count.AsInt16()).As<short, T>(),
        4 => AdvSimd.ShiftLogical(value.AsInt32(), count.AsInt32()).As<int, T>(),
        _ => AdvSimd.ShiftLogical(value.AsInt64(), count.AsInt64()).As<long, T>(),
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<T> ShiftArithmetic<T>(Vector128<T> value, Vector128<T> count) where T : unmanaged => sizeof(T) switch
    {
        1 => AdvSimd.ShiftArithmetic(value.AsSByte(), count.AsSByte()).As<sbyte, T>(),
        2 => AdvSimd.ShiftArithmetic(value.AsInt16(), count.AsInt16()).As<short, T>(),
        4 => AdvSimd.ShiftArithmetic(value.AsInt32(), count.AsInt32()).As<int, T>(),
        _ => AdvSimd.ShiftArithmetic(value.AsInt64(), count.AsInt64()).As<long, T>(),
    };

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Vector128<T> ShiftLeftLanes<T>(Vector128<T> value, Vector128<T> count) where T : unmanaged, IBinaryInteger<T>
    {
        T* lanes = (T*)&value, counts = (T*)&count;
        for (int lane = 0; lane < Vector128<T>.Count; lane++) lanes[lane] <<= int.CreateTruncating(counts[lane]);
        return value;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Vector128<T> ShiftRightArithmeticLanes<T>(Vector128<T> value, Vector128<T> count) where T : unmanaged, IBinaryInteger<T>
    {
        T* lanes = (T*)&value, counts = (T*)&count;
        for (int lane = 0; lane < Vector128<T>.Count; lane++) lanes[lane] >>= int.CreateTruncating(counts[lane]);
        return value;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Vector128<T> ShiftRightLogicalLanes<T>(Vector128<T> value, Vector128<T> count) where T : unmanaged, IBinaryInteger<T>
    {
        T* lanes = (T*)&value, counts = (T*)&count;
        for (int lane = 0; lane < Vector128<T>.Count; lane++) lanes[lane] >>>= int.CreateTruncating(counts[lane]);
        return value;
    }

    // Widenings of a fragment's lowest lanes by two or three steps (VectorEmitter.Resize): SSE4.1's pmovzx and pmovsx, and
    // AVX2's 256-bit forms, do each in one instruction where WidenLower twice is two (x264's zext <4 x i8> to <4 x i32>);
    // elsewhere, ARM64 included, the steps.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> WidenLowest32(Vector128<byte> value) =>
        Sse41.IsSupported ? Sse41.ConvertToVector128Int32(value).AsUInt32() : Vector128.WidenLower(Vector128.WidenLower(value));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> WidenLowest32(Vector128<sbyte> value) =>
        Sse41.IsSupported ? Sse41.ConvertToVector128Int32(value) : Vector128.WidenLower(Vector128.WidenLower(value));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<ulong> WidenLowest64(Vector128<byte> value) =>
        Sse41.IsSupported ? Sse41.ConvertToVector128Int64(value).AsUInt64() : Vector128.WidenLower(Vector128.WidenLower(Vector128.WidenLower(value)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<long> WidenLowest64(Vector128<sbyte> value) =>
        Sse41.IsSupported ? Sse41.ConvertToVector128Int64(value) : Vector128.WidenLower(Vector128.WidenLower(Vector128.WidenLower(value)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<ulong> WidenLowest64(Vector128<ushort> value) =>
        Sse41.IsSupported ? Sse41.ConvertToVector128Int64(value).AsUInt64() : Vector128.WidenLower(Vector128.WidenLower(value));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<long> WidenLowest64(Vector128<short> value) =>
        Sse41.IsSupported ? Sse41.ConvertToVector128Int64(value) : Vector128.WidenLower(Vector128.WidenLower(value));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<uint> WidenLowest32(Vector256<byte> value) =>
        Avx2.IsSupported ? Avx2.ConvertToVector256Int32(value.GetLower()).AsUInt32() : Vector256.WidenLower(Vector256.WidenLower(value));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<int> WidenLowest32(Vector256<sbyte> value) =>
        Avx2.IsSupported ? Avx2.ConvertToVector256Int32(value.GetLower()) : Vector256.WidenLower(Vector256.WidenLower(value));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<ulong> WidenLowest64(Vector256<byte> value) =>
        Avx2.IsSupported ? Avx2.ConvertToVector256Int64(value.GetLower()).AsUInt64() : Vector256.WidenLower(Vector256.WidenLower(Vector256.WidenLower(value)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<long> WidenLowest64(Vector256<sbyte> value) =>
        Avx2.IsSupported ? Avx2.ConvertToVector256Int64(value.GetLower()) : Vector256.WidenLower(Vector256.WidenLower(Vector256.WidenLower(value)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<ulong> WidenLowest64(Vector256<ushort> value) =>
        Avx2.IsSupported ? Avx2.ConvertToVector256Int64(value.GetLower()).AsUInt64() : Vector256.WidenLower(Vector256.WidenLower(value));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<long> WidenLowest64(Vector256<short> value) =>
        Avx2.IsSupported ? Avx2.ConvertToVector256Int64(value.GetLower()) : Vector256.WidenLower(Vector256.WidenLower(value));

    // Adds and subtracts of two extensions of one narrow vector type (VectorEmitter.Widening): AdvSimd computes the lanes
    // of a half at twice the width in one instruction (uaddl, usubl, saddl, ssubl and their upper forms), where each
    // extension was an uxtl or sxtl before the add (SV-37). Four times the width extends that result once more, with its
    // sign for a difference, which fits twice the width as a signed value, and as the operands for a sum. part is the
    // result fragment's place in its source fragment: its half (0, 1) or quarter (0 to 3). Other hosts add the extensions
    // the lowering computed where they were (wideLeft, wideRight), as before: on x64 a narrow load folds into its pmovzx
    // only there, and the extensions are dead code on ARM64.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<ushort> AddWidening16(Vector128<byte> left, Vector128<byte> right, int part, Vector128<ushort> wideLeft, Vector128<ushort> wideRight) =>
        AdvSimd.IsSupported ? part == 0 ? AdvSimd.AddWideningLower(left.GetLower(), right.GetLower()) : AdvSimd.AddWideningUpper(left, right) : wideLeft + wideRight;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<short> AddWidening16(Vector128<sbyte> left, Vector128<sbyte> right, int part, Vector128<short> wideLeft, Vector128<short> wideRight) =>
        AdvSimd.IsSupported ? part == 0 ? AdvSimd.AddWideningLower(left.GetLower(), right.GetLower()) : AdvSimd.AddWideningUpper(left, right) : wideLeft + wideRight;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> AddWidening32(Vector128<ushort> left, Vector128<ushort> right, int part, Vector128<uint> wideLeft, Vector128<uint> wideRight) =>
        AdvSimd.IsSupported ? part == 0 ? AdvSimd.AddWideningLower(left.GetLower(), right.GetLower()) : AdvSimd.AddWideningUpper(left, right) : wideLeft + wideRight;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> AddWidening32(Vector128<short> left, Vector128<short> right, int part, Vector128<int> wideLeft, Vector128<int> wideRight) =>
        AdvSimd.IsSupported ? part == 0 ? AdvSimd.AddWideningLower(left.GetLower(), right.GetLower()) : AdvSimd.AddWideningUpper(left, right) : wideLeft + wideRight;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<ulong> AddWidening64(Vector128<uint> left, Vector128<uint> right, int part, Vector128<ulong> wideLeft, Vector128<ulong> wideRight) =>
        AdvSimd.IsSupported ? part == 0 ? AdvSimd.AddWideningLower(left.GetLower(), right.GetLower()) : AdvSimd.AddWideningUpper(left, right) : wideLeft + wideRight;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<long> AddWidening64(Vector128<int> left, Vector128<int> right, int part, Vector128<long> wideLeft, Vector128<long> wideRight) =>
        AdvSimd.IsSupported ? part == 0 ? AdvSimd.AddWideningLower(left.GetLower(), right.GetLower()) : AdvSimd.AddWideningUpper(left, right) : wideLeft + wideRight;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<ushort> SubtractWidening16(Vector128<byte> left, Vector128<byte> right, int part, Vector128<ushort> wideLeft, Vector128<ushort> wideRight) =>
        AdvSimd.IsSupported ? part == 0 ? AdvSimd.SubtractWideningLower(left.GetLower(), right.GetLower()) : AdvSimd.SubtractWideningUpper(left, right) : wideLeft - wideRight;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<short> SubtractWidening16(Vector128<sbyte> left, Vector128<sbyte> right, int part, Vector128<short> wideLeft, Vector128<short> wideRight) =>
        AdvSimd.IsSupported ? part == 0 ? AdvSimd.SubtractWideningLower(left.GetLower(), right.GetLower()) : AdvSimd.SubtractWideningUpper(left, right) : wideLeft - wideRight;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> SubtractWidening32(Vector128<ushort> left, Vector128<ushort> right, int part, Vector128<uint> wideLeft, Vector128<uint> wideRight) =>
        AdvSimd.IsSupported ? part == 0 ? AdvSimd.SubtractWideningLower(left.GetLower(), right.GetLower()) : AdvSimd.SubtractWideningUpper(left, right) : wideLeft - wideRight;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> SubtractWidening32(Vector128<short> left, Vector128<short> right, int part, Vector128<int> wideLeft, Vector128<int> wideRight) =>
        AdvSimd.IsSupported ? part == 0 ? AdvSimd.SubtractWideningLower(left.GetLower(), right.GetLower()) : AdvSimd.SubtractWideningUpper(left, right) : wideLeft - wideRight;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<ulong> SubtractWidening64(Vector128<uint> left, Vector128<uint> right, int part, Vector128<ulong> wideLeft, Vector128<ulong> wideRight) =>
        AdvSimd.IsSupported ? part == 0 ? AdvSimd.SubtractWideningLower(left.GetLower(), right.GetLower()) : AdvSimd.SubtractWideningUpper(left, right) : wideLeft - wideRight;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<long> SubtractWidening64(Vector128<int> left, Vector128<int> right, int part, Vector128<long> wideLeft, Vector128<long> wideRight) =>
        AdvSimd.IsSupported ? part == 0 ? AdvSimd.SubtractWideningLower(left.GetLower(), right.GetLower()) : AdvSimd.SubtractWideningUpper(left, right) : wideLeft - wideRight;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> AddWidening32(Vector128<byte> left, Vector128<byte> right, int part, Vector128<uint> wideLeft, Vector128<uint> wideRight) =>
        AdvSimd.IsSupported ? Widen(part < 2 ? AdvSimd.AddWideningLower(left.GetLower(), right.GetLower()) : AdvSimd.AddWideningUpper(left, right), part % 2)
            : wideLeft + wideRight;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> AddWidening32(Vector128<sbyte> left, Vector128<sbyte> right, int part, Vector128<int> wideLeft, Vector128<int> wideRight) =>
        AdvSimd.IsSupported ? Widen(part < 2 ? AdvSimd.AddWideningLower(left.GetLower(), right.GetLower()) : AdvSimd.AddWideningUpper(left, right), part % 2)
            : wideLeft + wideRight;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> SubtractWidening32(Vector128<byte> left, Vector128<byte> right, int part, Vector128<uint> wideLeft, Vector128<uint> wideRight) =>
        AdvSimd.IsSupported
            ? Widen((part < 2 ? AdvSimd.SubtractWideningLower(left.GetLower(), right.GetLower()) : AdvSimd.SubtractWideningUpper(left, right)).AsInt16(), part % 2).AsUInt32()
            : wideLeft - wideRight;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> SubtractWidening32(Vector128<sbyte> left, Vector128<sbyte> right, int part, Vector128<int> wideLeft, Vector128<int> wideRight) =>
        AdvSimd.IsSupported ? Widen(part < 2 ? AdvSimd.SubtractWideningLower(left.GetLower(), right.GetLower()) : AdvSimd.SubtractWideningUpper(left, right), part % 2)
            : wideLeft - wideRight;

    // A half of halfwords extended to words, by the lanes' sign: uxtl, sxtl and their upper forms.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> Widen(Vector128<ushort> value, int half) =>
        half == 0 ? AdvSimd.ZeroExtendWideningLower(value.GetLower()) : AdvSimd.ZeroExtendWideningUpper(value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Widen(Vector128<short> value, int half) =>
        half == 0 ? AdvSimd.SignExtendWideningLower(value.GetLower()) : AdvSimd.SignExtendWideningUpper(value);

    // Narrowings of one fragment whose upper half is zero (VectorEmitter.Resize: a vector of half a fragment or less, as
    // the x86 cost model's 64-bit vectors): xtn alone on ARM64, which zeroes the upper half, where Vector128.Narrow with a
    // zero vector is xtn, movi and xtn2 (SV-36); sqxtn and uqxtn for the saturating forms, fcvtn for doubles.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<sbyte> NarrowLower(Vector128<short> value) =>
        AdvSimd.IsSupported ? AdvSimd.ExtractNarrowingLower(value).ToVector128Unsafe() : Vector128.Narrow(value, Vector128<short>.Zero);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> NarrowLower(Vector128<ushort> value) =>
        AdvSimd.IsSupported ? AdvSimd.ExtractNarrowingLower(value).ToVector128Unsafe() : Vector128.Narrow(value, Vector128<ushort>.Zero);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<short> NarrowLower(Vector128<int> value) =>
        AdvSimd.IsSupported ? AdvSimd.ExtractNarrowingLower(value).ToVector128Unsafe() : Vector128.Narrow(value, Vector128<int>.Zero);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<ushort> NarrowLower(Vector128<uint> value) =>
        AdvSimd.IsSupported ? AdvSimd.ExtractNarrowingLower(value).ToVector128Unsafe() : Vector128.Narrow(value, Vector128<uint>.Zero);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> NarrowLower(Vector128<long> value) =>
        AdvSimd.IsSupported ? AdvSimd.ExtractNarrowingLower(value).ToVector128Unsafe() : Vector128.Narrow(value, Vector128<long>.Zero);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> NarrowLower(Vector128<ulong> value) =>
        AdvSimd.IsSupported ? AdvSimd.ExtractNarrowingLower(value).ToVector128Unsafe() : Vector128.Narrow(value, Vector128<ulong>.Zero);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> NarrowLower(Vector128<double> value) =>
        AdvSimd.Arm64.IsSupported ? AdvSimd.Arm64.ConvertToSingleLower(value).ToVector128Unsafe() : Vector128.Narrow(value, Vector128<double>.Zero);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<sbyte> NarrowWithSaturationLower(Vector128<short> value) =>
        AdvSimd.IsSupported ? AdvSimd.ExtractNarrowingSaturateLower(value).ToVector128Unsafe() : Vector128.NarrowWithSaturation(value, Vector128<short>.Zero);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> NarrowWithSaturationLower(Vector128<ushort> value) =>
        AdvSimd.IsSupported ? AdvSimd.ExtractNarrowingSaturateLower(value).ToVector128Unsafe() : Vector128.NarrowWithSaturation(value, Vector128<ushort>.Zero);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<short> NarrowWithSaturationLower(Vector128<int> value) =>
        AdvSimd.IsSupported ? AdvSimd.ExtractNarrowingSaturateLower(value).ToVector128Unsafe() : Vector128.NarrowWithSaturation(value, Vector128<int>.Zero);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<ushort> NarrowWithSaturationLower(Vector128<uint> value) =>
        AdvSimd.IsSupported ? AdvSimd.ExtractNarrowingSaturateLower(value).ToVector128Unsafe() : Vector128.NarrowWithSaturation(value, Vector128<uint>.Zero);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> NarrowWithSaturationLower(Vector128<long> value) =>
        AdvSimd.IsSupported ? AdvSimd.ExtractNarrowingSaturateLower(value).ToVector128Unsafe() : Vector128.NarrowWithSaturation(value, Vector128<long>.Zero);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> NarrowWithSaturationLower(Vector128<ulong> value) =>
        AdvSimd.IsSupported ? AdvSimd.ExtractNarrowingSaturateLower(value).ToVector128Unsafe() : Vector128.NarrowWithSaturation(value, Vector128<ulong>.Zero);

    // The lanes of a shuffle's two sources by a constant mask of whole lanes (VectorEmitter.Gather): pblendvb where the host
    // has SSE4.1, one instruction, where RyuJIT compiles a ConditionalSelect whose constant mask it has hoisted into a
    // register as and, andn and or; ARM64's ConditionalSelect is one bsl.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<T> Select<T>(Vector128<T> mask, Vector128<T> ifTrue, Vector128<T> ifFalse) where T : unmanaged =>
        Sse41.IsSupported ? Sse41.BlendVariable(ifFalse.AsByte(), ifTrue.AsByte(), mask.AsByte()).As<byte, T>() : Vector128.ConditionalSelect(mask, ifTrue, ifFalse);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<T> Select<T>(Vector256<T> mask, Vector256<T> ifTrue, Vector256<T> ifFalse) where T : unmanaged =>
        Avx2.IsSupported ? Avx2.BlendVariable(ifFalse.AsByte(), ifTrue.AsByte(), mask.AsByte()).As<byte, T>() : Vector256.ConditionalSelect(mask, ifTrue, ifFalse);

    /// <summary>
    /// The sum of the absolute differences of the first <paramref name="bytes"/> bytes (2 to 16) of two vectors whose other
    /// bytes are zero in both: llvmnet's operation for a reduction of one row, where only the total matters (SV-35, from
    /// SV-21's recognizer, native/vectors.cpp). x64 runs psadbw and adds the 64-bit sums that summed the row's bytes, as
    /// the recognizer wrote it before; ARM64 runs uabd and uaddlv, where psadbw's two exact sums take three pairwise
    /// additions (uaddlp). uaddlv zeroes the register above its 16-bit sum, so its 64-bit view is the total, one umov.
    /// The total is 64 bits wide, as psadbw's sums are, and the reduction truncates it: an int result made RyuJIT spill
    /// every row's sum on x64.
    /// </summary>
    [LlvmIntrinsic("llvm.llvmnet.sad"), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long SumAbsoluteDifferences(Vector128<byte> left, Vector128<byte> right, int bytes)
    {
        if (AdvSimd.Arm64.IsSupported)
            return (bytes <= 8 ? AdvSimd.Arm64.AddAcrossWidening(AdvSimd.AbsoluteDifference(left.GetLower(), right.GetLower()))
                : AdvSimd.Arm64.AddAcrossWidening(AdvSimd.AbsoluteDifference(left, right))).AsInt64().ToScalar();
        Vector128<long> sums = X86.Psadbw(left, right).AsInt64();
        return bytes <= 8 ? sums.GetElement(0) : sums.GetElement(0) + sums.GetElement(1);
    }

    // Constant shuffles of one source that AdvSimd does in one instruction (VectorEmitter.Gather, SV-38). RyuJIT compiles
    // Vector128.Shuffle with constant indices to tbl and a constant load on ARM64, apart from a broadcast (dup) and a
    // rotation by half the vector (ext); the interleaves of a vector with itself (zip1, zip2), its even and odd lanes
    // (uzp1, uzp2), transposes (trn1, trn2), rotations (ext) and the reversals of lanes within 16-, 32- and 64-bit groups
    // (rev16, rev32, rev64) are one instruction. form names the instruction, a rotation's first lane or a reversal's group
    // bytes above bit 5; elsewhere Shuffle with the constant indices, as before.

    public const int PermuteZip1 = 1, PermuteZip2 = 2, PermuteUnzip1 = 3, PermuteUnzip2 = 4, PermuteTranspose1 = 5, PermuteTranspose2 = 6,
        PermuteRotate = 7, PermuteReverse = 8;

    /// <summary>A shuffle of one vector by constant indices: <paramref name="form"/>'s permute on ARM64, Shuffle elsewhere.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<T> Shuffle<T>(Vector128<T> value, Vector128<T> indices, int form) where T : unmanaged =>
        AdvSimd.Arm64.IsSupported ? sizeof(T) switch
        {
            1 => Permute(value.AsByte(), form & 31, form >> 5).As<byte, T>(),
            2 => Permute(value.AsUInt16(), form & 31, form >> 5).As<ushort, T>(),
            4 => Permute(value.AsUInt32(), form & 31, form >> 5).As<uint, T>(),
            _ => Permute(value.AsUInt64(), form & 31, form >> 5).As<ulong, T>(),
        } : ConstantShuffle(value, indices);

    /// <summary>Vector128.Shuffle of the lane type, the indices of floating-point lanes as integers.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<T> ConstantShuffle<T>(Vector128<T> value, Vector128<T> indices) where T : unmanaged
    {
        if (typeof(T) == typeof(float)) return Vector128.Shuffle(value.AsSingle(), indices.AsInt32()).As<float, T>();
        if (typeof(T) == typeof(double)) return Vector128.Shuffle(value.AsDouble(), indices.AsInt64()).As<double, T>();
        if (typeof(T) == typeof(sbyte)) return Vector128.Shuffle(value.AsSByte(), indices.AsSByte()).As<sbyte, T>();
        if (typeof(T) == typeof(byte)) return Vector128.Shuffle(value.AsByte(), indices.AsByte()).As<byte, T>();
        if (typeof(T) == typeof(short)) return Vector128.Shuffle(value.AsInt16(), indices.AsInt16()).As<short, T>();
        if (typeof(T) == typeof(ushort)) return Vector128.Shuffle(value.AsUInt16(), indices.AsUInt16()).As<ushort, T>();
        if (typeof(T) == typeof(int)) return Vector128.Shuffle(value.AsInt32(), indices.AsInt32()).As<int, T>();
        if (typeof(T) == typeof(uint)) return Vector128.Shuffle(value.AsUInt32(), indices.AsUInt32()).As<uint, T>();
        if (typeof(T) == typeof(long)) return Vector128.Shuffle(value.AsInt64(), indices.AsInt64()).As<long, T>();
        return Vector128.Shuffle(value.AsUInt64(), indices.AsUInt64()).As<ulong, T>();
    }

    // The permutes by lane size; count is a rotation's first lane or a reversal's group bytes.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Permute(Vector128<byte> value, int kind, int count) => kind switch
    {
        PermuteZip1 => AdvSimd.Arm64.ZipLow(value, value),
        PermuteZip2 => AdvSimd.Arm64.ZipHigh(value, value),
        PermuteUnzip1 => AdvSimd.Arm64.UnzipEven(value, value),
        PermuteUnzip2 => AdvSimd.Arm64.UnzipOdd(value, value),
        PermuteTranspose1 => AdvSimd.Arm64.TransposeEven(value, value),
        PermuteTranspose2 => AdvSimd.Arm64.TransposeOdd(value, value),
        PermuteRotate => AdvSimd.ExtractVector128(value, value, (byte)count),
        _ => count == 2 ? AdvSimd.ReverseElement8(value.AsInt16()).AsByte()
            : count == 4 ? AdvSimd.ReverseElement8(value.AsInt32()).AsByte() : AdvSimd.ReverseElement8(value.AsInt64()).AsByte(),
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> Permute(Vector128<ushort> value, int kind, int count) => kind switch
    {
        PermuteZip1 => AdvSimd.Arm64.ZipLow(value, value),
        PermuteZip2 => AdvSimd.Arm64.ZipHigh(value, value),
        PermuteUnzip1 => AdvSimd.Arm64.UnzipEven(value, value),
        PermuteUnzip2 => AdvSimd.Arm64.UnzipOdd(value, value),
        PermuteTranspose1 => AdvSimd.Arm64.TransposeEven(value, value),
        PermuteTranspose2 => AdvSimd.Arm64.TransposeOdd(value, value),
        PermuteRotate => AdvSimd.ExtractVector128(value, value, (byte)count),
        _ => count == 4 ? AdvSimd.ReverseElement16(value.AsInt32()).AsUInt16() : AdvSimd.ReverseElement16(value.AsInt64()).AsUInt16(),
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> Permute(Vector128<uint> value, int kind, int count) => kind switch
    {
        PermuteZip1 => AdvSimd.Arm64.ZipLow(value, value),
        PermuteZip2 => AdvSimd.Arm64.ZipHigh(value, value),
        PermuteUnzip1 => AdvSimd.Arm64.UnzipEven(value, value),
        PermuteUnzip2 => AdvSimd.Arm64.UnzipOdd(value, value),
        PermuteTranspose1 => AdvSimd.Arm64.TransposeEven(value, value),
        PermuteTranspose2 => AdvSimd.Arm64.TransposeOdd(value, value),
        PermuteRotate => AdvSimd.ExtractVector128(value, value, (byte)count),
        _ => AdvSimd.ReverseElement32(value.AsInt64()).AsUInt32(),
    };

    // Two 64-bit lanes: a single-source zip, unzip or transpose is a broadcast, which VectorEmitter leaves to RyuJIT.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ulong> Permute(Vector128<ulong> value, int kind, int count) => AdvSimd.ExtractVector128(value, value, (byte)count);

    /// <summary>A half of each of two vectors of two 64-bit lanes as the lower and the upper half of the result: one ins or
    /// ext on ARM64 (SV-38), where Create of the two lanes is dup and ins.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<double> Halves(Vector128<double> lower, int lowerHalf, Vector128<double> upper, int upperHalf) => AdvSimd.Arm64.IsSupported
        ? lowerHalf == 0 ? AdvSimd.Arm64.InsertSelectedScalar(lower, 1, upper, (byte)upperHalf)
            : upperHalf == 1 ? AdvSimd.Arm64.InsertSelectedScalar(upper, 0, lower, 1) : AdvSimd.ExtractVector128(lower, upper, 1)
        : Vector128.Create(lower.GetElement(lowerHalf), upper.GetElement(upperHalf));

    // Scalars that become vector lanes and that ILC must not fold as constants (VectorEmitter.LoadLane): function addresses,
    // relocatable constants to ILC, which a folded Vector128.Create keeps as their compile-time handle values.

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static nint OpaqueAddress(nint value) => value;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Opaque(int value) => value;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static long Opaque(long value) => value;

    // Bit counts and reversals per lane, composed from shifts, masks and adds that every host executes as vector
    // instructions (no ISA has a vector popcount below AVX-512 BITALG and NEON's byte cnt). A count of a zero lane is
    // the lane width, which ctlz and cttz leave as the defined result when their zero-is-poison flag is clear.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> PopCount(Vector128<byte> value)
    {
        value -= (value >>> 1) & Vector128.Create((byte)0x55);
        value = (value & Vector128.Create((byte)0x33)) + ((value >>> 2) & Vector128.Create((byte)0x33));
        return (value + (value >>> 4)) & Vector128.Create((byte)0x0f);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<ushort> PopCount(Vector128<ushort> value)
    {
        Vector128<ushort> bytes = PopCount(value.AsByte()).AsUInt16();
        return (bytes & Vector128.Create((ushort)0xff)) + (bytes >>> 8);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> PopCount(Vector128<uint> value)
    {
        Vector128<uint> halves = PopCount(value.AsUInt16()).AsUInt32();
        return (halves & Vector128.Create(0xffffu)) + (halves >>> 16);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<ulong> PopCount(Vector128<ulong> value)
    {
        Vector128<ulong> halves = PopCount(value.AsUInt32()).AsUInt64();
        return (halves & Vector128.Create(0xfffffffful)) + (halves >>> 32);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> LeadingZeroCount(Vector128<byte> value)
    {
        value |= value >>> 1;
        value |= value >>> 2;
        value |= value >>> 4;
        return Vector128.Create((byte)8) - PopCount(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<ushort> LeadingZeroCount(Vector128<ushort> value)
    {
        value |= value >>> 1;
        value |= value >>> 2;
        value |= value >>> 4;
        value |= value >>> 8;
        return Vector128.Create((ushort)16) - PopCount(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> LeadingZeroCount(Vector128<uint> value)
    {
        value |= value >>> 1;
        value |= value >>> 2;
        value |= value >>> 4;
        value |= value >>> 8;
        value |= value >>> 16;
        return Vector128.Create(32u) - PopCount(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<ulong> LeadingZeroCount(Vector128<ulong> value)
    {
        value |= value >>> 1;
        value |= value >>> 2;
        value |= value >>> 4;
        value |= value >>> 8;
        value |= value >>> 16;
        value |= value >>> 32;
        return Vector128.Create(64ul) - PopCount(value);
    }

    // The trailing zeros are the ones of ~x & (x - 1).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> TrailingZeroCount(Vector128<byte> value) => PopCount(Vector128.AndNot(value - Vector128<byte>.One, value));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<ushort> TrailingZeroCount(Vector128<ushort> value) => PopCount(Vector128.AndNot(value - Vector128<ushort>.One, value));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> TrailingZeroCount(Vector128<uint> value) => PopCount(Vector128.AndNot(value - Vector128<uint>.One, value));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<ulong> TrailingZeroCount(Vector128<ulong> value) => PopCount(Vector128.AndNot(value - Vector128<ulong>.One, value));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<ushort> ByteSwap(Vector128<ushort> value) =>
        Vector128.Shuffle(value.AsByte(), Vector128.Create((byte)1, 0, 3, 2, 5, 4, 7, 6, 9, 8, 11, 10, 13, 12, 15, 14)).AsUInt16();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> ByteSwap(Vector128<uint> value) =>
        Vector128.Shuffle(value.AsByte(), Vector128.Create((byte)3, 2, 1, 0, 7, 6, 5, 4, 11, 10, 9, 8, 15, 14, 13, 12)).AsUInt32();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<ulong> ByteSwap(Vector128<ulong> value) =>
        Vector128.Shuffle(value.AsByte(), Vector128.Create((byte)7, 6, 5, 4, 3, 2, 1, 0, 15, 14, 13, 12, 11, 10, 9, 8)).AsUInt64();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> ReverseBits(Vector128<byte> value)
    {
        value = ((value >>> 1) & Vector128.Create((byte)0x55)) | ((value & Vector128.Create((byte)0x55)) << 1);
        value = ((value >>> 2) & Vector128.Create((byte)0x33)) | ((value & Vector128.Create((byte)0x33)) << 2);
        return (value >>> 4) | (value << 4);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<ushort> ReverseBits(Vector128<ushort> value) => ReverseBits(ByteSwap(value).AsByte()).AsUInt16();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> ReverseBits(Vector128<uint> value) => ReverseBits(ByteSwap(value).AsByte()).AsUInt32();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<ulong> ReverseBits(Vector128<ulong> value) => ReverseBits(ByteSwap(value).AsByte()).AsUInt64();

    /// <summary>llvm.fshl and llvm.fshr lane by lane with a count per lane, taken modulo the lane width: the high bits of
    /// high:low shifted left, or its low bits shifted right.</summary>
    public static Vector128<T> FunnelShift<T>(Vector128<T> high, Vector128<T> low, Vector128<T> count, bool right) where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        int width = Unsafe.SizeOf<T>() * 8;
        Vector128<T> result = Vector128<T>.Zero;
        for (int lane = 0; lane < Vector128<T>.Count; lane++)
        {
            int shift = int.CreateTruncating(count.GetElement(lane)) & (width - 1);
            T upper = high.GetElement(lane), lower = low.GetElement(lane);
            T value = shift == 0 ? right ? lower : upper
                : right ? (lower >>> shift) | (upper << (width - shift)) : (upper << shift) | (lower >>> (width - shift));
            result = result.WithElement(lane, value);
        }
        return result;
    }

    // The same operations on 256-bit fragments (--vector-width=256): what Vector256 has no single operation for, by
    // halves or lane by lane.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<byte> LoadWide(nint address, int size) => size <= 16
        ? (size == 16 ? Unsafe.ReadUnaligned<Vector128<byte>>((void*)address) : Load(address, size)).ToVector256()
        : Vector256.Create(Unsafe.ReadUnaligned<Vector128<byte>>((void*)address), Load(address + 16, size - 16));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void StoreWide(nint address, Vector256<byte> value, int size)
    {
        if (size <= 16)
        {
            if (size == 16) Unsafe.WriteUnaligned((void*)address, value.GetLower());
            else Store(address, value.GetLower(), size);
            return;
        }
        Unsafe.WriteUnaligned((void*)address, value.GetLower());
        Store(address + 16, value.GetUpper(), size - 16);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<sbyte> Mask8Wide(ulong bits) => Vector256.Create(Mask8(bits), Mask8(bits >> 16));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<short> Mask16Wide(ulong bits) => Vector256.Create(Mask16(bits), Mask16(bits >> 8));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<int> Mask32Wide(ulong bits) => Vector256.Create(Mask32(bits), Mask32(bits >> 4));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<long> Mask64Wide(ulong bits) => Vector256.Create(Mask64(bits), Mask64(bits >> 2));

    public static Vector256<T> Divide<T>(Vector256<T> left, Vector256<T> right, int lanes) where T : unmanaged, INumber<T> =>
        Vector256.Create(Divide(left.GetLower(), right.GetLower(), Math.Min(lanes, Vector128<T>.Count)),
            Divide(left.GetUpper(), right.GetUpper(), Math.Max(0, lanes - Vector128<T>.Count)));

    public static Vector256<T> Remainder<T>(Vector256<T> left, Vector256<T> right, int lanes) where T : unmanaged, INumber<T> =>
        Vector256.Create(Remainder(left.GetLower(), right.GetLower(), Math.Min(lanes, Vector128<T>.Count)),
            Remainder(left.GetUpper(), right.GetUpper(), Math.Max(0, lanes - Vector128<T>.Count)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<T> ShiftLeft<T>(Vector256<T> value, Vector256<T> count) where T : unmanaged, IBinaryInteger<T>
    {
        if (Avx2.IsSupported && sizeof(T) == 4) return Avx2.ShiftLeftLogicalVariable(value.AsUInt32(), count.AsUInt32() & Vector256.Create(31u)).As<uint, T>();
        if (Avx2.IsSupported && sizeof(T) == 8) return Avx2.ShiftLeftLogicalVariable(value.AsUInt64(), count.AsUInt64() & Vector256.Create(63ul)).As<ulong, T>();
        return Vector256.Create(ShiftLeft(value.GetLower(), count.GetLower()), ShiftLeft(value.GetUpper(), count.GetUpper()));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<T> ShiftRightArithmetic<T>(Vector256<T> value, Vector256<T> count) where T : unmanaged, IBinaryInteger<T>
    {
        if (Avx2.IsSupported && sizeof(T) == 4) return Avx2.ShiftRightArithmeticVariable(value.AsInt32(), count.AsUInt32() & Vector256.Create(31u)).As<int, T>();
        return Vector256.Create(ShiftRightArithmetic(value.GetLower(), count.GetLower()), ShiftRightArithmetic(value.GetUpper(), count.GetUpper()));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<T> ShiftRightLogical<T>(Vector256<T> value, Vector256<T> count) where T : unmanaged, IBinaryInteger<T>
    {
        if (Avx2.IsSupported && sizeof(T) == 4) return Avx2.ShiftRightLogicalVariable(value.AsUInt32(), count.AsUInt32() & Vector256.Create(31u)).As<uint, T>();
        if (Avx2.IsSupported && sizeof(T) == 8) return Avx2.ShiftRightLogicalVariable(value.AsUInt64(), count.AsUInt64() & Vector256.Create(63ul)).As<ulong, T>();
        return Vector256.Create(ShiftRightLogical(value.GetLower(), count.GetLower()), ShiftRightLogical(value.GetUpper(), count.GetUpper()));
    }

    public static Vector256<T> FunnelShift<T>(Vector256<T> high, Vector256<T> low, Vector256<T> count, bool right) where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T> =>
        Vector256.Create(FunnelShift(high.GetLower(), low.GetLower(), count.GetLower(), right), FunnelShift(high.GetUpper(), low.GetUpper(), count.GetUpper(), right));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<byte> PopCount(Vector256<byte> value) => Vector256.Create(PopCount(value.GetLower()), PopCount(value.GetUpper()));
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<ushort> PopCount(Vector256<ushort> value) => Vector256.Create(PopCount(value.GetLower()), PopCount(value.GetUpper()));
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<uint> PopCount(Vector256<uint> value) => Vector256.Create(PopCount(value.GetLower()), PopCount(value.GetUpper()));
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<ulong> PopCount(Vector256<ulong> value) => Vector256.Create(PopCount(value.GetLower()), PopCount(value.GetUpper()));
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<byte> LeadingZeroCount(Vector256<byte> value) => Vector256.Create(LeadingZeroCount(value.GetLower()), LeadingZeroCount(value.GetUpper()));
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<ushort> LeadingZeroCount(Vector256<ushort> value) => Vector256.Create(LeadingZeroCount(value.GetLower()), LeadingZeroCount(value.GetUpper()));
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<uint> LeadingZeroCount(Vector256<uint> value) => Vector256.Create(LeadingZeroCount(value.GetLower()), LeadingZeroCount(value.GetUpper()));
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<ulong> LeadingZeroCount(Vector256<ulong> value) => Vector256.Create(LeadingZeroCount(value.GetLower()), LeadingZeroCount(value.GetUpper()));
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<byte> TrailingZeroCount(Vector256<byte> value) => Vector256.Create(TrailingZeroCount(value.GetLower()), TrailingZeroCount(value.GetUpper()));
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<ushort> TrailingZeroCount(Vector256<ushort> value) => Vector256.Create(TrailingZeroCount(value.GetLower()), TrailingZeroCount(value.GetUpper()));
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<uint> TrailingZeroCount(Vector256<uint> value) => Vector256.Create(TrailingZeroCount(value.GetLower()), TrailingZeroCount(value.GetUpper()));
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<ulong> TrailingZeroCount(Vector256<ulong> value) => Vector256.Create(TrailingZeroCount(value.GetLower()), TrailingZeroCount(value.GetUpper()));
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<ushort> ByteSwap(Vector256<ushort> value) => Vector256.Create(ByteSwap(value.GetLower()), ByteSwap(value.GetUpper()));
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<uint> ByteSwap(Vector256<uint> value) => Vector256.Create(ByteSwap(value.GetLower()), ByteSwap(value.GetUpper()));
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<ulong> ByteSwap(Vector256<ulong> value) => Vector256.Create(ByteSwap(value.GetLower()), ByteSwap(value.GetUpper()));
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<byte> ReverseBits(Vector256<byte> value) => Vector256.Create(ReverseBits(value.GetLower()), ReverseBits(value.GetUpper()));
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<ushort> ReverseBits(Vector256<ushort> value) => Vector256.Create(ReverseBits(value.GetLower()), ReverseBits(value.GetUpper()));
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<uint> ReverseBits(Vector256<uint> value) => Vector256.Create(ReverseBits(value.GetLower()), ReverseBits(value.GetUpper()));
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<ulong> ReverseBits(Vector256<ulong> value) => Vector256.Create(ReverseBits(value.GetLower()), ReverseBits(value.GetUpper()));

    public static Vector256<float> SignedToSingleWide(Vector256<long> lower, Vector256<long> upper) =>
        Vector256.Create(SignedToSingle(lower.GetLower(), lower.GetUpper()), SignedToSingle(upper.GetLower(), upper.GetUpper()));

    public static Vector256<float> UnsignedToSingleWide(Vector256<long> lower, Vector256<long> upper) =>
        Vector256.Create(UnsignedToSingle(lower.GetLower(), lower.GetUpper()), UnsignedToSingle(upper.GetLower(), upper.GetUpper()));

    /// <summary>Four floats from the four signed 64-bit lanes of two vectors, each rounded once, as the scalar conversion.</summary>
    public static Vector128<float> SignedToSingle(Vector128<long> lower, Vector128<long> upper) =>
        Vector128.Create(lower.GetElement(0), lower.GetElement(1), upper.GetElement(0), upper.GetElement(1));

    public static Vector128<float> UnsignedToSingle(Vector128<long> lower, Vector128<long> upper) =>
        Vector128.Create((ulong)lower.GetElement(0), (ulong)lower.GetElement(1), (ulong)upper.GetElement(0), (ulong)upper.GetElement(1));
}
