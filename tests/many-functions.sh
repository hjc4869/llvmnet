#!/usr/bin/env bash
# tags: core
# Programs with more methods than the CLR allows in one type (65,535; Clang has about 146,000 functions after the link)
# load and run: the backend spreads internal methods over several Program classes (BE-47). tests/c/many-functions.py
# writes 70,000 functions that call their neighbours across the class boundaries and are all called through a table of
# function pointers (in system mode each also gets a native callback entry point), with a thread-local counter and
# calls into the C library from the last class; the program must print what the native build prints in both runtime
# modes. A library with as many internal functions is entered through its export, which runs Program's type
# initializer (the globals) before any of them.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
rm -rf "$test_output"
mkdir -p "$test_output"
python3 "$root/tests/c/many-functions.py" 70000 "$test_output/program.c"
python3 "$root/tests/c/many-functions.py" --library 70000 "$test_output/library.c" "$test_output/consumer.c"
clang-22 -O0 "$test_output/program.c" -o "$test_output/native"
"$test_output/native" >"$test_output/native.txt"
clang-22 -O0 "$test_output/library.c" "$test_output/consumer.c" -o "$test_output/native-library"
"$test_output/native-library" >"$test_output/native-library.txt"
for runtime in portable system; do
    target="$test_output/many-$runtime"
    "$llvmnet" --runtime="$runtime" -O1 "$test_output/program.c" -o "$target.dll"
    dotnet "$checks" inspect "$target.dll" | grep -Eq 'IL-only: ([7-9][0-9]{4}|[0-9]{6,}) method bodies'
    dotnet "$target.dll" >"$target.txt"
    diff -u "$test_output/native.txt" "$target.txt"
    "$llvmnet" --runtime="$runtime" -O1 -shared -fvisibility=hidden "$test_output/library.c" -o "$test_output/Many$runtime.dll"
    "$llvmnet" --runtime="$runtime" -O1 "$test_output/consumer.c" --cil-reference "$test_output/Many$runtime.dll" -o "$target-consumer.dll"
    dotnet "$target-consumer.dll" >"$target-consumer.txt"
    diff -u "$test_output/native-library.txt" "$target-consumer.txt"
done
printf 'PASS: programs and libraries with 70,000 functions run in both modes\n'
