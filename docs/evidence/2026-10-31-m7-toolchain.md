# Evidence: M7 toolchain and distribution (2026-10-31)

Build host: Linux x86-64 (Debian 13), .NET SDK 10.0.403, LLVM/Clang/Flang 22.1.8. Every number below was produced by a command in this repository, named with it. One section per M7 item, in the order the items were closed.

## SA-01: `--native-import` removed

The explicit scalar P/Invoke path of system mode is gone: the `NativeImport` record, the import table in `CompilerOptions`, the `DefinePInvokeMethod` emission in `CilCompiler.ResolveFunction`, the two fixtures `tests/c/native-import.c` and `tests/c/native-aggregate.c`, and the `tests/link.sh` and `tests/modes.sh` cases that exercised them. `--system-library` is the one native path; `tests/system-abi.sh` covers scalar, aggregate, variadic and callback signatures through it.

```
$ bin/llvmnet --runtime=system tests/c/smoke.c --native-import 'abs=libc.so.6!abs' -o x.dll
llvmnet: error: --native-import was removed (ADR-04, SA-01): --runtime=system resolves every native function through generated bridges; add the library with --system-library <shared-library>.
(exit status 2)
```

`grep -rn 'native-import\|NativeImport' src tests` finds only the rejection and its two tests (`tests/modes.sh`, `tests/LlvmNet.UnitTests/DriverTests.cs`). Gates after the change: `tests/run.sh --tag core --tag toolchain --exclude-tag slow -j 8` 83 passed, 0 failed in 507 s; `dotnet test tests/LlvmNet.UnitTests -c Release` 86 passed; `dotnet format whitespace llvmnet.slnx --verify-no-changes` and `shellcheck -S error` clean.
