using System.Runtime.InteropServices;
using System.Text;
using LlvmNet.Runtime;

namespace LlvmNet.UnitTests;

/// <summary>
/// Builds the software x86-64 SysV va_list layout that translated variadic calls use:
/// six 8-byte integer registers, eight 16-byte vector registers, then an overflow area in argument order.
/// </summary>
internal sealed unsafe class NativeArguments : IDisposable
{
    private readonly List<nint> allocations = [];
    private readonly nint registers;
    private readonly nint overflow;
    private readonly nint list;

    internal NativeArguments(params object[] values)
    {
        registers = Allocate(176);
        overflow = Allocate(8 * Math.Max(values.Length, 1));
        int general = 0, floating = 0, spilled = 0;
        foreach (object value in values)
        {
            if (value is double or float)
            {
                double number = Convert.ToDouble(value);
                if (floating < 8) *(double*)(registers + 48 + 16 * floating++) = number;
                else *(double*)(overflow + 8 * spilled++) = number;
                continue;
            }
            long slot = value switch
            {
                string text => Text(text),
                char character => character,
                int integer => integer,
                uint integer => integer,
                long integer => integer,
                ulong integer => unchecked((long)integer),
                nint pointer => pointer,
                _ => throw new ArgumentException($"Unsupported argument {value.GetType()}")
            };
            if (general < 6) *(long*)(registers + 8 * general++) = slot;
            else *(long*)(overflow + 8 * spilled++) = slot;
        }
        list = Allocate(sizeof(VarArgs));
        *(VarArgs*)list = new VarArgs { GeneralOffset = 0, FloatingOffset = 48, Overflow = overflow, Registers = registers };
    }

    internal nint List => list;

    internal nint Text(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text + "\0");
        nint memory = Allocate(bytes.Length);
        Marshal.Copy(bytes, 0, memory, bytes.Length);
        return memory;
    }

    internal nint Allocate(int size)
    {
        nint memory = (nint)NativeMemory.AllocZeroed((nuint)size);
        allocations.Add(memory);
        return memory;
    }

    public void Dispose()
    {
        foreach (nint memory in allocations) NativeMemory.Free((void*)memory);
        allocations.Clear();
    }
}
