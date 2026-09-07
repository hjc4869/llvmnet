using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace LlvmNet.Runtime;

public static unsafe class Simd128
{
    [CExport("__llvmnet_simd128_load"), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> Load(nint address) => Vector128.Load((byte*)address);

    [CExport("__llvmnet_simd128_store"), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Store(nint address, Vector128<byte> value) => value.Store((byte*)address);

    [CExport("__llvmnet_simd128_add_i32"), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> AddInt32(Vector128<byte> left, Vector128<byte> right) => (left.AsInt32() + right.AsInt32()).AsByte();

    [CExport("__llvmnet_simd128_make"), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> Make(long lower, long upper) => Vector128.Create(lower, upper).AsByte();

    [CExport("__llvmnet_simd128_splat_i8"), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> SplatInt8(int value) => Vector128.Create((byte)value);

    [CExport("__llvmnet_simd128_splat_i16"), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> SplatInt16(int value) => Vector128.Create((short)value).AsByte();

    [CExport("__llvmnet_simd128_splat_i32"), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> SplatInt32(int value) => Vector128.Create(value).AsByte();

    [CExport("__llvmnet_simd128_load64_zero"), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> Load64Zero(nint address) => Vector128.CreateScalar(Unsafe.ReadUnaligned<ulong>((void*)address)).AsByte();

    [CExport("__llvmnet_simd128_load64_lane"), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> Load64Lane(nint address, Vector128<byte> value, int lane) =>
        value.AsUInt64().WithElement(lane, Unsafe.ReadUnaligned<ulong>((void*)address)).AsByte();

    [CExport("__llvmnet_simd128_store64_lane"), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Store64Lane(nint address, Vector128<byte> value, int lane) =>
        Unsafe.WriteUnaligned((void*)address, value.AsUInt64().GetElement(lane));

    [CExport("__llvmnet_simd128_add_i8"), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> AddInt8(Vector128<byte> left, Vector128<byte> right) => left + right;

    [CExport("__llvmnet_simd128_sub_i8"), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> SubtractInt8(Vector128<byte> left, Vector128<byte> right) => left - right;

    [CExport("__llvmnet_simd128_sub_i32"), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> SubtractInt32(Vector128<byte> left, Vector128<byte> right) => (left.AsInt32() - right.AsInt32()).AsByte();

    [CExport("__llvmnet_simd128_mul_i16"), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> MultiplyInt16(Vector128<byte> left, Vector128<byte> right) => (left.AsInt16() * right.AsInt16()).AsByte();

    [CExport("__llvmnet_simd128_shl_i8"), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> ShiftLeftInt8(Vector128<byte> value, int count) => Vector128.ShiftLeft(value, count & 7);

    [CExport("__llvmnet_simd128_shr_u8"), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> ShiftRightUInt8(Vector128<byte> value, int count) => Vector128.ShiftRightLogical(value, count & 7);

    [CExport("__llvmnet_simd128_shr_i16"), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> ShiftRightInt16(Vector128<byte> value, int count) => Vector128.ShiftRightArithmetic(value.AsInt16(), count & 15).AsByte();

    [CExport("__llvmnet_simd128_shr_i32"), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> ShiftRightInt32(Vector128<byte> value, int count) => Vector128.ShiftRightArithmetic(value.AsInt32(), count & 31).AsByte();

    [CExport("__llvmnet_simd128_or"), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> Or(Vector128<byte> left, Vector128<byte> right) => left | right;

    [CExport("__llvmnet_simd128_swizzle"), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> Swizzle(Vector128<byte> value, Vector128<byte> indices) => Vector128.Shuffle(value, indices);

    [CExport("__llvmnet_simd128_shuffle"), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> Shuffle(Vector128<byte> left, Vector128<byte> right, long lower, long upper)
    {
        Vector128<byte> indices = Make(lower, upper);
        return Vector128.Shuffle(left, indices) | Vector128.Shuffle(right, indices - Vector128.Create((byte)16));
    }

    [CExport("__llvmnet_simd128_bitselect"), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> BitSelect(Vector128<byte> left, Vector128<byte> right, Vector128<byte> mask) => Vector128.ConditionalSelect(mask, left, right);

    [CExport("__llvmnet_simd128_bitmask_i8"), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int BitmaskInt8(Vector128<byte> value) => (int)Vector128.ExtractMostSignificantBits(value);

    [CExport("__llvmnet_simd128_alltrue_i8"), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int AllTrueInt8(Vector128<byte> value) => Vector128.EqualsAny(value, Vector128<byte>.Zero) ? 0 : 1;

    [CExport("__llvmnet_simd128_anytrue"), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int AnyTrue(Vector128<byte> value) => Vector128.EqualsAll(value, Vector128<byte>.Zero) ? 0 : 1;

    [CExport("__llvmnet_simd128_gt_u8"), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> GreaterThanUInt8(Vector128<byte> left, Vector128<byte> right) => Vector128.GreaterThan(left, right);

    [CExport("__llvmnet_simd128_lt_u8"), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> LessThanUInt8(Vector128<byte> left, Vector128<byte> right) => Vector128.LessThan(left, right);

    [CExport("__llvmnet_simd128_extend_low_i8"), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> ExtendLowInt8(Vector128<byte> value) => Vector128.WidenLower(value.AsSByte()).AsByte();

    [CExport("__llvmnet_simd128_extend_high_i8"), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> ExtendHighInt8(Vector128<byte> value) => Vector128.WidenUpper(value.AsSByte()).AsByte();

    [CExport("__llvmnet_simd128_extend_low_u8"), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> ExtendLowUInt8(Vector128<byte> value) => Vector128.WidenLower(value).AsByte();

    [CExport("__llvmnet_simd128_extend_high_u8"), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> ExtendHighUInt8(Vector128<byte> value) => Vector128.WidenUpper(value).AsByte();

    [CExport("__llvmnet_simd128_extend_low_i16"), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> ExtendLowInt16(Vector128<byte> value) => Vector128.WidenLower(value.AsInt16()).AsByte();

    [CExport("__llvmnet_simd128_extmul_low_i16"), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> MultiplyLowInt16(Vector128<byte> left, Vector128<byte> right) =>
        (Vector128.WidenLower(left.AsInt16()) * Vector128.WidenLower(right.AsInt16())).AsByte();

    [CExport("__llvmnet_simd128_extmul_high_i16"), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> MultiplyHighInt16(Vector128<byte> left, Vector128<byte> right) =>
        (Vector128.WidenUpper(left.AsInt16()) * Vector128.WidenUpper(right.AsInt16())).AsByte();

    [CExport("__llvmnet_simd128_add_sat_i16"), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> AddSaturateInt16(Vector128<byte> left, Vector128<byte> right) => NarrowInt32(
        (Vector128.WidenLower(left.AsInt16()) + Vector128.WidenLower(right.AsInt16())).AsByte(),
        (Vector128.WidenUpper(left.AsInt16()) + Vector128.WidenUpper(right.AsInt16())).AsByte());

    [CExport("__llvmnet_simd128_add_sat_u8"), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> AddSaturateUInt8(Vector128<byte> left, Vector128<byte> right)
    {
        Vector128<ushort> lower = Vector128.WidenLower(left) + Vector128.WidenLower(right);
        Vector128<ushort> upper = Vector128.WidenUpper(left) + Vector128.WidenUpper(right);
        return Vector128.Narrow(Vector128.Min(lower, Vector128.Create((ushort)255)), Vector128.Min(upper, Vector128.Create((ushort)255)));
    }

    [CExport("__llvmnet_simd128_narrow_i32"), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> NarrowInt32(Vector128<byte> lower, Vector128<byte> upper) =>
        Vector128.Narrow(ClampInt32(lower.AsInt32()), ClampInt32(upper.AsInt32())).AsByte();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> ClampInt32(Vector128<int> value) =>
        Vector128.Min(Vector128.Max(value, Vector128.Create(-32768)), Vector128.Create(32767));

    [CExport("__llvmnet_simd128_narrow_i16"), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> NarrowInt16(Vector128<byte> lower, Vector128<byte> upper) =>
        Vector128.Narrow(ClampInt16(lower.AsInt16(), -128, 127), ClampInt16(upper.AsInt16(), -128, 127)).AsByte();

    [CExport("__llvmnet_simd128_narrow_u16"), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> NarrowUInt16(Vector128<byte> lower, Vector128<byte> upper) =>
        Vector128.Narrow(ClampInt16(lower.AsInt16(), 0, 255).AsUInt16(), ClampInt16(upper.AsInt16(), 0, 255).AsUInt16());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> ClampInt16(Vector128<short> value, short minimum, short maximum) =>
        Vector128.Min(Vector128.Max(value, Vector128.Create(minimum)), Vector128.Create(maximum));

    [CExport("__llvmnet_simd128_q15mulr"), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> Q15Multiply(Vector128<byte> left, Vector128<byte> right)
    {
        Vector128<int> lower = Vector128.WidenLower(left.AsInt16()) * Vector128.WidenLower(right.AsInt16());
        Vector128<int> upper = Vector128.WidenUpper(left.AsInt16()) * Vector128.WidenUpper(right.AsInt16());
        return NarrowInt32(Vector128.ShiftRightArithmetic(lower + Vector128.Create(0x4000), 15).AsByte(),
            Vector128.ShiftRightArithmetic(upper + Vector128.Create(0x4000), 15).AsByte());
    }

    [CExport("__llvmnet_simd128_min_f32"), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> MinimumSingle(Vector128<byte> left, Vector128<byte> right) => Vector128.Min(left.AsSingle(), right.AsSingle()).AsByte();

    [CExport("__llvmnet_simd128_max_f32"), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> MaximumSingle(Vector128<byte> left, Vector128<byte> right) => Vector128.Max(left.AsSingle(), right.AsSingle()).AsByte();

    [CExport("__llvmnet_simd128_pmin_f32"), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> PseudoMinimumSingle(Vector128<byte> left, Vector128<byte> right) =>
        Vector128.ConditionalSelect(Vector128.LessThan(right.AsSingle(), left.AsSingle()).AsByte(), right, left);

    [CExport("__llvmnet_simd128_eq_f32"), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> EqualSingle(Vector128<byte> left, Vector128<byte> right) => Vector128.Equals(left.AsSingle(), right.AsSingle()).AsByte();

    [CExport("__llvmnet_simd128_trunc_sat_i32"), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> TruncateSaturateInt32(Vector128<byte> value) => Vector128.ConvertToInt32(value.AsSingle()).AsByte();

    [CExport("__llvmnet_simd128_trunc_sat_u32"), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> TruncateSaturateUInt32(Vector128<byte> value) => Vector128.ConvertToUInt32(value.AsSingle()).AsByte();
}