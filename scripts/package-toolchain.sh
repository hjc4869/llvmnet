#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
prefix="${1:-$root/artifacts/toolchain}"
shift "$(( $# > 0 ? 1 : 0 ))"
self_contained=0
bundle_llvm=0
portable=0
readytorun=""
for option in "$@"; do
    case "$option" in
        --self-contained) self_contained=1 ;;
        --bundle-llvm) bundle_llvm=1 ;;
        --portable) portable=1 ;;
        --readytorun=*) readytorun="${option#--readytorun=}" ;;
        *) printf 'Usage: %s [prefix] [--self-contained] [--bundle-llvm] [--portable [--readytorun=<rid>]]\n' "$0" >&2; exit 2 ;;
    esac
done
if [[ -n "$readytorun" && "$portable" -eq 0 ]]; then printf -- '--readytorun=<rid> precompiles a portable toolchain; add --portable.\n' >&2; exit 2; fi
# --portable: the toolchain for every .NET 10 host (XP-04). The compiler is framework-dependent without an apphost and
# runs on LlvmNet.Llvm.dll, LLVM built by llvmnet (scripts/selfhost/build-library.sh, LLVMNET_PORTABLE_LIBRARY), and
# libexec/llvm holds the llvmnet-built tools (scripts/selfhost/build-tools.sh, LLVMNET_PORTABLE_STAGE). bin/ has the
# commands as copies, not links, and the Windows .cmd versions, so the same tree is the .tar.xz and the .zip.
portable_library="${LLVMNET_PORTABLE_LIBRARY:-$root/artifacts/selfhost-build/library/LlvmNet.Llvm.dll}"
portable_stage="${LLVMNET_PORTABLE_STAGE:-$root/artifacts/selfhost-build/stage}"
if [[ "$portable" -eq 1 ]]; then
    if [[ "$self_contained" -eq 1 || "$bundle_llvm" -eq 1 ]]; then printf -- '--portable takes neither --self-contained nor --bundle-llvm.\n' >&2; exit 2; fi
    [[ -f "$portable_library" ]] || { printf 'No %s; build it with scripts/selfhost/build-library.sh.\n' "$portable_library" >&2; exit 1; }
    [[ -x "$portable_stage/bin/clang-22" ]] || { printf 'No llvmnet-built tools in %s; stage them with scripts/selfhost/build-tools.sh.\n' "$portable_stage" >&2; exit 1; }
fi
prefix="$(realpath -m "$prefix")"
if [[ "$prefix" == / || "$prefix" == "$root" ]]; then
    printf 'Choose a separate toolchain installation prefix.\n' >&2
    exit 2
