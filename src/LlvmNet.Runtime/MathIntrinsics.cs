namespace LlvmNet.Runtime;

/// <summary>Helpers the compiler calls for LLVM math intrinsics; the C math library itself comes from musl or glibc.</summary>
public static class MathIntrinsics
{
    /// <summary>llvm.get.rounding: FLT_ROUNDS of the host's floating-point environment in system mode; round-to-nearest otherwise.</summary>
    public static unsafe int GetRounding(int system)
    {
        if (system == 0) return 1;
        var getRounding = (delegate* unmanaged[Cdecl]<int>)SystemAbi.Symbol("libm.so.6", "fegetround");
        return getRounding() switch { 0 => 1, 0x400 => 3, 0x800 => 2, 0xc00 => 0, _ => -1 };
    }

    /// <summary>A float constant from its bits, used for signaling NaNs: the JIT keeps float constants as doubles, and the
    /// widening quiets them. Not inlined, so the bits are never a foldable constant.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public static float SingleFromBits(int bits) => BitConverter.Int32BitsToSingle(bits);

    public static double Round(double value) => Math.Round(value, MidpointRounding.AwayFromZero);
    public static float RoundF(float value) => MathF.Round(value, MidpointRounding.AwayFromZero);
    public static long Lrint(double value) => (long)Math.Round(value);
    public static long LrintF(float value) => (long)MathF.Round(value);
    public static long Lround(double value) => (long)Round(value);
    public static long LroundF(float value) => (long)RoundF(value);
    public static double Ldexp(double value, int exponent) => Math.ScaleB(value, exponent);
    public static float LdexpF(float value, int exponent) => MathF.ScaleB(value, exponent);

    public static unsafe double Modf(double value, nint integral)
    {
        *(double*)integral = Math.Truncate(value);
        return Math.CopySign(double.IsInfinity(value) ? 0 : value - *(double*)integral, value);
    }

    public static unsafe double Frexp(double value, nint exponent)
    {
        if (value == 0 || !double.IsFinite(value)) { *(int*)exponent = 0; return value; }
        int power = Math.ILogB(value) + 1;
        *(int*)exponent = power;
        return Math.ScaleB(value, -power);
    }
}
