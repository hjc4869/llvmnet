#!/usr/bin/env bash
# tags: toolchain aot
# serial: 90
# timeout: 1800
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
output="$root/artifacts/tests/toolchain"
mkdir -p "$output"
bash "$root/scripts/package-toolchain.sh" "$output/install"
relocated="$output/relocated toolchain"
rm -rf "$relocated"
mkdir -p "$relocated"
cp -a "$output/install/." "$relocated/"
compiler="$relocated/bin/llvmnet-clang"
"$compiler" "$root/tests/c/core.c" -o "$output/normal executable"
"$output/normal executable"
"$compiler" -c "$root/tests/c/link-library.c" -o "$output/library.o"
"$relocated/bin/llvmnet-ar" rcs "$output/libexample.a" "$output/library.o"
"$compiler" "$root/tests/c/link-main.c" -L"$output" -lexample -o "$output/archive-executable"
"$output/archive-executable"
"$compiler" --nativeaot "$root/tests/c/core.c" -o "$output/aot-executable"
"$output/aot-executable"
printf 'PASS: relocated toolchain, executable outputs, archive utilities and self-contained NativeAOT\n'

# NX-12: the package ships the extensions of extensions/, <llvmnet/ext.h>, and the kit and template for building more;
# the template builds with the relocated toolchain's own kit, without the repository, and installs into its sysroot.
for file in sysroot/dotnet64-v2/ext/gles/manifest.json sysroot/dotnet64-v2/ext/vulkan/lib/LlvmNet.Ext.Vulkan.dll \
    sysroot/extensions/include/llvmnet/ext.h share/llvmnet/extensions/build-extensions.sh share/llvmnet/extensions/ext-gen.py \
    share/llvmnet/templates/native-extension/extension.json; do
    [[ -f "$relocated/$file" ]] || { printf 'FAIL: the toolchain package has no %s\n' "$file" >&2; exit 1; }
done
"$relocated/bin/llvmnet" --list-extensions >"$output/extensions.txt"
grep -q '^gles 1\.1\.0$' "$output/extensions.txt" && grep -q '^vulkan 1\.0\.0$' "$output/extensions.txt" && ! grep -q 'problem:' "$output/extensions.txt" ||
    { cat "$output/extensions.txt" >&2; exit 1; }
"$relocated/bin/llvmnet" --print-extension-kit | grep -qxF "kit $relocated/share/llvmnet/extensions"
template="$output/template"
rm -rf "$template" "$output/ext-build"
cp -a "$relocated/share/llvmnet/templates/native-extension" "$template"
clang-22 -O2 -shared -fPIC -I"$template/include" "$template/native/hello.c" -o "$template/libllvmnet-hello.so"
(cd "$output" && bash "$relocated/share/llvmnet/extensions/build-extensions.sh" --build-dir "$output/ext-build" "$template" \
    2>"$output/ext-build.log") || { cat "$output/ext-build.log" >&2; exit 1; }
grep -qF "Built extension hello into $relocated/sysroot/dotnet64-v2/ext/hello: " "$output/ext-build.log" || { cat "$output/ext-build.log" >&2; exit 1; }
"$compiler" -O1 "$template/example/main.c" -lllvmnet-hello -o "$output/hello"
LLVMNET_EXT_HELLO_DIR="$template" "$output/hello" | diff -u "$template/example/expected.txt" -
printf 'PASS: the package ships gles and vulkan, and its kit builds and installs the template extension\n'
