using System.Runtime.InteropServices;

namespace LlvmNet.Runtime;

[StructLayout(LayoutKind.Sequential)]
public unsafe struct VarArgs
{
    public uint GeneralOffset;
    public uint FloatingOffset;
    public nint Overflow;
    public nint Registers;

    public long Integer()
    {
        nint address;
        if (GeneralOffset <= 40)
        {
            address = Registers + (nint)GeneralOffset;
            GeneralOffset += 8;
        }
        else
        {
            address = Overflow;
            Overflow += 8;
        }
        return *(long*)address;
    }

    public double Floating()
    {
        nint address;
        if (FloatingOffset <= 160)
        {
            address = Registers + (nint)FloatingOffset;
            FloatingOffset += 16;
        }
        else
        {
            address = Overflow;
            Overflow += 8;
        }
        return *(double*)address;
    }
}