#!/usr/bin/env bash
# Fill a local NuGet folder feed with every package NativeAOT publishing needs (TC-12): the ILCompiler packages, the
# runtime pack and the ILLink tasks for each runtime identifier. A host without network access publishes from it with
# LLVMNET_NUGET_FEED=<feed-dir>, which makes the generated NativeAOT project restore from that feed alone.
#
# Usage: scripts/nativeaot-feed.sh <feed-dir> [rid...]       (default rid: this host's)
# Packages come from the sources NuGet is configured with, and from this user's global packages folder when it already
# holds them, so a machine that has published once can fill a feed without network access.
set -euo pipefail
feed="${1:?usage: nativeaot-feed.sh <feed-dir> [rid...]}"
shift
feed="$(realpath -m "$feed")"
rids=("$@")
if [[ ${#rids[@]} -eq 0 ]]; then
    rids=("$(dotnet --info 2>/dev/null | sed -n 's/^ *RID: *//p' | head -1 | tr -d '\r')")
    [[ -n "${rids[0]}" ]] || { printf 'Cannot determine the host runtime identifier; name one.\n' >&2; exit 2; }
fi
mkdir -p "$feed"
work="$(mktemp -d "${TMPDIR:-/tmp}/llvmnet-feed-XXXXXX")"
trap 'rm -rf "$work"' EXIT
stage="$work/packages"
mkdir -p "$work/project" "$stage"
cat >"$work/project/Feed.csproj" <<'PROJECT'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <PublishAot>true</PublishAot>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>
</Project>
PROJECT
printf 'internal static class Feed { private static int Main() => 0; }\n' >"$work/project/Feed.cs"
printf '<Project />\n' >"$work/project/Directory.Build.props"
printf '<Project />\n' >"$work/project/Directory.Build.targets"
# The global packages folder is a valid local source (the expanded id/version layout), so a cached package is reused.
global_packages="${NUGET_PACKAGES:-$HOME/.nuget/packages}"
sources=()
if [[ -d "$global_packages" ]]; then sources+=(--source "$global_packages"); fi
if [[ -z "${LLVMNET_FEED_OFFLINE:-}" ]]; then sources+=(--source https://api.nuget.org/v3/index.json); fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
for rid in "${rids[@]}"; do
    dotnet restore "$work/project/Feed.csproj" -r "$rid" --packages "$stage" "${sources[@]}" --nologo -v quiet
done
count=0
while IFS= read -r package; do
    cp "$package" "$feed/"
    count=$((count + 1))
done < <(find "$stage" -name '*.nupkg' | sort)
printf 'NativeAOT feed: %d packages in %s for %s\n' "$count" "$feed" "${rids[*]}"
