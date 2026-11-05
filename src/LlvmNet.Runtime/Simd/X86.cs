using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace LlvmNet.Runtime;

/// <summary>
/// The SSE to SSE4.2 target intrinsics (llvm.x86.*), with PCLMULQDQ, that Clang emits for the C intrinsics it does not
/// lower to generic vector IR, one method each (ADR-16 Level 2, docs/plan/12-simd.md). A method runs the x86 instruction
/// where the host has it, behind an explicit IsSupported branch, which is a constant for the JIT and a run-time check for
/// NativeAOT's opportunistic instruction sets; elsewhere it runs the AdvSimd instruction of the same meaning or a
/// composition of Vector128 operations, with the instruction's exact results: the operand a NaN comparison returns, the
/// integer indefinite of an out-of-range conversion, saturation and rounding. rcpps and rsqrtps are approximations whose
/// bits differ between processors; composed, they divide, which stays within the instructions' documented error.
/// </summary>
public static unsafe partial class X86
{
    // The immediates (comparison predicates, rounding modes, controls) are the intrinsic's constant operand, which the
    // JIT and ILC see as a constant once the helper is inlined at the call site; a helper left as a call takes .NET's
    // jump-table path for a variable immediate.
#pragma warning disable CA1857
    private const MethodImplOptions Inline = MethodImplOptions.AggressiveInlining;
    private const float MinNormal = 1.17549435E-38f;

    /// <summary>emms ends MMX code: Clang implements the MMX intrinsics with SSE2 vectors since Clang 20, so no x87 state
    /// remains to release and the other llvm.x86.mmx intrinsics never appear.</summary>
    [LlvmIntrinsic("llvm.x86.mmx.emms"), MethodImpl(Inline)]
    public static void Emms() { }

    // ---- SSE ----

    [LlvmIntrinsic("llvm.x86.sse.rcp.ps"), MethodImpl(Inline)]
    public static Vector128<float> Rcpps(Vector128<float> value) => Sse.IsSupported ? Sse.Reciprocal(value) : Reciprocal(value);

    [LlvmIntrinsic("llvm.x86.sse.rcp.ss"), MethodImpl(Inline)]
    public static Vector128<float> Rcpss(Vector128<float> value) =>
        Sse.IsSupported ? Sse.ReciprocalScalar(value) : value.WithElement(0, Reciprocal(value).ToScalar());

    [LlvmIntrinsic("llvm.x86.sse.rsqrt.ps"), MethodImpl(Inline)]
    public static Vector128<float> Rsqrtps(Vector128<float> value) => Sse.IsSupported ? Sse.ReciprocalSqrt(value) : ReciprocalSqrt(value);

    [LlvmIntrinsic("llvm.x86.sse.rsqrt.ss"), MethodImpl(Inline)]
    public static Vector128<float> Rsqrtss(Vector128<float> value) =>
        Sse.IsSupported ? Sse.ReciprocalSqrtScalar(value) : value.WithElement(0, ReciprocalSqrt(value).ToScalar());

    /// <summary>rcpps as a division: denormal inputs count as zeros of their sign and tiny results flush to zero, as the instruction's do.</summary>
    private static Vector128<float> Reciprocal(Vector128<float> value)
    {
        Vector128<float> sign = value & Vector128.Create(-0f);
        Vector128<float> result = Vector128.ConditionalSelect(Vector128.LessThan(Vector128.Abs(value), Vector128.Create(MinNormal)),
            sign | Vector128.Create(float.PositiveInfinity), Vector128<float>.One / value);
        return Vector128.ConditionalSelect(Vector128.LessThan(Vector128.Abs(result), Vector128.Create(MinNormal)), sign, result);
    }

    /// <summary>rsqrtps as a square root and a division: denormal inputs count as zeros of their sign, whose result is an infinity of that sign.</summary>
    private static Vector128<float> ReciprocalSqrt(Vector128<float> value) =>
        Vector128.ConditionalSelect(Vector128.LessThan(Vector128.Abs(value), Vector128.Create(MinNormal)),
            (value & Vector128.Create(-0f)) | Vector128.Create(float.PositiveInfinity), Vector128<float>.One / Vector128.Sqrt(value));

    // minps and maxps return the second operand when either is NaN and when both are zeros, which is a select on the
    // ordered comparison, not .NET's Min and Max.

    [LlvmIntrinsic("llvm.x86.sse.min.ps"), MethodImpl(Inline)]
    public static Vector128<float> Minps(Vector128<float> left, Vector128<float> right) =>
        Sse.IsSupported ? Sse.Min(left, right) : Vector128.ConditionalSelect(Vector128.LessThan(left, right), left, right);

    [LlvmIntrinsic("llvm.x86.sse.max.ps"), MethodImpl(Inline)]
    public static Vector128<float> Maxps(Vector128<float> left, Vector128<float> right) =>
        Sse.IsSupported ? Sse.Max(left, right) : Vector128.ConditionalSelect(Vector128.GreaterThan(left, right), left, right);

    [LlvmIntrinsic("llvm.x86.sse.min.ss"), MethodImpl(Inline)]
    public static Vector128<float> Minss(Vector128<float> left, Vector128<float> right) =>
        Sse.IsSupported ? Sse.MinScalar(left, right) : left.WithElement(0, left.ToScalar() < right.ToScalar() ? left.ToScalar() : right.ToScalar());

    [LlvmIntrinsic("llvm.x86.sse.max.ss"), MethodImpl(Inline)]
    public static Vector128<float> Maxss(Vector128<float> left, Vector128<float> right) =>
        Sse.IsSupported ? Sse.MaxScalar(left, right) : left.WithElement(0, left.ToScalar() > right.ToScalar() ? left.ToScalar() : right.ToScalar());

    [LlvmIntrinsic("llvm.x86.sse.cmp.ps"), MethodImpl(Inline)]
    public static Vector128<float> Cmpps(Vector128<float> left, Vector128<float> right, int predicate) => Compare(left, right, predicate);

    [LlvmIntrinsic("llvm.x86.sse.cmp.ss"), MethodImpl(Inline)]
    public static Vector128<float> Cmpss(Vector128<float> left, Vector128<float> right, int predicate) =>
        left.WithElement(0, Compare(left, right, predicate).ToScalar());

    /// <summary>cmpps and cmppd: the predicate's low four bits select the comparison (the fifth, AVX's, only whether it signals).</summary>
    [MethodImpl(Inline)]
    private static Vector128<T> Compare<T>(Vector128<T> left, Vector128<T> right, int predicate) => (predicate & 15) switch
    {
        0 => Vector128.Equals(left, right),
        1 => Vector128.LessThan(left, right),
        2 => Vector128.LessThanOrEqual(left, right),
        3 => ~Ordered(left, right),
        4 => ~Vector128.Equals(left, right),
        5 => ~Vector128.LessThan(left, right),
        6 => ~Vector128.LessThanOrEqual(left, right),
        7 => Ordered(left, right),
        8 => Vector128.Equals(left, right) | ~Ordered(left, right),
        9 => ~Vector128.GreaterThanOrEqual(left, right),
        10 => ~Vector128.GreaterThan(left, right),
        11 => Vector128<T>.Zero,
        12 => Ordered(left, right) & ~Vector128.Equals(left, right),
        13 => Vector128.GreaterThanOrEqual(left, right),
        14 => Vector128.GreaterThan(left, right),
        _ => Vector128<T>.AllBitsSet,
    };

    [MethodImpl(Inline)]
    private static Vector128<T> Ordered<T>(Vector128<T> left, Vector128<T> right) => Vector128.Equals(left, left) & Vector128.Equals(right, right);

    // comiss and ucomiss differ only in which NaNs raise the invalid exception, which translated code cannot observe.
    // Equality and the orderings are false for NaN, inequality true, as Clang's intrinsics define them.

    [LlvmIntrinsic("llvm.x86.sse.comieq.ss"), LlvmIntrinsic("llvm.x86.sse.ucomieq.ss"), MethodImpl(Inline)]
    public static int Comieqss(Vector128<float> left, Vector128<float> right) => left.ToScalar() == right.ToScalar() ? 1 : 0;

    [LlvmIntrinsic("llvm.x86.sse.comineq.ss"), LlvmIntrinsic("llvm.x86.sse.ucomineq.ss"), MethodImpl(Inline)]
    public static int Comineqss(Vector128<float> left, Vector128<float> right) => left.ToScalar() == right.ToScalar() ? 0 : 1;

