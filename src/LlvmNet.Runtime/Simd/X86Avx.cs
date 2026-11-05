using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace LlvmNet.Runtime;

/// <summary>
/// The AVX, AVX2, FMA, F16C and BMI target intrinsics of x86-64-v3 (llvm.x86.*, SV-09) that Clang emits for the C
/// intrinsics it does not lower to generic vector IR. A method runs the instruction where the host has it, behind an
/// explicit IsSupported branch; elsewhere it runs the SSE helpers of the same meaning on the two 128-bit halves, which
/// is exact because these instructions work within 128-bit lanes, or, for the operations across lanes (permutes,
/// gathers, masked memory access, tests), an exact composition. .NET's Vector256 is not accelerated without AVX2, so the
/// fallbacks never compute with whole Vector256 values.
/// </summary>
public static unsafe partial class X86
{
    // The immediates are constants once the helper is inlined at the call site, as in the SSE helpers.
#pragma warning disable CA1857
    [MethodImpl(Inline)]
    private static Vector256<T> Join<T>(Vector128<T> lower, Vector128<T> upper) => Vector256.Create(lower, upper);

    // ---- AVX: floating point ----

    [LlvmIntrinsic("llvm.x86.avx.addsub.ps.256"), MethodImpl(Inline)]
    public static Vector256<float> Addsubps256(Vector256<float> left, Vector256<float> right) => Avx.IsSupported ? Avx.AddSubtract(left, right)
        : Join(Addsubps(left.GetLower(), right.GetLower()), Addsubps(left.GetUpper(), right.GetUpper()));

    [LlvmIntrinsic("llvm.x86.avx.addsub.pd.256"), MethodImpl(Inline)]
    public static Vector256<double> Addsubpd256(Vector256<double> left, Vector256<double> right) => Avx.IsSupported ? Avx.AddSubtract(left, right)
        : Join(Addsubpd(left.GetLower(), right.GetLower()), Addsubpd(left.GetUpper(), right.GetUpper()));

    [LlvmIntrinsic("llvm.x86.avx.blendv.ps.256"), MethodImpl(Inline)]
    public static Vector256<float> Blendvps256(Vector256<float> left, Vector256<float> right, Vector256<float> mask) => Avx.IsSupported
        ? Avx.BlendVariable(left, right, mask)
        : Join(Blendvps(left.GetLower(), right.GetLower(), mask.GetLower()), Blendvps(left.GetUpper(), right.GetUpper(), mask.GetUpper()));

    [LlvmIntrinsic("llvm.x86.avx.blendv.pd.256"), MethodImpl(Inline)]
    public static Vector256<double> Blendvpd256(Vector256<double> left, Vector256<double> right, Vector256<double> mask) => Avx.IsSupported
        ? Avx.BlendVariable(left, right, mask)
        : Join(Blendvpd(left.GetLower(), right.GetLower(), mask.GetLower()), Blendvpd(left.GetUpper(), right.GetUpper(), mask.GetUpper()));

    [LlvmIntrinsic("llvm.x86.avx.cvt.pd2.ps.256"), MethodImpl(Inline)]
    public static Vector128<float> Cvtpd2ps256(Vector256<double> value) => Avx.IsSupported ? Avx.ConvertToVector128Single(value)
        : Vector128.Create(Cvtpd2ps(value.GetLower()).GetLower(), Cvtpd2ps(value.GetUpper()).GetLower());

    [LlvmIntrinsic("llvm.x86.avx.cvt.pd2dq.256"), MethodImpl(Inline)]
    public static Vector128<int> Cvtpd2dq256(Vector256<double> value) => Avx.IsSupported ? Avx.ConvertToVector128Int32(value)
        : Vector128.Create(Cvtpd2dq(value.GetLower()).GetLower(), Cvtpd2dq(value.GetUpper()).GetLower());

    [LlvmIntrinsic("llvm.x86.avx.cvtt.pd2dq.256"), MethodImpl(Inline)]
    public static Vector128<int> Cvttpd2dq256(Vector256<double> value) => Avx.IsSupported ? Avx.ConvertToVector128Int32WithTruncation(value)
        : Vector128.Create(Cvttpd2dq(value.GetLower()).GetLower(), Cvttpd2dq(value.GetUpper()).GetLower());

    [LlvmIntrinsic("llvm.x86.avx.cvt.ps2dq.256"), MethodImpl(Inline)]
    public static Vector256<int> Cvtps2dq256(Vector256<float> value) => Avx.IsSupported ? Avx.ConvertToVector256Int32(value)
        : Join(Cvtps2dq(value.GetLower()), Cvtps2dq(value.GetUpper()));

    [LlvmIntrinsic("llvm.x86.avx.cvtt.ps2dq.256"), MethodImpl(Inline)]
    public static Vector256<int> Cvttps2dq256(Vector256<float> value) => Avx.IsSupported ? Avx.ConvertToVector256Int32WithTruncation(value)
        : Join(Cvttps2dq(value.GetLower()), Cvttps2dq(value.GetUpper()));

    [LlvmIntrinsic("llvm.x86.avx.hadd.ps.256"), MethodImpl(Inline)]
    public static Vector256<float> Haddps256(Vector256<float> left, Vector256<float> right) => Avx.IsSupported ? Avx.HorizontalAdd(left, right)
        : Join(Haddps(left.GetLower(), right.GetLower()), Haddps(left.GetUpper(), right.GetUpper()));

    [LlvmIntrinsic("llvm.x86.avx.hadd.pd.256"), MethodImpl(Inline)]
    public static Vector256<double> Haddpd256(Vector256<double> left, Vector256<double> right) => Avx.IsSupported ? Avx.HorizontalAdd(left, right)
        : Join(Haddpd(left.GetLower(), right.GetLower()), Haddpd(left.GetUpper(), right.GetUpper()));

    [LlvmIntrinsic("llvm.x86.avx.hsub.ps.256"), MethodImpl(Inline)]
    public static Vector256<float> Hsubps256(Vector256<float> left, Vector256<float> right) => Avx.IsSupported ? Avx.HorizontalSubtract(left, right)
        : Join(Hsubps(left.GetLower(), right.GetLower()), Hsubps(left.GetUpper(), right.GetUpper()));

    [LlvmIntrinsic("llvm.x86.avx.hsub.pd.256"), MethodImpl(Inline)]
    public static Vector256<double> Hsubpd256(Vector256<double> left, Vector256<double> right) => Avx.IsSupported ? Avx.HorizontalSubtract(left, right)
        : Join(Hsubpd(left.GetLower(), right.GetLower()), Hsubpd(left.GetUpper(), right.GetUpper()));

    [LlvmIntrinsic("llvm.x86.avx.max.ps.256"), MethodImpl(Inline)]
    public static Vector256<float> Maxps256(Vector256<float> left, Vector256<float> right) => Avx.IsSupported ? Avx.Max(left, right)
        : Join(Maxps(left.GetLower(), right.GetLower()), Maxps(left.GetUpper(), right.GetUpper()));

    [LlvmIntrinsic("llvm.x86.avx.max.pd.256"), MethodImpl(Inline)]
    public static Vector256<double> Maxpd256(Vector256<double> left, Vector256<double> right) => Avx.IsSupported ? Avx.Max(left, right)
        : Join(Maxpd(left.GetLower(), right.GetLower()), Maxpd(left.GetUpper(), right.GetUpper()));

    [LlvmIntrinsic("llvm.x86.avx.min.ps.256"), MethodImpl(Inline)]
    public static Vector256<float> Minps256(Vector256<float> left, Vector256<float> right) => Avx.IsSupported ? Avx.Min(left, right)
        : Join(Minps(left.GetLower(), right.GetLower()), Minps(left.GetUpper(), right.GetUpper()));

    [LlvmIntrinsic("llvm.x86.avx.min.pd.256"), MethodImpl(Inline)]
    public static Vector256<double> Minpd256(Vector256<double> left, Vector256<double> right) => Avx.IsSupported ? Avx.Min(left, right)
        : Join(Minpd(left.GetLower(), right.GetLower()), Minpd(left.GetUpper(), right.GetUpper()));

    [LlvmIntrinsic("llvm.x86.avx.rcp.ps.256"), MethodImpl(Inline)]
    public static Vector256<float> Rcpps256(Vector256<float> value) => Avx.IsSupported ? Avx.Reciprocal(value) : Join(Rcpps(value.GetLower()), Rcpps(value.GetUpper()));

    [LlvmIntrinsic("llvm.x86.avx.rsqrt.ps.256"), MethodImpl(Inline)]
    public static Vector256<float> Rsqrtps256(Vector256<float> value) => Avx.IsSupported ? Avx.ReciprocalSqrt(value)
        : Join(Rsqrtps(value.GetLower()), Rsqrtps(value.GetUpper()));

