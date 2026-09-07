using System.Reflection;
using System.Reflection.Emit;
using LlvmNet.Runtime;

namespace LlvmNet;

internal sealed class TypeSystem : IDisposable
{
    private readonly nint layout;
    private readonly ModuleBuilder module;
    private readonly Dictionary<nint, Type> types = [];

    internal TypeSystem(nint llvmModule, ModuleBuilder module)
    {
        this.module = module;
        layout = Llvm.LLVMCreateTargetData(Llvm.LLVMGetDataLayoutStr(llvmModule));
        if (Llvm.LLVMPointerSize(layout) != 8 || Llvm.LLVMByteOrder(layout) != 1 || !BitConverter.IsLittleEndian || IntPtr.Size != 8)
            throw new NotSupportedException("Only little-endian 64-bit LLVM modules and .NET hosts are supported.");
    }

    internal long Size(nint type) => checked((long)Llvm.LLVMABISizeOfType(layout, type));
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
            10 or 11 or 13 => Aggregate(type),
            _ => throw new NotSupportedException($"Unsupported LLVM type: {Llvm.PrintType(type)}")
        };
        types[type] = result;
        return result;
    }

    internal unsafe Type[] Parameters(nint functionType)
    {
        nint[] parameters = new nint[Llvm.LLVMCountParamTypes(functionType)];
        fixed (nint* pointer = parameters)
            Llvm.LLVMGetParamTypes(functionType, pointer);
        Type[] result = parameters.Select(Map).ToArray();
        return Llvm.LLVMIsFunctionVarArg(functionType) != 0 ? [.. result, typeof(nint)] : result;
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

    private Type Aggregate(nint type)
    {
        TypeBuilder aggregate = module.DefineType($"__Layout{types.Count}",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.ExplicitLayout,
            typeof(ValueType), PackingSize.Size1, checked((int)Math.Max(Size(type), 1)));
        aggregate.DefineField("Data", typeof(byte), FieldAttributes.Public).SetOffset(0);
        return aggregate.CreateType()!;
    }

    public void Dispose() => Llvm.LLVMDisposeTargetData(layout);
}