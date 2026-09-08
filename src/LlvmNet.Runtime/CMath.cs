namespace LlvmNet.Runtime;

public static class CMath
{
    public static unsafe int GetRounding(int system)
    {
        if (system == 0) return 1;
        var getRounding = (delegate* unmanaged[Cdecl]<int>)SystemAbi.Symbol("libm.so.6", "fegetround");
        return getRounding() switch { 0 => 1, 0x400 => 3, 0x800 => 2, 0xc00 => 0, _ => -1 };
    }

    [CExport("sqrt")] public static double Sqrt(double value)
    {
        if (value < 0)
            ProcessRuntime.Error(33);
        return Math.Sqrt(value);
    }
    [CExport("sqrtf")] public static float SqrtF(float value) => (float)Sqrt(value);
    [CExport("sin")] public static double Sin(double value) => Math.Sin(value);
    [CExport("sinf")] public static float SinF(float value) => MathF.Sin(value);
    [CExport("cos")] public static double Cos(double value) => Math.Cos(value);
    [CExport("cosf")] public static float CosF(float value) => MathF.Cos(value);
    [CExport("tan")] public static double Tan(double value) => Math.Tan(value);
    [CExport("tanf")] public static float TanF(float value) => MathF.Tan(value);
    [CExport("asin")] public static double Asin(double value) => Math.Asin(value);
    [CExport("acos")] public static double Acos(double value) => Math.Acos(value);
    [CExport("atan")] public static double Atan(double value) => Math.Atan(value);
    [CExport("atanf")] public static float AtanF(float value) => MathF.Atan(value);
    [CExport("cbrt")] public static double Cbrt(double value) => Math.Cbrt(value);
    [CExport("cbrtf")] public static float CbrtF(float value) => MathF.Cbrt(value);
    [CExport("erf")] public static double Erf(double value) => MathNet.Numerics.SpecialFunctions.Erf(value);
    [CExport("erff")] public static float ErfF(float value) => (float)Erf(value);
    [CExport("tgamma")] public static double Tgamma(double value)
    {
        if (double.IsNaN(value) || double.IsPositiveInfinity(value)) return value;
        if (value == 0)
        {
            ProcessRuntime.Error(34);
            return Math.CopySign(double.PositiveInfinity, value);
        }
        if (value < 0 && value == Math.Truncate(value))
        {
            ProcessRuntime.Error(33);
            return double.NaN;
        }
        double result = MathNet.Numerics.SpecialFunctions.Gamma(value);
        if (double.IsInfinity(result) || result == 0) ProcessRuntime.Error(34);
        return result;
    }
    [CExport("tgammaf")] public static float TgammaF(float value)
    {
        double wide = Tgamma(value);
        float result = (float)wide;
        if (double.IsFinite(wide) && (float.IsInfinity(result) || wide != 0 && result == 0)) ProcessRuntime.Error(34);
        return result;
    }
    [CExport("hypot")] public static double Hypot(double left, double right) => double.Hypot(left, right);
    [CExport("hypotf")] public static float HypotF(float left, float right) => float.Hypot(left, right);
    [CExport("atan2")] public static double Atan2(double left, double right) => Math.Atan2(left, right);
    [CExport("atan2f")] public static float Atan2F(float left, float right) => MathF.Atan2(left, right);
    [CExport("exp")] public static double Exp(double value) => Math.Exp(value);
    [CExport("expf")] public static float ExpF(float value) => MathF.Exp(value);
    [CExport("exp2")] public static double Exp2(double value) => double.Exp2(value);
    [CExport("exp2f")] public static float Exp2F(float value) => float.Exp2(value);
    [CExport("log")] public static double Log(double value) => Math.Log(value);
    [CExport("logf")] public static float LogF(float value) => MathF.Log(value);
    [CExport("log2")] public static double Log2(double value) => Math.Log2(value);
    [CExport("log2f")] public static float Log2F(float value) => MathF.Log2(value);
    [CExport("log10")] public static double Log10(double value) => Math.Log10(value);
    [CExport("log10f")] public static float Log10F(float value) => MathF.Log10(value);
    [CExport("pow")] public static double Pow(double value, double power) => Math.Pow(value, power);
    [CExport("powf")] public static float PowF(float value, float power) => MathF.Pow(value, power);
    [CExport("floor")] public static double Floor(double value) => Math.Floor(value);
    [CExport("floorf")] public static float FloorF(float value) => MathF.Floor(value);
    [CExport("ceil")] public static double Ceil(double value) => Math.Ceiling(value);
    [CExport("ceilf")] public static float CeilF(float value) => MathF.Ceiling(value);
    [CExport("trunc")] public static double Trunc(double value) => Math.Truncate(value);
    [CExport("truncf")] public static float TruncF(float value) => MathF.Truncate(value);
    [CExport("round")] public static double Round(double value) => Math.Round(value, MidpointRounding.AwayFromZero);
    [CExport("roundf")] public static float RoundF(float value) => MathF.Round(value, MidpointRounding.AwayFromZero);
    [CExport("rint")] public static double Rint(double value) => Math.Round(value);
    [CExport("rintf")] public static float RintF(float value) => MathF.Round(value);
    [CExport("lrint"), CExport("llrint")] public static long Lrint(double value) => (long)Math.Round(value);
    [CExport("lrintf"), CExport("llrintf")] public static long LrintF(float value) => (long)MathF.Round(value);
    [CExport("lround")] public static long Lround(double value) => (long)Round(value);
    [CExport("lroundf")] public static long LroundF(float value) => (long)RoundF(value);
    [CExport("fabs")] public static double Fabs(double value) => Math.Abs(value);
    [CExport("fabsf")] public static float FabsF(float value) => MathF.Abs(value);
    [CExport("copysign")] public static double CopySign(double value, double sign) => Math.CopySign(value, sign);
    [CExport("copysignf")] public static float CopySignF(float value, float sign) => MathF.CopySign(value, sign);
    [CExport("fmod")] public static double Fmod(double value, double divisor) => value % divisor;
    [CExport("fmodf")] public static float FmodF(float value, float divisor) => value % divisor;
    [CExport("modf")] public static unsafe double Modf(double value, nint integral)
    {
        *(double*)integral = Math.Truncate(value);
        return Math.CopySign(double.IsInfinity(value) ? 0 : value - *(double*)integral, value);
    }
    [CExport("modff")] public static unsafe float ModfF(float value, nint integral)
    {
        *(float*)integral = MathF.Truncate(value);
        return MathF.CopySign(float.IsInfinity(value) ? 0 : value - *(float*)integral, value);
    }
    [CExport("fma")] public static double Fma(double left, double right, double addend) => Math.FusedMultiplyAdd(left, right, addend);
    [CExport("fmaf")] public static float FmaF(float left, float right, float addend) => MathF.FusedMultiplyAdd(left, right, addend);
    [CExport("ldexp")] public static double Ldexp(double value, int exponent) => Math.ScaleB(value, exponent);
    [CExport("ldexpf")] public static float LdexpF(float value, int exponent) => MathF.ScaleB(value, exponent);
    [CExport("scalbn")] public static double Scalbn(double value, int exponent) => Math.ScaleB(value, exponent);
    [CExport("scalbnf")] public static float ScalbnF(float value, int exponent) => MathF.ScaleB(value, exponent);
    [CExport("sinh")] public static double Sinh(double value) => Math.Sinh(value);
    [CExport("cosh")] public static double Cosh(double value) => Math.Cosh(value);
    [CExport("tanh")] public static double Tanh(double value) => Math.Tanh(value);
    [CExport("atanh")] public static double Atanh(double value)
    {
        if (Math.Abs(value) > 1) ProcessRuntime.Error(33);
        else if (Math.Abs(value) == 1) ProcessRuntime.Error(34);
        return Math.Atanh(value);
    }
    [CExport("atanhf")] public static float AtanhF(float value)
    {
        if (MathF.Abs(value) > 1) ProcessRuntime.Error(33);
        else if (MathF.Abs(value) == 1) ProcessRuntime.Error(34);
        return MathF.Atanh(value);
    }
    [CExport("frexp")] public static unsafe double Frexp(double value, nint exponent)
    {
        if (value == 0 || !double.IsFinite(value)) { *(int*)exponent = 0; return value; }
        int power = Math.ILogB(value) + 1;
        *(int*)exponent = power;
        return Math.ScaleB(value, -power);
    }
}