    [LlvmIntrinsic("llvm.x86.avx.round.ps.256"), MethodImpl(Inline)]
    public static Vector256<float> Roundps256(Vector256<float> value, int mode)
    {
        if (Avx.IsSupported)
            return Rounding(mode) switch
            {
                0 => Avx.RoundToNearestInteger(value),
                1 => Avx.RoundToNegativeInfinity(value),
                2 => Avx.RoundToPositiveInfinity(value),
                _ => Avx.RoundToZero(value)
            };
        return Join(Roundps(value.GetLower(), mode), Roundps(value.GetUpper(), mode));
    }

    [LlvmIntrinsic("llvm.x86.avx.round.pd.256"), MethodImpl(Inline)]
    public static Vector256<double> Roundpd256(Vector256<double> value, int mode)
    {
        if (Avx.IsSupported)
            return Rounding(mode) switch
            {
                0 => Avx.RoundToNearestInteger(value),
                1 => Avx.RoundToNegativeInfinity(value),
                2 => Avx.RoundToPositiveInfinity(value),
                _ => Avx.RoundToZero(value)
            };
        return Join(Roundpd(value.GetLower(), mode), Roundpd(value.GetUpper(), mode));
    }

    /// <summary>vdpps: dpps in each 128-bit lane, with the same control.</summary>
    [LlvmIntrinsic("llvm.x86.avx.dp.ps.256"), MethodImpl(Inline)]
    public static Vector256<float> Dpps256(Vector256<float> left, Vector256<float> right, int control) => Avx.IsSupported
        ? Avx.DotProduct(left, right, (byte)control)
        : Join(Dpps(left.GetLower(), right.GetLower(), control), Dpps(left.GetUpper(), right.GetUpper(), control));

    [LlvmIntrinsic("llvm.x86.avx.movmsk.ps.256"), MethodImpl(Inline)]
    public static int Movmskps256(Vector256<float> value) => Avx.IsSupported ? Avx.MoveMask(value)
        : Movmskps(value.GetLower()) | Movmskps(value.GetUpper()) << 4;

    [LlvmIntrinsic("llvm.x86.avx.movmsk.pd.256"), MethodImpl(Inline)]
    public static int Movmskpd256(Vector256<double> value) => Avx.IsSupported ? Avx.MoveMask(value)
        : Movmskpd(value.GetLower()) | Movmskpd(value.GetUpper()) << 2;

    [LlvmIntrinsic("llvm.x86.avx.ldu.dq.256"), MethodImpl(Inline)]
    public static Vector256<byte> Lddqu256(nint address) => Avx.IsSupported ? Avx.LoadDquVector256((byte*)address)
        : Join(Vector128.Load((byte*)address), Vector128.Load((byte*)address + 16));

    // vzeroupper and vzeroall only avoid SSE transition penalties, which .NET's code generation manages itself.

    [LlvmIntrinsic("llvm.x86.avx.vzeroupper"), LlvmIntrinsic("llvm.x86.avx.vzeroall"), MethodImpl(Inline)]
    public static void Vzeroupper() { }

    // vpermilps selects within each 128-bit lane by the low two bits of each control element; vpermilpd by bit 1.

    [LlvmIntrinsic("llvm.x86.avx.vpermilvar.ps"), MethodImpl(Inline)]
    public static Vector128<float> Vpermilvarps(Vector128<float> value, Vector128<int> control) => Avx.IsSupported ? Avx.PermuteVar(value, control)
        : Vector128.Shuffle(value, control & Vector128.Create(3));

    [LlvmIntrinsic("llvm.x86.avx.vpermilvar.ps.256"), MethodImpl(Inline)]
    public static Vector256<float> Vpermilvarps256(Vector256<float> value, Vector256<int> control) => Avx.IsSupported ? Avx.PermuteVar(value, control)
        : Join(Vpermilvarps(value.GetLower(), control.GetLower()), Vpermilvarps(value.GetUpper(), control.GetUpper()));

    [LlvmIntrinsic("llvm.x86.avx.vpermilvar.pd"), MethodImpl(Inline)]
    public static Vector128<double> Vpermilvarpd(Vector128<double> value, Vector128<long> control) => Avx.IsSupported ? Avx.PermuteVar(value, control)
        : Vector128.Shuffle(value, (control >>> 1) & Vector128.Create(1L));

    [LlvmIntrinsic("llvm.x86.avx.vpermilvar.pd.256"), MethodImpl(Inline)]
    public static Vector256<double> Vpermilvarpd256(Vector256<double> value, Vector256<long> control) => Avx.IsSupported ? Avx.PermuteVar(value, control)
        : Join(Vpermilvarpd(value.GetLower(), control.GetLower()), Vpermilvarpd(value.GetUpper(), control.GetUpper()));

    // ptest sets ZF when left AND right is zero and CF when right AND NOT left is zero; vtestps and vtestpd look only at the
    // sign bits of the elements.

    [LlvmIntrinsic("llvm.x86.avx.ptestz.256"), MethodImpl(Inline)]
    public static int Ptestz256(Vector256<long> left, Vector256<long> right) => Avx.IsSupported ? (Avx.TestZ(left, right) ? 1 : 0)
        : Ptestz(left.GetLower(), right.GetLower()) & Ptestz(left.GetUpper(), right.GetUpper());

    [LlvmIntrinsic("llvm.x86.avx.ptestc.256"), MethodImpl(Inline)]
    public static int Ptestc256(Vector256<long> left, Vector256<long> right) => Avx.IsSupported ? (Avx.TestC(left, right) ? 1 : 0)
        : Ptestc(left.GetLower(), right.GetLower()) & Ptestc(left.GetUpper(), right.GetUpper());

    [LlvmIntrinsic("llvm.x86.avx.ptestnzc.256"), MethodImpl(Inline)]
    public static int Ptestnzc256(Vector256<long> left, Vector256<long> right) => Avx.IsSupported ? (Avx.TestNotZAndNotC(left, right) ? 1 : 0)
        : (Ptestz256(left, right) | Ptestc256(left, right)) ^ 1;

    [MethodImpl(Inline)]
    private static uint Signs(Vector128<float> value) => value.ExtractMostSignificantBits();

    [MethodImpl(Inline)]
    private static uint Signs(Vector128<double> value) => value.ExtractMostSignificantBits();

    [LlvmIntrinsic("llvm.x86.avx.vtestz.ps"), MethodImpl(Inline)]
    public static int Vtestzps(Vector128<float> left, Vector128<float> right) => Avx.IsSupported ? (Avx.TestZ(left, right) ? 1 : 0) : Signs(left & right) == 0 ? 1 : 0;

    [LlvmIntrinsic("llvm.x86.avx.vtestc.ps"), MethodImpl(Inline)]
    public static int Vtestcps(Vector128<float> left, Vector128<float> right) => Avx.IsSupported ? (Avx.TestC(left, right) ? 1 : 0) : Signs(right & ~left) == 0 ? 1 : 0;

    [LlvmIntrinsic("llvm.x86.avx.vtestnzc.ps"), MethodImpl(Inline)]
    public static int Vtestnzcps(Vector128<float> left, Vector128<float> right) => (Vtestzps(left, right) | Vtestcps(left, right)) ^ 1;

    [LlvmIntrinsic("llvm.x86.avx.vtestz.pd"), MethodImpl(Inline)]
    public static int Vtestzpd(Vector128<double> left, Vector128<double> right) => Avx.IsSupported ? (Avx.TestZ(left, right) ? 1 : 0) : Signs(left & right) == 0 ? 1 : 0;

    [LlvmIntrinsic("llvm.x86.avx.vtestc.pd"), MethodImpl(Inline)]
    public static int Vtestcpd(Vector128<double> left, Vector128<double> right) => Avx.IsSupported ? (Avx.TestC(left, right) ? 1 : 0) : Signs(right & ~left) == 0 ? 1 : 0;

    [LlvmIntrinsic("llvm.x86.avx.vtestnzc.pd"), MethodImpl(Inline)]
    public static int Vtestnzcpd(Vector128<double> left, Vector128<double> right) => (Vtestzpd(left, right) | Vtestcpd(left, right)) ^ 1;

    [LlvmIntrinsic("llvm.x86.avx.vtestz.ps.256"), MethodImpl(Inline)]
    public static int Vtestzps256(Vector256<float> left, Vector256<float> right) => Avx.IsSupported ? (Avx.TestZ(left, right) ? 1 : 0)
        : Vtestzps(left.GetLower(), right.GetLower()) & Vtestzps(left.GetUpper(), right.GetUpper());

    [LlvmIntrinsic("llvm.x86.avx.vtestc.ps.256"), MethodImpl(Inline)]
    public static int Vtestcps256(Vector256<float> left, Vector256<float> right) => Avx.IsSupported ? (Avx.TestC(left, right) ? 1 : 0)
        : Vtestcps(left.GetLower(), right.GetLower()) & Vtestcps(left.GetUpper(), right.GetUpper());

