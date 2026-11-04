#!/usr/bin/env bash
# tags: core
# Linkage semantics: aliases, weak definitions, undefined weak references, archive extraction
# rules, alias exports from managed libraries, and ifuncs (resolved in system mode, unresolved in portable mode, whose
# target has none). Native clang is the oracle.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
fixtures="$root/tests/c/linkage"
rm -rf "$test_output"
mkdir -p "$test_output"
clang-22 -c "$fixtures/archive-member.c" -o "$test_output/member-native.o"
llvm-ar-22 rcs "$test_output/libmember-native.a" "$test_output/member-native.o"
clang-22 "$fixtures/weak-default.c" "$fixtures/strong.c" "$test_output/libmember-native.a" -o "$test_output/native"
"$test_output/native"
for runtime in system portable; do
    out="$test_output/$runtime"
    mkdir -p "$out"
    "$llvmnet" --runtime="$runtime" -c "$fixtures/archive-member.c" -o "$out/member.o"
    llvm-ar-22 rcs "$out/libmember.a" "$out/member.o"
    "$llvmnet" --runtime="$runtime" "$fixtures/weak-default.c" "$fixtures/strong.c" "$out/libmember.a" -o "$out/weak.dll"
    dotnet "$out/weak.dll"
    "$llvmnet" --runtime="$runtime" -shared "$fixtures/library.c" -o "$out/AliasLibrary.dll"
    "$llvmnet" --runtime="$runtime" "$fixtures/client.c" --cil-reference "$out/AliasLibrary.dll" -o "$out/alias-client.dll"
    dotnet "$out/alias-client.dll"
    abi=dotnet64-v2
    [[ "$runtime" == system ]] && abi=system-linux-x64-v1
    { cat "$root/tests/ir/linkage.ll"; printf '\n!llvm.module.flags = !{!0}\n!0 = !{i32 1, !"llvmnet.abi", !"%s"}\n' "$abi"; } >"$out/linkage.ll"
    "$llvmnet" --runtime="$runtime" "$out/linkage.ll" -o "$out/linkage-ir.dll"
    dotnet "$out/linkage-ir.dll"
    # -O0 keeps the call (at -O1 LLVM resolves this ifunc statically). System mode's ifuncs are resolved by a constructor
    # before the program's own (SV-12); portable targets a freestanding triple, where clang ignores the ifunc attribute
    # and the call is then unresolved.
    if [[ "$runtime" == system ]]; then
        "$llvmnet" --runtime=system -O0 "$fixtures/ifunc.c" -o "$out/ifunc.dll"
        dotnet "$out/ifunc.dll"
    else
        if "$llvmnet" --runtime="$runtime" -O0 "$fixtures/ifunc.c" -o "$out/ifunc.dll" >"$out/ifunc.log" 2>&1; then
            printf 'FAIL: an ifunc was accepted in portable mode, whose target clang gives none\n' >&2
            exit 1
        fi
        grep -q 'Unresolved function: selected' "$out/ifunc.log"
    fi
done
if "$llvmnet" "$root/tests/c/smoke.c" -o "$test_output/System.dll" >"$test_output/collision.log" 2>&1; then
    printf 'FAIL: an output name colliding with a framework assembly was accepted\n' >&2
    exit 1
fi
grep -q 'collides with the .NET framework assembly' "$test_output/collision.log"
printf 'PASS: aliases, weak overrides, undefined weak references, archive rules, alias exports and ifuncs in all modes\n'
