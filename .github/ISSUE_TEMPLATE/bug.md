---
name: Bug report
about: A program compiles or runs differently under llvmnet than natively
labels: bug
---

## Summary

## Reproducer

<!-- Smallest C, C++, Fortran or LLVM IR input. Attach `--keep-ir` output if the input cannot be shared. -->

```sh
llvmnet --runtime=portable repro.c -o repro.dll
dotnet repro.dll
```

## Expected and actual behavior

<!-- Native clang/flang output versus llvmnet output, including exit codes. -->

## Environment

- `llvmnet --version`:
- `dotnet --info` runtime version:
- OS and architecture:
- Runtime mode and execution (JIT or NativeAOT):