    [LlvmIntrinsic("llvm.x86.avx.vtestnzc.ps.256"), MethodImpl(Inline)]
    public static int Vtestnzcps256(Vector256<float> left, Vector256<float> right) => (Vtestzps256(left, right) | Vtestcps256(left, right)) ^ 1;

    [LlvmIntrinsic("llvm.x86.avx.vtestz.pd.256"), MethodImpl(Inline)]
    public static int Vtestzpd256(Vector256<double> left, Vector256<double> right) => Avx.IsSupported ? (Avx.TestZ(left, right) ? 1 : 0)
        : Vtestzpd(left.GetLower(), right.GetLower()) & Vtestzpd(left.GetUpper(), right.GetUpper());

    [LlvmIntrinsic("llvm.x86.avx.vtestc.pd.256"), MethodImpl(Inline)]
    public static int Vtestcpd256(Vector256<double> left, Vector256<double> right) => Avx.IsSupported ? (Avx.TestC(left, right) ? 1 : 0)
        : Vtestcpd(left.GetLower(), right.GetLower()) & Vtestcpd(left.GetUpper(), right.GetUpper());

    [LlvmIntrinsic("llvm.x86.avx.vtestnzc.pd.256"), MethodImpl(Inline)]
    public static int Vtestnzcpd256(Vector256<double> left, Vector256<double> right) => (Vtestzpd256(left, right) | Vtestcpd256(left, right)) ^ 1;

    // ---- AVX and AVX2: masked loads and stores ----

    // An element is accessed when its mask element's sign bit is set; masked-out elements read as zero and their memory is
    // not touched, so a masked access at the end of a buffer cannot fault.

    private static Vector128<int> MaskedLoad(int* address, Vector128<int> mask)
    {
        Vector128<int> result = Vector128<int>.Zero;
        for (int lane = 0; lane < 4; lane++)
            if (mask.GetElement(lane) < 0) result = result.WithElement(lane, Unsafe.ReadUnaligned<int>(address + lane));
        return result;
    }

    private static Vector128<long> MaskedLoad(long* address, Vector128<long> mask)
    {
        Vector128<long> result = Vector128<long>.Zero;
        for (int lane = 0; lane < 2; lane++)
            if (mask.GetElement(lane) < 0) result = result.WithElement(lane, Unsafe.ReadUnaligned<long>(address + lane));
        return result;
    }

    private static void MaskedStore(int* address, Vector128<int> mask, Vector128<int> value)
    {
        for (int lane = 0; lane < 4; lane++)
            if (mask.GetElement(lane) < 0) Unsafe.WriteUnaligned(address + lane, value.GetElement(lane));
    }

    private static void MaskedStore(long* address, Vector128<long> mask, Vector128<long> value)
    {
        for (int lane = 0; lane < 2; lane++)
            if (mask.GetElement(lane) < 0) Unsafe.WriteUnaligned(address + lane, value.GetElement(lane));
    }

    [LlvmIntrinsic("llvm.x86.avx.maskload.ps"), MethodImpl(Inline)]
    public static Vector128<float> Maskloadps(nint address, Vector128<int> mask) => Avx.IsSupported ? Avx.MaskLoad((float*)address, mask.AsSingle())
        : MaskedLoad((int*)address, mask).AsSingle();

    [LlvmIntrinsic("llvm.x86.avx.maskload.ps.256"), MethodImpl(Inline)]
    public static Vector256<float> Maskloadps256(nint address, Vector256<int> mask) => Avx.IsSupported ? Avx.MaskLoad((float*)address, mask.AsSingle())
        : Join(MaskedLoad((int*)address, mask.GetLower()), MaskedLoad((int*)address + 4, mask.GetUpper())).AsSingle();

    [LlvmIntrinsic("llvm.x86.avx.maskload.pd"), MethodImpl(Inline)]
    public static Vector128<double> Maskloadpd(nint address, Vector128<long> mask) => Avx.IsSupported ? Avx.MaskLoad((double*)address, mask.AsDouble())
        : MaskedLoad((long*)address, mask).AsDouble();

    [LlvmIntrinsic("llvm.x86.avx.maskload.pd.256"), MethodImpl(Inline)]
    public static Vector256<double> Maskloadpd256(nint address, Vector256<long> mask) => Avx.IsSupported ? Avx.MaskLoad((double*)address, mask.AsDouble())
        : Join(MaskedLoad((long*)address, mask.GetLower()), MaskedLoad((long*)address + 2, mask.GetUpper())).AsDouble();

    [LlvmIntrinsic("llvm.x86.avx2.maskload.d"), MethodImpl(Inline)]
    public static Vector128<int> Maskloadd(nint address, Vector128<int> mask) => Avx2.IsSupported ? Avx2.MaskLoad((int*)address, mask) : MaskedLoad((int*)address, mask);

    [LlvmIntrinsic("llvm.x86.avx2.maskload.d.256"), MethodImpl(Inline)]
    public static Vector256<int> Maskloadd256(nint address, Vector256<int> mask) => Avx2.IsSupported ? Avx2.MaskLoad((int*)address, mask)
        : Join(MaskedLoad((int*)address, mask.GetLower()), MaskedLoad((int*)address + 4, mask.GetUpper()));

    [LlvmIntrinsic("llvm.x86.avx2.maskload.q"), MethodImpl(Inline)]
    public static Vector128<long> Maskloadq(nint address, Vector128<long> mask) => Avx2.IsSupported ? Avx2.MaskLoad((long*)address, mask) : MaskedLoad((long*)address, mask);

    [LlvmIntrinsic("llvm.x86.avx2.maskload.q.256"), MethodImpl(Inline)]
    public static Vector256<long> Maskloadq256(nint address, Vector256<long> mask) => Avx2.IsSupported ? Avx2.MaskLoad((long*)address, mask)
        : Join(MaskedLoad((long*)address, mask.GetLower()), MaskedLoad((long*)address + 2, mask.GetUpper()));

    [LlvmIntrinsic("llvm.x86.avx.maskstore.ps"), MethodImpl(Inline)]
    public static void Maskstoreps(nint address, Vector128<int> mask, Vector128<float> value)
    {
        if (Avx.IsSupported) Avx.MaskStore((float*)address, mask.AsSingle(), value);
        else MaskedStore((int*)address, mask, value.AsInt32());
    }

    [LlvmIntrinsic("llvm.x86.avx.maskstore.ps.256"), MethodImpl(Inline)]
    public static void Maskstoreps256(nint address, Vector256<int> mask, Vector256<float> value)
    {
        if (Avx.IsSupported) Avx.MaskStore((float*)address, mask.AsSingle(), value);
        else
        {
            MaskedStore((int*)address, mask.GetLower(), value.GetLower().AsInt32());
            MaskedStore((int*)address + 4, mask.GetUpper(), value.GetUpper().AsInt32());
        }
    }

    [LlvmIntrinsic("llvm.x86.avx.maskstore.pd"), MethodImpl(Inline)]
    public static void Maskstorepd(nint address, Vector128<long> mask, Vector128<double> value)
    {
        if (Avx.IsSupported) Avx.MaskStore((double*)address, mask.AsDouble(), value);
        else MaskedStore((long*)address, mask, value.AsInt64());
    }

    [LlvmIntrinsic("llvm.x86.avx.maskstore.pd.256"), MethodImpl(Inline)]
    public static void Maskstorepd256(nint address, Vector256<long> mask, Vector256<double> value)
    {
        if (Avx.IsSupported) Avx.MaskStore((double*)address, mask.AsDouble(), value);
        else
        {
            MaskedStore((long*)address, mask.GetLower(), value.GetLower().AsInt64());
            MaskedStore((long*)address + 2, mask.GetUpper(), value.GetUpper().AsInt64());
        }
    }

    [LlvmIntrinsic("llvm.x86.avx2.maskstore.d"), MethodImpl(Inline)]
    public static void Maskstored(nint address, Vector128<int> mask, Vector128<int> value)
    {
        if (Avx2.IsSupported) Avx2.MaskStore((int*)address, mask, value);
        else MaskedStore((int*)address, mask, value);
    }

    [LlvmIntrinsic("llvm.x86.avx2.maskstore.d.256"), MethodImpl(Inline)]
    public static void Maskstored256(nint address, Vector256<int> mask, Vector256<int> value)
    {
        if (Avx2.IsSupported) Avx2.MaskStore((int*)address, mask, value);
        else
        {
            MaskedStore((int*)address, mask.GetLower(), value.GetLower());
            MaskedStore((int*)address + 4, mask.GetUpper(), value.GetUpper());
        }
    }

