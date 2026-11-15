using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace LlvmNet.Runtime;

/// <summary>
/// CPU detection without assembly (ADR-16, SV-12). Portable code's CPUID and XGETBV inline assembly (the forms system
/// mode runs in native thunks, FunctionEmitter.CpuQuery) comes here: CPUID is the host's own through
/// X86Base.CpuId on x64 and reports nothing elsewhere, so a library's x86 detection selects its x86 paths exactly where
/// they run as x86 instructions; XCR0 is the host's supported state with the AVX and AVX-512 components only where .NET
/// uses them. &lt;llvmnet/cpu.h&gt; reads the same view from the IsSupported properties.
/// </summary>
public static unsafe class CpuFeatures
{
    /// <summary>CPUID: EAX, EBX, ECX and EDX of the leaf and subleaf at <paramref name="registers"/>; zeros on a host without
    /// x86 or with .NET's hardware intrinsics off, where leaf 0's maximum leaf of 0 tells a library there is nothing to ask.</summary>
    public static void CpuId(nint registers, int leaf, int subleaf)
    {
        int* result = (int*)registers;
        if (!X86Base.IsSupported)
        {
            result[0] = result[1] = result[2] = result[3] = 0;
            return;
        }
        (result[0], result[1], result[2], result[3]) = X86Base.CpuId(leaf, subleaf);
    }

    /// <summary>XGETBV: the extended control register as EAX and EDX at <paramref name="registers"/>.</summary>
    public static void XGetBv(nint registers, int index)
    {
        ulong value = index == 0 ? ExtendedControlRegister() : 0;
        ((uint*)registers)[0] = (uint)value;
        ((uint*)registers)[1] = (uint)(value >> 32);
    }

    /// <summary>
    /// XCR0, which .NET does not expose: the state components the processor supports (CPUID leaf 0xD, subleaf 0), which an
    /// operating system enables in full, without AVX's (bit 2) where .NET does not use AVX and without AVX-512's (bits 5
    /// to 7) where it does not use AVX-512, so that DOTNET_EnableAVX=0 reads as an operating system without AVX state.
    /// </summary>
    private static ulong ExtendedControlRegister()
    {
        if (!X86Base.IsSupported) return 0;
        (int maximum, _, _, _) = X86Base.CpuId(0, 0);
        ulong supported = 3;
        if (maximum >= 0xD)
        {
            (int low, _, _, int high) = X86Base.CpuId(0xD, 0);
            supported = (uint)low | (ulong)(uint)high << 32 | 3;
        }
        if (!Avx.IsSupported) supported &= ~0x4ul;
        if (!Avx512F.IsSupported) supported &= ~0xe0ul;
        return supported;
    }

    /// <summary>
    /// uint64_t llvmnet_cpu_features(void) (&lt;llvmnet/cpu.h&gt;): the instruction sets the host runs as .NET reports them,
    /// one bit each, x86's in the low half and ARM64's in the high half; 0 with hardware intrinsics off.
    /// </summary>
    [CExport("llvmnet_cpu_features")]
    public static long Features()
    {
        ulong bits = 0;
        void Add(bool supported, int bit) { if (supported) bits |= 1ul << bit; }
        Add(Sse2.IsSupported, 0);
        Add(Sse3.IsSupported, 1);
        Add(Ssse3.IsSupported, 2);
        Add(Sse41.IsSupported, 3);
        Add(Sse42.IsSupported, 4);
        Add(Popcnt.IsSupported, 5);
        Add(System.Runtime.Intrinsics.X86.Pclmulqdq.IsSupported, 6);
        Add(System.Runtime.Intrinsics.X86.Aes.IsSupported, 7);
        Add(Avx.IsSupported, 8);
        Add(Avx2.IsSupported, 9);
        Add(Fma.IsSupported, 10);
        Add(Bmi1.IsSupported, 11);
        Add(Bmi2.IsSupported, 12);
        Add(Lzcnt.IsSupported, 13);
        Add(Avx512F.IsSupported, 14);
        Add(Avx512BW.IsSupported, 15);
        Add(AdvSimd.IsSupported, 32);
        Add(System.Runtime.Intrinsics.Arm.Aes.IsSupported, 33);
        Add(Crc32.IsSupported, 34);
        Add(Dp.IsSupported, 35);
        Add(Rdm.IsSupported, 36);
        Add(Sha1.IsSupported, 37);
        Add(Sha256.IsSupported, 38);
#pragma warning disable SYSLIB5003 // Sve is experimental in .NET 10; its IsSupported is all this reads.
        Add(Sve.IsSupported, 39);
        return (long)bits;
    }