    [LlvmIntrinsic("llvm.x86.sse.comilt.ss"), LlvmIntrinsic("llvm.x86.sse.ucomilt.ss"), MethodImpl(Inline)]
    public static int Comiltss(Vector128<float> left, Vector128<float> right) => left.ToScalar() < right.ToScalar() ? 1 : 0;

    [LlvmIntrinsic("llvm.x86.sse.comile.ss"), LlvmIntrinsic("llvm.x86.sse.ucomile.ss"), MethodImpl(Inline)]
    public static int Comiless(Vector128<float> left, Vector128<float> right) => left.ToScalar() <= right.ToScalar() ? 1 : 0;

    [LlvmIntrinsic("llvm.x86.sse.comigt.ss"), LlvmIntrinsic("llvm.x86.sse.ucomigt.ss"), MethodImpl(Inline)]
    public static int Comigtss(Vector128<float> left, Vector128<float> right) => left.ToScalar() > right.ToScalar() ? 1 : 0;

    [LlvmIntrinsic("llvm.x86.sse.comige.ss"), LlvmIntrinsic("llvm.x86.sse.ucomige.ss"), MethodImpl(Inline)]
    public static int Comigess(Vector128<float> left, Vector128<float> right) => left.ToScalar() >= right.ToScalar() ? 1 : 0;

    // Conversions round to nearest even (the MXCSR default, which translated code cannot change) or truncate, and give the
    // integer indefinite (the minimum value) for NaN and out-of-range inputs, where .NET saturates.

    [LlvmIntrinsic("llvm.x86.sse.cvtss2si"), MethodImpl(Inline)]
    public static int Cvtss2si(Vector128<float> value) => Sse.IsSupported ? Sse.ConvertToInt32(value) : ToInt32(MathF.Round(value.ToScalar()));

    [LlvmIntrinsic("llvm.x86.sse.cvttss2si"), MethodImpl(Inline)]
    public static int Cvttss2si(Vector128<float> value) =>
        Sse.IsSupported ? Sse.ConvertToInt32WithTruncation(value) : ToInt32(MathF.Truncate(value.ToScalar()));

    [LlvmIntrinsic("llvm.x86.sse.cvtss2si64"), MethodImpl(Inline)]
    public static long Cvtss2si64(Vector128<float> value) =>
        Sse.X64.IsSupported ? Sse.X64.ConvertToInt64(value) : ToInt64(MathF.Round(value.ToScalar()));

    [LlvmIntrinsic("llvm.x86.sse.cvttss2si64"), MethodImpl(Inline)]
    public static long Cvttss2si64(Vector128<float> value) =>
        Sse.X64.IsSupported ? Sse.X64.ConvertToInt64WithTruncation(value) : ToInt64(MathF.Truncate(value.ToScalar()));

    private static int ToInt32(double integral) => integral >= int.MinValue && integral <= int.MaxValue ? (int)integral : int.MinValue;

    private static long ToInt64(double integral) => integral >= long.MinValue && integral < 9223372036854775808.0 ? (long)integral : long.MinValue;

    [LlvmIntrinsic("llvm.x86.sse.movmsk.ps"), MethodImpl(Inline)]
    public static int Movmskps(Vector128<float> value) => (int)Vector128.ExtractMostSignificantBits(value);

    /// <summary>_mm_getcsr: the default control word (all exceptions masked, round to nearest, no flush to zero) with no
    /// flags, because translated code runs with that environment and does not track the exception flags.</summary>
    [LlvmIntrinsic("llvm.x86.sse.stmxcsr"), MethodImpl(Inline)]
    public static void Stmxcsr(nint address) => *(uint*)address = 0x1F80;

    // ---- SSE2 ----

    [LlvmIntrinsic("llvm.x86.sse2.min.pd"), MethodImpl(Inline)]
    public static Vector128<double> Minpd(Vector128<double> left, Vector128<double> right) =>
        Sse2.IsSupported ? Sse2.Min(left, right) : Vector128.ConditionalSelect(Vector128.LessThan(left, right), left, right);

    [LlvmIntrinsic("llvm.x86.sse2.max.pd"), MethodImpl(Inline)]
    public static Vector128<double> Maxpd(Vector128<double> left, Vector128<double> right) =>
        Sse2.IsSupported ? Sse2.Max(left, right) : Vector128.ConditionalSelect(Vector128.GreaterThan(left, right), left, right);

    [LlvmIntrinsic("llvm.x86.sse2.min.sd"), MethodImpl(Inline)]
    public static Vector128<double> Minsd(Vector128<double> left, Vector128<double> right) =>
        Sse2.IsSupported ? Sse2.MinScalar(left, right) : left.WithElement(0, left.ToScalar() < right.ToScalar() ? left.ToScalar() : right.ToScalar());

    [LlvmIntrinsic("llvm.x86.sse2.max.sd"), MethodImpl(Inline)]
    public static Vector128<double> Maxsd(Vector128<double> left, Vector128<double> right) =>
        Sse2.IsSupported ? Sse2.MaxScalar(left, right) : left.WithElement(0, left.ToScalar() > right.ToScalar() ? left.ToScalar() : right.ToScalar());

    [LlvmIntrinsic("llvm.x86.sse2.cmp.pd"), MethodImpl(Inline)]
    public static Vector128<double> Cmppd(Vector128<double> left, Vector128<double> right, int predicate) => Compare(left, right, predicate);

    [LlvmIntrinsic("llvm.x86.sse2.cmp.sd"), MethodImpl(Inline)]
    public static Vector128<double> Cmpsd(Vector128<double> left, Vector128<double> right, int predicate) =>
        left.WithElement(0, Compare(left, right, predicate).ToScalar());

    [LlvmIntrinsic("llvm.x86.sse2.comieq.sd"), LlvmIntrinsic("llvm.x86.sse2.ucomieq.sd"), MethodImpl(Inline)]
    public static int Comieqsd(Vector128<double> left, Vector128<double> right) => left.ToScalar() == right.ToScalar() ? 1 : 0;

    [LlvmIntrinsic("llvm.x86.sse2.comineq.sd"), LlvmIntrinsic("llvm.x86.sse2.ucomineq.sd"), MethodImpl(Inline)]
    public static int Comineqsd(Vector128<double> left, Vector128<double> right) => left.ToScalar() == right.ToScalar() ? 0 : 1;

    [LlvmIntrinsic("llvm.x86.sse2.comilt.sd"), LlvmIntrinsic("llvm.x86.sse2.ucomilt.sd"), MethodImpl(Inline)]
    public static int Comiltsd(Vector128<double> left, Vector128<double> right) => left.ToScalar() < right.ToScalar() ? 1 : 0;

    [LlvmIntrinsic("llvm.x86.sse2.comile.sd"), LlvmIntrinsic("llvm.x86.sse2.ucomile.sd"), MethodImpl(Inline)]
    public static int Comilesd(Vector128<double> left, Vector128<double> right) => left.ToScalar() <= right.ToScalar() ? 1 : 0;

    [LlvmIntrinsic("llvm.x86.sse2.comigt.sd"), LlvmIntrinsic("llvm.x86.sse2.ucomigt.sd"), MethodImpl(Inline)]
    public static int Comigtsd(Vector128<double> left, Vector128<double> right) => left.ToScalar() > right.ToScalar() ? 1 : 0;

    [LlvmIntrinsic("llvm.x86.sse2.comige.sd"), LlvmIntrinsic("llvm.x86.sse2.ucomige.sd"), MethodImpl(Inline)]
    public static int Comigesd(Vector128<double> left, Vector128<double> right) => left.ToScalar() >= right.ToScalar() ? 1 : 0;

    [LlvmIntrinsic("llvm.x86.sse2.pmulh.w"), MethodImpl(Inline)]
    public static Vector128<short> Pmulhw(Vector128<short> left, Vector128<short> right) =>
        Sse2.IsSupported ? Sse2.MultiplyHigh(left, right)
        : AdvSimd.Arm64.IsSupported ? AdvSimd.Arm64.UnzipOdd(AdvSimd.MultiplyWideningLower(left.GetLower(), right.GetLower()).AsInt16(), AdvSimd.MultiplyWideningUpper(left, right).AsInt16())
        : Vector128.Narrow((Vector128.WidenLower(left) * Vector128.WidenLower(right)) >> 16, (Vector128.WidenUpper(left) * Vector128.WidenUpper(right)) >> 16);