    [LlvmIntrinsic("llvm.x86.avx2.maskstore.q"), MethodImpl(Inline)]
    public static void Maskstoreq(nint address, Vector128<long> mask, Vector128<long> value)
    {
        if (Avx2.IsSupported) Avx2.MaskStore((long*)address, mask, value);
        else MaskedStore((long*)address, mask, value);
    }

    [LlvmIntrinsic("llvm.x86.avx2.maskstore.q.256"), MethodImpl(Inline)]
    public static void Maskstoreq256(nint address, Vector256<long> mask, Vector256<long> value)
    {
        if (Avx2.IsSupported) Avx2.MaskStore((long*)address, mask, value);
        else
        {
            MaskedStore((long*)address, mask.GetLower(), value.GetLower());
            MaskedStore((long*)address + 2, mask.GetUpper(), value.GetUpper());
        }
    }

    // ---- AVX2: integer operations, each within its 128-bit lanes ----

    [LlvmIntrinsic("llvm.x86.avx2.packssdw"), MethodImpl(Inline)]
    public static Vector256<short> Packssdw256(Vector256<int> left, Vector256<int> right) => Avx2.IsSupported ? Avx2.PackSignedSaturate(left, right)
        : Join(Packssdw(left.GetLower(), right.GetLower()), Packssdw(left.GetUpper(), right.GetUpper()));

    [LlvmIntrinsic("llvm.x86.avx2.packsswb"), MethodImpl(Inline)]
    public static Vector256<sbyte> Packsswb256(Vector256<short> left, Vector256<short> right) => Avx2.IsSupported ? Avx2.PackSignedSaturate(left, right)
        : Join(Packsswb(left.GetLower(), right.GetLower()), Packsswb(left.GetUpper(), right.GetUpper()));

    [LlvmIntrinsic("llvm.x86.avx2.packusdw"), MethodImpl(Inline)]
    public static Vector256<ushort> Packusdw256(Vector256<int> left, Vector256<int> right) => Avx2.IsSupported ? Avx2.PackUnsignedSaturate(left, right)
        : Join(Packusdw(left.GetLower(), right.GetLower()), Packusdw(left.GetUpper(), right.GetUpper()));

    [LlvmIntrinsic("llvm.x86.avx2.packuswb"), MethodImpl(Inline)]
    public static Vector256<byte> Packuswb256(Vector256<short> left, Vector256<short> right) => Avx2.IsSupported ? Avx2.PackUnsignedSaturate(left, right)
        : Join(Packuswb(left.GetLower(), right.GetLower()), Packuswb(left.GetUpper(), right.GetUpper()));

    [LlvmIntrinsic("llvm.x86.avx2.pavg.b"), MethodImpl(Inline)]
    public static Vector256<byte> Pavgb256(Vector256<byte> left, Vector256<byte> right) => Avx2.IsSupported ? Avx2.Average(left, right)
        : Join(Pavgb(left.GetLower(), right.GetLower()), Pavgb(left.GetUpper(), right.GetUpper()));

    [LlvmIntrinsic("llvm.x86.avx2.pavg.w"), MethodImpl(Inline)]
    public static Vector256<ushort> Pavgw256(Vector256<ushort> left, Vector256<ushort> right) => Avx2.IsSupported ? Avx2.Average(left, right)
        : Join(Pavgw(left.GetLower(), right.GetLower()), Pavgw(left.GetUpper(), right.GetUpper()));

    [LlvmIntrinsic("llvm.x86.avx2.pblendvb"), MethodImpl(Inline)]
    public static Vector256<byte> Pblendvb256(Vector256<byte> left, Vector256<byte> right, Vector256<byte> mask) => Avx2.IsSupported
        ? Avx2.BlendVariable(left, right, mask)
        : Join(Pblendvb(left.GetLower(), right.GetLower(), mask.GetLower()), Pblendvb(left.GetUpper(), right.GetUpper(), mask.GetUpper()));

    [LlvmIntrinsic("llvm.x86.avx2.phadd.w"), MethodImpl(Inline)]
    public static Vector256<short> Phaddw256(Vector256<short> left, Vector256<short> right) => Avx2.IsSupported ? Avx2.HorizontalAdd(left, right)
        : Join(Phaddw(left.GetLower(), right.GetLower()), Phaddw(left.GetUpper(), right.GetUpper()));

    [LlvmIntrinsic("llvm.x86.avx2.phadd.d"), MethodImpl(Inline)]
    public static Vector256<int> Phaddd256(Vector256<int> left, Vector256<int> right) => Avx2.IsSupported ? Avx2.HorizontalAdd(left, right)
        : Join(Phaddd(left.GetLower(), right.GetLower()), Phaddd(left.GetUpper(), right.GetUpper()));

    [LlvmIntrinsic("llvm.x86.avx2.phadd.sw"), MethodImpl(Inline)]
    public static Vector256<short> Phaddsw256(Vector256<short> left, Vector256<short> right) => Avx2.IsSupported ? Avx2.HorizontalAddSaturate(left, right)
        : Join(Phaddsw(left.GetLower(), right.GetLower()), Phaddsw(left.GetUpper(), right.GetUpper()));

    [LlvmIntrinsic("llvm.x86.avx2.phsub.w"), MethodImpl(Inline)]
    public static Vector256<short> Phsubw256(Vector256<short> left, Vector256<short> right) => Avx2.IsSupported ? Avx2.HorizontalSubtract(left, right)
        : Join(Phsubw(left.GetLower(), right.GetLower()), Phsubw(left.GetUpper(), right.GetUpper()));

    [LlvmIntrinsic("llvm.x86.avx2.phsub.d"), MethodImpl(Inline)]
    public static Vector256<int> Phsubd256(Vector256<int> left, Vector256<int> right) => Avx2.IsSupported ? Avx2.HorizontalSubtract(left, right)
        : Join(Phsubd(left.GetLower(), right.GetLower()), Phsubd(left.GetUpper(), right.GetUpper()));

    [LlvmIntrinsic("llvm.x86.avx2.phsub.sw"), MethodImpl(Inline)]
    public static Vector256<short> Phsubsw256(Vector256<short> left, Vector256<short> right) => Avx2.IsSupported ? Avx2.HorizontalSubtractSaturate(left, right)
        : Join(Phsubsw(left.GetLower(), right.GetLower()), Phsubsw(left.GetUpper(), right.GetUpper()));

    [LlvmIntrinsic("llvm.x86.avx2.pmadd.ub.sw"), MethodImpl(Inline)]
    public static Vector256<short> Pmaddubsw256(Vector256<byte> left, Vector256<sbyte> right) => Avx2.IsSupported ? Avx2.MultiplyAddAdjacent(left, right)
        : Join(Pmaddubsw(left.GetLower(), right.GetLower()), Pmaddubsw(left.GetUpper(), right.GetUpper()));

    [LlvmIntrinsic("llvm.x86.avx2.pmadd.wd"), MethodImpl(Inline)]
    public static Vector256<int> Pmaddwd256(Vector256<short> left, Vector256<short> right) => Avx2.IsSupported ? Avx2.MultiplyAddAdjacent(left, right)
        : Join(Pmaddwd(left.GetLower(), right.GetLower()), Pmaddwd(left.GetUpper(), right.GetUpper()));

    [LlvmIntrinsic("llvm.x86.avx2.pmul.hr.sw"), MethodImpl(Inline)]
    public static Vector256<short> Pmulhrsw256(Vector256<short> left, Vector256<short> right) => Avx2.IsSupported ? Avx2.MultiplyHighRoundScale(left, right)
        : Join(Pmulhrsw(left.GetLower(), right.GetLower()), Pmulhrsw(left.GetUpper(), right.GetUpper()));

    [LlvmIntrinsic("llvm.x86.avx2.pmulh.w"), MethodImpl(Inline)]
    public static Vector256<short> Pmulhw256(Vector256<short> left, Vector256<short> right) => Avx2.IsSupported ? Avx2.MultiplyHigh(left, right)
        : Join(Pmulhw(left.GetLower(), right.GetLower()), Pmulhw(left.GetUpper(), right.GetUpper()));

    [LlvmIntrinsic("llvm.x86.avx2.pmulhu.w"), MethodImpl(Inline)]
    public static Vector256<ushort> Pmulhuw256(Vector256<ushort> left, Vector256<ushort> right) => Avx2.IsSupported ? Avx2.MultiplyHigh(left, right)
        : Join(Pmulhuw(left.GetLower(), right.GetLower()), Pmulhuw(left.GetUpper(), right.GetUpper()));

    [LlvmIntrinsic("llvm.x86.avx2.psad.bw"), MethodImpl(Inline)]
    public static Vector256<ulong> Psadbw256(Vector256<byte> left, Vector256<byte> right) => Avx2.IsSupported ? Avx2.SumAbsoluteDifferences(left, right).AsUInt64()
        : Join(Psadbw(left.GetLower(), right.GetLower()), Psadbw(left.GetUpper(), right.GetUpper()));

