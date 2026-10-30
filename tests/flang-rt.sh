#!/usr/bin/env bash
# tags: core libc fortran aot
# requires: flang
# timeout: 3600
# Upstream flang-rt 22.1.8 on musl and libc++ (FT-01, FT-05): every single-file Fortran fixture is compiled with
# llvmnet against flang-rt and compared with native Flang, plus one NativeAOT build. Excluded: fixtures needing
# extra sources or OpenMP flags (arithmetic, concurrent, module-main, portable-interop).
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
rm -rf "$test_output"
mkdir -p "$test_output"
export test_output llvmnet root
check() {
    local name="$1" source_file="$root/tests/fortran/$1.f90" directory="$test_output/$1"
    mkdir -p "$directory/native" "$directory/llvmnet"
    flang-22 -O1 -module-dir "$directory/native" "$source_file" -o "$directory/native/program" >/dev/null 2>&1
    local native=0 managed=0
    (cd "$directory/native" && timeout 60 ./program >out.txt 2>/dev/null) || native=$?
    "$llvmnet" --runtime=portable -O1 -module-dir "$directory/llvmnet" "$source_file" -o "$directory/llvmnet/program.dll" >"$directory/build.log" 2>&1 ||
        { printf 'FAIL: %s did not compile\n' "$name"; return 1; }
    (cd "$directory/llvmnet" && timeout 120 dotnet program.dll >out.txt 2>err.txt) || managed=$?
    if [[ "$native" != "$managed" ]] || ! cmp -s "$directory/native/out.txt" "$directory/llvmnet/out.txt"; then
        printf 'FAIL: %s differs (native exit %s, llvmnet exit %s)\n' "$name" "$native" "$managed"
        diff "$directory/native/out.txt" "$directory/llvmnet/out.txt" | head -10
        return 1
    fi
}
export -f check
mapfile -t fixtures < <(for source in "$root"/tests/fortran/*.f90; do basename "$source" .f90; done |
    grep -v -x -E 'arithmetic|concurrent|module-main|portable-interop')
printf '%s\n' "${fixtures[@]}" | xargs -P "${JOBS:-8}" -I{} bash -c 'check "$1"' _ {}
"$llvmnet" --runtime=portable -O1 -module-dir "$test_output" --nativeaot "$root/tests/fortran/namelist.f90" -o "$test_output/namelist-aot" >"$test_output/aot.log" 2>&1
(cd "$test_output/namelist/native" && timeout 60 "$test_output/namelist-aot" >aot.txt)
cmp "$test_output/namelist/native/out.txt" "$test_output/namelist/native/aot.txt"
printf 'PASS: flang-rt on musl and libc++: %d Fortran fixtures match native Flang, namelist also under NativeAOT\n' "${#fixtures[@]}"
