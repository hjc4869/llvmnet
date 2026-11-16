#!/usr/bin/env bash
# tags: core
# The ABI tag of a portable object that Clang compiles (XP-17). The sysroot holds a stamp for each ISA level
# (lib/abi/<level>.bc, scripts/build-sysroot.sh): a module with the portable target and nothing but the module flags that
# tag an object of that level. Clang links the stamp into the object it compiles, so the driver does not load its
# in-process LLVM to parse the object and write it again, which on the managed LLVM of a portable toolchain took most of
# the driver's time for a compile. The object must be what the driver's own tagging gives, and what Clang does not
# compile into bitcode (LLVM IR, textual output) or what a sysroot without stamps compiles is still tagged by the driver.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
rm -rf "$test_output"
mkdir -p "$test_output"
cd "$test_output"
sysroot="$root/sysroot/dotnet64-v2"
fail() { printf 'FAIL: %s\n' "$1" >&2; exit 1; }
# The module flags of an object or a stamp, one per line.
flags() { llvm-dis-22 "$1" -o - | grep -E '^!llvm\.module\.flags|"llvmnet\.(abi|isa)"|^![0-9]+ = !\{!"(x86-64-v[23]|wasm-simd128)"\}$'; }
# An object's IR without what names the file and without comments: the driver's tagging reads the module and writes it
# again, which reverses the use lists that the predecessor comments follow.
body() { llvm-dis-22 "$1" -o - | sed -e '/^source_filename/d' -e 's/ *;.*$//'; }

# Each stamp is the target, the data layout and the flags of its level, and nothing else.
target="$(printf 'target datalayout = "%s"\ntarget triple = "x86_64-unknown-none"' 'e-m:e-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-f80:128-n8:16:32:64-S128')"
for isa in baseline x86-64-v2 x86-64-v3 wasm-simd128; do
    [[ -f "$sysroot/lib/abi/$isa.bc" ]] || fail "the sysroot has no ABI stamp for $isa; rebuild it with scripts/build-sysroot.sh --force"
    if [[ "$isa" == baseline ]]; then
        expected="$(printf '%s\n!llvm.module.flags = !{!0}\n!0 = !{i32 1, !"llvmnet.abi", !"dotnet64-v2"}' "$target")"
    else
        expected="$(printf '%s\n!llvm.module.flags = !{!0, !1}\n!0 = !{i32 1, !"llvmnet.abi", !"dotnet64-v2"}\n!1 = !{i32 6, !"llvmnet.isa", !2}\n!2 = !{!"%s"}' "$target" "$isa")"
    fi
    actual="$(llvm-dis-22 "$sysroot/lib/abi/$isa.bc" -o - | sed -e '/^; ModuleID/d' -e '/^source_filename/d' -e '/^$/d')"
    [[ "$actual" == "$expected" ]] || { diff <(printf '%s\n' "$expected") <(printf '%s\n' "$actual") >&2; fail "the ABI stamp of $isa holds something else"; }
done

cat >unit.c <<'EOF'
#include <stdio.h>
#include <string.h>
struct pair { long first, second; };
static long total(const struct pair *pairs, int count)
{
    long sum = 0;
    for (int index = 0; index < count; index++) sum += pairs[index].first * pairs[index].second;
    return sum;
}
int main(int argc, char **argv)
{
    struct pair pairs[3] = { { 1, 2 }, { 3, 4 }, { 5, 6 } };
    printf("%ld %zu\n", total(pairs, 3), strlen(argv[0]) != 0 ? (size_t)argc : 0);
    return 0;
}
EOF
cat >unit.cpp <<'EOF'
#include <numeric>
#include <string>
#include <vector>
#include <cstdio>
int main()
{
    std::vector<std::string> words { "tagged", "by", "the", "frontend" };
    std::printf("%zu\n", std::accumulate(words.begin(), words.end(), std::size_t { 0 }, [](std::size_t length, const std::string &word) { return length + word.size(); }));
}
EOF

# A sysroot without the stamps, where the driver tags the objects itself as it did before there were stamps.
mkdir -p plain/lib
for entry in "$sysroot"/* "$sysroot"/.llvmnet-*; do [[ "${entry##*/}" == lib ]] || ln -s "$entry" plain/; done
for entry in "$sysroot"/lib/*; do [[ "${entry##*/}" == abi ]] || ln -s "$entry" plain/lib/; done

