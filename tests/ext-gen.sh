#!/usr/bin/env bash
# tags: core aot
# requires: python3
# NX-04: the native extension generator (scripts/ext-gen.py, scripts/build-extensions.sh). The synthetic registry
# tests/extensions/generated/registry.xml describes an API in two host libraries with every kind of entry point the
# generator handles (narrow signed and unsigned integers, 64-bit and pointer-sized integers, floating point, strings,
# buffers, opaque handles, callbacks with reverse thunks, a lookup function, a default implementation, an excluded
# platform type, an entry point that returns a host function pointer). The test builds the host libraries natively
# with clang, the extension into a toolchain root of its own, and tests/extensions/generated.c three ways: natively,
# in portable mode with -l (JIT and NativeAOT) and in system mode with --extension; all print the same. The portable
# image's own behavior is checked too: availability, a missing host library, a callback pool that runs out, a callback
# type without thunks, the unsupported entry point. Descriptions with types the portable ABI cannot proxy are rejected,
# and generating twice gives the same files.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
rm -rf "$test_output"
mkdir -p "$test_output"
fixtures="$root/tests/extensions"
description="$fixtures/generated/extension.json"
include="$fixtures/generated/include"
native="$test_output/native"
mkdir -p "$native" "$test_output/empty"
clang-22 -shared -fPIC -O1 -I"$include" "$fixtures/generated/native-aux.c" -Wl,-soname,libllvmnet-gen-aux.so.1 \
    -o "$native/libllvmnet-gen-aux.so.1"
clang-22 -shared -fPIC -O1 -I"$include" "$fixtures/generated/native.c" -Wl,-soname,libllvmnet-gen.so.1 -L"$native" \
    -l:libllvmnet-gen-aux.so.1 -o "$native/libllvmnet-gen.so.1"
clang-22 -O1 -I"$include" "$fixtures/generated.c" -L"$native" -l:libllvmnet-gen.so.1 -l:libllvmnet-gen-aux.so.1 \
    -o "$test_output/generated-native"
LD_LIBRARY_PATH="$native" "$test_output/generated-native" >"$test_output/native.txt"

toolchain="$test_output/toolchain"
extension_root "$toolchain"
bash "$root/scripts/build-extensions.sh" --sysroot "$toolchain/sysroot/dotnet64-v2" --build-dir "$test_output/ext-build" "$description" \
    2>"$test_output/build.log" || { cat "$test_output/build.log" >&2; exit 1; }
grep -q 'generated 2.1.0: 25 entry points, 1 that fail when called, 1 left out, 2 callback types with reverse thunks' "$test_output/build.log" ||
    { cat "$test_output/build.log" >&2; exit 1; }
grep -q 'verified 26 stubs against the C prototypes llvmnet compiled' "$test_output/build.log"
# An unchanged description is not rebuilt; the same inputs generate the same files.
bash "$root/scripts/build-extensions.sh" --sysroot "$toolchain/sysroot/dotnet64-v2" --build-dir "$test_output/ext-build" "$description" \
    2>&1 | grep -q '^Extension generated is up to date'
python3 -I "$root/scripts/ext-gen.py" generate "$description" --output "$test_output/again" --llvmnet-version 0.0.0 >/dev/null
python3 -I "$root/scripts/ext-gen.py" generate "$description" --output "$test_output/again2" --llvmnet-version 0.0.0 >/dev/null
diff -r "$test_output/again" "$test_output/again2"
printf 'PASS: the synthetic registry generates, verifies and installs an extension; generation is deterministic\n'

export LLVMNET_ROOT="$toolchain"
"$llvmnet" -O1 "$fixtures/generated.c" -lllvmnet-gen -o "$test_output/jit/generated.dll"
LLVMNET_EXT_GENERATED_DIR="$native" dotnet "$test_output/jit/generated.dll" | diff -u "$test_output/native.txt" -
mkdir -p "$test_output/aot"
"$llvmnet" -O1 --nativeaot "$fixtures/generated.c" -lllvmnet-gen -o "$test_output/aot/generated" >"$test_output/aot.log" 2>&1 ||
    { cat "$test_output/aot.log" >&2; exit 1; }
