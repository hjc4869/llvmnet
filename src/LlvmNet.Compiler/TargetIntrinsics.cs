using System.Reflection;
using LlvmNet.Runtime;

namespace LlvmNet;

/// <summary>
/// The target intrinsics the runtime implements (ADR-16 Level 2), by LLVM name: the methods of <see cref="X86"/> marked
/// with <see cref="LlvmIntrinsicAttribute"/>, and of <see cref="Vectors"/> for llvmnet's own operations the vector idioms
/// write (llvm.llvmnet.*, SV-35). A call of one becomes a call of its method (FunctionEmitter), which the JIT and ILC
/// inline.
/// </summary>
internal static class TargetIntrinsics
{
    private static readonly Dictionary<string, MethodInfo> Helpers = new[] { typeof(X86), typeof(Vectors) }
        .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static))
        .SelectMany(method => method.GetCustomAttributes<LlvmIntrinsicAttribute>().Select(attribute => (attribute.Name, Method: method)))
        .ToDictionary(pair => pair.Name, pair => pair.Method, StringComparer.Ordinal);

    internal static MethodInfo? Helper(string name) => Helpers.GetValueOrDefault(name);

    /// <summary>Every intrinsic name with a helper, for the conformance test's coverage check.</summary>
    internal static IEnumerable<string> Names => Helpers.Keys;
}
