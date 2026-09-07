using System.Runtime.Intrinsics;

internal static class Simd128Probe
{
    internal static void Run(string inputPath)
    {
        byte[] records = File.ReadAllBytes(inputPath);
        if (!BitConverter.IsLittleEndian || records.Length == 0 || records.Length % 128 != 0)
            throw new ArgumentException("Expected little-endian 128-byte SIMD input records.");
        Console.Error.WriteLine($"Vector128.IsHardwareAccelerated={Vector128.IsHardwareAccelerated}");
        byte[] input = new byte[129];
        for (int record = 0; record < records.Length / 128; record++)
        {
            records.AsSpan(record * 128, 128).CopyTo(input.AsSpan(1));
            Vector128<byte> Load(int offset) => Vector128.LoadUnsafe(ref input[offset + 1]);
            Vector128<byte> left = Load(0), right = Load(16), indices = Load(32);
            Vector128<short> signedLeft = Load(48).AsInt16(), signedRight = Load(64).AsInt16();
            Vector128<float> floatLeft = Load(80).AsSingle(), floatRight = Load(96).AsSingle(), conversions = Load(112).AsSingle();
            int operation = 0;
            void Write<T>(Vector128<T> value) where T : struct
            {
                Span<byte> bytes = stackalloc byte[16];
                value.AsByte().CopyTo(bytes);
                Console.WriteLine($"{record}:{operation++} {Convert.ToHexString(bytes)}");
            }
            Write(left);
            Write(left + right);
            Write(signedLeft * signedRight);
            Write(Vector128.ShiftLeft(left, 9 & 7));
            Write(Vector128.ShiftRightArithmetic(signedLeft, 19 & 15));
            Write(Vector128.Shuffle(left, indices));
            Write(Vector128.ConditionalSelect(indices, left, right));
            Write(Vector128.Create(Vector128.ExtractMostSignificantBits(left)));
            Write(Vector128.Create(Vector128.EqualsAny(left, Vector128<byte>.Zero) ? 0 : 1));
            Write(Vector128.Create(Vector128.EqualsAll(left, Vector128<byte>.Zero) ? 0 : 1));
            Write(SaturatingAdd(left, right));
            Write(Vector128.Narrow(Clamp(signedLeft, -128, 127), Clamp(signedRight, -128, 127)));
            Write(Vector128.Narrow(Clamp(signedLeft, 0, 255).AsUInt16(), Clamp(signedRight, 0, 255).AsUInt16()));
            Write(CanonicalizeNaN(Vector128.Min(floatLeft, floatRight)));
            Write(CanonicalizeNaN(Vector128.Max(floatLeft, floatRight)));
            Write(Vector128.ConvertToInt32(conversions));
            Write(Vector128.ConvertToUInt32(conversions));
            Write(Q15Multiply(signedLeft, signedRight));
            Write(Vector128.WidenLower(signedLeft));
            Write(Vector128.ConditionalSelect(Vector128.LessThan(floatRight, floatLeft), floatRight, floatLeft));
            Vector128<byte> shuffle = Vector128.Create((byte)0, 16, 1, 17, 2, 18, 15, 31, 7, 23, 8, 24, 3, 19, 4, 20);
            Write(Vector128.Shuffle(left, shuffle) | Vector128.Shuffle(right, shuffle - Vector128.Create((byte)16)));
        }
    }

    private static Vector128<short> Clamp(Vector128<short> value, short minimum, short maximum) =>
        Vector128.Min(Vector128.Max(value, Vector128.Create(minimum)), Vector128.Create(maximum));

    private static Vector128<byte> SaturatingAdd(Vector128<byte> left, Vector128<byte> right)
    {
        Vector128<ushort> lower = Vector128.WidenLower(left) + Vector128.WidenLower(right);
        Vector128<ushort> upper = Vector128.WidenUpper(left) + Vector128.WidenUpper(right);
        return Vector128.Narrow(Vector128.Min(lower, Vector128.Create((ushort)255)), Vector128.Min(upper, Vector128.Create((ushort)255)));
    }

    private static Vector128<short> Q15Multiply(Vector128<short> left, Vector128<short> right)
    {
        Vector128<int> lower = Vector128.WidenLower(left) * Vector128.WidenLower(right);
        Vector128<int> upper = Vector128.WidenUpper(left) * Vector128.WidenUpper(right);
        Vector128<int> Round(Vector128<int> value) =>
            Vector128.Min(Vector128.ShiftRightArithmetic(value + Vector128.Create(0x4000), 15), Vector128.Create(32767));
        return Vector128.Narrow(Round(lower), Round(upper));
    }

    private static Vector128<float> CanonicalizeNaN(Vector128<float> value) =>
        Vector128.ConditionalSelect(Vector128.Equals(value, value), value, Vector128.Create(0x7fc00000).AsSingle());
}