LLVMNET_EXT_GENERATED_DIR="$native" "$test_output/aot/generated" | diff -u "$test_output/native.txt" -
# The non-blocking entry points (genAdd, genSwap) skip the GC transition when the run asks for it (NX-11).
LLVMNET_EXT_GENERATED_DIR="$native" LLVMNET_EXT_GENERATED_NON_BLOCKING=1 dotnet "$test_output/jit/generated.dll" | diff -u "$test_output/native.txt" -
LLVMNET_EXT_GENERATED_DIR="$native" LLVMNET_EXT_GENERATED_NON_BLOCKING=1 "$test_output/aot/generated" | diff -u "$test_output/native.txt" -
# Callbacks on threads the host library starts get musl thread state (NX-08), also while collections run often, so
# that the finalizers that release it run during the program.
LD_LIBRARY_PATH="$native" "$test_output/generated-native" threads >"$test_output/threads-native.txt"
LLVMNET_EXT_GENERATED_DIR="$native" dotnet "$test_output/jit/generated.dll" threads | diff -u "$test_output/threads-native.txt" -
LLVMNET_EXT_GENERATED_DIR="$native" DOTNET_GCgen0size=0x20000 dotnet "$test_output/jit/generated.dll" threads | diff -u "$test_output/threads-native.txt" -
LLVMNET_EXT_GENERATED_DIR="$native" "$test_output/aot/generated" threads | diff -u "$test_output/threads-native.txt" -
LIBRARY_PATH="$native" LD_LIBRARY_PATH="$native" "$llvmnet" --runtime=system -O1 -DLLVMNET_SYSTEM_TEST -I"$include" \
    "$fixtures/generated.c" --extension=generated -o "$test_output/system/generated.dll"
LD_LIBRARY_PATH="$native" dotnet "$test_output/system/generated.dll" | diff -u "$test_output/native.txt" -
LD_LIBRARY_PATH="$native" dotnet "$test_output/system/generated.dll" system >"$test_output/system.txt"
diff -u - "$test_output/system.txt" <<'EOF'
available: 1
library: generated: linked in system mode with libllvmnet-gen-aux.so.1 libllvmnet-gen.so.1
EOF
printf 'PASS: the generated extension prints what native prints under the JIT, NativeAOT (also with the non-blocking calls) and system mode\n'

# expect_failure <expected stderr line> <command...>: the command must exit with 127 and print exactly that line last.
expect_failure() {
    local expected="$1" status=0
    shift
    "$@" >"$test_output/failure.out" 2>"$test_output/failure.err" || status=$?
    if [[ "$status" -ne 127 || "$(tail -n 1 "$test_output/failure.err")" != "$expected" ]]; then
        printf 'FAIL: %s exited with %d; expected 127 and:\n%s\nstderr:\n' "$*" "$status" "$expected" >&2
        cat "$test_output/failure.err" >&2
        exit 1
    fi
}
LLVMNET_EXT_GENERATED_DIR="$native" dotnet "$test_output/jit/generated.dll" portable >"$test_output/portable.txt"
diff -u - <(sed "s|$native|NATIVE|g" "$test_output/portable.txt") <<'EOF'
available: 1
library: GenAux: NATIVE/libllvmnet-gen-aux.so.1
Gen: NATIVE/libllvmnet-gen.so.1
not linked: 0 null
status: 0x37
lookup: set
dlopen: set
twice direct: 42
EOF
image="$test_output/jit"
tried="tried $test_output/empty/libllvmnet-gen.so.1, $image/libllvmnet-gen.so.1, libllvmnet-gen.so.1): libllvmnet-gen.so.1: cannot open shared object file: No such file or directory"
expect_failure "llvmnet: genTwiceEXT: the host library Gen of the native extension generated is not available ($tried" \
    env LLVMNET_EXT_GENERATED_DIR="$test_output/empty" dotnet "$image/generated.dll" portable