    [LlvmIntrinsic("llvm.x86.avx2.pmovmskb"), MethodImpl(Inline)]
    public static int Pmovmskb256(Vector256<byte> value) => Avx2.IsSupported ? Avx2.MoveMask(value)
        : Pmovmskb(value.GetLower()) | Pmovmskb(value.GetUpper()) << 16;

    [LlvmIntrinsic("llvm.x86.avx2.pshuf.b"), MethodImpl(Inline)]
    public static Vector256<byte> Pshufb256(Vector256<byte> value, Vector256<byte> indices) => Avx2.IsSupported ? Avx2.Shuffle(value, indices)
        : Join(Pshufb(value.GetLower(), indices.GetLower()), Pshufb(value.GetUpper(), indices.GetUpper()));

    [LlvmIntrinsic("llvm.x86.avx2.psign.b"), MethodImpl(Inline)]
    public static Vector256<sbyte> Psignb256(Vector256<sbyte> value, Vector256<sbyte> sign) => Avx2.IsSupported ? Avx2.Sign(value, sign)
        : Join(Psignb(value.GetLower(), sign.GetLower()), Psignb(value.GetUpper(), sign.GetUpper()));

    [LlvmIntrinsic("llvm.x86.avx2.psign.w"), MethodImpl(Inline)]
    public static Vector256<short> Psignw256(Vector256<short> value, Vector256<short> sign) => Avx2.IsSupported ? Avx2.Sign(value, sign)
        : Join(Psignw(value.GetLower(), sign.GetLower()), Psignw(value.GetUpper(), sign.GetUpper()));

    [LlvmIntrinsic("llvm.x86.avx2.psign.d"), MethodImpl(Inline)]
    public static Vector256<int> Psignd256(Vector256<int> value, Vector256<int> sign) => Avx2.IsSupported ? Avx2.Sign(value, sign)
        : Join(Psignd(value.GetLower(), sign.GetLower()), Psignd(value.GetUpper(), sign.GetUpper()));

    /// <summary>vmpsadbw: mpsadbw in each 128-bit lane, the low lane with control bits 0 to 2 and the high one with bits 3 to 5.</summary>
    [LlvmIntrinsic("llvm.x86.avx2.mpsadbw"), MethodImpl(Inline)]
    public static Vector256<ushort> Mpsadbw256(Vector256<byte> left, Vector256<byte> right, int control) => Avx2.IsSupported
        ? Avx2.MultipleSumAbsoluteDifferences(left, right, (byte)control)
        : Join(Mpsadbw(left.GetLower(), right.GetLower(), control & 7), Mpsadbw(left.GetUpper(), right.GetUpper(), (control >> 3) & 7));

    // Shifts by the count in the low 64 bits of an xmm register, the same for both lanes, and by an immediate. As for SSE2,
    // a count beyond the element's width gives zero (the sign for psra); .NET's shift operators would take it modulo the width.

    [LlvmIntrinsic("llvm.x86.avx2.psll.w"), MethodImpl(Inline)]
    public static Vector256<short> Psllw256(Vector256<short> value, Vector128<short> count) => Avx2.IsSupported ? Avx2.ShiftLeftLogical(value, count)
        : Join(Psllw(value.GetLower(), count), Psllw(value.GetUpper(), count));

    [LlvmIntrinsic("llvm.x86.avx2.psll.d"), MethodImpl(Inline)]
    public static Vector256<int> Pslld256(Vector256<int> value, Vector128<int> count) => Avx2.IsSupported ? Avx2.ShiftLeftLogical(value, count)
        : Join(Pslld(value.GetLower(), count), Pslld(value.GetUpper(), count));

    [LlvmIntrinsic("llvm.x86.avx2.psll.q"), MethodImpl(Inline)]
    public static Vector256<long> Psllq256(Vector256<long> value, Vector128<long> count) => Avx2.IsSupported ? Avx2.ShiftLeftLogical(value, count)
        : Join(Psllq(value.GetLower(), count), Psllq(value.GetUpper(), count));

    [LlvmIntrinsic("llvm.x86.avx2.psrl.w"), MethodImpl(Inline)]
    public static Vector256<short> Psrlw256(Vector256<short> value, Vector128<short> count) => Avx2.IsSupported ? Avx2.ShiftRightLogical(value, count)
        : Join(Psrlw(value.GetLower(), count), Psrlw(value.GetUpper(), count));

    [LlvmIntrinsic("llvm.x86.avx2.psrl.d"), MethodImpl(Inline)]
    public static Vector256<int> Psrld256(Vector256<int> value, Vector128<int> count) => Avx2.IsSupported ? Avx2.ShiftRightLogical(value, count)
        : Join(Psrld(value.GetLower(), count), Psrld(value.GetUpper(), count));

    [LlvmIntrinsic("llvm.x86.avx2.psrl.q"), MethodImpl(Inline)]
    public static Vector256<long> Psrlq256(Vector256<long> value, Vector128<long> count) => Avx2.IsSupported ? Avx2.ShiftRightLogical(value, count)
        : Join(Psrlq(value.GetLower(), count), Psrlq(value.GetUpper(), count));

    [LlvmIntrinsic("llvm.x86.avx2.psra.w"), MethodImpl(Inline)]
    public static Vector256<short> Psraw256(Vector256<short> value, Vector128<short> count) => Avx2.IsSupported ? Avx2.ShiftRightArithmetic(value, count)
        : Join(Psraw(value.GetLower(), count), Psraw(value.GetUpper(), count));

    [LlvmIntrinsic("llvm.x86.avx2.psra.d"), MethodImpl(Inline)]
    public static Vector256<int> Psrad256(Vector256<int> value, Vector128<int> count) => Avx2.IsSupported ? Avx2.ShiftRightArithmetic(value, count)
        : Join(Psrad(value.GetLower(), count), Psrad(value.GetUpper(), count));

    [LlvmIntrinsic("llvm.x86.avx2.pslli.w"), MethodImpl(Inline)]
    public static Vector256<short> Pslliw256(Vector256<short> value, int count) => Avx2.IsSupported ? (uint)count > 15 ? Vector256<short>.Zero : value << count
        : Join(Pslliw(value.GetLower(), count), Pslliw(value.GetUpper(), count));

    [LlvmIntrinsic("llvm.x86.avx2.pslli.d"), MethodImpl(Inline)]
    public static Vector256<int> Psllid256(Vector256<int> value, int count) => Avx2.IsSupported ? (uint)count > 31 ? Vector256<int>.Zero : value << count
        : Join(Psllid(value.GetLower(), count), Psllid(value.GetUpper(), count));

    [LlvmIntrinsic("llvm.x86.avx2.pslli.q"), MethodImpl(Inline)]
    public static Vector256<long> Pslliq256(Vector256<long> value, int count) => Avx2.IsSupported ? (uint)count > 63 ? Vector256<long>.Zero : value << count
        : Join(Pslliq(value.GetLower(), count), Pslliq(value.GetUpper(), count));

    [LlvmIntrinsic("llvm.x86.avx2.psrli.w"), MethodImpl(Inline)]
    public static Vector256<short> Psrliw256(Vector256<short> value, int count) => Avx2.IsSupported ? (uint)count > 15 ? Vector256<short>.Zero : value >>> count
        : Join(Psrliw(value.GetLower(), count), Psrliw(value.GetUpper(), count));

    [LlvmIntrinsic("llvm.x86.avx2.psrli.d"), MethodImpl(Inline)]
    public static Vector256<int> Psrlid256(Vector256<int> value, int count) => Avx2.IsSupported ? (uint)count > 31 ? Vector256<int>.Zero : value >>> count
        : Join(Psrlid(value.GetLower(), count), Psrlid(value.GetUpper(), count));

    [LlvmIntrinsic("llvm.x86.avx2.psrli.q"), MethodImpl(Inline)]
    public static Vector256<long> Psrliq256(Vector256<long> value, int count) => Avx2.IsSupported ? (uint)count > 63 ? Vector256<long>.Zero : value >>> count
        : Join(Psrliq(value.GetLower(), count), Psrliq(value.GetUpper(), count));

    [LlvmIntrinsic("llvm.x86.avx2.psrai.w"), MethodImpl(Inline)]
    public static Vector256<short> Psraiw256(Vector256<short> value, int count) => Avx2.IsSupported ? value >> (int)Math.Min((uint)count, 15)
        : Join(Psraiw(value.GetLower(), count), Psraiw(value.GetUpper(), count));

    [LlvmIntrinsic("llvm.x86.avx2.psrai.d"), MethodImpl(Inline)]
    public static Vector256<int> Psraid256(Vector256<int> value, int count) => Avx2.IsSupported ? value >> (int)Math.Min((uint)count, 31)
        : Join(Psraid(value.GetLower(), count), Psraid(value.GetUpper(), count));

