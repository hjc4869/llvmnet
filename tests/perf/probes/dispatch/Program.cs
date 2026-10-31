// PF-05: the ways a translated indirect call could be dispatched, on the calli microbenchmark's pattern: a table of
// eight functions selected by a pseudo-random index (polymorphic) and one target called through a pointer (monomorphic).
using System.Diagnostics;
using System.Runtime.CompilerServices;

unsafe static class Program
{
    const long Calls = 200_000_000;
    static readonly delegate*<uint, uint, uint>[] pointers = [&Ops.Add, &Ops.Sub, &Ops.Mul, &Ops.Xor, &Ops.Rotate, &Ops.Mix, &Ops.Low, &Ops.High];
    static readonly Func<uint, uint, uint>[] delegates = [Ops.Add, Ops.Sub, Ops.Mul, Ops.Xor, Ops.Rotate, Ops.Mix, Ops.Low, Ops.High];
    static readonly IOp[] objects = [new AddOp(), new SubOp(), new MulOp(), new XorOp(), new RotateOp(), new MixOp(), new LowOp(), new HighOp()];
    static readonly nint[] addresses = new nint[8];
    static nint single;
    static Func<uint, uint, uint> singleDelegate = Ops.Mix;

    static void Main()
    {
        for (int i = 0; i < 8; i++) addresses[i] = (nint)pointers[i];
        single = (nint)(delegate*<uint, uint, uint>)&Ops.Mix;
        for (int round = 0; round < 2; round++)
        {
            Run("direct calls, switch on the index", &Switch);
            Run("calli through nint addresses", &Calli);
            Run("delegate table", &Delegates);
            Run("interface table", &Interfaces);
            Run("monomorphic calli", &MonoCalli);
            Run("monomorphic delegate", &MonoDelegate);
            Run("monomorphic direct call", &MonoDirect);
            Console.WriteLine();
        }
    }

    static void Run(string name, delegate*<uint> body)
    {
        var clock = Stopwatch.StartNew();
        uint result = body();
        Console.WriteLine($"{name,-34} {clock.Elapsed.TotalMilliseconds * 1e6 / Calls,6:F2} ns per call  ({result})");
    }

    static uint Next(uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return state;
    }

    static uint Switch() { uint s = 2463534242u, v = 1; for (long i = 0; i < Calls; i++) { s = Next(s); v = Ops.Switch(s & 7, v, s); } return v; }
    static uint Calli()
    {
        uint s = 2463534242u, v = 1;
        fixed (nint* table = addresses)
            for (long i = 0; i < Calls; i++) { s = Next(s); v = ((delegate*<uint, uint, uint>)table[s & 7])(v, s); }
        return v;
    }
    static uint Delegates() { uint s = 2463534242u, v = 1; var d = delegates; for (long i = 0; i < Calls; i++) { s = Next(s); v = d[s & 7](v, s); } return v; }
    static uint Interfaces() { uint s = 2463534242u, v = 1; var o = objects; for (long i = 0; i < Calls; i++) { s = Next(s); v = o[s & 7].Apply(v, s); } return v; }
    static uint MonoCalli() { uint s = 2463534242u, v = 1; var f = (delegate*<uint, uint, uint>)single; for (long i = 0; i < Calls; i++) { s = Next(s); v = f(v, s); } return v; }
    static uint MonoDelegate() { uint s = 2463534242u, v = 1; var f = singleDelegate; for (long i = 0; i < Calls; i++) { s = Next(s); v = f(v, s); } return v; }
    static uint MonoDirect() { uint s = 2463534242u, v = 1; for (long i = 0; i < Calls; i++) { s = Next(s); v = Ops.Mix(v, s); } return v; }
}

static class Ops
{
    [MethodImpl(MethodImplOptions.NoInlining)] public static uint Add(uint a, uint b) => a + b;
    [MethodImpl(MethodImplOptions.NoInlining)] public static uint Sub(uint a, uint b) => a - b;
    [MethodImpl(MethodImplOptions.NoInlining)] public static uint Mul(uint a, uint b) => a * b + 1;
    [MethodImpl(MethodImplOptions.NoInlining)] public static uint Xor(uint a, uint b) => a ^ b;
    [MethodImpl(MethodImplOptions.NoInlining)] public static uint Rotate(uint a, uint b) => a << (int)(b & 31) | a >> (int)(-b & 31);
    [MethodImpl(MethodImplOptions.NoInlining)] public static uint Mix(uint a, uint b) => (a ^ b) * 0x9e3779b9u;
    [MethodImpl(MethodImplOptions.NoInlining)] public static uint Low(uint a, uint b) => a < b ? a : b;
    [MethodImpl(MethodImplOptions.NoInlining)] public static uint High(uint a, uint b) => a > b ? a : b;
    public static uint Switch(uint index, uint a, uint b) => index switch
    {
        0 => Add(a, b), 1 => Sub(a, b), 2 => Mul(a, b), 3 => Xor(a, b), 4 => Rotate(a, b), 5 => Mix(a, b), 6 => Low(a, b), _ => High(a, b)
    };
}

interface IOp { uint Apply(uint a, uint b); }
sealed class AddOp : IOp { public uint Apply(uint a, uint b) => Ops.Add(a, b); }
sealed class SubOp : IOp { public uint Apply(uint a, uint b) => Ops.Sub(a, b); }
sealed class MulOp : IOp { public uint Apply(uint a, uint b) => Ops.Mul(a, b); }
sealed class XorOp : IOp { public uint Apply(uint a, uint b) => Ops.Xor(a, b); }
sealed class RotateOp : IOp { public uint Apply(uint a, uint b) => Ops.Rotate(a, b); }
sealed class MixOp : IOp { public uint Apply(uint a, uint b) => Ops.Mix(a, b); }
sealed class LowOp : IOp { public uint Apply(uint a, uint b) => Ops.Low(a, b); }
sealed class HighOp : IOp { public uint Apply(uint a, uint b) => Ops.High(a, b); }
