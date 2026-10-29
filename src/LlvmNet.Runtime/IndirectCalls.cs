using System.Collections.Concurrent;

namespace LlvmNet.Runtime;

/// <summary>
/// Debug support for --check-indirect-calls (BE-18). The CLR dispatches calli by the call site's CIL
/// signature without checking the target, so a function called through a pointer of an incompatible
/// type is undefined behavior. Images compiled with the option record each address-taken function's CIL
/// signature and verify it at every indirect call. Pointers that were never registered (native functions,
/// runtime callbacks) are not checked.
/// </summary>
public static class IndirectCalls
{
    private static readonly ConcurrentDictionary<nint, (string Signature, string Name)> targets = new();

    public static void Register(nint target, string signature, string name) => targets.TryAdd(target, (signature, name));

    public static nint Check(nint target, string signature)
    {
        if (targets.TryGetValue(target, out var registered) && registered.Signature != signature)
            throw new InvalidProgramException(
                $"Indirect call with CIL signature {signature} reached {registered.Name}, whose signature is {registered.Signature}; " +
                "the C types differ in a way the CLR cannot call safely (see docs/dev/calling-convention.md).");
        return target;
    }
}
