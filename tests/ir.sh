#!/usr/bin/env bash
# tags: core
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
output="$root/artifacts/tests/ir"
mkdir -p "$output"
for source in "$root"/tests/ir/*.ll; do
    name="$(basename "$source" .ll)"
    clang-22 "$source" -lm -lstdc++ -o "$output/$name-native"
    "$output/$name-native"
    for runtime in system portable; do
        tag_ir "$runtime" "$source" "$output/$name-$runtime.bc"
        "$llvmnet" --runtime="$runtime" "$output/$name-$runtime.bc" -o "$output/$name-$runtime.dll"
        dotnet "$output/$name-$runtime.dll"
    done
    printf 'PASS: native and CIL (system and portable) LLVM IR %s\n' "$name"
done
tag_ir portable "$root/tests/ir/rejected/used-void-return.ll" "$output/rejected.bc"
if "$llvmnet" "$output/rejected.bc" -o "$output/rejected.dll" > "$output/used-void-return.log" 2>&1; then
    printf 'FAIL: consumed return from a void definition was accepted\n' >&2
    exit 1
fi
grep -q 'Incompatible direct call signature' "$output/used-void-return.log"
# Link inputs must carry an ABI tag; untagged IR is rejected with guidance.
if "$llvmnet" "$root/tests/ir/switch.ll" -o "$output/untagged.dll" > "$output/untagged.log" 2>&1; then
    printf 'FAIL: untagged LLVM IR was accepted as a link input\n' >&2
    exit 1
fi
grep -q 'untagged LLVM input' "$output/untagged.log"
