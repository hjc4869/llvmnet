using System.Runtime.InteropServices;

namespace LlvmNet.Runtime;

public static unsafe class FortranTemporaryStack
{
    private static List<byte[]> Get(nint handle) => (List<byte[]>)GCHandle.FromIntPtr(handle).Target!;

    [CExport("_FortranACreateDescriptorStack")]
    public static nint CreateDescriptors(nint source, int line) => GCHandle.ToIntPtr(GCHandle.Alloc(new List<byte[]>()));

    [CExport("_FortranAPushDescriptor")]
    public static void PushDescriptor(nint handle, nint descriptor)
    {
        int size = new FortranDescriptor(descriptor).StorageBytes;
        Get(handle).Add(new ReadOnlySpan<byte>((void*)descriptor, size).ToArray());
    }

    [CExport("_FortranAPopDescriptor")]
    public static void PopDescriptor(nint handle, nint result)
    {
        List<byte[]> entries = Get(handle);
        if (entries.Count == 0) throw new InvalidOperationException("Cannot pop an empty Fortran descriptor stack.");
        byte[] descriptor = entries[^1];
        descriptor.CopyTo(new Span<byte>((void*)result, descriptor.Length));
        entries.RemoveAt(entries.Count - 1);
    }

    [CExport("_FortranADescriptorAt")]
    public static void DescriptorAt(nint handle, long index, nint result)
    {
        List<byte[]> entries = Get(handle);
        if (index < 0 || index >= entries.Count) throw new IndexOutOfRangeException("Fortran descriptor stack index.");
        byte[] descriptor = entries[(int)index];
        descriptor.CopyTo(new Span<byte>((void*)result, descriptor.Length));
    }

    [CExport("_FortranADestroyDescriptorStack")]
    public static void DestroyDescriptors(nint handle) => GCHandle.FromIntPtr(handle).Free();
}