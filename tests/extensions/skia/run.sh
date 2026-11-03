#!/usr/bin/env bash
# The NX-13 scenario on Windows (Git Bash) or macOS, from the bundle tests/ext-skia.sh --bundle <dir> writes: the
# portable Skia renderer on a pbuffer, then in the .NET host's window under the JIT, then host and renderer as one
# NativeAOT executable published here from the same portable image. ANGLE from Visual Studio Code's Electron is the
# host's EGL and GLES unless LLVMNET_EXT_GLES_DIR names another. --no-window (macOS) gives the renderer a CALayer in no
# window, for a session without a window server.
set -u
cd "$(dirname "$0")"
here="$PWD"
case "$(uname -s)" in
    MINGW*|MSYS*)
        if [[ -z "${LLVMNET_EXT_GLES_DIR:-}" ]]; then
            angle="$(ls -d "/c/Program Files/Microsoft VS Code"/*/libEGL.dll 2>/dev/null | head -1)"
            export LLVMNET_EXT_GLES_DIR="$(cygpath -w "$(dirname "$angle")")"
        fi
        rid=win-x64; exe=.exe; here="$(cygpath -w "$PWD")" ;;
    Darwin)
        export LLVMNET_EXT_GLES_DIR="${LLVMNET_EXT_GLES_DIR:-/Applications/Visual Studio Code.app/Contents/Frameworks/Electron Framework.framework/Versions/A/Libraries}"
        rid=osx-arm64; exe= ;;
    *) printf 'run.sh is for Windows and macOS; tests/ext-skia.sh runs the scenario on Linux\n' >&2; exit 2 ;;
esac
printf 'ANGLE from %s\n' "$LLVMNET_EXT_GLES_DIR"
window=()
[[ "${1:-}" == --no-window ]] && window=(--no-window)
status=0
dotnet jit/skia-gl.dll --pbuffer || status=1
printf 'JIT on a pbuffer: exit %d\n\n' "$status"
dotnet host/SkiaHost.dll ${window[@]+"${window[@]}"} --image jit/skia-gl.dll || status=1
printf 'JIT in the host window: exit %d\n\n' "$status"
dotnet publish src/host-aot/SkiaHostAot.csproj -c Release -r "$rid" -p:LlvmNetImage="$here/jit/skia-gl.dll" -o aot-out --nologo -v quiet \
    >publish.log 2>&1 || { tail -20 publish.log; exit 1; }
"aot-out/SkiaHost$exe" ${window[@]+"${window[@]}"} || status=1
printf 'NativeAOT, host and renderer in one executable: exit %d\n' "$status"
exit "$status"