    [LlvmIntrinsic("llvm.x86.sse2.pmulhu.w"), MethodImpl(Inline)]
    public static Vector128<ushort> Pmulhuw(Vector128<ushort> left, Vector128<ushort> right) =>
        Sse2.IsSupported ? Sse2.MultiplyHigh(left, right)
        : AdvSimd.Arm64.IsSupported ? AdvSimd.Arm64.UnzipOdd(AdvSimd.MultiplyWideningLower(left.GetLower(), right.GetLower()).AsUInt16(), AdvSimd.MultiplyWideningUpper(left, right).AsUInt16())
        : Vector128.Narrow((Vector128.WidenLower(left) * Vector128.WidenLower(right)) >> 16, (Vector128.WidenUpper(left) * Vector128.WidenUpper(right)) >> 16);

    /// <summary>pmaddwd: the sums of adjacent products of 16-bit lanes, wrapping (two products of -32768 sum to int.MinValue).</summary>
    [LlvmIntrinsic("llvm.x86.sse2.pmadd.wd"), MethodImpl(Inline)]
    public static Vector128<int> Pmaddwd(Vector128<short> left, Vector128<short> right)
    {
        if (Sse2.IsSupported) return Sse2.MultiplyAddAdjacent(left, right);
        if (AdvSimd.Arm64.IsSupported)
            return AdvSimd.Arm64.AddPairwise(AdvSimd.MultiplyWideningLower(left.GetLower(), right.GetLower()), AdvSimd.MultiplyWideningUpper(left, right));
        Vector128<int> l = left.AsInt32(), r = right.AsInt32();
        return ((l << 16) >> 16) * ((r << 16) >> 16) + (l >> 16) * (r >> 16);
    }

    [LlvmIntrinsic("llvm.x86.sse2.pavg.b"), MethodImpl(Inline)]
    public static Vector128<byte> Pavgb(Vector128<byte> left, Vector128<byte> right) =>
        Sse2.IsSupported ? Sse2.Average(left, right) : AdvSimd.IsSupported ? AdvSimd.FusedAddRoundedHalving(left, right) : (left | right) - ((left ^ right) >> 1);

    [LlvmIntrinsic("llvm.x86.sse2.pavg.w"), MethodImpl(Inline)]
    public static Vector128<ushort> Pavgw(Vector128<ushort> left, Vector128<ushort> right) =>
        Sse2.IsSupported ? Sse2.Average(left, right) : AdvSimd.IsSupported ? AdvSimd.FusedAddRoundedHalving(left, right) : (left | right) - ((left ^ right) >> 1);

    /// <summary>psadbw: the sums of the absolute differences of bytes 0 to 7 and 8 to 15, each in a 64-bit lane.</summary>
    [LlvmIntrinsic("llvm.x86.sse2.psad.bw"), MethodImpl(Inline)]
    public static Vector128<ulong> Psadbw(Vector128<byte> left, Vector128<byte> right)
    {
        if (Sse2.IsSupported) return Sse2.SumAbsoluteDifferences(left, right).AsUInt64();
        if (AdvSimd.IsSupported)
            return AdvSimd.AddPairwiseWidening(AdvSimd.AddPairwiseWidening(AdvSimd.AddPairwiseWidening(AdvSimd.AbsoluteDifference(left, right))));
        Vector128<byte> difference = Vector128.Max(left, right) - Vector128.Min(left, right);
        return Vector128.Create((ulong)Vector128.Sum(Vector128.WidenLower(difference)), Vector128.Sum(Vector128.WidenUpper(difference)));
    }

    // Shifts by a count in the low 64 bits of a vector: a count of the lane width or more clears the lanes (shifts them
    // to the sign for psra). The i forms take an i32 count, which LLVM folds to a shift when it is a constant in range.

    private static ulong Count<T>(Vector128<T> count) => count.AsUInt64().ToScalar();

    [LlvmIntrinsic("llvm.x86.sse2.psll.w"), MethodImpl(Inline)]
    public static Vector128<short> Psllw(Vector128<short> value, Vector128<short> count) =>
        Sse2.IsSupported ? Sse2.ShiftLeftLogical(value, count) : Count(count) > 15 ? Vector128<short>.Zero : value << (int)Count(count);

    [LlvmIntrinsic("llvm.x86.sse2.psll.d"), MethodImpl(Inline)]
    public static Vector128<int> Pslld(Vector128<int> value, Vector128<int> count) =>
        Sse2.IsSupported ? Sse2.ShiftLeftLogical(value, count) : Count(count) > 31 ? Vector128<int>.Zero : value << (int)Count(count);

    [LlvmIntrinsic("llvm.x86.sse2.psll.q"), MethodImpl(Inline)]
    public static Vector128<long> Psllq(Vector128<long> value, Vector128<long> count) =>
        Sse2.IsSupported ? Sse2.ShiftLeftLogical(value, count) : Count(count) > 63 ? Vector128<long>.Zero : value << (int)Count(count);

    [LlvmIntrinsic("llvm.x86.sse2.psrl.w"), MethodImpl(Inline)]
    public static Vector128<short> Psrlw(Vector128<short> value, Vector128<short> count) =>
        Sse2.IsSupported ? Sse2.ShiftRightLogical(value, count) : Count(count) > 15 ? Vector128<short>.Zero : value >>> (int)Count(count);

    [LlvmIntrinsic("llvm.x86.sse2.psrl.d"), MethodImpl(Inline)]
    public static Vector128<int> Psrld(Vector128<int> value, Vector128<int> count) =>
        Sse2.IsSupported ? Sse2.ShiftRightLogical(value, count) : Count(count) > 31 ? Vector128<int>.Zero : value >>> (int)Count(count);

    [LlvmIntrinsic("llvm.x86.sse2.psrl.q"), MethodImpl(Inline)]
    public static Vector128<long> Psrlq(Vector128<long> value, Vector128<long> count) =>
        Sse2.IsSupported ? Sse2.ShiftRightLogical(value, count) : Count(count) > 63 ? Vector128<long>.Zero : value >>> (int)Count(count);

    [LlvmIntrinsic("llvm.x86.sse2.psra.w"), MethodImpl(Inline)]
    public static Vector128<short> Psraw(Vector128<short> value, Vector128<short> count) =>
        Sse2.IsSupported ? Sse2.ShiftRightArithmetic(value, count) : value >> (int)Math.Min(Count(count), 15);

    [LlvmIntrinsic("llvm.x86.sse2.psra.d"), MethodImpl(Inline)]
    public static Vector128<int> Psrad(Vector128<int> value, Vector128<int> count) =>
        Sse2.IsSupported ? Sse2.ShiftRightArithmetic(value, count) : value >> (int)Math.Min(Count(count), 31);

    [LlvmIntrinsic("llvm.x86.sse2.pslli.w"), MethodImpl(Inline)]
    public static Vector128<short> Pslliw(Vector128<short> value, int count) => (uint)count > 15 ? Vector128<short>.Zero : value << count;

    [LlvmIntrinsic("llvm.x86.sse2.pslli.d"), MethodImpl(Inline)]
    public static Vector128<int> Psllid(Vector128<int> value, int count) => (uint)count > 31 ? Vector128<int>.Zero : value << count;

    [LlvmIntrinsic("llvm.x86.sse2.pslli.q"), MethodImpl(Inline)]
    public static Vector128<long> Pslliq(Vector128<long> value, int count) => (uint)count > 63 ? Vector128<long>.Zero : value << count;

    [LlvmIntrinsic("llvm.x86.sse2.psrli.w"), MethodImpl(Inline)]
    public static Vector128<short> Psrliw(Vector128<short> value, int count) => (uint)count > 15 ? Vector128<short>.Zero : value >>> count;

    [LlvmIntrinsic("llvm.x86.sse2.psrli.d"), MethodImpl(Inline)]
    public static Vector128<int> Psrlid(Vector128<int> value, int count) => (uint)count > 31 ? Vector128<int>.Zero : value >>> count;

    [LlvmIntrinsic("llvm.x86.sse2.psrli.q"), MethodImpl(Inline)]
    public static Vector128<long> Psrliq(Vector128<long> value, int count) => (uint)count > 63 ? Vector128<long>.Zero : value >>> count;

    [LlvmIntrinsic("llvm.x86.sse2.psrai.w"), MethodImpl(Inline)]
    public static Vector128<short> Psraiw(Vector128<short> value, int count) => value >> (int)Math.Min((uint)count, 15);

