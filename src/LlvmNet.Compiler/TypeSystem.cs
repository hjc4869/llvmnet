using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Intrinsics;
using LlvmNet.Runtime;

namespace LlvmNet;

/// <summary>
/// How a fixed-length vector of 8- to 64-bit integers, pointers, float, double or i1 is held in CIL by the typed
/// vector lowering (ADR-16 Level 1): as fragments of <see cref="Vector128{T}"/> (or <see cref="Vector256{T}"/> with
/// --vector-width=256), one value when the vector fits one fragment (the unused upper lanes of a shorter one are
/// undefined) and otherwise a struct with one field per fragment. Integer lanes are signed and pointers are 64-bit integers; operations reinterpret them where they need
/// another view. An i1 vector is a mask: each lane all ones or zero, as wide as its lane count gives (the fragment's
/// bits over the lane count rounded up to a power of two, from 8 to 64 bits), whatever comparison produced it.
/// </summary>
internal sealed record VectorShape(int Lanes, Type Lane, int LaneBits, bool Mask, Type Fragment, Type Cil, FieldInfo[] Fields, int Bits)
{
    internal int PerFragment => Bits / LaneBits;
    internal int Fragments => Math.Max(1, Fields.Length);
}

internal sealed class TypeSystem : IDisposable
{
    private readonly nint layout;
    private readonly ModuleBuilder module;
    private readonly bool simd128;
    private readonly bool typedVectors;
    private readonly Dictionary<nint, Type> types = [];
    private readonly Dictionary<nint, VectorShape?> vectors = [];
    private readonly Dictionary<nint, Type> returns = [];
    private readonly Dictionary<(Type Lane, int Fragments), (Type Cil, FieldInfo[] Fields)> containers = [];

    internal TypeSystem(nint llvmModule, ModuleBuilder module, bool simd128, bool typedVectors, int vectorBits = 128)
    {
        this.module = module;
        this.simd128 = simd128;
        this.typedVectors = typedVectors;
        FragmentBits = vectorBits;
        layout = Llvm.LLVMCreateTargetData(Llvm.LLVMGetDataLayoutStr(llvmModule));
        if (Llvm.LLVMPointerSize(layout) != 8 || Llvm.LLVMByteOrder(layout) != 1 || !BitConverter.IsLittleEndian || IntPtr.Size != 8)
            throw new NotSupportedException("Only little-endian 64-bit LLVM modules and .NET hosts are supported.");
    }

    /// <summary>The bits of a vector fragment: 128, or 256 with --vector-width=256.</summary>
    internal int FragmentBits { get; }
    internal long Size(nint type) => checked((long)Llvm.LLVMABISizeOfType(layout, type));
    /// <summary>The bytes a load or store of the type touches, which for a vector of three lanes is less than its size.</summary>
    internal long StoreSize(nint type) => checked((long)Llvm.LLVMStoreSizeOfType(layout, type));
    internal int Alignment(nint type) => checked((int)Llvm.LLVMABIAlignmentOfType(layout, type));
    internal long Offset(nint type, uint index) => checked((long)Llvm.LLVMOffsetOfElement(layout, type, index));
    internal static int Kind(nint type) => Llvm.LLVMGetTypeKind(type);
    internal static int Width(nint type) => Kind(type) == 8 ? checked((int)Llvm.LLVMGetIntTypeWidth(type)) : 0;
    internal static bool IsAggregate(nint type) => Kind(type) is 10 or 11 or 13;

    internal Type Map(nint type)
    {
        if (types.TryGetValue(type, out Type? result))
            return result;
        result = Kind(type) switch
        {
            0 => typeof(void),
            2 => typeof(float),
            3 => typeof(double),
            4 => typeof(Float80),
            8 when Width(type) <= 32 => typeof(int),
            8 when Width(type) <= 64 => typeof(long),
            8 when Width(type) <= 128 => typeof(UInt128),
            12 => typeof(nint),
            13 when simd128 && Size(type) == 16 && VectorBits(type) == 128 => typeof(Vector128<byte>),
            13 when Vector(type) is VectorShape shape => shape.Cil,
            10 or 11 or 13 => Aggregate(type),
            _ => throw new NotSupportedException($"Unsupported LLVM type: {Llvm.PrintType(type)}")
        };
        types[type] = result;
        return result;
    }

    /// <summary>
    /// The CIL type a function returns a value of this LLVM type as. A typed vector of at most 16 bytes returns as its
    /// explicit-layout bytes, as before the typed lowering: .NET returns those in registers (RAX and RDX on x64), but a
    /// Vector128 through a hidden buffer, which NativeAOT does not pass on in a tail call through a function pointer,
    /// so a musttail chain of such calls would grow the stack. Wider vectors return through a buffer either way.
    /// </summary>
    internal Type Return(nint type)
    {
        if (Vector(type) is null || Size(type) > 16) return Map(type);
        if (!returns.TryGetValue(type, out Type? result)) returns[type] = result = Aggregate(type, $"__Return{returns.Count}");
        return result;
    }

