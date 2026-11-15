#!/usr/bin/env bash
# The LLVM tools of a portable toolchain (Clang, Flang, opt, llvm-ar, llvm-nm) as NativeAOT executables for one host
# (XP-14), which scripts/precompile-toolchain.sh puts in a precompiled package in place of their payloads and launchers.
# NativeAOT compiles for the operating system it runs on only, so the release workflow writes the projects on Linux and
# publishes them on a runner of each host.
#
#   scripts/aot-tools.sh projects <llvm-bin> <projects-dir>
#       Writes the NativeAOT project of each tool image in <llvm-bin> (the <image>.llvmnet/ payloads of a portable
#       toolchain's libexec/llvm/bin, or of scripts/selfhost/build-tools.sh's stage) into <projects-dir>/<image>, with
#       this checkout's compiler (llvmnet --write-aot-project, the project --aot-project writes for a link). The projects
#       are relocatable.
#   scripts/aot-tools.sh publish <projects-dir> <rid> <out-dir>
#       Publishes each project for <rid> (dotnet publish; the .NET 10 SDK and the host's native toolchain, as for
#       --nativeaot) and puts the executables in <out-dir> under the tools' versioned names: clang-22, flang-22, opt-22,
#       llvm-ar-22 and llvm-nm-22, with .exe for Windows. Without stack-trace data: Flang's 234,651 methods would exceed
#       ILC's 32 MB metadata blob with it. bash 3.2 compatible (macOS); runs in Git Bash on Windows.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
usage() { sed -n '7,17p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//' >&2; exit 2; }
[[ $# -ge 1 ]] || usage
command="$1"
shift
case "$command" in
    projects)
        [[ $# -eq 2 ]] || usage
        bin="$1"
        projects="$2"
        found=0
        for payload in "$bin"/*.llvmnet; do
            [[ -f "$payload/program.dll" ]] || continue
            image="$(basename "$payload" .llvmnet)"
            rm -rf "${projects:?}/$image"
            "$root/bin/llvmnet" --write-aot-project="$payload/program.dll" -o "$projects/$image" >/dev/null
            printf 'Wrote the NativeAOT project of %s\n' "$image"
            found=$((found + 1))
        done
        [[ "$found" -gt 0 ]] || { printf 'No tool payloads (<image>.llvmnet/program.dll) in %s\n' "$bin" >&2; exit 1; }
        ;;
    publish)
        [[ $# -eq 3 ]] || usage
        projects="$1"
        rid="$2"
        out="$3"
        suffix=""
        case "$rid" in win-*) suffix=".exe" ;; esac
        mkdir -p "$out"
        for project in "$projects"/*/NativeHost.csproj; do
            [[ -f "$project" ]] || { printf 'No NativeAOT projects in %s\n' "$projects" >&2; exit 1; }
            image="$(basename "$(dirname "$project")")"
            case "$image" in *-22) name="$image" ;; *) name="$image-22" ;; esac
            publish="$out/.publish/$image"
            rm -rf "$publish"
            start="$(date +%s)"
            dotnet publish "$project" -c Release -r "$rid" -o "$publish" --nologo -v minimal -p:StackTraceSupport=false
            # The executable is named after the image's assembly (program).
            [[ -f "$publish/program$suffix" ]] || { printf 'NativeAOT produced no program%s in %s\n' "$suffix" "$publish" >&2; exit 1; }
            mv -f "$publish/program$suffix" "$out/$name$suffix"
            printf 'Published %s for %s in %d s\n' "$name$suffix" "$rid" "$(($(date +%s) - start))"
        done
        rm -rf "$out/.publish"
        ;;
    *) usage ;;
esac