# A compile with the stamps does not use the in-process LLVM at all: it succeeds when there is none to load.
export LLVMNET_LLVM_MISSING="$test_output/no-such/LlvmNet.Llvm.dll"
for isa in baseline x86-64-v2 x86-64-v3; do
    LLVMNET_LLVM="$LLVMNET_LLVM_MISSING" "$llvmnet" --isa="$isa" -O2 -MD -MF "c-$isa.d" -c unit.c -o "c-$isa.o" || fail "the C compile for $isa used the in-process LLVM"
    LLVMNET_LLVM="$LLVMNET_LLVM_MISSING" LLVMNET_DRIVER=clang++ "$llvmnet" --isa="$isa" -O2 -c unit.cpp -o "cpp-$isa.o" || fail "the C++ compile for $isa used the in-process LLVM"
    "$llvmnet" --portable-sysroot "$test_output/plain" --isa="$isa" -O2 -c unit.c -o "c-$isa.plain.o"
    LLVMNET_DRIVER=clang++ "$llvmnet" --portable-sysroot "$test_output/plain" --isa="$isa" -O2 -c unit.cpp -o "cpp-$isa.plain.o"
    for unit in c cpp; do
        [[ "$(flags "$unit-$isa.o")" == "$(flags "$unit-$isa.plain.o")" ]] || { diff <(flags "$unit-$isa.plain.o") <(flags "$unit-$isa.o") >&2; fail "$unit-$isa.o has other module flags than the driver writes"; }
        flags "$unit-$isa.o" | grep -q '"llvmnet.abi", !"dotnet64-v2"' || fail "$unit-$isa.o has no ABI tag"
        [[ "$isa" == baseline ]] || flags "$unit-$isa.o" | grep -q "^![0-9]* = !{!\"$isa\"}\$" || fail "$unit-$isa.o does not record $isa"
        cmp -s <(body "$unit-$isa.o") <(body "$unit-$isa.plain.o") || { diff <(body "$unit-$isa.plain.o") <(body "$unit-$isa.o") | head -n 20 >&2; fail "$unit-$isa.o differs from the object the driver tags"; }
    done
    # The stamp is no dependency of the object: a build would otherwise rebuild everything when the sysroot is rebuilt.
    if grep -q 'lib/abi' "c-$isa.d"; then fail "the dependency file names the ABI stamp"; fi
done
# The baseline object records no level.
if flags c-baseline.o | grep -q 'llvmnet.isa'; then fail "the baseline object records an ISA level"; fi

# What Clang does not write as bitcode is tagged by the driver, which then needs its LLVM.
"$llvmnet" -O1 -S unit.c -o unit.ll
grep -q '"llvmnet.abi", !"dotnet64-v2"' unit.ll || fail "textual output has no ABI tag"
if LLVMNET_LLVM="$LLVMNET_LLVM_MISSING" "$llvmnet" -O1 -S unit.c -o unused.ll 2>/dev/null; then fail "textual output was written without the in-process LLVM"; fi
tag_ir portable "$root/tests/ir/odd-width.ll" from-ir.o
flags from-ir.o | grep -q '"llvmnet.abi", !"dotnet64-v2"' || fail "an object compiled from LLVM IR has no ABI tag"

# The objects link and run, alone and with ones the driver tagged, and an object of another ABI is still refused.
"$llvmnet" c-baseline.o -o c.dll
[[ "$(dotnet c.dll)" == "44 1" ]] || fail "the program of the tagged object printed something else"
LLVMNET_DRIVER=clang++ "$llvmnet" cpp-x86-64-v3.o -o cpp.dll
[[ "$(dotnet cpp.dll)" == "19" ]] || fail "the C++ program of the tagged object printed something else"
cat >other.c <<'EOF'
long scale(long value) { return value * 3; }
EOF
cat >caller.c <<'EOF'
#include <stdio.h>
long scale(long value);
int main(void) { printf("%ld\n", scale(14)); return 0; }
EOF
"$llvmnet" -c caller.c -o caller.o
"$llvmnet" --portable-sysroot "$test_output/plain" -c other.c -o other.plain.o
"$llvmnet" caller.o other.plain.o -o mixed.dll
[[ "$(dotnet mixed.dll)" == "42" ]] || fail "objects tagged by Clang and by the driver do not link"
"$llvmnet" --runtime=system -c other.c -o other.system.o
if "$llvmnet" caller.o other.system.o -o refused.dll 2>refused.log; then fail "a system-mode object linked with a portable one"; fi
grep -q 'ABI mismatch: other.system.o was compiled for system-linux-x64-v1' refused.log || { cat refused.log >&2; fail "the link of mixed ABIs failed for another reason"; }
printf 'PASS: abi-stamp\n'
