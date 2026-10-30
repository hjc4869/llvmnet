# LlvmNet.UnitTests

Fast tests for managed components that do not need a frontend: build and version compatibility, `Float80`, wide integers, response-file parsing and driver option validation.

```sh
dotnet test tests/LlvmNet.UnitTests -c Release
```

`Float80` expectations are standard x87 extended-precision encodings or exactly representable results. When adding a case, take the expected value from a native oracle, not from llvmnet.
