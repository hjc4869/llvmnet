#!/usr/bin/env bash
# tags: toolchain aot
# timeout: 1800
# TC-12: NativeAOT provisioning. scripts/nativeaot-feed.sh fills a folder feed, a publish with LLVMNET_NUGET_FEED and an
# empty package cache restores from that feed alone, and each missing prerequisite (SDK, native toolchain, feed
# contents, a target another host must publish) is reported in one line naming the fix before dotnet publish runs.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
rm -rf "$test_output"
mkdir -p "$test_output/bin"
cd "$test_output"
rid="$(dotnet --info | sed -n 's/^ *RID: *//p' | head -1)"
bash "$root/scripts/nativeaot-feed.sh" feed "$rid" | tee feed.log
ls feed | grep -q -i "^runtime\.$rid\.microsoft\.dotnet\.ilcompiler\..*\.nupkg$"
ls feed | grep -q -i "^microsoft\.netcore\.app\.runtime\.$rid\..*\.nupkg$"
# An empty package cache and the feed as the only source: the publish must come from the feed.
mkdir -p packages
LLVMNET_NUGET_FEED="$test_output/feed" NUGET_PACKAGES="$test_output/packages" \
    "$llvmnet" --runtime=portable --nativeaot "$root/tests/c/core.c" -o core-offline >publish.log 2>&1
./core-offline
test -d "packages/runtime.$rid.microsoft.dotnet.ilcompiler"
printf 'PASS: a NativeAOT publish restores from the offline feed alone (%s packages)\n' "$(ls feed | wc -l)"
# The relocatable project records the feed too.
LLVMNET_NUGET_FEED="$test_output/feed" "$llvmnet" --runtime=portable "$root/tests/c/core.c" --aot-project project
grep -q '<clear />' project/nuget.config && grep -q "$test_output/feed" project/nuget.config
expect() {
    local pattern="$1"
    shift
    if env "$@" "$llvmnet" --runtime=portable --nativeaot "$root/tests/c/smoke.c" -o never >expect.log 2>&1; then
        printf 'FAIL: the publish ran although a prerequisite was missing (%s)\n' "$*" >&2
        exit 1
    fi
    grep -q "NativeAOT prerequisite missing: $pattern" expect.log || { printf 'FAIL: expected "%s" in:\n' "$pattern" >&2; cat expect.log >&2; exit 1; }
    if grep -q -E 'MSB[0-9]+|NETSDK[0-9]+|NU[0-9]+' expect.log; then printf 'FAIL: the SDK ran before the check (%s)\n' "$*" >&2; exit 1; fi
}
# A dotnet that has only the runtime: it answers --list-sdks with nothing and otherwise runs the real one.
cat >bin/runtime-only-dotnet <<SCRIPT
#!/bin/sh
if [ "\$1" = "--list-sdks" ]; then exit 0; fi
exec "$(command -v dotnet)" "\$@"
SCRIPT
chmod +x bin/runtime-only-dotnet
expect "the .NET 10 SDK. '$test_output/bin/runtime-only-dotnet --list-sdks' reports no SDK (only a runtime is installed)" LLVMNET_DOTNET="$test_output/bin/runtime-only-dotnet"
mkdir -p empty-feed
expect "the offline feed $test_output/empty-feed has no microsoft.dotnet.ilcompiler package; fill it with \"scripts/nativeaot-feed.sh" LLVMNET_NUGET_FEED="$test_output/empty-feed"
# PATH without a C compiler: the frontend and dotnet are still reachable through their variables.
for utility in bash sh dirname basename readlink realpath env; do ln -sfn "$(command -v "$utility")" "bin/$utility"; done
expect "a C compiler and linker for $rid. Install clang" PATH="$test_output/bin" LLVMNET_DOTNET="$(command -v dotnet)" LLVMNET_CLANG="$(command -v "${LLVMNET_CLANG:-clang-22}")" LLVMNET_OPT="$(command -v "${LLVMNET_OPT:-opt-22}")"
if ! env "$llvmnet" --runtime=portable --nativeaot --runtime-id win-x64 "$root/tests/c/smoke.c" -o never >cross.log 2>&1; then
    grep -q 'NativeAOT prerequisite missing: win-x64 executables cannot be published from' cross.log
else
    printf 'FAIL: a win-x64 publish was attempted on this host\n' >&2
    exit 1
fi
printf 'PASS: missing prerequisites are reported in one line before dotnet publish runs\n'
