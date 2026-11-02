#!/usr/bin/env bash
# tags: core aot
# Native extensions of the portable ABI (ADR-15). NX-02: a program registers a synthetic extension whose stubs are
# managed (tests/extensions/registry) and calls dlopen, dlsym, dlclose, dlerror and dladdr through musl's port and
# the host entry points, under the JIT and as NativeAOT; tests/extensions/registry.expected is the output.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
rm -rf "$test_output"
mkdir -p "$test_output"
fixtures="$root/tests/extensions"
runtime_dll="$root/src/LlvmNet.Runtime/bin/Release/net10.0/LlvmNet.Runtime.dll"
build_assembly() { # project, output directory
    dotnet build "$1" -c Release --nologo -v quiet -clp:ErrorsOnly -p:LlvmNetRuntime="$runtime_dll" \
        --artifacts-path "$test_output/msbuild" -o "$2" >"$2.log" 2>&1 || { cat "$2.log" >&2; return 1; }
}
build_assembly "$fixtures/registry/LlvmNet.Ext.Registry.csproj" "$test_output/registry"
registry="$test_output/registry/LlvmNet.Ext.Registry.dll"
"$llvmnet" -O1 "$fixtures/registry.c" --cil-reference "$registry" -o "$test_output/registry.dll"
dotnet "$test_output/registry.dll" | diff -u "$fixtures/registry.expected" -
"$llvmnet" -O1 --nativeaot "$fixtures/registry.c" --cil-reference "$registry" -o "$test_output/registry-aot" >"$test_output/aot.log" 2>&1 ||
    { cat "$test_output/aot.log" >&2; exit 1; }
"$test_output/registry-aot" | diff -u "$fixtures/registry.expected" -
printf 'PASS: dlopen, dlsym, dlclose, dlerror and dladdr serve a registered extension and report its errors (JIT and NativeAOT)\n'
