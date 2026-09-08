namespace LlvmNet.Runtime;

public static unsafe class FortranRandom
{
    private static readonly object gate = new();
    private static ulong state = 1;
    private static ulong? pending;

    private static void Seed(ulong seed)
    {
        state = seed % 2147483647;
        if (state == 0) state = 1;
    }

    private static ulong Next()
    {
        if (pending is ulong saved) { pending = null; return saved; }
        state = state * 48271 % 2147483647;
        return state;
    }

    [CExport("_FortranARandomInit")]
    public static void Init(int repeatable, int imageDistinct)
    {
        lock (gate) Seed(repeatable != 0 ? 0 : unchecked((ulong)DateTime.UtcNow.Ticks));
    }

    [CExport("_FortranARandomSeedDefaultPut")]
    public static void DefaultSeed()
    {
        lock (gate) Seed(0);
    }

    [CExport("_FortranARandomSeedSize")]
    public static void SeedSize(nint descriptor, nint source, int line)
    {
        if (descriptor == 0 || *(nint*)descriptor == 0) { DefaultSeed(); return; }
        var value = new FortranDescriptor(descriptor);
        if (value.Rank != 0) throw new InvalidOperationException("RANDOM_SEED SIZE must be scalar.");
        WriteInteger(value, 1);
    }

    [CExport("_FortranARandomSeedPut")]
    public static void SeedPut(nint descriptor, nint source, int line)
    {
        if (descriptor == 0 || *(nint*)descriptor == 0) { DefaultSeed(); return; }
        var value = new FortranDescriptor(descriptor);
        if (value.Rank != 1 || value.Elements < 1) throw new InvalidOperationException("RANDOM_SEED PUT requires an array element.");
        ulong seed = value.ElementBytes switch
        {
            4 => unchecked((ulong)*(int*)value.Base),
            8 => unchecked((ulong)*(long*)value.Base),
            _ => throw new NotSupportedException("RANDOM_SEED integer kind is not implemented.")
        };
        lock (gate) { Seed(seed); pending = seed; }
    }

    [CExport("_FortranARandomSeedGet")]
    public static void SeedGet(nint descriptor, nint source, int line)
    {
        if (descriptor == 0 || *(nint*)descriptor == 0) { DefaultSeed(); return; }
        var value = new FortranDescriptor(descriptor);
        if (value.Rank != 1 || value.Elements < 1) throw new InvalidOperationException("RANDOM_SEED GET requires an array element.");
        lock (gate)
        {
            ulong seed = Next();
            pending = seed;
            WriteInteger(value, seed);
        }
    }

    [CExport("_FortranARandomSeed")]
    public static void RandomSeed(nint size, nint put, nint get, nint source, int line)
    {
        bool hasSize = size != 0 && *(nint*)size != 0;
        bool hasPut = put != 0 && *(nint*)put != 0;
        bool hasGet = get != 0 && *(nint*)get != 0;
        if ((hasSize ? 1 : 0) + (hasPut ? 1 : 0) + (hasGet ? 1 : 0) > 1)
            throw new InvalidOperationException("RANDOM_SEED accepts at most one argument.");
        if (hasSize) SeedSize(size, source, line);
        else if (hasPut) SeedPut(put, source, line);
        else if (hasGet) SeedGet(get, source, line);
        else DefaultSeed();
    }

    private static void WriteInteger(FortranDescriptor value, ulong number)
    {
        if (value.ElementBytes == 4) *(int*)value.Base = unchecked((int)number);
        else if (value.ElementBytes == 8) *(long*)value.Base = unchecked((long)number);
        else throw new NotSupportedException("RANDOM_SEED integer kind is not implemented.");
    }

    [CExport("_FortranARandomNumber")]
    public static void RandomNumber(nint descriptor, nint source, int line)
    {
        var array = new FortranDescriptor(descriptor);
        int precision = array.Type switch { 27 => 24, 28 => 53, _ => throw new NotSupportedException($"RANDOM_NUMBER type {array.Type} is not implemented.") };
        int words = (precision + 29) / 30;
        lock (gate)
        {
            for (long index = 0; index < array.Elements; index++)
            {
                double value;
                do
                {
                    ulong fraction = Next();
                    for (int word = 1; word < words; word++)
                        fraction = unchecked((fraction << 30) | ((Next() - 1) & 0x3fffffff));
                    fraction >>= words * 30 - precision;
                    value = precision == 24 ? MathF.ScaleB((float)fraction, -25) : Math.ScaleB(fraction, -54);
                } while (!(value >= 0 && value < 1));
                if (precision == 24) *(float*)array.Address(index) = (float)value;
                else *(double*)array.Address(index) = value;
            }
        }
    }
}