    // Shifts of each element by its own count, unsigned: a count of the element's width or more gives zero (the sign for
    // psrav). AdvSimd shifts right by a negative count and gives zero (the sign) for one of the width.

    [LlvmIntrinsic("llvm.x86.avx2.psllv.d"), MethodImpl(Inline)]
    public static Vector128<int> Psllvd(Vector128<int> value, Vector128<int> count)
    {
        if (Avx2.IsSupported) return Avx2.ShiftLeftLogicalVariable(value, count.AsUInt32());
        Vector128<int> clamped = Vector128.Min(count.AsUInt32(), Vector128.Create(32u)).AsInt32();
        if (AdvSimd.IsSupported) return AdvSimd.ShiftLogical(value, clamped);
        return Vector128.Create(Shift(value[0], clamped[0]), Shift(value[1], clamped[1]), Shift(value[2], clamped[2]), Shift(value[3], clamped[3]));
        static int Shift(int element, int bits) => bits > 31 ? 0 : element << bits;
    }

    [LlvmIntrinsic("llvm.x86.avx2.psrlv.d"), MethodImpl(Inline)]
    public static Vector128<int> Psrlvd(Vector128<int> value, Vector128<int> count)
    {
        if (Avx2.IsSupported) return Avx2.ShiftRightLogicalVariable(value, count.AsUInt32());
        Vector128<int> clamped = Vector128.Min(count.AsUInt32(), Vector128.Create(32u)).AsInt32();
        if (AdvSimd.IsSupported) return AdvSimd.ShiftLogical(value, -clamped);
        return Vector128.Create(Shift(value[0], clamped[0]), Shift(value[1], clamped[1]), Shift(value[2], clamped[2]), Shift(value[3], clamped[3]));
        static int Shift(int element, int bits) => bits > 31 ? 0 : (int)((uint)element >> bits);
    }

    [LlvmIntrinsic("llvm.x86.avx2.psrav.d"), MethodImpl(Inline)]
    public static Vector128<int> Psravd(Vector128<int> value, Vector128<int> count)
    {
        if (Avx2.IsSupported) return Avx2.ShiftRightArithmeticVariable(value, count.AsUInt32());
        Vector128<int> clamped = Vector128.Min(count.AsUInt32(), Vector128.Create(31u)).AsInt32();
        if (AdvSimd.IsSupported) return AdvSimd.ShiftArithmetic(value, -clamped);
        return Vector128.Create(value[0] >> clamped[0], value[1] >> clamped[1], value[2] >> clamped[2], value[3] >> clamped[3]);
    }

    [LlvmIntrinsic("llvm.x86.avx2.psllv.q"), MethodImpl(Inline)]
    public static Vector128<long> Psllvq(Vector128<long> value, Vector128<long> count)
    {
        if (Avx2.IsSupported) return Avx2.ShiftLeftLogicalVariable(value, count.AsUInt64());
        Vector128<long> clamped = Vector128.Min(count.AsUInt64(), Vector128.Create(64ul)).AsInt64();
        if (AdvSimd.Arm64.IsSupported) return AdvSimd.ShiftLogical(value, clamped);
        return Vector128.Create(Shift(value[0], clamped[0]), Shift(value[1], clamped[1]));
        static long Shift(long element, long bits) => bits > 63 ? 0 : element << (int)bits;
    }

    [LlvmIntrinsic("llvm.x86.avx2.psrlv.q"), MethodImpl(Inline)]
    public static Vector128<long> Psrlvq(Vector128<long> value, Vector128<long> count)
    {
        if (Avx2.IsSupported) return Avx2.ShiftRightLogicalVariable(value, count.AsUInt64());
        Vector128<long> clamped = Vector128.Min(count.AsUInt64(), Vector128.Create(64ul)).AsInt64();
        if (AdvSimd.Arm64.IsSupported) return AdvSimd.ShiftLogical(value, -clamped);
        return Vector128.Create(Shift(value[0], clamped[0]), Shift(value[1], clamped[1]));
        static long Shift(long element, long bits) => bits > 63 ? 0 : (long)((ulong)element >> (int)bits);
    }

    [LlvmIntrinsic("llvm.x86.avx2.psllv.d.256"), MethodImpl(Inline)]
    public static Vector256<int> Psllvd256(Vector256<int> value, Vector256<int> count) => Avx2.IsSupported ? Avx2.ShiftLeftLogicalVariable(value, count.AsUInt32())
        : Join(Psllvd(value.GetLower(), count.GetLower()), Psllvd(value.GetUpper(), count.GetUpper()));

    [LlvmIntrinsic("llvm.x86.avx2.psrlv.d.256"), MethodImpl(Inline)]
    public static Vector256<int> Psrlvd256(Vector256<int> value, Vector256<int> count) => Avx2.IsSupported ? Avx2.ShiftRightLogicalVariable(value, count.AsUInt32())
        : Join(Psrlvd(value.GetLower(), count.GetLower()), Psrlvd(value.GetUpper(), count.GetUpper()));

    [LlvmIntrinsic("llvm.x86.avx2.psrav.d.256"), MethodImpl(Inline)]
    public static Vector256<int> Psravd256(Vector256<int> value, Vector256<int> count) => Avx2.IsSupported ? Avx2.ShiftRightArithmeticVariable(value, count.AsUInt32())
        : Join(Psravd(value.GetLower(), count.GetLower()), Psravd(value.GetUpper(), count.GetUpper()));

    [LlvmIntrinsic("llvm.x86.avx2.psllv.q.256"), MethodImpl(Inline)]
    public static Vector256<long> Psllvq256(Vector256<long> value, Vector256<long> count) => Avx2.IsSupported ? Avx2.ShiftLeftLogicalVariable(value, count.AsUInt64())
        : Join(Psllvq(value.GetLower(), count.GetLower()), Psllvq(value.GetUpper(), count.GetUpper()));

    [LlvmIntrinsic("llvm.x86.avx2.psrlv.q.256"), MethodImpl(Inline)]
    public static Vector256<long> Psrlvq256(Vector256<long> value, Vector256<long> count) => Avx2.IsSupported ? Avx2.ShiftRightLogicalVariable(value, count.AsUInt64())
        : Join(Psrlvq(value.GetLower(), count.GetLower()), Psrlvq(value.GetUpper(), count.GetUpper()));

    // ---- AVX2: across lanes ----

    [LlvmIntrinsic("llvm.x86.avx2.permd"), MethodImpl(Inline)]
    public static Vector256<int> Permd(Vector256<int> value, Vector256<int> control) => Avx2.IsSupported ? Avx2.PermuteVar8x32(value, control)
        : Permute(value, control);

    [LlvmIntrinsic("llvm.x86.avx2.permps"), MethodImpl(Inline)]
    public static Vector256<float> Permps(Vector256<float> value, Vector256<int> control) => Avx2.IsSupported ? Avx2.PermuteVar8x32(value, control)
        : Permute(value.AsInt32(), control).AsSingle();

    /// <summary>vpermd without AVX2: each element from the element its control's low three bits select.</summary>
    private static Vector256<int> Permute(Vector256<int> value, Vector256<int> control)
    {
        Vector128<int> lower = value.GetLower(), upper = value.GetUpper();
        Vector128<int> Select(Vector128<int> indices)
        {
            Vector128<int> index = indices & Vector128.Create(7);
            Vector128<int> fromLower = Vector128.Shuffle(lower, index), fromUpper = Vector128.Shuffle(upper, index - Vector128.Create(4));
            return Vector128.ConditionalSelect(Vector128.LessThan(index, Vector128.Create(4)), fromLower, fromUpper);
        }
        return Join(Select(control.GetLower()), Select(control.GetUpper()));
    }

    // Gathers: an element is loaded from base + index * scale where its mask element's sign bit is set, and taken from the
    // source elsewhere; indices are signed. Elements beyond the result's width of a 64-bit index gather of 32-bit data
    // (gather.q.d and gather.q.ps of 128 bits) are zero.

    [MethodImpl(Inline)]
    private static T Element<T>(nint address, long index, int scale) where T : unmanaged => Unsafe.ReadUnaligned<T>((byte*)address + index * scale);

    [LlvmIntrinsic("llvm.x86.avx2.gather.d.d"), MethodImpl(Inline)]
    public static Vector128<int> Gatherdd(Vector128<int> source, nint address, Vector128<int> index, Vector128<int> mask, int scale)
    {
        if (Avx2.IsSupported) return Avx2.GatherMaskVector128(source, (int*)address, index, mask, (byte)scale);
        for (int lane = 0; lane < 4; lane++)
            if (mask[lane] < 0) source = source.WithElement(lane, Element<int>(address, index[lane], scale));
        return source;
    }

