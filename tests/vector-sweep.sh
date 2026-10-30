#!/usr/bin/env bash
# tags: core aot
# Vectorized code must either run exactly like native or be rejected with an ordinary diagnostic,
# never miscompile or fail internally. Fixtures are compiled with the same flags natively. The AVX2
# profile disables FP contraction: llvm.fmuladd may legally fuse natively (FMA) but not in llvmnet.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
rm -rf "$test_output"
mkdir -p "$test_output"
fixtures=(vector-loops core math intrinsics int128 unaligned)
profiles=("vectorize:-O3 -fvectorize -fslp-vectorize" "fast-math:-O3 -ffast-math -fvectorize -fslp-vectorize" "avx2:-O3 -march=x86-64-v3 -ffp-contract=off -fvectorize -fslp-vectorize")
matched=0 rejected=()
for profile in "${profiles[@]}"; do
    label="${profile%%:*}"
    read -r -a flags <<<"${profile#*:}"
    selected=("${fixtures[@]}")
    # Self-checking fixtures test NaN and errno behavior, which -ffast-math deliberately gives up.
    [[ "$label" == fast-math ]] && selected=(vector-loops)
    for fixture in "${selected[@]}"; do
        source_file="$root/tests/c/$fixture.c"
        clang-22 "${flags[@]}" "$source_file" -lm -o "$test_output/$fixture-$label-native"
        "$test_output/$fixture-$label-native" >"$test_output/$fixture-$label-native.txt"
        for runtime in system portable; do
            # A host-CPU instruction-set flag is a native-ABI concept; portable mode keeps the fixed target.
            [[ "$label" == avx2 && "$runtime" == portable ]] && continue
            name="$fixture-$label-$runtime"
            status=0
            "$llvmnet" --runtime="$runtime" "${flags[@]}" "$source_file" -o "$test_output/$name.dll" >"$test_output/$name.log" 2>&1 || status=$?
            if [[ "$status" -eq 1 ]]; then
                rejected+=("$name: $(grep -m1 'error:' "$test_output/$name.log")")
                continue
            elif [[ "$status" -ne 0 ]]; then
                printf 'FAIL: %s failed with exit %s (internal error?)\n' "$name" "$status" >&2
                cat "$test_output/$name.log" >&2
                exit 1
            fi
            dotnet "$test_output/$name.dll" >"$test_output/$name.txt"
            diff -u "$test_output/$fixture-$label-native.txt" "$test_output/$name.txt"
            matched=$((matched + 1))
        done
    done
done
"$llvmnet" --runtime=portable -O3 -fvectorize -fslp-vectorize --nativeaot "$root/tests/c/vector-loops.c" -o "$test_output/vector-loops-aot" >"$test_output/aot.log"
"$test_output/vector-loops-aot" >"$test_output/vector-loops-aot.txt"
diff -u "$test_output/vector-loops-vectorize-native.txt" "$test_output/vector-loops-aot.txt"
printf '%s\n' "${rejected[@]}" >"$test_output/rejected.txt"
printf 'PASS: %d vectorized builds match native; %d rejected with diagnostics (listed in %s)\n' "$matched" "${#rejected[@]}" "$test_output/rejected.txt"