    [LlvmIntrinsic("llvm.x86.sse2.psrai.d"), MethodImpl(Inline)]
    public static Vector128<int> Psraid(Vector128<int> value, int count) => value >> (int)Math.Min((uint)count, 31);

    [LlvmIntrinsic("llvm.x86.sse2.cvtpd2dq"), MethodImpl(Inline)]
    public static Vector128<int> Cvtpd2dq(Vector128<double> value) =>
        Sse2.IsSupported ? Sse2.ConvertToVector128Int32(value) : Vector128.Create(ToInt32(Math.Round(value.ToScalar())), ToInt32(Math.Round(value.GetElement(1))), 0, 0);

    [LlvmIntrinsic("llvm.x86.sse2.cvttpd2dq"), MethodImpl(Inline)]
    public static Vector128<int> Cvttpd2dq(Vector128<double> value) =>
        Sse2.IsSupported ? Sse2.ConvertToVector128Int32WithTruncation(value)
        : Vector128.Create(ToInt32(Math.Truncate(value.ToScalar())), ToInt32(Math.Truncate(value.GetElement(1))), 0, 0);

    [LlvmIntrinsic("llvm.x86.sse2.cvtpd2ps"), MethodImpl(Inline)]
    public static Vector128<float> Cvtpd2ps(Vector128<double> value) =>
        Sse2.IsSupported ? Sse2.ConvertToVector128Single(value) : Vector128.Create((float)value.ToScalar(), (float)value.GetElement(1), 0f, 0f);

    [LlvmIntrinsic("llvm.x86.sse2.cvtps2dq"), MethodImpl(Inline)]
    public static Vector128<int> Cvtps2dq(Vector128<float> value) =>
        Sse2.IsSupported ? Sse2.ConvertToVector128Int32(value)
        : Vector128.ConditionalSelect(InInt32Range(value), Vector128.ConvertToInt32(Vector128.Round(value)), Vector128.Create(int.MinValue));

    [LlvmIntrinsic("llvm.x86.sse2.cvttps2dq"), MethodImpl(Inline)]
    public static Vector128<int> Cvttps2dq(Vector128<float> value) =>
        Sse2.IsSupported ? Sse2.ConvertToVector128Int32WithTruncation(value)
        : Vector128.ConditionalSelect(InInt32Range(value), Vector128.ConvertToInt32(value), Vector128.Create(int.MinValue));

    /// <summary>Lanes whose value rounds into the int range either way (below 2^31 every float is an integer near the bounds).</summary>
    private static Vector128<int> InInt32Range(Vector128<float> value) =>
        (Vector128.GreaterThanOrEqual(value, Vector128.Create(-2147483648f)) & Vector128.LessThan(value, Vector128.Create(2147483648f))).AsInt32();

    [LlvmIntrinsic("llvm.x86.sse2.cvtsd2si"), MethodImpl(Inline)]
    public static int Cvtsd2si(Vector128<double> value) => Sse2.IsSupported ? Sse2.ConvertToInt32(value) : ToInt32(Math.Round(value.ToScalar()));

    [LlvmIntrinsic("llvm.x86.sse2.cvttsd2si"), MethodImpl(Inline)]
    public static int Cvttsd2si(Vector128<double> value) =>
        Sse2.IsSupported ? Sse2.ConvertToInt32WithTruncation(value) : ToInt32(Math.Truncate(value.ToScalar()));

    [LlvmIntrinsic("llvm.x86.sse2.cvtsd2si64"), MethodImpl(Inline)]
    public static long Cvtsd2si64(Vector128<double> value) =>
        Sse2.X64.IsSupported ? Sse2.X64.ConvertToInt64(value) : ToInt64(Math.Round(value.ToScalar()));

    [LlvmIntrinsic("llvm.x86.sse2.cvttsd2si64"), MethodImpl(Inline)]
    public static long Cvttsd2si64(Vector128<double> value) =>
        Sse2.X64.IsSupported ? Sse2.X64.ConvertToInt64WithTruncation(value) : ToInt64(Math.Truncate(value.ToScalar()));

    [LlvmIntrinsic("llvm.x86.sse2.cvtsd2ss"), MethodImpl(Inline)]
    public static Vector128<float> Cvtsd2ss(Vector128<float> upper, Vector128<double> value) =>
        Sse2.IsSupported ? Sse2.ConvertScalarToVector128Single(upper, value) : upper.WithElement(0, (float)value.ToScalar());

    [LlvmIntrinsic("llvm.x86.sse2.packsswb.128"), MethodImpl(Inline)]
    public static Vector128<sbyte> Packsswb(Vector128<short> left, Vector128<short> right) =>
        Sse2.IsSupported ? Sse2.PackSignedSaturate(left, right) : Vector128.NarrowWithSaturation(left, right);

    [LlvmIntrinsic("llvm.x86.sse2.packssdw.128"), MethodImpl(Inline)]
    public static Vector128<short> Packssdw(Vector128<int> left, Vector128<int> right) =>
        Sse2.IsSupported ? Sse2.PackSignedSaturate(left, right) : Vector128.NarrowWithSaturation(left, right);

    [LlvmIntrinsic("llvm.x86.sse2.packuswb.128"), MethodImpl(Inline)]
    public static Vector128<byte> Packuswb(Vector128<short> left, Vector128<short> right) =>
        Sse2.IsSupported ? Sse2.PackUnsignedSaturate(left, right)
        : AdvSimd.IsSupported ? AdvSimd.ExtractNarrowingSaturateUnsignedUpper(AdvSimd.ExtractNarrowingSaturateUnsignedLower(left), right)
        : Vector128.NarrowWithSaturation(Vector128.Max(left, Vector128<short>.Zero).AsUInt16(), Vector128.Max(right, Vector128<short>.Zero).AsUInt16());

    [LlvmIntrinsic("llvm.x86.sse2.movmsk.pd"), MethodImpl(Inline)]
    public static int Movmskpd(Vector128<double> value) => (int)Vector128.ExtractMostSignificantBits(value);

    [LlvmIntrinsic("llvm.x86.sse2.pmovmskb.128"), MethodImpl(Inline)]
    public static int Pmovmskb(Vector128<byte> value) => (int)Vector128.ExtractMostSignificantBits(value);

    /// <summary>maskmovdqu: stores the bytes whose mask byte has its top bit set and touches no other byte.</summary>
    [LlvmIntrinsic("llvm.x86.sse2.maskmov.dqu"), MethodImpl(Inline)]
    public static void Maskmovdqu(Vector128<byte> value, Vector128<byte> mask, nint address)
    {
        if (Sse2.IsSupported) Sse2.MaskMove(value, mask, (byte*)address);
        else MaskStore(value, mask, (byte*)address);
    }

    private static void MaskStore(Vector128<byte> value, Vector128<byte> mask, byte* address)
    {
        for (int lane = 0; lane < 16; lane++)
            if (mask.GetElement(lane) >= 0x80) address[lane] = value.GetElement(lane);
    }

    /// <summary>clflush writes back and evicts a cache line, which changes nothing a program reads.</summary>
    [LlvmIntrinsic("llvm.x86.sse2.clflush"), MethodImpl(Inline)]
    public static void Clflush(nint address) { }

    // ---- SSE3 ----

    [LlvmIntrinsic("llvm.x86.sse3.addsub.ps"), MethodImpl(Inline)]
    public static Vector128<float> Addsubps(Vector128<float> left, Vector128<float> right) =>
        Sse3.IsSupported ? Sse3.AddSubtract(left, right) : Vector128.ConditionalSelect(Vector128.Create(0, -1, 0, -1).AsSingle(), left + right, left - right);

    [LlvmIntrinsic("llvm.x86.sse3.addsub.pd"), MethodImpl(Inline)]
    public static Vector128<double> Addsubpd(Vector128<double> left, Vector128<double> right) =>
        Sse3.IsSupported ? Sse3.AddSubtract(left, right) : Vector128.ConditionalSelect(Vector128.Create(0, -1L).AsDouble(), left + right, left - right);

    [LlvmIntrinsic("llvm.x86.sse3.hadd.ps"), MethodImpl(Inline)]
    public static Vector128<float> Haddps(Vector128<float> left, Vector128<float> right) =>
        Sse3.IsSupported ? Sse3.HorizontalAdd(left, right) : AdvSimd.Arm64.IsSupported ? AdvSimd.Arm64.AddPairwise(left, right) : Evens(left, right) + Odds(left, right);

