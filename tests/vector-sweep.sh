#!/usr/bin/env bash
# tags: core aot
# Vectorized code must either run exactly like native or be rejected with an ordinary diagnostic,
# never miscompile or fail internally. Fixtures are compiled with the same flags natively. The AVX2
# profile runs both modes at -march=x86-64-v3 (portable mode's alias of --isa=x86-64-v3), where llvm.fmuladd is fused
# as natively (SV-09).
# vector-loops, whose loops the vectorizers transform, must run as vector code (ADR-16 Level 1): its
# images call Vector128. vector-pixels has the encoder kernels whose ARM64 forms differ from x64's (SV-34 to SV-39); it
# also runs as NativeAOT and without hardware intrinsics, the helpers' portable paths.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
rm -rf "$test_output"
mkdir -p "$test_output"
fixtures=(vector-loops vector-pixels core math intrinsics int128 unaligned)
profiles=("vectorize:-O3 -fvectorize -fslp-vectorize" "fast-math:-O3 -ffast-math -fvectorize -fslp-vectorize" "avx2:-O3 -march=x86-64-v3 -fvectorize -fslp-vectorize")
matched=0 rejected=() vector_calls=()
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
            if [[ "$fixture" == vector-loops ]]; then
                calls="$(dotnet "$checks" vector-calls "$test_output/$name.dll" | sed -E 's/^vector-calls: ([0-9]+).*/\1/')"
                if (( calls < 100 )); then
                    printf 'FAIL: %s makes %s Vector128 calls; its vector IR did not run as vector code\n' "$name" "$calls" >&2
                    exit 1
                fi
                vector_calls+=("$name $calls")
            fi
            matched=$((matched + 1))
        done
    done
done
"$llvmnet" --runtime=portable -O3 -fvectorize -fslp-vectorize --nativeaot "$root/tests/c/vector-loops.c" -o "$test_output/vector-loops-aot" >"$test_output/aot.log"
"$test_output/vector-loops-aot" >"$test_output/vector-loops-aot.txt"
diff -u "$test_output/vector-loops-vectorize-native.txt" "$test_output/vector-loops-aot.txt"
"$llvmnet" --runtime=portable -O3 -fvectorize -fslp-vectorize --nativeaot "$root/tests/c/vector-pixels.c" -o "$test_output/vector-pixels-aot" >"$test_output/pixels-aot.log"
"$test_output/vector-pixels-aot" | diff -u "$test_output/vector-pixels-vectorize-native.txt" -
DOTNET_EnableHWIntrinsic=0 dotnet "$test_output/vector-pixels-vectorize-portable.dll" | diff -u "$test_output/vector-pixels-vectorize-native.txt" -
# The kernels' ARM64 forms are helpers that --report-simd names.
"$llvmnet" --runtime=portable -O3 --report-simd "$root/tests/c/vector-pixels.c" -o "$test_output/vector-pixels-report.dll" 2>"$test_output/pixels-report.log"
for helper in SumAbsoluteDifferences NarrowLower AddWidening16 SubtractWidening16 SubtractWidening32 Shuffle Halves; do
    if ! grep -Eq "^llvmnet: simd: .* Vectors\.${helper}[ :]" "$test_output/pixels-report.log"; then
        printf 'FAIL: vector-pixels does not call Vectors.%s\n' "$helper" >&2
        exit 1
    fi
done
printf '%s\n' "${rejected[@]}" >"$test_output/rejected.txt"
printf 'PASS: %d vectorized builds match native; %d rejected with diagnostics (listed in %s); Vector128 calls: %s\n' "$matched" "${#rejected[@]}" "$test_output/rejected.txt" "$(IFS=,; echo "${vector_calls[*]}")"
