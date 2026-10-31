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
bash "$root/scripts/ensure-built.sh"
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
cp "$root/README.md" "$root/LICENSE.TXT" "$root/THIRD-PARTY-NOTICES.md" "$root/CHANGELOG.md" "$prefix/share/llvmnet/"
mkdir -p "$prefix/share/llvmnet/licenses"
if [[ "$self_contained" -eq 1 ]]; then
    dotnet_root="$(dirname "$(readlink -f "$(command -v dotnet)")")"
    cp "$dotnet_root/LICENSE.txt" "$prefix/share/llvmnet/licenses/dotnet.LICENSE.txt"
    cp "$dotnet_root/ThirdPartyNotices.txt" "$prefix/share/llvmnet/licenses/dotnet.ThirdPartyNotices.txt"
fi
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
    # Of the resource directory only the builtin headers are needed: its lib/ holds the sanitizer and profiling
    # runtimes, which llvmnet cannot link (TC-13).
    resource="$(clang-22 -print-resource-dir)"
    mkdir -p "$llvm_prefix/lib/clang/$(basename "$resource")"
    cp -a "$resource/include" "$llvm_prefix/lib/clang/$(basename "$resource")/include"
    mkdir -p "$llvm_prefix/include/flang"
    cp "$(dirname "$(dirname "$(realpath "$(command -v flang-22)")")")"/include/flang/*.mod "$llvm_prefix/include/flang/"
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
    # Strip what the distribution did not (TC-13); a library keeps its dynamic symbol table.
    find "$llvm_prefix/bin" "$llvm_prefix/lib" -maxdepth 1 -type f \( -name '*.so*' -o -perm -u+x \) -exec strip --strip-unneeded {} + 2>/dev/null || true
    # Keep the Debian copyright file of every package whose files were bundled above.
    while read -r package; do
        if [[ -f "/usr/share/doc/$package/copyright" ]]; then
            cp "/usr/share/doc/$package/copyright" "$prefix/share/llvmnet/licenses/$package.copyright"
        fi
    done < <(for file in "${original_binaries[@]}" "$llvm_prefix"/lib/*.so*; do
        source_file="$file"
        [[ "$file" == "$llvm_prefix"/lib/* ]] && source_file="$(find /usr/lib/llvm-22/lib /usr/lib/x86_64-linux-gnu -maxdepth 1 -name "$(basename "$file")" -print -quit)"
        [[ -n "$source_file" ]] && dpkg -S "$(realpath "$source_file")" 2>/dev/null | cut -d: -f1
    done | sort -u)
fi
# The compiler's own native bridge and the publish output are stripped of what a run does not need.
strip --strip-unneeded "$prefix/lib/llvmnet/libllvmnet-llvm.so" 2>/dev/null || true
rm -f "$prefix/lib/llvmnet/libllvmnet-llvm.stamp"
printf 'Toolchain installed in %s (bundled compiler runtime=%s, bundled LLVM=%s, %s). NativeAOT publishing requires the .NET 10 SDK.\n' "$prefix" "$self_contained" "$bundle_llvm" "$(du -sh "$prefix" | cut -f1)"