    [LlvmIntrinsic("llvm.x86.sse3.hadd.pd"), MethodImpl(Inline)]
    public static Vector128<double> Haddpd(Vector128<double> left, Vector128<double> right) =>
        Sse3.IsSupported ? Sse3.HorizontalAdd(left, right) : AdvSimd.Arm64.IsSupported ? AdvSimd.Arm64.AddPairwise(left, right) : Evens(left, right) + Odds(left, right);

    [LlvmIntrinsic("llvm.x86.sse3.hsub.ps"), MethodImpl(Inline)]
    public static Vector128<float> Hsubps(Vector128<float> left, Vector128<float> right) =>
        Sse3.IsSupported ? Sse3.HorizontalSubtract(left, right) : Evens(left, right) - Odds(left, right);

    [LlvmIntrinsic("llvm.x86.sse3.hsub.pd"), MethodImpl(Inline)]
    public static Vector128<double> Hsubpd(Vector128<double> left, Vector128<double> right) =>
        Sse3.IsSupported ? Sse3.HorizontalSubtract(left, right) : Evens(left, right) - Odds(left, right);

    /// <summary>lddqu: an unaligned load, which differs from movdqu only in how it splits cache lines.</summary>
    [LlvmIntrinsic("llvm.x86.sse3.ldu.dq"), MethodImpl(Inline)]
    public static Vector128<byte> Lddqu(nint address) => Vector128.Load((byte*)address);

    // The even and odd lanes of two vectors, the first's then the second's, for the horizontal operations.

    [MethodImpl(Inline)]
    private static Vector128<float> Evens(Vector128<float> left, Vector128<float> right)
    {
        if (AdvSimd.Arm64.IsSupported) return AdvSimd.Arm64.UnzipEven(left, right);
        Vector128<int> order = Vector128.Create(0, 2, 1, 3);
        return Vector128.Create(Vector128.Shuffle(left, order).AsDouble().ToScalar(), Vector128.Shuffle(right, order).AsDouble().ToScalar()).AsSingle();
    }

    [MethodImpl(Inline)]
    private static Vector128<float> Odds(Vector128<float> left, Vector128<float> right)
    {
        if (AdvSimd.Arm64.IsSupported) return AdvSimd.Arm64.UnzipOdd(left, right);
        Vector128<int> order = Vector128.Create(0, 2, 1, 3);
        return Vector128.Create(Vector128.Shuffle(left, order).AsDouble().GetElement(1), Vector128.Shuffle(right, order).AsDouble().GetElement(1)).AsSingle();
    }

    [MethodImpl(Inline)]
    private static Vector128<double> Evens(Vector128<double> left, Vector128<double> right) => Vector128.Create(left.ToScalar(), right.ToScalar());

    [MethodImpl(Inline)]
    private static Vector128<double> Odds(Vector128<double> left, Vector128<double> right) => Vector128.Create(left.GetElement(1), right.GetElement(1));

    [MethodImpl(Inline)]
    private static Vector128<short> Evens(Vector128<short> left, Vector128<short> right) =>
        AdvSimd.Arm64.IsSupported ? AdvSimd.Arm64.UnzipEven(left, right) : Vector128.Narrow(left.AsInt32(), right.AsInt32());

    [MethodImpl(Inline)]
    private static Vector128<short> Odds(Vector128<short> left, Vector128<short> right) =>
        AdvSimd.Arm64.IsSupported ? AdvSimd.Arm64.UnzipOdd(left, right) : Vector128.Narrow(left.AsInt32() >> 16, right.AsInt32() >> 16);

    [MethodImpl(Inline)]
    private static Vector128<int> Evens(Vector128<int> left, Vector128<int> right) =>
        AdvSimd.Arm64.IsSupported ? AdvSimd.Arm64.UnzipEven(left, right) : Vector128.Narrow(left.AsInt64(), right.AsInt64());

    [MethodImpl(Inline)]
    private static Vector128<int> Odds(Vector128<int> left, Vector128<int> right) =>
        AdvSimd.Arm64.IsSupported ? AdvSimd.Arm64.UnzipOdd(left, right) : Vector128.Narrow(left.AsInt64() >>> 32, right.AsInt64() >>> 32);

    // ---- SSSE3 ----

    [LlvmIntrinsic("llvm.x86.ssse3.phadd.w.128"), MethodImpl(Inline)]
    public static Vector128<short> Phaddw(Vector128<short> left, Vector128<short> right) =>
        Ssse3.IsSupported ? Ssse3.HorizontalAdd(left, right) : AdvSimd.Arm64.IsSupported ? AdvSimd.Arm64.AddPairwise(left, right) : Evens(left, right) + Odds(left, right);

    [LlvmIntrinsic("llvm.x86.ssse3.phadd.d.128"), MethodImpl(Inline)]
    public static Vector128<int> Phaddd(Vector128<int> left, Vector128<int> right) =>
        Ssse3.IsSupported ? Ssse3.HorizontalAdd(left, right) : AdvSimd.Arm64.IsSupported ? AdvSimd.Arm64.AddPairwise(left, right) : Evens(left, right) + Odds(left, right);

    [LlvmIntrinsic("llvm.x86.ssse3.phadd.sw.128"), MethodImpl(Inline)]
    public static Vector128<short> Phaddsw(Vector128<short> left, Vector128<short> right) =>
        Ssse3.IsSupported ? Ssse3.HorizontalAddSaturate(left, right) : Vector128.AddSaturate(Evens(left, right), Odds(left, right));

    [LlvmIntrinsic("llvm.x86.ssse3.phsub.w.128"), MethodImpl(Inline)]
    public static Vector128<short> Phsubw(Vector128<short> left, Vector128<short> right) =>
        Ssse3.IsSupported ? Ssse3.HorizontalSubtract(left, right) : Evens(left, right) - Odds(left, right);

    [LlvmIntrinsic("llvm.x86.ssse3.phsub.d.128"), MethodImpl(Inline)]
    public static Vector128<int> Phsubd(Vector128<int> left, Vector128<int> right) =>
        Ssse3.IsSupported ? Ssse3.HorizontalSubtract(left, right) : Evens(left, right) - Odds(left, right);

    [LlvmIntrinsic("llvm.x86.ssse3.phsub.sw.128"), MethodImpl(Inline)]
    public static Vector128<short> Phsubsw(Vector128<short> left, Vector128<short> right) =>
        Ssse3.IsSupported ? Ssse3.HorizontalSubtractSaturate(left, right) : Vector128.SubtractSaturate(Evens(left, right), Odds(left, right));

    /// <summary>pmaddubsw: unsigned bytes of the first operand times signed bytes of the second, adjacent products added with signed saturation.</summary>
    [LlvmIntrinsic("llvm.x86.ssse3.pmadd.ub.sw.128"), MethodImpl(Inline)]
    public static Vector128<short> Pmaddubsw(Vector128<byte> left, Vector128<sbyte> right)
    {
        if (Ssse3.IsSupported) return Ssse3.MultiplyAddAdjacent(left, right);
        Vector128<short> l = left.AsInt16(), r = right.AsInt16();
        return Vector128.AddSaturate((l & Vector128.Create((short)0xff)) * ((r << 8) >> 8), (l >>> 8) * (r >> 8));
    }

    /// <summary>pmulhrsw: bits 15 to 30 of each product, rounded; -32768 squared gives -32768.</summary>
    [LlvmIntrinsic("llvm.x86.ssse3.pmul.hr.sw.128"), MethodImpl(Inline)]
    public static Vector128<short> Pmulhrsw(Vector128<short> left, Vector128<short> right)
    {
        if (Ssse3.IsSupported) return Ssse3.MultiplyHighRoundScale(left, right);
        Vector128<int> low = Vector128.WidenLower(left) * Vector128.WidenLower(right), high = Vector128.WidenUpper(left) * Vector128.WidenUpper(right);
        return Vector128.Narrow(((low >> 14) + Vector128<int>.One) >> 1, ((high >> 14) + Vector128<int>.One) >> 1);
    }

    /// <summary>pshufb: each byte selected by the low four bits of its index, zero where the index's top bit is set.</summary>
    [LlvmIntrinsic("llvm.x86.ssse3.pshuf.b.128"), MethodImpl(Inline)]
    public static Vector128<byte> Pshufb(Vector128<byte> value, Vector128<byte> indices) =>
        Ssse3.IsSupported ? Ssse3.Shuffle(value, indices)
        : AdvSimd.Arm64.IsSupported ? AdvSimd.Arm64.VectorTableLookup(value, indices & Vector128.Create((byte)0x8f))
        : Vector128.Shuffle(value, indices & Vector128.Create((byte)0x8f));