    [LlvmIntrinsic("llvm.x86.avx2.gather.d.d.256"), MethodImpl(Inline)]
    public static Vector256<int> Gatherdd256(Vector256<int> source, nint address, Vector256<int> index, Vector256<int> mask, int scale) => Avx2.IsSupported
        ? Avx2.GatherMaskVector256(source, (int*)address, index, mask, (byte)scale)
        : Join(Gatherdd(source.GetLower(), address, index.GetLower(), mask.GetLower(), scale), Gatherdd(source.GetUpper(), address, index.GetUpper(), mask.GetUpper(), scale));

    [LlvmIntrinsic("llvm.x86.avx2.gather.d.ps"), MethodImpl(Inline)]
    public static Vector128<float> Gatherdps(Vector128<float> source, nint address, Vector128<int> index, Vector128<float> mask, int scale) => Avx2.IsSupported
        ? Avx2.GatherMaskVector128(source, (float*)address, index, mask, (byte)scale)
        : Gatherdd(source.AsInt32(), address, index, mask.AsInt32(), scale).AsSingle();

    [LlvmIntrinsic("llvm.x86.avx2.gather.d.ps.256"), MethodImpl(Inline)]
    public static Vector256<float> Gatherdps256(Vector256<float> source, nint address, Vector256<int> index, Vector256<float> mask, int scale) => Avx2.IsSupported
        ? Avx2.GatherMaskVector256(source, (float*)address, index, mask, (byte)scale)
        : Gatherdd256(source.AsInt32(), address, index, mask.AsInt32(), scale).AsSingle();

    [LlvmIntrinsic("llvm.x86.avx2.gather.d.q"), MethodImpl(Inline)]
    public static Vector128<long> Gatherdq(Vector128<long> source, nint address, Vector128<int> index, Vector128<long> mask, int scale)
    {
        if (Avx2.IsSupported) return Avx2.GatherMaskVector128(source, (long*)address, index, mask, (byte)scale);
        for (int lane = 0; lane < 2; lane++)
            if (mask[lane] < 0) source = source.WithElement(lane, Element<long>(address, index[lane], scale));
        return source;
    }

    [LlvmIntrinsic("llvm.x86.avx2.gather.d.q.256"), MethodImpl(Inline)]
    public static Vector256<long> Gatherdq256(Vector256<long> source, nint address, Vector128<int> index, Vector256<long> mask, int scale) => Avx2.IsSupported
        ? Avx2.GatherMaskVector256(source, (long*)address, index, mask, (byte)scale)
        : Join(Gatherdq(source.GetLower(), address, index, mask.GetLower(), scale),
            Gatherdq(source.GetUpper(), address, Vector128.Shuffle(index, Vector128.Create(2, 3, 0, 1)), mask.GetUpper(), scale));

    [LlvmIntrinsic("llvm.x86.avx2.gather.d.pd"), MethodImpl(Inline)]
    public static Vector128<double> Gatherdpd(Vector128<double> source, nint address, Vector128<int> index, Vector128<double> mask, int scale) => Avx2.IsSupported
        ? Avx2.GatherMaskVector128(source, (double*)address, index, mask, (byte)scale)
        : Gatherdq(source.AsInt64(), address, index, mask.AsInt64(), scale).AsDouble();

    [LlvmIntrinsic("llvm.x86.avx2.gather.d.pd.256"), MethodImpl(Inline)]
    public static Vector256<double> Gatherdpd256(Vector256<double> source, nint address, Vector128<int> index, Vector256<double> mask, int scale) => Avx2.IsSupported
        ? Avx2.GatherMaskVector256(source, (double*)address, index, mask, (byte)scale)
        : Gatherdq256(source.AsInt64(), address, index, mask.AsInt64(), scale).AsDouble();

    [LlvmIntrinsic("llvm.x86.avx2.gather.q.q"), MethodImpl(Inline)]
    public static Vector128<long> Gatherqq(Vector128<long> source, nint address, Vector128<long> index, Vector128<long> mask, int scale)
    {
        if (Avx2.IsSupported) return Avx2.GatherMaskVector128(source, (long*)address, index, mask, (byte)scale);
        for (int lane = 0; lane < 2; lane++)
            if (mask[lane] < 0) source = source.WithElement(lane, Element<long>(address, index[lane], scale));
        return source;
    }

    [LlvmIntrinsic("llvm.x86.avx2.gather.q.q.256"), MethodImpl(Inline)]
    public static Vector256<long> Gatherqq256(Vector256<long> source, nint address, Vector256<long> index, Vector256<long> mask, int scale) => Avx2.IsSupported
        ? Avx2.GatherMaskVector256(source, (long*)address, index, mask, (byte)scale)
        : Join(Gatherqq(source.GetLower(), address, index.GetLower(), mask.GetLower(), scale), Gatherqq(source.GetUpper(), address, index.GetUpper(), mask.GetUpper(), scale));

    [LlvmIntrinsic("llvm.x86.avx2.gather.q.pd"), MethodImpl(Inline)]
    public static Vector128<double> Gatherqpd(Vector128<double> source, nint address, Vector128<long> index, Vector128<double> mask, int scale) => Avx2.IsSupported
        ? Avx2.GatherMaskVector128(source, (double*)address, index, mask, (byte)scale)
        : Gatherqq(source.AsInt64(), address, index, mask.AsInt64(), scale).AsDouble();

    [LlvmIntrinsic("llvm.x86.avx2.gather.q.pd.256"), MethodImpl(Inline)]
    public static Vector256<double> Gatherqpd256(Vector256<double> source, nint address, Vector256<long> index, Vector256<double> mask, int scale) => Avx2.IsSupported
        ? Avx2.GatherMaskVector256(source, (double*)address, index, mask, (byte)scale)
        : Gatherqq256(source.AsInt64(), address, index, mask.AsInt64(), scale).AsDouble();

    [LlvmIntrinsic("llvm.x86.avx2.gather.q.d"), MethodImpl(Inline)]
    public static Vector128<int> Gatherqd(Vector128<int> source, nint address, Vector128<long> index, Vector128<int> mask, int scale)
    {
        if (Avx2.IsSupported) return Avx2.GatherMaskVector128(source, (int*)address, index, mask, (byte)scale);
        Vector128<int> result = Vector128<int>.Zero;
        for (int lane = 0; lane < 2; lane++)
            result = result.WithElement(lane, mask[lane] < 0 ? Element<int>(address, index[lane], scale) : source[lane]);
        return result;
    }

    [LlvmIntrinsic("llvm.x86.avx2.gather.q.d.256"), MethodImpl(Inline)]
    public static Vector128<int> Gatherqd256(Vector128<int> source, nint address, Vector256<long> index, Vector128<int> mask, int scale)
    {
        if (Avx2.IsSupported) return Avx2.GatherMaskVector128(source, (int*)address, index, mask, (byte)scale);
        for (int lane = 0; lane < 4; lane++)
            if (mask[lane] < 0) source = source.WithElement(lane, Element<int>(address, index[lane], scale));
        return source;
    }

    [LlvmIntrinsic("llvm.x86.avx2.gather.q.ps"), MethodImpl(Inline)]
    public static Vector128<float> Gatherqps(Vector128<float> source, nint address, Vector128<long> index, Vector128<float> mask, int scale) => Avx2.IsSupported
        ? Avx2.GatherMaskVector128(source, (float*)address, index, mask, (byte)scale)
        : Gatherqd(source.AsInt32(), address, index, mask.AsInt32(), scale).AsSingle();

    [LlvmIntrinsic("llvm.x86.avx2.gather.q.ps.256"), MethodImpl(Inline)]
    public static Vector128<float> Gatherqps256(Vector128<float> source, nint address, Vector256<long> index, Vector128<float> mask, int scale) => Avx2.IsSupported
        ? Avx2.GatherMaskVector128(source, (float*)address, index, mask, (byte)scale)
        : Gatherqd256(source.AsInt32(), address, index, mask.AsInt32(), scale).AsSingle();

    // ---- FMA ----

    // vfmaddsub: the product minus the addend in the even elements, plus it in the odd ones, rounded once. Negating the
    // addend's even elements is exact, so a fused multiply-add of the result computes both.

    [LlvmIntrinsic("llvm.x86.fma.vfmaddsub.ps"), MethodImpl(Inline)]
    public static Vector128<float> Vfmaddsubps(Vector128<float> left, Vector128<float> right, Vector128<float> addend) => Fma.IsSupported
        ? Fma.MultiplyAddSubtract(left, right, addend) : Vector128.FusedMultiplyAdd(left, right, addend ^ Vector128.Create(-0f, 0f, -0f, 0f));

