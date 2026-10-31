#!/usr/bin/env bash
# tags: optional aot
# requires: flang
# serial: 40
# after: xp
# timeout: 3600
# Publishes the NativeAOT projects of the cross-platform bundle on this host (the projects tests/xp/build.sh writes with
# --aot-project, which the Windows and macOS CI jobs publish with their own toolchains) and runs the executables.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
bundle="$root/artifacts/tests/xp/bundle"
if [[ ! -f "$bundle/manifest.tsv" ]]; then bash "$root/tests/xp/build.sh" "$bundle"; fi
bash "$root/tests/xp/run.sh" --aot-only --jobs "${LLVMNET_TEST_JOBS:-2}" "$bundle"
count="$(grep -c $'\taot\tPASS' "$bundle"/results/*/results.tsv)"
printf 'PASS: %s NativeAOT executables published from relocatable projects reproduce their native output on this host\n' "$count"
