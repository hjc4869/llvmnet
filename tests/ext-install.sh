#!/usr/bin/env bash
# tags: core aot
# requires: python3
# NX-12: a third-party extension built from templates/native-extension/ (its own header, host library and example
# program) with scripts/build-extensions.sh, which installs it with llvmnet --install-extension; the example prints what
# it prints natively under the JIT, as NativeAOT and in system mode, and reports a missing library through
# <llvmnet/ext.h>. --install-extension refuses a directory without a manifest, an assembly built for an incompatible
# llvmnet, an assembly that describes another version, and names that clash with the sysroot (keeping the installed
# version), and replaces an installed version in one step; --uninstall-extension removes one. tests/toolchain.sh builds the same template with the
# kit that the toolchain package ships.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
rm -rf "$test_output"
mkdir -p "$test_output"
template="$root/templates/native-extension"
expected="$template/example/expected.txt"
native="$test_output/native"
mkdir -p "$native"
clang-22 -O2 -shared -fPIC -I"$template/include" "$template/native/hello.c" -o "$native/libllvmnet-hello.so"
clang-22 -O1 -I"$template/include" "$template/example/main.c" -L"$native" -l:libllvmnet-hello.so -o "$test_output/hello-native"
LD_LIBRARY_PATH="$native" "$test_output/hello-native" | diff -u "$expected" -

toolchain="$test_output/toolchain"
extension_root "$toolchain"
sysroot="$toolchain/sysroot/dotnet64-v2"
bash "$root/scripts/build-extensions.sh" --sysroot "$sysroot" --build-dir "$test_output/ext-build" "$template" 2>"$test_output/build.log" ||
    { cat "$test_output/build.log" >&2; exit 1; }
grep -q 'hello 1.0.0: 4 entry points, 0 that fail when called, 0 left out, 1 callback types with reverse thunks' "$test_output/build.log" ||
    { cat "$test_output/build.log" >&2; exit 1; }
grep -q 'verified 4 stubs against the C prototypes llvmnet compiled' "$test_output/build.log" || { cat "$test_output/build.log" >&2; exit 1; }
[[ -f "$sysroot/ext/hello/lib/LlvmNet.Ext.Hello.dll" && -f "$sysroot/ext/hello/lib/wrappers.o" ]]
export LLVMNET_ROOT="$toolchain"
"$llvmnet" -O1 "$template/example/main.c" -lllvmnet-hello -o "$test_output/jit/hello.dll"
LLVMNET_EXT_HELLO_DIR="$native" dotnet "$test_output/jit/hello.dll" | diff -u "$expected" -
mkdir -p "$test_output/aot"
"$llvmnet" -O1 --nativeaot "$template/example/main.c" -lllvmnet-hello -o "$test_output/aot/hello" >"$test_output/aot.log" 2>&1 ||
    { cat "$test_output/aot.log" >&2; exit 1; }
LLVMNET_EXT_HELLO_DIR="$native" "$test_output/aot/hello" | diff -u "$expected" -
# System mode links the host library itself and calls it with native aggregates, as a native build does.
LIBRARY_PATH="$native" LD_LIBRARY_PATH="$native" "$llvmnet" --runtime=system -O1 -I"$template/include" "$template/example/main.c" --extension=hello \
    -o "$test_output/system/hello.dll"
LD_LIBRARY_PATH="$native" dotnet "$test_output/system/hello.dll" | diff -u "$expected" -
status=0
dotnet "$test_output/jit/hello.dll" >"$test_output/missing.out" 2>&1 || status=$?
[[ "$status" -eq 1 ]] && grep -q 'the hello library cannot be used here:' "$test_output/missing.out" &&
    grep -q 'libllvmnet-hello.so' "$test_output/missing.out" || { cat "$test_output/missing.out" >&2; exit 1; }
printf 'PASS: the template extension builds, installs and prints what its native build prints (JIT, NativeAOT, system mode)\n'

refuse() {
    local expected_status="$1" message="$2" status=0
    shift 2
    "$llvmnet" "$@" >"$test_output/refusal.out" 2>&1 || status=$?
    [[ "$status" -eq "$expected_status" ]] && grep -qF "$message" "$test_output/refusal.out" ||
        { printf 'expected exit %s and "%s" from llvmnet %s, got exit %s:\n' "$expected_status" "$message" "$*" "$status" >&2; cat "$test_output/refusal.out" >&2; exit 1; }
}
package="$test_output/ext-build/hello/package"
"$llvmnet" --install-extension "$package" | grep -q '^Installed native extension hello 1.0.0 in .*, replacing the installed one\.$'
refuse 2 'is not a native extension package (no manifest.json)' --install-extension "$test_output/native"
# An assembly that describes another version of the extension than its manifest.
cp -a "$package" "$test_output/renamed"
sed -i 's/"version": "1.0.0"/"version": "2.0.0"/' "$test_output/renamed/manifest.json"
refuse 1 "its assembly declares llvmnet.extension hello/1.0.0, but its manifest describes hello/2.0.0" --install-extension "$test_output/renamed"
# An assembly built for an llvmnet whose images this one does not load.
python3 -I "$root/scripts/ext-gen.py" generate "$template/extension.json" --output "$test_output/old/generated" --llvmnet-version 0.1.0 \
    --compiler "$llvmnet" >"$test_output/old.log"
dotnet build "$test_output/old/generated/project/LlvmNet.Ext.Hello.csproj" -c Release --nologo -v quiet -clp:ErrorsOnly \
    -p:LlvmNetRuntime="$root/src/LlvmNet.Runtime/bin/Release/net10.0/LlvmNet.Runtime.dll" --artifacts-path "$test_output/old/msbuild" \
    -o "$test_output/old/bin" >"$test_output/old-build.log" 2>&1 || { cat "$test_output/old-build.log" >&2; exit 1; }
cp -a "$test_output/old/generated/package" "$test_output/old/package"
mkdir -p "$test_output/old/package/lib"
cp "$test_output/old/bin/LlvmNet.Ext.Hello.dll" "$test_output/old/package/lib/"
refuse 1 'its assembly was built for llvmnet 0.1.0, which is incompatible with' --install-extension "$test_output/old/package"
# A new version whose names clash with the sysroot (a header that would shadow <stdio.h>, -lm) is refused, and the
# installed version stays.
cp -a "$package" "$test_output/clash"
python3 -I - "$test_output/clash/manifest.json" <<'PYTHON'
import json, sys
manifest = json.load(open(sys.argv[1]))
manifest["link"].append("m")
json.dump(manifest, open(sys.argv[1], "w"))
PYTHON
printf '/* shadows the C library */\n' >"$test_output/clash/include/stdio.h"
refuse 1 "native extension 'hello' cannot be installed: its header stdio.h clashes with the sysroot's stdio.h; -lm clashes with the sysroot's library of that name." \
    --install-extension "$test_output/clash"
cmp -s "$package/manifest.json" "$sysroot/ext/hello/manifest.json" && [[ ! -e "$sysroot/ext/hello/include/stdio.h" ]]
[[ "$(ls "$sysroot/ext")" == hello ]] || { ls -a "$sysroot/ext" >&2; exit 1; }
"$llvmnet" --list-extensions | grep -q '^hello 1.0.0$'
"$llvmnet" --uninstall-extension hello | grep -q '^Removed native extension hello from '
refuse 2 "native extension 'hello' is not installed in" --uninstall-extension hello
"$llvmnet" --list-extensions | grep -q '^No native extensions are installed in '
printf 'PASS: --install-extension checks packages and replaces in one step; --uninstall-extension removes them\n'