    [LlvmIntrinsic("llvm.x86.fma.vfmaddsub.pd"), MethodImpl(Inline)]
    public static Vector128<double> Vfmaddsubpd(Vector128<double> left, Vector128<double> right, Vector128<double> addend) => Fma.IsSupported
        ? Fma.MultiplyAddSubtract(left, right, addend) : Vector128.FusedMultiplyAdd(left, right, addend ^ Vector128.Create(-0d, 0d));

    [LlvmIntrinsic("llvm.x86.fma.vfmaddsub.ps.256"), MethodImpl(Inline)]
    public static Vector256<float> Vfmaddsubps256(Vector256<float> left, Vector256<float> right, Vector256<float> addend) => Fma.IsSupported
        ? Fma.MultiplyAddSubtract(left, right, addend)
        : Join(Vfmaddsubps(left.GetLower(), right.GetLower(), addend.GetLower()), Vfmaddsubps(left.GetUpper(), right.GetUpper(), addend.GetUpper()));

    [LlvmIntrinsic("llvm.x86.fma.vfmaddsub.pd.256"), MethodImpl(Inline)]
    public static Vector256<double> Vfmaddsubpd256(Vector256<double> left, Vector256<double> right, Vector256<double> addend) => Fma.IsSupported
        ? Fma.MultiplyAddSubtract(left, right, addend)
        : Join(Vfmaddsubpd(left.GetLower(), right.GetLower(), addend.GetLower()), Vfmaddsubpd(left.GetUpper(), right.GetUpper(), addend.GetUpper()));

    // ---- F16C ----

    // vcvtps2ph rounds each float to half precision by bits 0 and 1 of the control, or MXCSR's mode (nearest even) when
    // bit 2 is set. .NET has no F16C intrinsics, so the conversion is computed exactly: a NaN stays a NaN with its sign and
    // the top of its payload, quieted; an overflow gives the infinity or the largest finite value the mode rounds to.

    [LlvmIntrinsic("llvm.x86.vcvtps2ph.128"), MethodImpl(Inline)]
    public static Vector128<short> Vcvtps2ph(Vector128<float> value, int control) =>
        Vector128.Create(ToHalf(value[0], control), ToHalf(value[1], control), ToHalf(value[2], control), ToHalf(value[3], control), 0, 0, 0, 0);

    [LlvmIntrinsic("llvm.x86.vcvtps2ph.256"), MethodImpl(Inline)]
    public static Vector128<short> Vcvtps2ph256(Vector256<float> value, int control)
    {
        Vector128<float> lower = value.GetLower(), upper = value.GetUpper();
        return Vector128.Create(ToHalf(lower[0], control), ToHalf(lower[1], control), ToHalf(lower[2], control), ToHalf(lower[3], control),
            ToHalf(upper[0], control), ToHalf(upper[1], control), ToHalf(upper[2], control), ToHalf(upper[3], control));
    }

    private static short ToHalf(float value, int control)
    {
        uint bits = BitConverter.SingleToUInt32Bits(value);
        uint sign = (bits >> 16) & 0x8000;
        int exponent = (int)((bits >> 23) & 0xff);
        uint mantissa = bits & 0x7fffff;
        if (exponent == 0xff) return (short)(sign | 0x7c00 | (mantissa != 0 ? 0x200 | (mantissa >> 13) : 0));
        if (exponent == 0 && mantissa == 0) return (short)sign;
        int mode = Rounding(control);
        bool negative = sign != 0;
        // value = significand * 2^scale, with the 24-bit significand of a normal float.
        uint significand = exponent == 0 ? mantissa : mantissa | 0x800000;
        int scale = exponent == 0 ? -149 : exponent - 150;
        int top = 31 - BitOperations.LeadingZeroCount(significand);
        int halfExponent = top + scale + 15;
        // Keep 11 bits for a normal half, or the multiple of 2^-24 for a subnormal one.
        int shift = halfExponent >= 1 ? top - 10 : -scale - 24;
        ulong kept = shift >= 40 ? 0 : (ulong)significand >> shift;
        ulong dropped = shift >= 40 ? significand : significand & ((1UL << shift) - 1);
        ulong halfway = shift >= 40 ? ulong.MaxValue : 1UL << (shift - 1);
        bool up = mode switch
        {
            0 => dropped > halfway || dropped == halfway && (kept & 1) != 0,
            1 => negative && dropped != 0,
            2 => !negative && dropped != 0,
            _ => false
        };
        kept += up ? 1UL : 0;
        if (halfExponent >= 1)
        {
            if (kept == 0x800)
            {
                kept >>= 1;
                halfExponent++;
            }
            if (halfExponent >= 31)
                return (short)(sign | (mode == 0 || mode == 1 && negative || mode == 2 && !negative ? 0x7c00u : 0x7bffu));
            return (short)(sign | ((uint)halfExponent << 10) | ((uint)kept & 0x3ff));
        }
        return (short)(sign | (uint)kept);
    }

    // ---- BMI1 and BMI2 ----

    [LlvmIntrinsic("llvm.x86.bmi.bextr.32"), MethodImpl(Inline)]
    public static int Bextr32(int value, int control)
    {
        if (Bmi1.IsSupported) return (int)Bmi1.BitFieldExtract((uint)value, (ushort)control);
        int start = control & 0xff, length = (control >> 8) & 0xff;
        if (start > 31) return 0;
        uint field = (uint)value >> start;
        return (int)(length > 31 ? field : field & ((1u << length) - 1));
    }

    [LlvmIntrinsic("llvm.x86.bmi.bextr.64"), MethodImpl(Inline)]
    public static long Bextr64(long value, long control)
    {
        if (Bmi1.X64.IsSupported) return (long)Bmi1.X64.BitFieldExtract((ulong)value, (ushort)control);
        int start = (int)(control & 0xff), length = (int)((control >> 8) & 0xff);
        if (start > 63) return 0;
        ulong field = (ulong)value >> start;
        return (long)(length > 63 ? field : field & ((1UL << length) - 1));
    }

    [LlvmIntrinsic("llvm.x86.bmi.bzhi.32"), MethodImpl(Inline)]
    public static int Bzhi32(int value, int index)
    {
        if (Bmi2.IsSupported) return (int)Bmi2.ZeroHighBits((uint)value, (uint)index);
        int count = index & 0xff;
        return count > 31 ? value : (int)((uint)value & ((1u << count) - 1));
    }

    [LlvmIntrinsic("llvm.x86.bmi.bzhi.64"), MethodImpl(Inline)]
    public static long Bzhi64(long value, long index)
    {
        if (Bmi2.X64.IsSupported) return (long)Bmi2.X64.ZeroHighBits((ulong)value, (ulong)index);
        int count = (int)(index & 0xff);
        return count > 63 ? value : (long)((ulong)value & ((1UL << count) - 1));
    }

    [LlvmIntrinsic("llvm.x86.bmi.pdep.32"), MethodImpl(Inline)]
    public static int Pdep32(int value, int mask) => Bmi2.IsSupported ? (int)Bmi2.ParallelBitDeposit((uint)value, (uint)mask) : (int)Deposit((uint)value, (uint)mask);

    [LlvmIntrinsic("llvm.x86.bmi.pdep.64"), MethodImpl(Inline)]
    public static long Pdep64(long value, long mask) => Bmi2.X64.IsSupported ? (long)Bmi2.X64.ParallelBitDeposit((ulong)value, (ulong)mask)
        : (long)Deposit((ulong)value, (ulong)mask);

    [LlvmIntrinsic("llvm.x86.bmi.pext.32"), MethodImpl(Inline)]
    public static int Pext32(int value, int mask) => Bmi2.IsSupported ? (int)Bmi2.ParallelBitExtract((uint)value, (uint)mask) : (int)Extract((uint)value, (uint)mask);

    [LlvmIntrinsic("llvm.x86.bmi.pext.64"), MethodImpl(Inline)]
    public static long Pext64(long value, long mask) => Bmi2.X64.IsSupported ? (long)Bmi2.X64.ParallelBitExtract((ulong)value, (ulong)mask)
        : (long)Extract((ulong)value, (ulong)mask);

    /// <summary>pdep: the low bits of the value, in order, at the positions of the mask's set bits.</summary>
    private static ulong Deposit(ulong value, ulong mask)
    {
        ulong result = 0;
        for (ulong bit = 1; mask != 0; bit <<= 1, mask &= mask - 1)
            if ((value & bit) != 0) result |= mask & ~(mask - 1);
        return result;
    }

    /// <summary>pext: the value's bits at the positions of the mask's set bits, packed into the low bits.</summary>
    private static ulong Extract(ulong value, ulong mask)
    {
        ulong result = 0;
        for (ulong bit = 1; mask != 0; bit <<= 1, mask &= mask - 1)
            if ((value & mask & ~(mask - 1)) != 0) result |= bit;
        return result;
    }
}
