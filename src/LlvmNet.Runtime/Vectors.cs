using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace LlvmNet.Runtime;

/// <summary>
/// Helpers of the typed vector lowering (ADR-16 Level 1) for what Vector128 has no single operation for: memory accesses
/// of fewer than 16 bytes, i1 vectors as bits, and the lane-by-lane operations no SIMD instruction performs.
/// </summary>
public static unsafe class Vectors
{
    /// <summary>Reads <paramref name="size"/> bytes (1 to 15) into the low lanes of a vector, zero above.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> Load(nint address, int size)
    {
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
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Store(nint address, Vector128<byte> value, int size)
    {
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

    /// <summary>A shift of each lane by the count in the same lane. A count at or above the lane width is poison in LLVM;
    /// it is taken modulo the width here, as C# shifts do.</summary>
    public static Vector128<T> ShiftLeft<T>(Vector128<T> value, Vector128<T> count) where T : unmanaged, IBinaryInteger<T>
    {
        Vector128<T> result = Vector128<T>.Zero;
        for (int lane = 0; lane < Vector128<T>.Count; lane++)
            result = result.WithElement(lane, value.GetElement(lane) << int.CreateTruncating(count.GetElement(lane)));
        return result;
    }

    /// <summary>A shift right of each lane by the count in the same lane: arithmetic for signed lanes, logical for unsigned.</summary>
    public static Vector128<T> ShiftRightArithmetic<T>(Vector128<T> value, Vector128<T> count) where T : unmanaged, IBinaryInteger<T>
    {
        Vector128<T> result = Vector128<T>.Zero;
        for (int lane = 0; lane < Vector128<T>.Count; lane++)
            result = result.WithElement(lane, value.GetElement(lane) >> int.CreateTruncating(count.GetElement(lane)));
        return result;
    }

    public static Vector128<T> ShiftRightLogical<T>(Vector128<T> value, Vector128<T> count) where T : unmanaged, IBinaryInteger<T>
    {
        Vector128<T> result = Vector128<T>.Zero;
        for (int lane = 0; lane < Vector128<T>.Count; lane++)
            result = result.WithElement(lane, value.GetElement(lane) >>> int.CreateTruncating(count.GetElement(lane)));
        return result;
    }

    /// <summary>Four floats from the four signed 64-bit lanes of two vectors, each rounded once, as the scalar conversion.</summary>
    public static Vector128<float> SignedToSingle(Vector128<long> lower, Vector128<long> upper) =>
        Vector128.Create(lower.GetElement(0), lower.GetElement(1), upper.GetElement(0), upper.GetElement(1));

    public static Vector128<float> UnsignedToSingle(Vector128<long> lower, Vector128<long> upper) =>
        Vector128.Create((ulong)lower.GetElement(0), (ulong)lower.GetElement(1), (ulong)upper.GetElement(0), (ulong)upper.GetElement(1));
}
