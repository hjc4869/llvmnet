#!/usr/bin/env bash
# tags: core toolchain
# requires: flang
# Cross-platform bundle (XP-01 to XP-03): builds tests/xp/fixtures.tsv into artifacts/tests/xp/bundle with the native
# results recorded, then runs the bundle on this host under the JIT, as the Windows, macOS and Linux ARM64 CI jobs do
# with the same bundle. tests/xp-aot.sh publishes and runs its NativeAOT projects.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
bash "$root/tests/xp/build.sh" "$root/artifacts/tests/xp/bundle"
bash "$root/tests/xp/run.sh" "$root/artifacts/tests/xp/bundle"
count="$(grep -c $'\tjit\tPASS' "$root"/artifacts/tests/xp/bundle/results/*/results.tsv)"
printf 'PASS: %s cross-platform fixtures reproduce their native output and exit status under the JIT on this host\n' "$count"