fi
bash "$root/scripts/ensure-built.sh"
# The native extensions of extensions/ ship in the portable sysroot (NX-12); a package is rebuilt only when an input
# changed, and the sysroot copied below carries what is installed in ext/.
LLVMNET_SKIP_BUILD=1 bash "$root/scripts/build-extensions.sh"
mkdir -p "$prefix/bin" "$prefix/lib/llvmnet" "$prefix/share/llvmnet" "$prefix/sysroot"
publish_options=(--no-self-contained)
if [[ "$self_contained" -eq 1 ]]; then publish_options=(--self-contained -r linux-x64); fi
if [[ "$portable" -eq 1 ]]; then publish_options=(--no-self-contained -p:UseAppHost=false); fi
dotnet publish "$root/src/LlvmNet.Compiler" -c Release "${publish_options[@]}" --nologo -o "$prefix/lib/llvmnet"
cp "$root/bin/llvmnet" "$prefix/bin/llvmnet"
chmod +x "$prefix/bin/llvmnet"
# bin/llvmnet is every command, chosen by the name it is started by.
commands=(llvmnet-clang llvmnet-clang++ llvmnet-flang llvmnet-ar llvmnet-ranlib llvmnet-nm)
if [[ "$portable" -eq 1 ]]; then
    rm -f "$prefix/lib/llvmnet/libllvmnet-llvm.so"
    cp "$portable_library" "$prefix/lib/llvmnet/LlvmNet.Llvm.dll"
    for command in "${commands[@]}"; do cp "$root/bin/llvmnet" "$prefix/bin/$command"; done
    for command in llvmnet "${commands[@]}"; do cp "$root/bin/llvmnet.cmd" "$prefix/bin/$command.cmd"; done
    mkdir -p "$prefix/libexec"
    rm -rf "$prefix/libexec/llvm"
    cp -a "$portable_stage" "$prefix/libexec/llvm"
    # Every tool runs on the package's own runtime, so a runtime fix reaches the tools without relinking them.
    for payload in "$prefix"/libexec/llvm/bin/*.llvmnet; do cp "$prefix/lib/llvmnet/LlvmNet.Runtime.dll" "$payload/LlvmNet.Runtime.dll"; done
else
    cp "$root/src/LlvmNet.Compiler/bin/Release/net10.0/libllvmnet-llvm.so" "$prefix/lib/llvmnet/"
    for command in "${commands[@]}"; do ln -sfn llvmnet "$prefix/bin/$command"; done
fi
cp -a "$root/docs" "$prefix/share/llvmnet/"
# The build-system files the user documentation names: <prefix>/share/llvmnet/cmake/llvmnet.cmake finds the
# toolchain's bin/ from there (with its platform module and build rules beside it), and the Meson cross file calls
# llvmnet from PATH.
mkdir -p "$prefix/share/llvmnet/cmake/Platform" "$prefix/share/llvmnet/meson"
cp "$root/cmake/llvmnet.cmake" "$root/cmake/llvmnet-rules.cmake" "$prefix/share/llvmnet/cmake/"
cp "$root/cmake/Platform/LlvmNet.cmake" "$prefix/share/llvmnet/cmake/Platform/"
cp "$root/meson/llvmnet.ini" "$prefix/share/llvmnet/meson/"
# The kit that builds native extensions from an installed toolchain, and the template for third-party ones (NX-12).
mkdir -p "$prefix/share/llvmnet/extensions" "$prefix/share/llvmnet/templates"
cp "$root/scripts/build-extensions.sh" "$root/scripts/ext-gen.py" "$prefix/share/llvmnet/extensions/"
cp -a "$root/templates/native-extension" "$prefix/share/llvmnet/templates/"
mkdir -p "$prefix/share/llvmnet/spec"
cp "$root/config/spec/llvmnet.cfg" "$root/config/spec/llvmnet.xml" "$prefix/share/llvmnet/spec/"
cp "$root/README.md" "$root/LICENSE.TXT" "$root/THIRD-PARTY-NOTICES.md" "$root/CHANGELOG.md" "$prefix/share/llvmnet/"
mkdir -p "$prefix/share/llvmnet/licenses"
if [[ "$self_contained" -eq 1 ]]; then
    dotnet_root="$(dirname "$(readlink -f "$(command -v dotnet)")")"
    cp "$dotnet_root/LICENSE.txt" "$prefix/share/llvmnet/licenses/dotnet.LICENSE.txt"
    cp "$dotnet_root/ThirdPartyNotices.txt" "$prefix/share/llvmnet/licenses/dotnet.ThirdPartyNotices.txt"
fi
# A portable package holds no symbolic links: a zip for Windows cannot, and a checkout's sysroot may link elsewhere.
if [[ -d "$root/sysroot" && "$portable" -eq 1 ]]; then cp -RL "$root/sysroot/." "$prefix/sysroot/"
elif [[ -d "$root/sysroot" ]]; then cp -a "$root/sysroot/." "$prefix/sysroot/"; fi
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
    # ldd runs on the resolved file: Debian trixie's /usr/bin/flang-22 links to flang, which links to flang-22, and
    # through that chain $ORIGIN/../lib does not reach /usr/lib/llvm-22/lib, the only home of libMLIR. A library
    # ldd cannot find would be missing from the bundle, so it fails the package.
    for binary in "${original_binaries[@]}" "$root/src/LlvmNet.Compiler/bin/Release/net10.0/libllvmnet-llvm.so"; do
        while read -r library; do
            if [[ "$library" == missing:* ]]; then
                printf 'ldd finds no %s for %s; the bundle would be incomplete.\n' "${library#missing:}" "$binary" >&2
                exit 1
            fi
            [[ -f "$library" ]] || continue
            destination="$llvm_prefix/lib/$(basename "$library")"
            if [[ "$(realpath "$library")" != "$(realpath -m "$destination")" ]]; then
                cp -L "$library" "$destination"
            fi
        done < <(ldd "$(realpath "$binary")" | awk '/libLLVM|libclang-cpp|libflang|libMLIR/ {print ($3 == "not" ? "missing:" $1 : $3)}')
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
# --readytorun=<rid>: the same toolchain with every managed assembly precompiled for one runtime identifier, which still
# runs everywhere else under the JIT; on that host compiles start in about a second instead of five to ten.
if [[ -n "$readytorun" ]]; then
    bash "$root/scripts/readytorun.sh" "$readytorun" "$prefix"/lib/llvmnet/llvmnet.dll "$prefix"/lib/llvmnet/LlvmNet.Llvm.dll \
        "$prefix"/lib/llvmnet/LlvmNet.Runtime.dll "$prefix"/libexec/llvm/bin/*.llvmnet/program.dll "$prefix"/libexec/llvm/bin/*.llvmnet/LlvmNet.Runtime.dll >/dev/null
    printf '%s\n' "$readytorun" >"$prefix/lib/llvmnet/readytorun.txt"
fi
# The compiler's own native bridge and the publish output are stripped of what a run does not need.
if [[ -f "$prefix/lib/llvmnet/libllvmnet-llvm.so" ]]; then strip --strip-unneeded "$prefix/lib/llvmnet/libllvmnet-llvm.so" 2>/dev/null || true; fi
rm -f "$prefix/lib/llvmnet/libllvmnet-llvm.stamp"
printf 'Toolchain installed in %s (bundled compiler runtime=%s, bundled LLVM=%s, portable=%s%s, %s). NativeAOT publishing requires the .NET 10 SDK.\n' "$prefix" "$self_contained" "$bundle_llvm" "$portable" "${readytorun:+ precompiled for $readytorun}" "$(du -sh "$prefix" | cut -f1)"