    /// <summary>Whether <see cref="Return"/> differs from the value's own type, so a return converts it.</summary>
    internal bool ReturnsBytes(nint type) => Vector(type) is not null && Size(type) <= 16;

    internal unsafe Type[] Parameters(nint functionType)
    {
        nint[] parameters = new nint[Llvm.LLVMCountParamTypes(functionType)];
        fixed (nint* pointer = parameters)
            Llvm.LLVMGetParamTypes(functionType, pointer);
        Type[] result = parameters.Select(Map).ToArray();
        return Llvm.LLVMIsFunctionVarArg(functionType) != 0 ? [.. result, typeof(nint)] : result;
    }

    /// <summary>The typed representation of a vector type, or null when the lowering is off or no CIL vector has its lanes.</summary>
    internal VectorShape? Vector(nint type)
    {
        if (!typedVectors || Kind(type) != 13) return null;
        if (vectors.TryGetValue(type, out VectorShape? shape)) return shape;
        nint element = Llvm.LLVMGetElementType(type);
        int lanes = checked((int)Llvm.LLVMGetVectorSize(type));
        Type? lane = Kind(element) switch
        {
            2 => typeof(float),
            3 => typeof(double),
            12 => typeof(long),
            8 => Width(element) switch { 1 => MaskLane(lanes, FragmentBits), 8 => typeof(sbyte), 16 => typeof(short), 32 => typeof(int), 64 => typeof(long), _ => null },
            _ => null
        };
        shape = lane is null ? null : Shape(lanes, lane, Kind(element) == 8 && Width(element) == 1);
        vectors[type] = shape;
        return shape;
    }

    /// <summary>The shape of <paramref name="lanes"/> lanes of <paramref name="lane"/>, for values of no LLVM type of their own.</summary>
    internal VectorShape Shape(int lanes, Type lane, bool mask)
    {
        int bits = LaneBits(lane);
        int fragments = (lanes * bits + FragmentBits - 1) / FragmentBits;
        Type fragment = (FragmentBits == 256 ? typeof(Vector256<>) : typeof(Vector128<>)).MakeGenericType(lane);
        if (fragments == 1)
            return new VectorShape(lanes, lane, bits, mask, fragment, fragment, [], FragmentBits);
        if (!containers.TryGetValue((lane, fragments), out var container))
        {
            TypeBuilder builder = module.DefineType(FragmentBits == 256 ? $"__Vector256x{fragments}x{lane.Name}" : $"__Vector{fragments}x{lane.Name}",
                TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout, typeof(ValueType));
            FieldInfo[] fields = Enumerable.Range(0, fragments).Select(index => (FieldInfo)builder.DefineField($"F{index}", fragment, FieldAttributes.Public)).ToArray();
            containers[(lane, fragments)] = container = (builder.CreateType()!, fields);
        }
        return new VectorShape(lanes, lane, bits, mask, fragment, container.Cil, container.Fields, FragmentBits);
    }

    /// <summary>The lane of a mask of this many lanes: as wide as a fragment's bits shared among them, from 8 to 64 bits.</summary>
    internal static Type MaskLane(int lanes, int fragmentBits) => Math.Clamp(fragmentBits / (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)lanes), 8, 64) switch
    {
        8 => typeof(sbyte),
        16 => typeof(short),
        32 => typeof(int),
        _ => typeof(long)
    };

    internal static int LaneBits(Type lane) => System.Runtime.InteropServices.Marshal.SizeOf(lane) * 8;

    private static int VectorBits(nint type)
    {
        nint element = Llvm.LLVMGetElementType(type);
        int bits = Kind(element) switch { 2 => 32, 3 => 64, 8 => Width(element), _ => 0 };
        return checked(bits * (int)Llvm.LLVMGetVectorSize(type));
    }

    internal (nint Type, long Offset) Element(nint type, uint index)
    {
        if (Kind(type) == 10)
            return (Llvm.LLVMStructGetTypeAtIndex(type, index), Offset(type, index));
        nint element = Llvm.LLVMGetElementType(type);
        return (element, checked(Size(element) * index));
    }

    internal uint ElementCount(nint type) => Kind(type) switch
    {
        10 => Llvm.LLVMCountStructElementTypes(type),
        11 => checked((uint)Llvm.LLVMGetArrayLength2(type)),
        13 => Llvm.LLVMGetVectorSize(type),
        _ => throw new NotSupportedException($"Not an aggregate: {Llvm.PrintType(type)}")
    };

    private Type Aggregate(nint type, string? name = null)
    {
        TypeBuilder aggregate = module.DefineType(name ?? $"__Layout{types.Count}",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.ExplicitLayout,
            typeof(ValueType), PackingSize.Size1, checked((int)Math.Max(Size(type), 1)));
        aggregate.DefineField("Data", typeof(byte), FieldAttributes.Public).SetOffset(0);
        return aggregate.CreateType()!;
    }

    public void Dispose() => Llvm.LLVMDisposeTargetData(layout);
}
