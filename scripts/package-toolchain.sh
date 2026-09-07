#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
prefix="${1:-$root/artifacts/toolchain}"
shift "$(( $# > 0 ? 1 : 0 ))"
self_contained=0
bundle_llvm=0
for option in "$@"; do
    case "$option" in
        --self-contained) self_contained=1 ;;
        --bundle-llvm) bundle_llvm=1 ;;
        *) printf 'Usage: %s [prefix] [--self-contained] [--bundle-llvm]\n' "$0" >&2; exit 2 ;;
    esac
done
prefix="$(realpath -m "$prefix")"
if [[ "$prefix" == / || "$prefix" == "$root" ]]; then
    printf 'Choose a separate toolchain installation prefix.\n' >&2
    exit 2
fi
dotnet build "$root/llvmnet.slnx" -c Release --nologo
bash "$root/scripts/build-portable-runtime.sh"
mkdir -p "$prefix/bin" "$prefix/lib/llvmnet" "$prefix/share/llvmnet" "$prefix/sysroot"
publish_options=(--no-self-contained)
if [[ "$self_contained" -eq 1 ]]; then publish_options=(--self-contained -r linux-x64); fi
dotnet publish "$root/src/LlvmNet.Compiler" -c Release "${publish_options[@]}" --nologo -o "$prefix/lib/llvmnet"
cp "$root/src/LlvmNet.Compiler/bin/Release/net10.0/libllvmnet-llvm.so" "$prefix/lib/llvmnet/"
cp "$root/bin/llvmnet" "$prefix/bin/llvmnet"
chmod +x "$prefix/bin/llvmnet"
for driver in llvmnet-clang llvmnet-clang++ llvmnet-flang; do
    ln -sfn llvmnet "$prefix/bin/$driver"
done
for utility in ar ranlib nm; do
    cp "$root/bin/llvmnet-tool" "$prefix/bin/llvmnet-$utility"
    chmod +x "$prefix/bin/llvmnet-$utility"
done
cp -a "$root/docs" "$prefix/share/llvmnet/"
mkdir -p "$prefix/share/llvmnet/spec"
cp "$root/config/spec/llvmnet.cfg" "$root/config/spec/llvmnet.xml" "$prefix/share/llvmnet/spec/"
cp "$root/README.md" "$prefix/share/llvmnet/"
if [[ -d "$root/sysroot" ]]; then cp -a "$root/sysroot/." "$prefix/sysroot/"; fi
if [[ "$bundle_llvm" -eq 1 ]]; then
    llvm_prefix="$prefix/libexec/llvm"
    mkdir -p "$llvm_prefix/bin" "$llvm_prefix/lib"
    original_binaries=()
    for tool in clang-22 clang++-22 flang-22 opt-22 llvm-ar-22 llvm-ranlib-22 llvm-nm-22; do
        original="$(command -v "$tool")"
        original_binaries+=("$original")
        cp -L "$original" "$llvm_prefix/bin/$tool"
    done
    ln -sfn flang-22 "$llvm_prefix/bin/flang"
    ln -sfn clang-22 "$llvm_prefix/bin/clang"
    ln -sfn clang++-22 "$llvm_prefix/bin/clang++"
    resource="$(clang-22 -print-resource-dir)"
    mkdir -p "$llvm_prefix/lib/clang"
    cp -a "$resource" "$llvm_prefix/lib/clang/$(basename "$resource")"
    mkdir -p "$llvm_prefix/include/flang"
    cp /usr/lib/llvm-22/include/flang/*.mod "$llvm_prefix/include/flang/"
    for binary in "${original_binaries[@]}" "$root/src/LlvmNet.Compiler/bin/Release/net10.0/libllvmnet-llvm.so"; do
        while read -r library; do
            [[ -f "$library" ]] || continue
            destination="$llvm_prefix/lib/$(basename "$library")"
            if [[ "$(realpath "$library")" != "$(realpath -m "$destination")" ]]; then
                cp -L "$library" "$destination"
            fi
        done < <(ldd "$binary" | awk '/libLLVM|libclang-cpp|libflang|libMLIR/ {print $3}')
    done
    llvm_library="$(realpath "$(llvm-config-22 --libdir)/libLLVM-22.so")"
    cp -L "$llvm_library" "$llvm_prefix/lib/$(basename "$llvm_library")"
    ln -sfn "$(basename "$llvm_library")" "$llvm_prefix/lib/libLLVM-22.so"
    if [[ -f /usr/share/doc/libllvm22/copyright ]]; then cp /usr/share/doc/libllvm22/copyright "$prefix/share/llvmnet/LLVM-copyright"; fi
fi
printf 'Toolchain installed in %s (bundled compiler runtime=%s, bundled LLVM=%s). NativeAOT publishing requires the .NET 10 SDK.\n' "$prefix" "$self_contained" "$bundle_llvm"