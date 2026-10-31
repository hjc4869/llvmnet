// PF-08: the SIMD128 helpers the ffmpeg SIMD128 profile calls most (LlvmNet.Runtime.Simd128), timed one at a time
// over 50 million iterations. Published as NativeAOT for different instruction sets, it shows which helpers lose
// their hardware instructions under the baseline set.
using System.Diagnostics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using LlvmNet.Runtime;

const int Iterations = 50_000_000;
Console.WriteLine($"Ssse3 {Ssse3.IsSupported} Sse41 {Sse41.IsSupported} Sse42 {Sse42.IsSupported} Avx2 {Avx2.IsSupported} Avx512 {Avx512F.IsSupported}");
Vector128<byte> a = Vector128.Create((byte)1, 200, 3, 4, 250, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16);
Vector128<byte> b = Vector128.Create((byte)15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0);
Time("Swizzle (variable shuffle)", (x, y) => Simd128.Swizzle(x, y & Vector128.Create((byte)15)));
Time("Shuffle (constant lanes)", (x, y) => Simd128.Shuffle(x, y, 0x0706050403020100, 0x1716151413121110));
Time("ExtendLowInt8 (widen)", (x, y) => Simd128.ExtendLowInt8(x) ^ y);
Time("ExtendLowUInt8 (widen)", (x, y) => Simd128.ExtendLowUInt8(x) ^ y);
Time("NarrowInt16 (saturate)", (x, y) => Simd128.NarrowInt16(x, y));
Time("NarrowUInt16 (saturate)", (x, y) => Simd128.NarrowUInt16(x, y));
Time("MultiplyInt16", (x, y) => Simd128.MultiplyInt16(x, y));
Time("AddSaturateInt16", (x, y) => Simd128.AddSaturateInt16(x, y));
Time("BitSelect", (x, y) => Simd128.BitSelect(x, y, x ^ y));
Time("AddInt32", (x, y) => Simd128.AddInt32(x, y));

void Time(string name, Func<Vector128<byte>, Vector128<byte>, Vector128<byte>> operation)
{
    Vector128<byte> x = a, y = b;
    for (int i = 0; i < 1000; i++) x = operation(x, y);
    var clock = Stopwatch.StartNew();
    for (int i = 0; i < Iterations; i++) { x = operation(x, y); y += Vector128<byte>.One; }
    Console.WriteLine($"{name,-30} {clock.Elapsed.TotalMilliseconds * 1e6 / Iterations,6:F2} ns  ({x.GetElement(0)})");
}
