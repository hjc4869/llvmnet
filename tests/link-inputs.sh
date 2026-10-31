#!/usr/bin/env bash
# tags: core
# TC-02: link inputs. -c on .ll, .bc and .o inputs; -l:<file>; archives in any order with rescanning; -Wl,--whole-archive;
# duplicate definitions reported with both files; -r of archives; -nostdlib, -nostartfiles and -nodefaultlibs.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
fixtures="$root/tests/c/link"
rm -rf "$test_output"
expect_failure() {
    local log="$1" pattern="$2"
    shift 2
    if "$@" >"$log" 2>&1; then
        printf 'FAIL: expected a failure from: %s\n' "$*" >&2
        exit 1
    fi
    grep -q -E "$pattern" "$log" || { printf 'FAIL: "%s" not in the error:\n' "$pattern" >&2; cat "$log" >&2; exit 1; }
}
for runtime in system portable; do
    out="$test_output/$runtime"
    mkdir -p "$out"
    compiler=("$llvmnet" --runtime="$runtime")
    "${compiler[@]}" -c "$root/tests/c/link-main.c" -o "$out/main.o"
    "${compiler[@]}" -c "$root/tests/c/link-library.c" -o "$out/library.o"
    # -c accepts LLVM IR by extension and objects, which keep their tag; the output links like any object.
    "${compiler[@]}" -S -emit-llvm "$root/tests/c/link-library.c" -o "$out/library.ll"
    "${compiler[@]}" -c "$out/library.ll" -o "$out/from-ll.o" 2>"$out/from-ll.log"
    cp "$out/library.o" "$out/library.bc"
    "${compiler[@]}" -c "$out/library.bc" -o "$out/from-bc.o"
    "${compiler[@]}" -c "$out/library.o" -o "$out/from-o.o"
    for object in from-ll from-bc from-o; do
        "${compiler[@]}" "$out/main.o" "$out/$object.o" -o "$out/$object.dll"
        dotnet "$out/$object.dll"
    done
    # -l:<file> names the archive file exactly; -l<name> looks for lib<name>.a.
    llvm-ar-22 rcs "$out/example.a" "$out/library.o"
    "${compiler[@]}" "$out/main.o" -L"$out" -l:example.a -o "$out/exact.dll"
    dotnet "$out/exact.dll"
    expect_failure "$out/missing.log" 'Cannot find LLVM bitcode archive libmissing.a in the -L directories' "${compiler[@]}" "$out/main.o" -L"$out" -lmissing -o "$out/missing.dll"
    # Archives resolve in any order: libneeds.a needs provided() from libprovides.a, which is named first.
    "${compiler[@]}" -c "$fixtures/needs.c" -o "$out/needs.o"
    "${compiler[@]}" -c "$fixtures/provides.c" -o "$out/provides.o"
    llvm-ar-22 rcs "$out/libneeds.a" "$out/needs.o"
    llvm-ar-22 rcs "$out/libprovides.a" "$out/provides.o"
    "${compiler[@]}" "$fixtures/main-needs.c" -L"$out" -lprovides -lneeds -o "$out/rescan.dll"
    dotnet "$out/rescan.dll" | grep -q '^needs 5$'
    # -Wl,--whole-archive links every member of the archives that follow; otherwise an unreferenced member stays out.
    "${compiler[@]}" -c "$fixtures/ctor.c" -o "$out/ctor.o"
    llvm-ar-22 rcs "$out/libctor.a" "$out/ctor.o"
    "${compiler[@]}" "$fixtures/main-needs.c" -L"$out" -lprovides -lneeds -lctor -o "$out/without-ctor.dll"
    if dotnet "$out/without-ctor.dll" | grep -q constructor; then
        printf 'FAIL: an unreferenced archive member was linked without --whole-archive (%s)\n' "$runtime" >&2
        exit 1
    fi
    "${compiler[@]}" "$fixtures/main-needs.c" -L"$out" -lprovides -lneeds -Wl,--whole-archive -lctor -Wl,--no-whole-archive -o "$out/with-ctor.dll"
    dotnet "$out/with-ctor.dll" | grep -q '^constructor from the archive$'
    "${compiler[@]}" "$fixtures/main-needs.c" -L"$out" -lprovides -lneeds -Xlinker --whole-archive -Xlinker -lctor -o "$out/xlinker-ctor.dll" 2>"$out/xlinker.log" || true
    # A duplicate definition names both files, for objects and for archive members.
    "${compiler[@]}" -c "$fixtures/dup.c" -o "$out/dup.o"
    expect_failure "$out/dup.log" "duplicate definition of 'calculate' in .*dup.o; it is already defined in .*library.o" "${compiler[@]}" "$out/main.o" "$out/library.o" "$out/dup.o" -o "$out/dup.dll"
    llvm-ar-22 rcs "$out/libdup.a" "$out/dup.o"
    expect_failure "$out/dup-archive.log" "duplicate definition of 'calculate' in .*libdup.a\(dup.o\); it is already defined in .*library.o" "${compiler[@]}" "$out/main.o" "$out/library.o" -L"$out" -Wl,--whole-archive -ldup -o "$out/dup-archive.dll"
    # -r links archives too: the needed members become part of the relocatable object, and --whole-archive all of them.
    "${compiler[@]}" -r "$out/main.o" -L"$out" -l:example.a -o "$out/combined.o"
    llvm-nm-22 --defined-only "$out/combined.o" | grep -q ' T calculate$'
    "${compiler[@]}" "$out/combined.o" -o "$out/combined.dll"
    dotnet "$out/combined.dll"
    "${compiler[@]}" -r -L"$out" -Wl,--whole-archive -lctor -o "$out/all-members.o"
    llvm-nm-22 --defined-only "$out/all-members.o" | grep -q ' T archived_only$'
    expect_failure "$out/only-archives.log" 'at least one explicit object or source as the link root' "${compiler[@]}" -r -L"$out" -lctor -o "$out/no-root.o"
    printf 'PASS: %s: -c on IR and objects, -l:<file>, archive order and rescanning, whole archives, duplicate diagnostics, -r of archives\n' "$runtime"
