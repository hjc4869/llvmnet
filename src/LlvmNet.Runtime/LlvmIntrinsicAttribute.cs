namespace LlvmNet.Runtime;

/// <summary>
/// Binds a runtime method to the LLVM target intrinsic of this name (ADR-16 Level 2): the backend translates each call of
/// the intrinsic into a call of the method, reinterpreting vector operands and the result as vectors of the same width.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class LlvmIntrinsicAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}
