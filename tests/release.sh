#!/usr/bin/env bash
# tags: toolchain slow
# serial: 96
# timeout: 3600
# TC-11: the release artifacts are reproducible: two builds of the same commit give byte-identical tar.xz archives and
# SHA256SUMS; the thin variant has no bundled LLVM, the bundled one does, and both carry the license files.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
rm -rf "$test_output"
mkdir -p "$test_output"
cd "$test_output"
version="0.0.0-test+$(git -C "$root" rev-parse --short=12 HEAD)"
variants="${LLVMNET_RELEASE_VARIANTS:-thin llvm tool}"
LLVMNET_RELEASE_VARIANTS="$variants" bash "$root/scripts/release-artifacts.sh" first "$version" >first.log
LLVMNET_RELEASE_VARIANTS="$variants" bash "$root/scripts/release-artifacts.sh" second "$version" >second.log
diff first/SHA256SUMS second/SHA256SUMS
cat first/SHA256SUMS
if [[ "$variants" == *thin* ]]; then
    tar tJf "first/llvmnet-$version-linux-x64.tar.xz" >thin.txt
    grep -q "^llvmnet-$version-linux-x64/bin/llvmnet-clang$" thin.txt
    grep -q "^llvmnet-$version-linux-x64/lib/llvmnet/llvmnet$" thin.txt
    grep -q "^llvmnet-$version-linux-x64/sysroot/dotnet64-v2/lib/libc.a$" thin.txt
    grep -q "^llvmnet-$version-linux-x64/share/llvmnet/LICENSE.TXT$" thin.txt
    grep -q "^llvmnet-$version-linux-x64/share/llvmnet/THIRD-PARTY-NOTICES.md$" thin.txt
    if grep -q "/libexec/llvm/" thin.txt; then printf 'FAIL: the thin package bundles LLVM\n' >&2; exit 1; fi
fi
if [[ "$variants" == *llvm* ]]; then
    tar tJf "first/llvmnet-$version-linux-x64-llvm.tar.xz" >llvm.txt
    grep -q "^llvmnet-$version-linux-x64-llvm/libexec/llvm/bin/clang-22$" llvm.txt
    grep -q "^llvmnet-$version-linux-x64-llvm/libexec/llvm/lib/clang/22/include/stddef.h$" llvm.txt
    grep -q "^llvmnet-$version-linux-x64-llvm/share/llvmnet/licenses/" llvm.txt
fi
if [[ "$variants" == *tool* ]]; then
    # The dotnet tool installs from the package and compiles C and C++ with the sysroots it carries.
    package="$(basename first/llvmnet.*.nupkg)"
    tool_version="${package#llvmnet.}"
    tool_version="${tool_version%.nupkg}"
    rm -rf tool
    dotnet tool install --tool-path tool llvmnet --add-source first --version "$tool_version" >tool-install.log
    ./tool/llvmnet "$root/tests/c/core.c" -o tool-core.dll
    dotnet tool-core.dll
    ./tool/llvmnet --driver-mode=g++ "$root/tests/cpp/core.cpp" -o tool-cpp.dll
    dotnet tool-cpp.dll
    # The tool carries the shipped native extensions, <llvmnet/ext.h> and the kit that builds more (NX-12).
    ./tool/llvmnet --list-extensions | grep -q '^gles 1\.0\.0$'
    kit="$(./tool/llvmnet --print-extension-kit | sed -n 's/^kit //p')"
    [[ -f "$kit/build-extensions.sh" && -f "$kit/ext-gen.py" && -f "$kit/../../../sysroot/extensions/include/llvmnet/ext.h" ]]
fi
du -h first/*.tar.xz first/*.nupkg 2>/dev/null | tee sizes.txt
printf 'PASS: release artifacts are reproducible (%s)\n' "$variants"