    /// <summary>
    /// The AT_HWCAP of Linux on this host for the auxiliary vector portable programs start with: on x64 the kernel's own
    /// on Linux and CPUID leaf 1's EDX elsewhere, as Linux reports it, and the HWCAP_* bits of the instruction sets .NET reports on ARM64 (FP, ASIMD, AES,
    /// PMULL, SHA1, SHA2, CRC32, ASIMDRDM, ASIMDDP, SVE); 0 elsewhere.
    /// </summary>
    internal static nint HardwareCapabilities()
    {
        if (X86Base.IsSupported) return KernelCapabilities is { } kernel ? kernel.Hwcap : (nint)(uint)X86Base.CpuId(1, 0).Edx;
        ulong bits = 0;
        void Add(bool supported, ulong mask) { if (supported) bits |= mask; }
        Add(AdvSimd.IsSupported, 0x3);
        Add(System.Runtime.Intrinsics.Arm.Aes.IsSupported, 0x18);
        Add(Sha1.IsSupported, 0x20);
        Add(Sha256.IsSupported, 0x40);
        Add(Crc32.IsSupported, 0x80);
        Add(Rdm.IsSupported, 0x1000);
        Add(Dp.IsSupported, 0x100000);
        Add(Sve.IsSupported, 0x400000);
#pragma warning restore SYSLIB5003
        return (nint)bits;
    }

    /// <summary>The AT_HWCAP2 of Linux on this host: the kernel's on Linux, else HWCAP2_FSGSBASE (bit 1) on x64 processors
    /// with FSGSBASE, which Linux enables since 5.9; 0 elsewhere.</summary>
    internal static nint HardwareCapabilities2()
    {
        if (!X86Base.IsSupported) return 0;
        if (KernelCapabilities is { } kernel) return kernel.Hwcap2;
        if (X86Base.CpuId(0, 0).Eax < 7) return 0;
        return (X86Base.CpuId(7, 0).Ebx & 1) != 0 ? 2 : 0;
    }

    /// <summary>
    /// On Linux, the AT_HWCAP and AT_HWCAP2 the kernel gave this process (/proc/self/auxv), which native processes get
    /// too: a kernel or hypervisor can clear bits that CPUID still reports (a GitHub runner's AT_HWCAP differs from its
    /// CPUID leaf 1). Null elsewhere or when the file cannot be read, where CPUID stands in.
    /// </summary>
    private static readonly (nint Hwcap, nint Hwcap2)? KernelCapabilities = ReadKernelCapabilities();

    private static (nint, nint)? ReadKernelCapabilities()
    {
        if (!OperatingSystem.IsLinux() || IntPtr.Size != 8) return null;
        try
        {
            byte[] vector = File.ReadAllBytes("/proc/self/auxv");
            nint hwcap = 0, hwcap2 = 0;
            bool found = false;
            for (int offset = 0; offset + 16 <= vector.Length; offset += 16)
            {
                long key = BitConverter.ToInt64(vector, offset), value = BitConverter.ToInt64(vector, offset + 8);
                if (key == 0) break;
                if (key == 16) { hwcap = (nint)value; found = true; }
                else if (key == 26) hwcap2 = (nint)value;
            }
            return found ? (hwcap, hwcap2) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }
}
