using LlvmNet;
using LlvmNet.Runtime;

// Exit codes: 0 success, 1 compilation error (including unsupported constructs), 2 invalid usage, 3 internal compiler error.
bool trace = Environment.GetEnvironmentVariable("LLVMNET_TRACE") == "1";
try
{
    return Driver.Run(args);
}
catch (CompilationError error)
{
    Console.Error.WriteLine($"{error.Location ?? "llvmnet"}: error: {error.Message}");
    if (error.Function is not null)
        Console.Error.WriteLine($"llvmnet: note: in function '{error.Function}'{(error.Location is null ? " (compile with -g or -gline-tables-only for source locations)" : "")}");
    if (error.Global is not null)
        Console.Error.WriteLine($"llvmnet: note: in the initializer of global '{error.Global}'");
    if (error.Instruction is not null)
        Console.Error.WriteLine($"llvmnet: note: while translating: {error.Instruction}");
    if (error.KeptIr is not null)
        Console.Error.WriteLine($"llvmnet: note: linked LLVM IR kept at {error.KeptIr}");
    if (error.Internal)
        Console.Error.WriteLine($"llvmnet: internal compiler error ({error.InnerException?.GetType().Name}) in llvmnet {BuildInfo.Version}; please report it with the kept IR. Set LLVMNET_TRACE=1 for a stack trace.");
    if (trace) Console.Error.WriteLine(error.InnerException ?? error);
    return error.Internal ? 3 : 1;
}
catch (ArgumentException error)
{
    Console.Error.WriteLine($"llvmnet: error: {error.Message}");
    if (trace) Console.Error.WriteLine(error);
    return 2;
}
catch (Exception error)
{
    Console.Error.WriteLine($"llvmnet: error: {error.Message}");
    if (trace) Console.Error.WriteLine(error);
    return 1;
}
