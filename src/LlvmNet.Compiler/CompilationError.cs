namespace LlvmNet;

/// <summary>
/// A failure while translating a specific piece of LLVM IR. <see cref="Internal"/> separates compiler
/// bugs (unexpected exceptions) from documented unsupported constructs, which are user-facing errors.
/// </summary>
internal sealed class CompilationError(string message, Exception? inner = null) : Exception(message, inner)
{
    internal string? Function { get; init; }
    internal string? Global { get; init; }
    internal string? Instruction { get; init; }
    internal string? Location { get; init; }
    internal bool Internal { get; init; }
    internal string? KeptIr { get; set; }

    internal static bool IsUserFacing(Exception error) =>
        error is NotSupportedException or PlatformNotSupportedException or DllNotFoundException or ArgumentException;

    /// <summary>Wraps an exception raised while lowering <paramref name="instruction"/> in <paramref name="function"/>.</summary>
    internal static CompilationError InFunction(Exception error, nint function, nint instruction = 0) => error as CompilationError ??
        new CompilationError(error.Message, error)
        {
            Function = Llvm.Demangle(Llvm.Name(function)),
            Instruction = instruction == 0 ? null : Llvm.Print(instruction).Trim(),
            Location = (instruction == 0 ? null : Llvm.DebugLocation(instruction)) ?? Llvm.DebugLocation(function),
            Internal = !IsUserFacing(error)
        };

    internal static CompilationError InGlobal(Exception error, nint global) => error as CompilationError ??
        new CompilationError(error.Message, error)
        {
            Global = Llvm.Demangle(Llvm.Name(global)),
            Location = Llvm.DebugLocation(global),
            Internal = !IsUserFacing(error)
        };
}