diff -u - <(sed "s|$test_output|OUT|g" "$test_output/failure.out") <<'EOF'
available: 0
library: GenAux: the host library GenAux of the native extension generated is not available (tried OUT/empty/libllvmnet-gen-aux.so.1, OUT/jit/libllvmnet-gen-aux.so.1, libllvmnet-gen-aux.so.1): libllvmnet-gen-aux.so.1: cannot open shared object file: No such file or directory
Gen: the host library Gen of the native extension generated is not available (tried OUT/empty/libllvmnet-gen.so.1, OUT/jit/libllvmnet-gen.so.1, libllvmnet-gen.so.1): libllvmnet-gen.so.1: cannot open shared object file: No such file or directory
not linked: 0 null
status: 0x77
lookup: null
dlopen: the host library Gen of the native extension generated is not available (tried OUT/empty/libllvmnet-gen.so.1, OUT/jit/libllvmnet-gen.so.1, libllvmnet-gen.so.1): libllvmnet-gen.so.1: cannot open shared object file: No such file or directory
EOF
expect_failure 'llvmnet: the native extension generated has entry points for 2 distinct GENlogproc callbacks, and the program passed another' \
    env LLVMNET_EXT_GENERATED_DIR="$native" dotnet "$image/generated.dll" exhaust
expect_failure 'llvmnet: genSetOther: the native extension generated has no reverse thunks for GENotherproc callbacks, so it can pass only a null one' \
    env LLVMNET_EXT_GENERATED_DIR="$native" dotnet "$image/generated.dll" other
grep -qx 'a null callback passes' "$test_output/failure.out"
expect_failure 'llvmnet: genGetOther: it returns a host function pointer (GENotherproc), which a portable program cannot call; the native extension generated does not provide it' \
    env LLVMNET_EXT_GENERATED_DIR="$native" dotnet "$image/generated.dll" returned
printf 'PASS: availability, defaults, a missing host library, callback pools and unsupported entry points behave as documented\n'

# Types the portable ABI cannot proxy are build errors of the extension, naming the entry point and the reason.
reject() { # <C parameter text> <expected message>
    local case="$test_output/reject" status=0
    rm -rf "$case"
    mkdir -p "$case/include"
    sed "s|<param><ptype>GENint</ptype> <name>b</name></param>|<param>$1 <name>b</name></param>|" \
        "$fixtures/generated/registry.xml" >"$case/registry.xml"
    cp "$description" "$case/extension.json"
    cp "$include/llvmnet-gen.h" "$case/include/"
    python3 -I "$root/scripts/ext-gen.py" generate "$case/extension.json" --output "$case/out" --llvmnet-version 0.0.0 \
        >/dev/null 2>"$case/error.txt" || status=$?
    if [[ "$status" -ne 1 ]] || ! grep -qF "$2" "$case/error.txt"; then
        printf 'FAIL: a parameter %s was not rejected with "%s":\n' "$1" "$2" >&2
        cat "$case/error.txt" >&2
        exit 1
    fi
}
reject 'long' 'genAdd in registry.xml: long: long is 64 bits in the portable ABI but 32 bits on Windows'
reject 'unsigned long' 'genAdd in registry.xml: unsigned long: unsigned long is 64 bits in the portable ABI but 32 bits on Windows'
reject 'long double' 'genAdd in registry.xml: long double: long double is 80-bit extended precision'
reject 'struct GENobject_T' 'genAdd in registry.xml: struct GENobject_T is passed by value; structures by value are not supported yet (NX-07)'
reject 'GENunknown' "genAdd in registry.xml: unknown C type 'GENunknown'"
printf 'PASS: long, long double, structures by value and unknown types are rejected with the entry point and the reason\n'
