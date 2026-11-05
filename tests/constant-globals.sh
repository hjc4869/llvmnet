#!/usr/bin/env bash
# tags: core cpp aot
# requires: clang++
# Global and constant addresses as initonly fields (JP-04): tests/cpp/constant-globals.cpp reads globals from a
# constructor, a static object's constructor and hot loops, with 33,000 more globals in a second source so that some
# address fields live in a holder class past Program, which must stay writable. It must print what the native build
# prints in both modes, under the default tiering, without tiered compilation and with optimized loops from the first
# call (so the constructors are optimized before main), with aggressive tiering, as NativeAOT and, where crossgen2 is
# in the NuGet cache, as a ReadyToRun image.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
rm -rf "$test_output"
mkdir -p "$test_output"
many=33000
python3 - "$many" "$test_output/many.cpp" <<'EOF'
import sys
count, path = int(sys.argv[1]), sys.argv[2]
with open(path, "w") as out:
    for index in range(count):
        out.write(f"int g{index} = {index % 1000 + 1};\n")
    out.write(f"extern int *const many_table[{count}];\n")
    out.write(f"int *const many_table[{count}] = {{" + ", ".join(f"&g{index}" for index in range(count)) + "};\n")
EOF
sources=("$root/tests/cpp/constant-globals.cpp" "$test_output/many.cpp")
clang++-22 -O2 -DMANY=$many "${sources[@]}" -o "$test_output/native"
"$test_output/native" >"$test_output/native.txt"
run_variants() {
    local label="$1" image="$2" setting
    for setting in "" DOTNET_TieredCompilation=0 DOTNET_TC_QuickJitForLoops=0 DOTNET_TC_AggressiveTiering=1; do
        env ${setting:+"$setting"} dotnet "$image" >"$test_output/out.txt"
        diff -u "$test_output/native.txt" "$test_output/out.txt" || { printf 'FAIL: %s %s\n' "$label" "${setting:-default}" >&2; exit 1; }
    done
}
for runtime in portable system; do
    image="$test_output/$runtime/constant-globals.dll"
    "$llvmnet" --runtime="$runtime" -O2 -DMANY=$many "${sources[@]}" --keep-ir "$test_output/$runtime.bc" -o "$image"
    # Program's fields are initonly and a holder class's are not.
    dotnet "$checks" address-fields "$image" >"$test_output/$runtime-fields.txt"
    grep -Eq '^Program: [0-9]+ address fields, [1-9][0-9]* initonly$' "$test_output/$runtime-fields.txt"
    grep -Eq '^__Statics[0-9]+: [1-9][0-9]* address fields, 0 initonly$' "$test_output/$runtime-fields.txt"
    run_variants "$runtime" "$image"
done
"$llvmnet" --runtime=portable --nativeaot "$test_output/portable.bc" -o "$test_output/aot/constant-globals" >"$test_output/aot.log"
"$test_output/aot/constant-globals" >"$test_output/out.txt"
diff -u "$test_output/native.txt" "$test_output/out.txt"
crossgen2=$(find "$HOME/.nuget/packages/microsoft.netcore.app.crossgen2.linux-x64" -path '*/10.*/tools/crossgen2' -type f 2>/dev/null | sort -V | tail -1 || true)
framework=$(dotnet --list-runtimes | awk '$1 == "Microsoft.NETCore.App" && $2 ~ /^10\./ {gsub(/[][]/, "", $3); path = $3 "/" $2} END {print path}')
if [[ -n "$crossgen2" && "$(uname -sm)" == "Linux x86_64" ]]; then
    mkdir -p "$test_output/r2r"
    cp "$test_output/portable/LlvmNet.Runtime.dll" "$test_output/portable/constant-globals.runtimeconfig.json" "$test_output/r2r/"
    "$crossgen2" "$test_output/portable/constant-globals.dll" -o "$test_output/r2r/constant-globals.dll" -r "$framework/*.dll" \
        -r "$test_output/portable/LlvmNet.Runtime.dll" --targetos:linux --targetarch:x64 -O >"$test_output/r2r.log"
    run_variants "ReadyToRun" "$test_output/r2r/constant-globals.dll"
    printf 'PASS: initonly global addresses read before main and in hot loops (both modes, JIT settings, NativeAOT, ReadyToRun)\n'
else
    printf 'PASS: initonly global addresses read before main and in hot loops (both modes, JIT settings, NativeAOT; ReadyToRun skipped: no crossgen2 for linux-x64 in the NuGet cache)\n'
fi
