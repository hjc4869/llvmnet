using LlvmNet;
using LlvmNet.Runtime;

// Exit codes: 0 success, 1 compilation error (including unsupported constructs) or a frontend failure, 2 invalid usage,
// 3 internal compiler error. Messages follow Clang's form ("file:line:col: error: ..." or "llvmnet: error: ...", then
// notes) and its colors, and a stack trace is printed only with LLVMNET_TRACE=1 (docs/user/driver.md).
bool trace = Environment.GetEnvironmentVariable("LLVMNET_TRACE") == "1";
var diagnostics = new Diagnostics(args);
try
{
    return Driver.Run(args);
}
catch (CompilationError error)
{
    diagnostics.Error(error.Location, error.Message);
    if (error.Function is not null)
        diagnostics.Note(null, $"in function '{error.Function}'{(error.Location is null ? " (compile with -g or -gline-tables-only for source locations)" : "")}");
    if (error.Global is not null)
        diagnostics.Note(null, $"in the initializer of global '{error.Global}'");
    if (error.Instruction is not null)
        diagnostics.Note(null, $"while translating: {error.Instruction}");
    if (error.KeptIr is not null)
        diagnostics.Note(null, $"linked LLVM IR kept at {error.KeptIr}");
    if (error.Internal)
        diagnostics.Note(null, $"internal compiler error ({error.InnerException?.GetType().Name}) in llvmnet {BuildInfo.Version}; please report it with the kept IR. Set LLVMNET_TRACE=1 for a stack trace.");
    if (trace) Console.Error.WriteLine(error.InnerException ?? error);
    return error.Internal ? 3 : 1;
}
catch (ArgumentException error)
{
    diagnostics.Error(null, error.Message);
    if (trace) Console.Error.WriteLine(error);
    return 2;
}
catch (Exception error)
{
    diagnostics.Error(null, error.Message);
    if (trace) Console.Error.WriteLine(error);
    return 1;
}
