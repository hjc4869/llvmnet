#!/usr/bin/env bash
# tags: core cpp fortran
# requires: flang
# timeout: 2400
# TC-03 (ADR-11): the default is -O1, which does not vectorize, and -O2 and above vectorize as in Clang (the owner's SV-05
# decision); every level from -O0 to -O3, -Os, -Oz and -O3 with explicit vectorization compiles the C, C++ and Fortran fixtures in both modes with output identical to the native build at
# the same level, the level reaches the frontend (-O0 keeps optnone), and the -O0 and -O3 images pass JIT validation.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
rm -rf "$test_output"
mkdir -p "$test_output"
cd "$test_output"
# The default and an explicit level, as the frontend sees them.
"$llvmnet" -### "$root/tests/c/smoke.c" -o default.dll 2>default.txt
grep -q '"-O1"' default.txt && ! grep -q -- '-fno-vectorize' default.txt
"$llvmnet" -### -O0 "$root/tests/c/smoke.c" -o zero.dll 2>zero.txt
grep -q '"-O1" .*"-O0"' zero.txt
# The level decides the vectorizers as in Clang: a loop is vector code at -O2 and -O3, scalar at -O1 and with -fno-vectorize.
printf 'void add(int *restrict a, const int *restrict b, int n) { for (int i = 0; i < n; i++) a[i] += b[i]; }\n' >loop.c
vector_ops() { "$llvmnet" "$@" -S -c loop.c -o - | grep -c '<4 x i32>' || true; }
test "$(vector_ops -O1)" -eq 0
test "$(vector_ops -O2)" -gt 0
test "$(vector_ops -O3)" -gt 0
test "$(vector_ops -O2 -fno-vectorize -fno-slp-vectorize)" -eq 0
printf 'PASS: the default is -O1 without vectorization, -O2 and above vectorize, and an explicit level follows the default\n'
fixtures=(c/core.c c/intrinsics.c c/byval.c c/unaligned.c cpp/core.cpp fortran/core.f90)
levels=("-O0" "-O1" "-O2" "-O3" "-Os" "-Oz" "-O3 -fvectorize -fslp-vectorize")
validated=()
for fixture in "${fixtures[@]}"; do
    name="$(basename "$fixture")"
    name="${name%.*}-${fixture%%/*}"
    for level in "${levels[@]}"; do
        # Flang has -O0 to -O3 only.
        if [[ "$fixture" == fortran/* && "$level" == -O[sz] ]]; then continue; fi
        # shellcheck disable=SC2206
        flags=($level)
        tag="$(tr -d ' -' <<<"$level")"
        case "$fixture" in
            c/*) clang-22 "${flags[@]}" "$root/tests/$fixture" -lm -o "native-$name-$tag" ;;
            cpp/*) clang++-22 "${flags[@]}" "$root/tests/$fixture" -o "native-$name-$tag" ;;
            fortran/*) flang-22 "${flags[@]}" "$root/tests/$fixture" -o "native-$name-$tag" ;;
        esac
        native_status=0
        "./native-$name-$tag" >"native-$name-$tag.txt" 2>&1 || native_status=$?
        for runtime in system portable; do
            image="$name-$tag-$runtime"
            "$llvmnet" --runtime="$runtime" "${flags[@]}" --keep-ir "$image.bc" "$root/tests/$fixture" -o "$image.dll"
            status=0
            dotnet "$image.dll" >"$image.txt" 2>&1 || status=$?
            if [[ "$status" -ne "$native_status" ]]; then
                printf 'FAIL: %s %s %s exits with %s, native with %s\n' "$fixture" "$level" "$runtime" "$status" "$native_status" >&2
                exit 1
            fi
            diff -u "native-$name-$tag.txt" "$image.txt"
            # The level reaches the frontend: at -O0 Clang marks the program's main optnone, at -O3 it does not (the
            # runtime libraries linked in keep their own attributes; Flang marks nothing at -O0).
            llvm-dis-22 "$image.bc" -o "$image.ll"
            group="$(grep -E '^define .*@(main|_QQmain)\(' "$image.ll" | grep -o '#[0-9]*' | head -1)"
            attributes="$(grep -E "^attributes $group = " "$image.ll")"
            if [[ "$fixture" == fortran/* ]]; then
                :
            elif [[ "$level" == "-O0" && "$attributes" != *optnone* ]]; then
                printf 'FAIL: -O0 did not reach the frontend for %s (%s): %s\n' "$fixture" "$runtime" "$attributes" >&2
                exit 1
            elif [[ "$level" == "-O3" && "$attributes" == *optnone* ]]; then
                printf 'FAIL: -O3 output still carries optnone for %s (%s)\n' "$fixture" "$runtime" >&2
                exit 1
            fi
            if [[ "$level" == "-O0" || "$level" == "-O3" ]]; then validated+=("$test_output/$image.dll"); fi
        done
    done
    printf 'PASS: %s matches native at every level in both modes\n' "$fixture"
done
# Every method of the -O0 and -O3 images JIT-compiles (FD-15), so unoptimized IR with its many locals is verifiable.
dotnet "$checks" jit-all "${validated[@]}"
printf 'PASS: %d images at -O0 and -O3 pass JIT validation\n' "${#validated[@]}"
