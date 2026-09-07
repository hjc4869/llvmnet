namespace LlvmNet.Runtime;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class CExportAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}