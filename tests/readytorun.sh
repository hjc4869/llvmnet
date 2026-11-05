#!/usr/bin/env bash
# tags: toolchain aot
# --readytorun <rid> (JP-06): a JIT image precompiled for one runtime identifier. A generated program with a function
# past RyuJIT's IL threshold (60,000 bytes) prints what the native build prints as a .dll and as a launcher, in
# portable and system mode; the image and LlvmNet.Runtime.dll come with their composite image (program.r2r.dll); with
# tiered compilation off the JIT compiles the large function in the IL image, with minimal optimization, and not in the
# precompiled one, whose code it runs. --readytorun's restore brings crossgen2 and the runtime pack from NuGet, the
# global packages folder or LLVMNET_NUGET_FEED. The option's misuses fail with one line each.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
rm -rf "$test_output"
mkdir -p "$test_output"
cd "$test_output"
case "$(uname -m)" in aarch64 | arm64) rid=linux-arm64 ;; *) rid=linux-x64 ;; esac
python3 - large.c <<'EOF'
import sys
# One function of 2,500 statements on volatile globals, which no optimization removes: about 75 KB of IL.
lines = ["#include <stdio.h>", "volatile unsigned cells[64];", "unsigned large(unsigned seed) {", "    cells[0] = seed;"]
for index in range(1, 2500):
    lines.append(f"    cells[{index % 64}] = cells[{(index * 7) % 64}] * {index % 13 + 3}u + {index}u;")
lines += ["    return cells[17] ^ cells[42];", "}",
          "int main(void) {", "    unsigned total = 0;", "    for (unsigned round = 0; round < 50; round++) total += large(round);",
          '    printf("%u\\n", total);', "    return 0;", "}"]
open(sys.argv[1], "w").write("\n".join(lines) + "\n")
EOF
clang-22 -O2 large.c -o native
./native >native.txt
"$llvmnet" -O2 large.c -o il/large.dll
"$llvmnet" -O2 --readytorun "$rid" large.c -o r2r/large.dll
"$llvmnet" -O2 --readytorun "$rid" large.c -o launcher/large
"$llvmnet" --runtime=system -O2 --readytorun linux-x64 large.c -o system/large.dll
test -f r2r/large.r2r.dll && test -f launcher/large.llvmnet/program.r2r.dll && test -f system/large.r2r.dll
for program in "dotnet il/large.dll" "dotnet r2r/large.dll" "launcher/large" "dotnet system/large.dll"; do
    $program >out.txt
    diff -u native.txt out.txt || { printf 'FAIL: %s differs from native\n' "$program" >&2; exit 1; }
done
# The large function: past the IL threshold, so the JIT compiles it with MinOpts; the precompiled image runs crossgen2's code.
"$llvmnet" -O2 --diagnose-limits large.c -o limits/large.dll 2>limits.txt
grep -Eq ' il[, ].* large$' limits.txt
DOTNET_TieredCompilation=0 DOTNET_JitDisasmSummary=1 DOTNET_JitStdOutFile="$PWD/il-summary.txt" dotnet il/large.dll >/dev/null
DOTNET_TieredCompilation=0 DOTNET_JitDisasmSummary=1 DOTNET_JitStdOutFile="$PWD/r2r-summary.txt" dotnet r2r/large.dll >/dev/null
grep -q 'Program:large(int).*MinOpts' il-summary.txt
if grep -q 'Program:large(' r2r-summary.txt; then printf 'FAIL: the ReadyToRun image jitted large()\n' >&2; exit 1; fi
fails() {  # fails <expected message> <llvmnet arguments...>
    local expected="$1"
    shift
    if "$llvmnet" "$@" >misuse.txt 2>&1; then printf 'FAIL: llvmnet %s succeeded\n' "$*" >&2; exit 1; fi
    grep -qF -- "$expected" misuse.txt || { printf 'FAIL: llvmnet %s: expected "%s"\n' "$*" "$expected" >&2; cat misuse.txt >&2; exit 1; }
}
fails "--readytorun requires an executable link" --readytorun "$rid" --nativeaot large.c -o misuse
fails "--readytorun requires an executable link" --readytorun "$rid" -shared large.c -o misuse.dll
fails "--readytorun requires one of linux-x64" --readytorun linux-riscv64 large.c -o misuse.dll
fails "The current system ABI profile requires --readytorun linux-x64." --runtime=system --readytorun win-x64 large.c -o misuse.dll
printf 'PASS: --readytorun %s images run as native does (.dll, launcher, system mode) and run the precompiled code of a function past the JIT limits\n' "$rid"
