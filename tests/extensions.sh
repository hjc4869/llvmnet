#!/usr/bin/env bash
# tags: core aot
# Native extensions of the portable ABI (ADR-15).
# NX-02: a program registers a synthetic extension whose stubs are managed (tests/extensions/registry) and calls dlopen,
# dlsym, dlclose, dlerror and dladdr through musl's port and the host entry points, under the JIT and as NativeAOT;
# tests/extensions/registry.expected is the output.
# NX-03: the synthetic extension package tests/extensions/synthetic (manifest, header, assembly) is installed in a
# toolchain root of its own, in front of a native test library built with clang; tests/extensions/synthetic.c, linked
# with --extension or -l, must print what the native build prints, under the JIT, as NativeAOT and in system mode, and
# the image lists the extension in its metadata.
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

native="$test_output/native"
mkdir -p "$native"
clang-22 -shared -fPIC -O1 -I"$fixtures/synthetic/include" "$fixtures/synthetic/native.c" -Wl,-soname,libllvmnet-synthetic.so.1 \
    -o "$native/libllvmnet-synthetic.so.1"
clang-22 -O1 -I"$fixtures/synthetic/include" "$fixtures/synthetic.c" -L"$native" -l:libllvmnet-synthetic.so.1 -o "$test_output/synthetic-native"
LD_LIBRARY_PATH="$native" "$test_output/synthetic-native" >"$test_output/native.txt"
toolchain="$test_output/toolchain"
extension_root "$toolchain"
package="$toolchain/sysroot/dotnet64-v2/ext/synthetic"
mkdir -p "$package/lib"
cp -r "$fixtures/synthetic/manifest.json" "$fixtures/synthetic/include" "$package/"
build_assembly "$fixtures/synthetic/LlvmNet.Ext.Synthetic.csproj" "$test_output/synthetic-build"
cp "$test_output/synthetic-build/LlvmNet.Ext.Synthetic.dll" "$package/lib/"
export LLVMNET_ROOT="$toolchain"
"$llvmnet" --list-extensions >"$test_output/list.txt"
grep -q '^synthetic 1.0.0$' "$test_output/list.txt"
! grep -q 'problem' "$test_output/list.txt" || { cat "$test_output/list.txt" >&2; exit 1; }
# The extension's header is on the portable include path, so compiling needs no option; linking names the extension.
"$llvmnet" -O1 -c "$fixtures/synthetic.c" -o "$test_output/synthetic.o"
"$llvmnet" "$test_output/synthetic.o" --extension=synthetic -o "$test_output/jit/synthetic.dll"
test -f "$test_output/jit/LlvmNet.Ext.Synthetic.dll"
LLVMNET_EXT_SYNTHETIC_DIR="$native" dotnet "$test_output/jit/synthetic.dll" | diff -u "$test_output/native.txt" -
dotnet "$checks" inspect "$test_output/jit/synthetic.dll" >"$test_output/inspect.txt"
grep -qx 'llvmnet.extensions: synthetic/1.0.0' "$test_output/inspect.txt" || { cat "$test_output/inspect.txt" >&2; exit 1; }
dotnet "$test_output/jit/synthetic.dll" probe >"$test_output/probe.txt"
grep -qx "dlopen: synthetic: cannot load the host library (tried libllvmnet-synthetic.so.1)" "$test_output/probe.txt" ||
    { cat "$test_output/probe.txt" >&2; exit 1; }
"$llvmnet" "$test_output/synthetic.o" -lllvmnet-synthetic -o "$test_output/lname/synthetic.dll"
LLVMNET_EXT_SYNTHETIC_DIR="$native" dotnet "$test_output/lname/synthetic.dll" | diff -u "$test_output/native.txt" -
"$llvmnet" "$test_output/synthetic.o" --extension synthetic --nativeaot -o "$test_output/synthetic-aot" >"$test_output/synthetic-aot.log" 2>&1 ||
    { cat "$test_output/synthetic-aot.log" >&2; exit 1; }
LLVMNET_EXT_SYNTHETIC_DIR="$native" "$test_output/synthetic-aot" | diff -u "$test_output/native.txt" -
# System mode links the manifest's Linux library through a generated bridge; the headers are the program's business.
LIBRARY_PATH="$native" LD_LIBRARY_PATH="$native" "$llvmnet" --runtime=system -O1 -I"$fixtures/synthetic/include" \
    "$fixtures/synthetic.c" --extension=synthetic -o "$test_output/system/synthetic.dll"
LD_LIBRARY_PATH="$native" dotnet "$test_output/system/synthetic.dll" | diff -u "$test_output/native.txt" -
printf 'PASS: the synthetic extension links with --extension and -l, prints what native prints under the JIT, NativeAOT and system mode, and is listed in the image\n'