    [LlvmIntrinsic("llvm.x86.ssse3.psign.b.128"), MethodImpl(Inline)]
    public static Vector128<sbyte> Psignb(Vector128<sbyte> value, Vector128<sbyte> sign) => Ssse3.IsSupported ? Ssse3.Sign(value, sign) : Sign(value, sign);

    [LlvmIntrinsic("llvm.x86.ssse3.psign.w.128"), MethodImpl(Inline)]
    public static Vector128<short> Psignw(Vector128<short> value, Vector128<short> sign) => Ssse3.IsSupported ? Ssse3.Sign(value, sign) : Sign(value, sign);

    [LlvmIntrinsic("llvm.x86.ssse3.psign.d.128"), MethodImpl(Inline)]
    public static Vector128<int> Psignd(Vector128<int> value, Vector128<int> sign) => Ssse3.IsSupported ? Ssse3.Sign(value, sign) : Sign(value, sign);

    /// <summary>psign: the lane negated (wrapping) where the sign lane is negative, cleared where it is zero.</summary>
    [MethodImpl(Inline)]
    private static Vector128<T> Sign<T>(Vector128<T> value, Vector128<T> sign) =>
        Vector128.ConditionalSelect(Vector128.LessThan(sign, Vector128<T>.Zero), Vector128<T>.Zero - value, value) & ~Vector128.Equals(sign, Vector128<T>.Zero);

    // ---- SSE4.1 ----

    // roundps and its siblings: bits 0 and 1 of the mode select the rounding unless bit 2 selects MXCSR's, which is
    // round to nearest even; bit 3 only suppresses the precision exception. A signaling NaN comes back quiet.

    [LlvmIntrinsic("llvm.x86.sse41.round.ps"), MethodImpl(Inline)]
    public static Vector128<float> Roundps(Vector128<float> value, int mode)
    {
        if (Sse41.IsSupported)
            return Rounding(mode) switch
            {
                0 => Sse41.RoundToNearestInteger(value),
                1 => Sse41.RoundToNegativeInfinity(value),
                2 => Sse41.RoundToPositiveInfinity(value),
                _ => Sse41.RoundToZero(value)
            };
        Vector128<float> rounded = Rounding(mode) switch
        {
            0 => Vector128.Round(value),
            1 => Vector128.Floor(value),
            2 => Vector128.Ceiling(value),
            _ => Vector128.Truncate(value)
        };
        return Vector128.ConditionalSelect(Vector128.Equals(value, value), rounded, value + value);
    }

    [LlvmIntrinsic("llvm.x86.sse41.round.pd"), MethodImpl(Inline)]
    public static Vector128<double> Roundpd(Vector128<double> value, int mode)
    {
        if (Sse41.IsSupported)
            return Rounding(mode) switch
            {
                0 => Sse41.RoundToNearestInteger(value),
                1 => Sse41.RoundToNegativeInfinity(value),
                2 => Sse41.RoundToPositiveInfinity(value),
                _ => Sse41.RoundToZero(value)
            };
        Vector128<double> rounded = Rounding(mode) switch
        {
            0 => Vector128.Round(value),
            1 => Vector128.Floor(value),
            2 => Vector128.Ceiling(value),
            _ => Vector128.Truncate(value)
        };
        return Vector128.ConditionalSelect(Vector128.Equals(value, value), rounded, value + value);
    }

    [LlvmIntrinsic("llvm.x86.sse41.round.ss"), MethodImpl(Inline)]
    public static Vector128<float> Roundss(Vector128<float> upper, Vector128<float> value, int mode)
    {
        if (Sse41.IsSupported)
            return Rounding(mode) switch
            {
                0 => Sse41.RoundToNearestIntegerScalar(upper, value),
                1 => Sse41.RoundToNegativeInfinityScalar(upper, value),
                2 => Sse41.RoundToPositiveInfinityScalar(upper, value),
                _ => Sse41.RoundToZeroScalar(upper, value)
            };
        float scalar = value.ToScalar();
        return upper.WithElement(0, float.IsNaN(scalar) ? scalar + scalar : Rounding(mode) switch
        {
            0 => MathF.Round(scalar),
            1 => MathF.Floor(scalar),
            2 => MathF.Ceiling(scalar),
            _ => MathF.Truncate(scalar)
        });
    }

    [LlvmIntrinsic("llvm.x86.sse41.round.sd"), MethodImpl(Inline)]
    public static Vector128<double> Roundsd(Vector128<double> upper, Vector128<double> value, int mode)
    {
        if (Sse41.IsSupported)
            return Rounding(mode) switch
            {
                0 => Sse41.RoundToNearestIntegerScalar(upper, value),
                1 => Sse41.RoundToNegativeInfinityScalar(upper, value),
                2 => Sse41.RoundToPositiveInfinityScalar(upper, value),
                _ => Sse41.RoundToZeroScalar(upper, value)
            };
        double scalar = value.ToScalar();
        return upper.WithElement(0, double.IsNaN(scalar) ? scalar + scalar : Rounding(mode) switch
        {
            0 => Math.Round(scalar),
            1 => Math.Floor(scalar),
            2 => Math.Ceiling(scalar),
            _ => Math.Truncate(scalar)
        });
    }

    private static int Rounding(int mode) => (mode & 4) != 0 ? 0 : mode & 3;

    /// <summary>phminposuw: the smallest unsigned 16-bit lane in lane 0 and the first lane holding it in lane 1, zero above.</summary>
    [LlvmIntrinsic("llvm.x86.sse41.phminposuw"), MethodImpl(Inline)]
    public static Vector128<ushort> Phminposuw(Vector128<ushort> value)
    {
        if (Sse41.IsSupported) return Sse41.MinHorizontal(value);
        ushort minimum;
        if (AdvSimd.Arm64.IsSupported) minimum = AdvSimd.Arm64.MinAcross(value).ToScalar();
        else
        {
            Vector128<ushort> folded = Vector128.Min(value, Vector128.Shuffle(value, Vector128.Create((ushort)4, 5, 6, 7, 0, 1, 2, 3)));
            folded = Vector128.Min(folded, Vector128.Shuffle(folded, Vector128.Create((ushort)2, 3, 0, 1, 2, 3, 0, 1)));
            minimum = Vector128.Min(folded, Vector128.Shuffle(folded, Vector128.Create((ushort)1, 0, 1, 0, 1, 0, 1, 0))).ToScalar();
        }
        uint index = uint.TrailingZeroCount(Vector128.ExtractMostSignificantBits(Vector128.Equals(value, Vector128.Create(minimum))));
        return Vector128.Create(minimum, (ushort)index, 0, 0, 0, 0, 0, 0);
    }

    [LlvmIntrinsic("llvm.x86.sse41.packusdw"), MethodImpl(Inline)]
    public static Vector128<ushort> Packusdw(Vector128<int> left, Vector128<int> right) =>
        Sse41.IsSupported ? Sse41.PackUnsignedSaturate(left, right)
        : AdvSimd.IsSupported ? AdvSimd.ExtractNarrowingSaturateUnsignedUpper(AdvSimd.ExtractNarrowingSaturateUnsignedLower(left), right)
        : Vector128.NarrowWithSaturation(Vector128.Max(left, Vector128<int>.Zero).AsUInt32(), Vector128.Max(right, Vector128<int>.Zero).AsUInt32());

    /// <summary>insertps: lane control[7:6] of the second operand into lane control[5:4] of the first, then the lanes of control[3:0] cleared.</summary>
    [LlvmIntrinsic("llvm.x86.sse41.insertps"), MethodImpl(Inline)]
    public static Vector128<float> Insertps(Vector128<float> value, Vector128<float> data, int control)
    {
        if (Sse41.IsSupported) return Sse41.Insert(value, data, (byte)control);
        Vector128<float> result = value.WithElement((control >> 4) & 3, data.GetElement((control >> 6) & 3));
        return result & ~Lanes(control).AsSingle();
    }

    /// <summary>All ones in lane i (of four 32-bit lanes) where bit i of <paramref name="bits"/> is set.</summary>
    [MethodImpl(Inline)]
    private static Vector128<int> Lanes(int bits)
    {
        Vector128<int> selected = Vector128.Create(1, 2, 4, 8);
        return Vector128.Equals(Vector128.Create(bits) & selected, selected);
    }

