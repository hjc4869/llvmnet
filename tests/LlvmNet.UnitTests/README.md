# LlvmNet.UnitTests

Fast tests for managed components that do not need a frontend: build/version identity, `Float80`, wide integers, printf formatting over the software SysV `va_list`, numeric parsing, response-file handling and driver option validation.

```sh
dotnet test tests/LlvmNet.UnitTests -c Release
```

Formatting expectations were produced by compiling the same format strings with `clang-22` against glibc; `Float80` expectations are standard x87 extended-precision encodings or exactly representable results. When adding a case, take the expected value from a native oracle, not from llvmnet.
