#!/usr/bin/env bash
# Precompile managed assemblies in place with ReadyToRun for one runtime identifier (XP-04): crossgen2 for this host and
# the target's runtime pack come from NuGet through a restore, as the compiler's --readytorun gets them. Each assembly is
# its own version bubble and keeps its IL, so it still runs on every other .NET host, under the JIT; on hosts of the
# identifier the runtime starts from the precompiled code. A JIT-compiled llvmnet-built clang spends seconds compiling
# itself at every start, which this removes (docs/user/deployment.md#portable-toolchain).
#
# Usage: scripts/readytorun.sh <rid> <assembly>...
#   JOBS                    assemblies compiled at once (default 2; crossgen2 takes about 4 GB for clang)
#   LLVMNET_NUGET_FEED      a folder feed to restore from instead of the configured sources (scripts/nativeaot-feed.sh)
# References are the runtime pack and the other assemblies in each assembly's directory. An assembly that is already
# precompiled is left as it is.
set -euo pipefail
rid="${1:?usage: readytorun.sh <rid> <assembly>...}"
shift
case "$rid" in linux-x64|linux-arm64|win-x64|win-arm64|osx-x64|osx-arm64) ;; *) printf 'Unsupported runtime identifier %s\n' "$rid" >&2; exit 2 ;; esac
[[ $# -gt 0 ]] || { printf 'No assemblies given.\n' >&2; exit 2; }
work="$(mktemp -d "${TMPDIR:-/tmp}/llvmnet-readytorun.XXXXXX")"
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/project"
cat >"$work/project/ReadyToRun.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <RuntimeIdentifier>$rid</RuntimeIdentifier>
    <SelfContained>false</SelfContained>
    <PublishReadyToRun>true</PublishReadyToRun>
  </PropertyGroup>
</Project>
EOF
printf '<Project />\n' >"$work/project/Directory.Build.props"
printf '<Project />\n' >"$work/project/Directory.Build.targets"
if [[ -n "${LLVMNET_NUGET_FEED:-}" ]]; then
    printf '<configuration><packageSources><clear /><add key="llvmnet" value="%s" /></packageSources></configuration>\n' "$LLVMNET_NUGET_FEED" >"$work/project/nuget.config"
fi
dotnet restore "$work/project/ReadyToRun.csproj" --nologo -v quiet >"$work/restore.log" 2>&1 || { cat "$work/restore.log" >&2; exit 1; }
# The restore records the packs it downloaded and the packages folder holding them.
read -r crossgen framework < <(python3 - "$work/project/obj/project.assets.json" "$rid" <<'PYTHON'
import json, os, platform, sys
assets = json.load(open(sys.argv[1]))
rid = sys.argv[2]
packages = next(iter(assets["packageFolders"]))
found = {}
for framework in assets["project"]["frameworks"].values():
    for download in framework.get("downloadDependencies", []):
        found[download["name"].lower()] = download["version"].strip("[]").split(",")[0].strip()
def pack(prefix):
    for name, version in found.items():
        if name.startswith(prefix):
            return os.path.join(packages, name, version), name
    raise SystemExit(f"the restore did not provide {prefix}*")
crossgen, name = pack("microsoft.netcore.app.crossgen2.")
executable = os.path.join(crossgen, "tools", "crossgen2.exe" if os.name == "nt" or name.endswith("win-x64") or name.endswith("win-arm64") else "crossgen2")
runtime, _ = pack(f"microsoft.netcore.app.runtime.{rid}")
print(executable, os.path.join(runtime, "runtimes", rid, "lib", "net10.0"))
PYTHON
)
os="${rid%%-*}"
[[ "$os" == win ]] && os=windows
architecture="${rid##*-}"
compile() {
    local assembly="$1" directory name output reference references=()
    directory="$(cd "$(dirname "$assembly")" && pwd)"
    name="$(basename "$assembly")"
    for reference in "$directory"/*.dll; do
        [[ "$(basename "$reference")" == "$name" ]] || references+=(-r "$reference")
    done
    output="$work/out/$(printf '%s' "$directory/$name" | cksum | cut -d' ' -f1)"
    mkdir -p "$output"
    if ! "$crossgen" "$directory/$name" -r "$framework/*.dll" "${references[@]}" --targetos:"$os" --targetarch:"$architecture" -O \
        -o "$output/$name" >"$output/log" 2>&1; then
        if grep -q 'input file is already compiled' "$output/log"; then return 0; fi
        cat "$output/log" >&2
        return 1
    fi
    mv -f "$output/$name" "$directory/$name"
    printf 'precompiled %s\n' "$directory/$name"
}
export -f compile
export crossgen framework os architecture work
printf '%s\0' "$@" | xargs -0 -P "${JOBS:-2}" -I{} bash -c 'compile "$1"' _ {}