    [LlvmIntrinsic("llvm.x86.sse41.pblendvb"), MethodImpl(Inline)]
    public static Vector128<byte> Pblendvb(Vector128<byte> left, Vector128<byte> right, Vector128<byte> mask) =>
        Sse41.IsSupported ? Sse41.BlendVariable(left, right, mask)
        : Vector128.ConditionalSelect(Vector128.LessThan(mask.AsSByte(), Vector128<sbyte>.Zero).AsByte(), right, left);

    [LlvmIntrinsic("llvm.x86.sse41.blendvps"), MethodImpl(Inline)]
    public static Vector128<float> Blendvps(Vector128<float> left, Vector128<float> right, Vector128<float> mask) =>
        Sse41.IsSupported ? Sse41.BlendVariable(left, right, mask)
        : Vector128.ConditionalSelect(Vector128.LessThan(mask.AsInt32(), Vector128<int>.Zero).AsSingle(), right, left);

    [LlvmIntrinsic("llvm.x86.sse41.blendvpd"), MethodImpl(Inline)]
    public static Vector128<double> Blendvpd(Vector128<double> left, Vector128<double> right, Vector128<double> mask) =>
        Sse41.IsSupported ? Sse41.BlendVariable(left, right, mask)
        : Vector128.ConditionalSelect(Vector128.LessThan(mask.AsInt64(), Vector128<long>.Zero).AsDouble(), right, left);

    /// <summary>dpps: the products of the lanes control[7:4] selects (+0 for the others), lanes 0 and 1 added, lanes 2
    /// and 3 added, then the two sums; the result in the lanes control[3:0] selects, +0 elsewhere.</summary>
    [LlvmIntrinsic("llvm.x86.sse41.dpps"), MethodImpl(Inline)]
    public static Vector128<float> Dpps(Vector128<float> left, Vector128<float> right, int control)
    {
        if (Sse41.IsSupported) return Sse41.DotProduct(left, right, (byte)control);
        Vector128<float> products = (left * right) & Lanes(control >> 4).AsSingle();
        float sum = (products.ToScalar() + products.GetElement(1)) + (products.GetElement(2) + products.GetElement(3));
        return Vector128.Create(sum) & Lanes(control).AsSingle();
    }

    [LlvmIntrinsic("llvm.x86.sse41.dppd"), MethodImpl(Inline)]
    public static Vector128<double> Dppd(Vector128<double> left, Vector128<double> right, int control)
    {
        if (Sse41.IsSupported) return Sse41.DotProduct(left, right, (byte)control);
        Vector128<double> products = (left * right) & Vector128.Equals(Vector128.Create((long)(control >> 4)) & Vector128.Create(1L, 2), Vector128.Create(1L, 2)).AsDouble();
        return Vector128.Create(products.ToScalar() + products.GetElement(1)) & Vector128.Equals(Vector128.Create((long)control) & Vector128.Create(1L, 2), Vector128.Create(1L, 2)).AsDouble();
    }

    /// <summary>mpsadbw: eight sums of absolute differences between four bytes of the second operand at control[1:0]
    /// times four and the eleven bytes of the first at control[2] times four, at each of eight byte offsets.</summary>
    [LlvmIntrinsic("llvm.x86.sse41.mpsadbw"), MethodImpl(Inline)]
    public static Vector128<ushort> Mpsadbw(Vector128<byte> left, Vector128<byte> right, int control) =>
        Sse41.IsSupported ? Sse41.MultipleSumAbsoluteDifferences(left, right, (byte)control) : MultipleSumAbsoluteDifferences(left, right, control);

    private static Vector128<ushort> MultipleSumAbsoluteDifferences(Vector128<byte> left, Vector128<byte> right, int control)
    {
        int first = (control & 4) != 0 ? 4 : 0, second = (control & 3) * 4;
        Vector128<ushort> result = Vector128<ushort>.Zero;
        for (int offset = 0; offset < 8; offset++)
        {
            int sum = 0;
            for (int index = 0; index < 4; index++)
                sum += Math.Abs(left.GetElement(first + offset + index) - right.GetElement(second + index));
            result = result.WithElement(offset, (ushort)sum);
        }
        return result;
    }

    [LlvmIntrinsic("llvm.x86.sse41.ptestz"), MethodImpl(Inline)]
    public static int Ptestz(Vector128<long> left, Vector128<long> right) =>
        (Sse41.IsSupported ? Sse41.TestZ(left, right) : (left & right) == Vector128<long>.Zero) ? 1 : 0;

    [LlvmIntrinsic("llvm.x86.sse41.ptestc"), MethodImpl(Inline)]
    public static int Ptestc(Vector128<long> left, Vector128<long> right) =>
        (Sse41.IsSupported ? Sse41.TestC(left, right) : (~left & right) == Vector128<long>.Zero) ? 1 : 0;

    [LlvmIntrinsic("llvm.x86.sse41.ptestnzc"), MethodImpl(Inline)]
    public static int Ptestnzc(Vector128<long> left, Vector128<long> right) =>
        (Sse41.IsSupported ? Sse41.TestNotZAndNotC(left, right) : (left & right) != Vector128<long>.Zero && (~left & right) != Vector128<long>.Zero) ? 1 : 0;

    // ---- SSE4.2 ----

    // crc32 accumulates CRC-32C without the initial and final inversions, which is BitOperations.Crc32C (the SSE4.2 or
    // ARMv8 instruction where the host has one, a table elsewhere). The 64-bit form reads the low 32 bits of the CRC.

    [LlvmIntrinsic("llvm.x86.sse42.crc32.32.8"), MethodImpl(Inline)]
    public static int Crc32b(int crc, int data) => (int)BitOperations.Crc32C((uint)crc, (byte)data);

    [LlvmIntrinsic("llvm.x86.sse42.crc32.32.16"), MethodImpl(Inline)]
    public static int Crc32w(int crc, int data) => (int)BitOperations.Crc32C((uint)crc, (ushort)data);

    [LlvmIntrinsic("llvm.x86.sse42.crc32.32.32"), MethodImpl(Inline)]
    public static int Crc32d(int crc, int data) => (int)BitOperations.Crc32C((uint)crc, (uint)data);

    [LlvmIntrinsic("llvm.x86.sse42.crc32.64.64"), MethodImpl(Inline)]
    public static long Crc32q(long crc, long data) => BitOperations.Crc32C((uint)crc, (ulong)data);

    // ---- PCLMULQDQ ----

    /// <summary>pclmulqdq: the carry-less product of the 64-bit lane control[0] of the first operand and control[4] of
    /// the second, which CRC code pairs with SSE4.2 (it is in no x86-64 level, so -mpclmul or a target attribute selects it).</summary>
    [LlvmIntrinsic("llvm.x86.pclmulqdq"), MethodImpl(Inline)]
    public static Vector128<long> Clmul(Vector128<long> left, Vector128<long> right, int control)
    {
        if (System.Runtime.Intrinsics.X86.Pclmulqdq.IsSupported) return System.Runtime.Intrinsics.X86.Pclmulqdq.CarrylessMultiply(left, right, (byte)control);
        Vector64<long> first = Vector64.Create(left.GetElement(control & 1)), second = Vector64.Create(right.GetElement((control >> 4) & 1));
        if (System.Runtime.Intrinsics.Arm.Aes.IsSupported) return System.Runtime.Intrinsics.Arm.Aes.PolynomialMultiplyWideningLower(first, second);
        return CarrylessMultiply((ulong)first.ToScalar(), (ulong)second.ToScalar());
    }

    private static Vector128<long> CarrylessMultiply(ulong left, ulong right)
    {
        ulong low = 0, high = 0;
        for (int bit = 0; bit < 64; bit++)
        {
            if ((right >> bit & 1) == 0) continue;
            low ^= left << bit;
            if (bit != 0) high ^= left >> (64 - bit);
        }
        return Vector128.Create((long)low, (long)high);
    }

    // pcmpistri, pcmpestri and their siblings have no .NET instruction, so each runs the instruction's pseudo-code (Intel
    // SDM volume 2, section 4.1): the elements of the second operand compared with those of the first by the control's
    // aggregation, the polarity applied, then the index, the mask or one of the flags. The implicit-length forms end each
    // operand at its first zero element, the explicit-length forms at the absolute value of a length register, saturated.