done
# An object of the other mode is rejected by -c as by a link.
expect_failure "$test_output/mismatch.log" 'ABI mismatch' "$llvmnet" --runtime=portable -c "$test_output/system/library.o" -o "$test_output/mismatch.o"
# -nostdlib leaves out crt1.o and every sysroot library: the entry point calls main directly.
sysroot="$root/sysroot/dotnet64-v2/lib"
"$llvmnet" --runtime=portable -nostdlib "$fixtures/freestanding.c" -o "$test_output/freestanding.dll"
status=0
dotnet "$test_output/freestanding.dll" || status=$?
test "$status" -eq 42
# -nodefaultlibs keeps the start object; the sysroot archives named explicitly make an ordinary program again.
"$llvmnet" --runtime=portable -nodefaultlibs "$fixtures/main-needs.c" "$test_output/portable/provides.o" "$test_output/portable/needs.o" "$sysroot/libc.a" "$sysroot/libclang_rt.builtins.a" -o "$test_output/explicit-libc.dll"
dotnet "$test_output/explicit-libc.dll" | grep -q '^needs 5$'
expect_failure "$test_output/nodefaultlibs.log" 'Unresolved function' "$llvmnet" --runtime=portable -nodefaultlibs "$fixtures/main-needs.c" "$test_output/portable/provides.o" "$test_output/portable/needs.o" -o "$test_output/no-libc.dll"
# -nostartfiles alone: main runs without musl's initialization, which a program that only returns can afford.
"$llvmnet" --runtime=portable -nostartfiles "$fixtures/freestanding.c" -o "$test_output/nostart.dll"
status=0
dotnet "$test_output/nostart.dll" || status=$?
test "$status" -eq 42
printf 'PASS: mode mismatch on -c, -nostdlib, -nodefaultlibs with explicit sysroot archives, -nostartfiles\n'