    [LlvmIntrinsic("llvm.x86.sse42.pcmpistri128"), MethodImpl(Inline)]
    public static int Pcmpistri(Vector128<byte> left, Vector128<byte> right, int control) =>
        StringIndex(StringCompare(left, Length(left, control), right, Length(right, control), control), control);

    [LlvmIntrinsic("llvm.x86.sse42.pcmpestri128"), MethodImpl(Inline)]
    public static int Pcmpestri(Vector128<byte> left, int leftLength, Vector128<byte> right, int rightLength, int control) =>
        StringIndex(StringCompare(left, Length(leftLength, control), right, Length(rightLength, control), control), control);

    [LlvmIntrinsic("llvm.x86.sse42.pcmpistrm128"), MethodImpl(Inline)]
    public static Vector128<byte> Pcmpistrm(Vector128<byte> left, Vector128<byte> right, int control) =>
        StringMask(StringCompare(left, Length(left, control), right, Length(right, control), control), control);

    [LlvmIntrinsic("llvm.x86.sse42.pcmpestrm128"), MethodImpl(Inline)]
    public static Vector128<byte> Pcmpestrm(Vector128<byte> left, int leftLength, Vector128<byte> right, int rightLength, int control) =>
        StringMask(StringCompare(left, Length(leftLength, control), right, Length(rightLength, control), control), control);

    /// <summary>The "above" condition: no element of the result set and the second operand without an end (CF and ZF clear).</summary>
    [LlvmIntrinsic("llvm.x86.sse42.pcmpistria128"), MethodImpl(Inline)]
    public static int Pcmpistria(Vector128<byte> left, Vector128<byte> right, int control) =>
        Length(right, control) == Elements(control) && StringCompare(left, Length(left, control), right, Elements(control), control) == 0 ? 1 : 0;

    [LlvmIntrinsic("llvm.x86.sse42.pcmpestria128"), MethodImpl(Inline)]
    public static int Pcmpestria(Vector128<byte> left, int leftLength, Vector128<byte> right, int rightLength, int control) =>
        Length(rightLength, control) == Elements(control) && StringCompare(left, Length(leftLength, control), right, Elements(control), control) == 0 ? 1 : 0;

    /// <summary>CF: some element of the result set.</summary>
    [LlvmIntrinsic("llvm.x86.sse42.pcmpistric128"), MethodImpl(Inline)]
    public static int Pcmpistric(Vector128<byte> left, Vector128<byte> right, int control) =>
        StringCompare(left, Length(left, control), right, Length(right, control), control) != 0 ? 1 : 0;

    [LlvmIntrinsic("llvm.x86.sse42.pcmpestric128"), MethodImpl(Inline)]
    public static int Pcmpestric(Vector128<byte> left, int leftLength, Vector128<byte> right, int rightLength, int control) =>
        StringCompare(left, Length(leftLength, control), right, Length(rightLength, control), control) != 0 ? 1 : 0;

    /// <summary>OF: element 0 of the result.</summary>
    [LlvmIntrinsic("llvm.x86.sse42.pcmpistrio128"), MethodImpl(Inline)]
    public static int Pcmpistrio(Vector128<byte> left, Vector128<byte> right, int control) =>
        (int)(StringCompare(left, Length(left, control), right, Length(right, control), control) & 1);

    [LlvmIntrinsic("llvm.x86.sse42.pcmpestrio128"), MethodImpl(Inline)]
    public static int Pcmpestrio(Vector128<byte> left, int leftLength, Vector128<byte> right, int rightLength, int control) =>
        (int)(StringCompare(left, Length(leftLength, control), right, Length(rightLength, control), control) & 1);

    /// <summary>SF: the first operand ends before its last element.</summary>
    [LlvmIntrinsic("llvm.x86.sse42.pcmpistris128"), MethodImpl(Inline)]
    public static int Pcmpistris(Vector128<byte> left, Vector128<byte> right, int control) => Length(left, control) < Elements(control) ? 1 : 0;

    [LlvmIntrinsic("llvm.x86.sse42.pcmpestris128"), MethodImpl(Inline)]
    public static int Pcmpestris(Vector128<byte> left, int leftLength, Vector128<byte> right, int rightLength, int control) =>
        Length(leftLength, control) < Elements(control) ? 1 : 0;

    /// <summary>ZF: the second operand ends before its last element.</summary>
    [LlvmIntrinsic("llvm.x86.sse42.pcmpistriz128"), MethodImpl(Inline)]
    public static int Pcmpistriz(Vector128<byte> left, Vector128<byte> right, int control) => Length(right, control) < Elements(control) ? 1 : 0;

    [LlvmIntrinsic("llvm.x86.sse42.pcmpestriz128"), MethodImpl(Inline)]
    public static int Pcmpestriz(Vector128<byte> left, int leftLength, Vector128<byte> right, int rightLength, int control) =>
        Length(rightLength, control) < Elements(control) ? 1 : 0;

    /// <summary>16 byte or 8 word elements, by control bit 0.</summary>
    private static int Elements(int control) => (control & 1) != 0 ? 8 : 16;

    /// <summary>An implicit length: the index of the first zero element, or all of them.</summary>
    [MethodImpl(Inline)]
    private static int Length(Vector128<byte> value, int control)
    {
        uint zeros = (control & 1) != 0
            ? Vector128.ExtractMostSignificantBits(Vector128.Equals(value.AsUInt16(), Vector128<ushort>.Zero))
            : Vector128.ExtractMostSignificantBits(Vector128.Equals(value, Vector128<byte>.Zero));
        return zeros == 0 ? Elements(control) : BitOperations.TrailingZeroCount(zeros);
    }

    /// <summary>An explicit length: the absolute value of the register, saturated at the element count.</summary>
    private static int Length(int length, int control) => length < -Elements(control) || length > Elements(control) ? Elements(control) : Math.Abs(length);

    /// <summary>The comparison's result after the polarity, bit j for element j of the second operand.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static uint StringCompare(Vector128<byte> left, int leftLength, Vector128<byte> right, int rightLength, int control)
    {
        int count = Elements(control);
        int Element(Vector128<byte> value, int index) => (control & 3) switch
        {
            0 => value.GetElement(index),
            1 => value.AsUInt16().GetElement(index),
            2 => value.AsSByte().GetElement(index),
            _ => value.AsInt16().GetElement(index)
        };
        uint result = 0;
        for (int j = 0; j < count; j++)
        {
            bool match = false;
            switch ((control >> 2) & 3)
            {
                case 0:
                    // Equal any: element j of the second operand equals a valid element of the first.
                    for (int i = 0; i < leftLength && j < rightLength && !match; i++)
                        match = Element(right, j) == Element(left, i);
                    break;
                case 1:
                    // Ranges: element j lies in a range of the first operand's valid pairs, lower bound first.
                    for (int i = 0; i + 1 < leftLength && j < rightLength && !match; i += 2)
                        match = Element(right, j) >= Element(left, i) && Element(right, j) <= Element(left, i + 1);
                    break;
                case 2:
                    // Equal each: element j of both, true where both have ended.
                    match = j < leftLength && j < rightLength ? Element(left, j) == Element(right, j) : j >= leftLength && j >= rightLength;
                    break;
                default:
                    // Equal ordered: the first operand occurs at element j of the second; past the last element counts as a match.
                    match = true;
                    for (int k = 0; k < leftLength && j + k < count && match; k++)
                        match = j + k < rightLength && Element(right, j + k) == Element(left, k);
                    break;
            }
            if (match) result |= 1u << j;
        }
        return ((control >> 4) & 3) switch
        {
            1 => result ^ ((1u << count) - 1),
            3 => result ^ ((1u << rightLength) - 1),
            _ => result
        };
    }

    /// <summary>The lowest set element of the result (the highest with control bit 6), or the element count.</summary>
    private static int StringIndex(uint result, int control) =>
        result == 0 ? Elements(control) : (control & 0x40) != 0 ? 31 - BitOperations.LeadingZeroCount(result) : BitOperations.TrailingZeroCount(result);

    /// <summary>The result as bits in the low element (or, with control bit 6, as a mask of whole elements).</summary>
    private static Vector128<byte> StringMask(uint result, int control) =>
        (control & 0x40) == 0 ? Vector128.CreateScalar(result).AsByte()
        : (control & 1) != 0 ? Vectors.Mask16(result).AsByte() : Vectors.Mask8(result).AsByte();
#pragma warning restore CA1857
}
