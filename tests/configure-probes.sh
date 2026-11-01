#!/usr/bin/env bash
# tags: core
# Configure probes (TC-07): autoconf, Meson, ffmpeg and CMake check that a function exists by linking a call through a
# prototype without parameters, with a return type that differs from the definition's (tests/c/configure-probe.c).
# Such a call compiles to a run-time trap with a warning, so every probe links exactly when a native clang-22 link
# does, in both modes: C library functions, functions the runtime implements in managed code, a function in a bitcode
# archive, and functions that exist nowhere. Executing the call throws with the diagnostic, -w silences the warning, and
# a contradicting call through a prototype with parameters is still rejected.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
rm -rf "$test_output"
mkdir -p "$test_output"
cd "$test_output"
probe="$root/tests/c/configure-probe.c"
# A library function with parameters, as libjpeg's jpeg_mem_src(j_decompress_ptr, const unsigned char *, size_t).
printf '#include <stddef.h>\nvoid lib_mem_src(void *c, const unsigned char *b, size_t n) { (void)c; (void)b; (void)n; }\n' >lib.c
clang-22 -c lib.c -o lib-native.o
llvm-ar-22 rcs liblib-native.a lib-native.o
for runtime in portable system; do
    "$llvmnet" --runtime="$runtime" -c lib.c -o "lib-$runtime.o"
    llvm-ar-22 rcs "liblib-$runtime.a" "lib-$runtime.o"
done
probes=0
# check <style> <function> <runtime>: the probe links with llvmnet exactly when it links natively. A system-mode call to
# the host's C library goes through a native bridge with the call site's signature, as a native call does; every
# other call that links is a trap.
check() {
    local style="$1" function="$2" runtime="$3" name="probe$1-$2-$3" native=no managed=no
    clang-22 -w -std=gnu17 -O2 -DSTYLE="$style" -DPROBE="$function" "$probe" -L. -llib-native -o "$name-native" 2>/dev/null && native=yes
    "$llvmnet" --runtime="$runtime" -std=gnu17 -Wno-deprecated-non-prototype -Wno-incompatible-library-redeclaration -O2 \
        -DSTYLE="$style" -DPROBE="$function" "$probe" -L. -llib-"$runtime" -o "$name" 2>"$name.log" && managed=yes
    if [[ "$native" != "$managed" ]]; then
        printf 'FAIL: probe style %s for %s links natively: %s, with llvmnet in %s mode: %s; see %s\n' \
            "$style" "$function" "$native" "$runtime" "$managed" "$test_output/$name.log" >&2
        exit 1
    fi
    if [[ "$managed" == yes && ! ("$runtime" == system && "$function" =~ ^(gmtime_r|open|mmap)$) ]] && ! grep -q 'warning: emitting runtime trap: Invalid direct call through a prototype without parameters' "$name.log"; then
        printf 'FAIL: probe style %s for %s in %s mode linked without the trap warning; see %s\n' "$style" "$function" "$runtime" "$test_output/$name.log" >&2
        exit 1
    fi
    if [[ "$managed" == no ]] && ! grep -Eq "Unresolved (function:|system ABI symbol) $function" "$name.log"; then
        printf 'FAIL: probe style %s for %s in %s mode failed for another reason; see %s\n' "$style" "$function" "$runtime" "$test_output/$name.log" >&2
        exit 1
    fi
    probes=$((probes + 1))
}
# gmtime_r returns a pointer, open is variadic, setjmp and longjmp are implemented by the runtime, pthread_create and
# atexit by system-mode adapters; gmtime_s and the last name exist in neither C library.
for runtime in portable system; do
    for function in gmtime_r open mmap setjmp longjmp pthread_create atexit lib_mem_src gmtime_s llvmnet_no_such_function; do
        check 1 "$function" "$runtime"
    done
done
for style in 2 3; do
    for function in gmtime_r lib_mem_src llvmnet_no_such_function; do check "$style" "$function" portable; done
done
# Executing a trapped call stops the program with the diagnostic.
for program in probe1-gmtime_r-portable probe1-lib_mem_src-portable probe1-setjmp-portable probe1-pthread_create-system; do
    if "./$program" >"$program.run.log" 2>&1; then
        printf 'FAIL: %s ran the contradicting call without trapping\n' "$program" >&2
        exit 1
    fi
    grep -q 'InvalidProgramException: Invalid direct call through a prototype without parameters: main -> ' "$program.run.log" ||
        { printf 'FAIL: %s did not report the trap; see %s\n' "$program" "$test_output/$program.run.log" >&2; exit 1; }
done
# -w silences the warning, as it silences the frontend's.
"$llvmnet" -w -DSTYLE=1 -DPROBE=gmtime_r "$probe" -o quiet 2>quiet.log
if [[ -s quiet.log ]]; then
    printf 'FAIL: -w did not silence the trap warning; see %s\n' "$test_output/quiet.log" >&2
    exit 1
fi
# Through a prototype with parameters, the contradiction is still a compile error.
printf 'int lib_mem_src(int);\nint main(void) { return lib_mem_src(1); }\n' >parameters.c
for runtime in portable system; do
    if "$llvmnet" --runtime="$runtime" parameters.c -L. -llib-"$runtime" -o parameters >"parameters-$runtime.log" 2>&1; then
        printf 'FAIL: %s mode accepted a call whose prototype with parameters contradicts the definition\n' "$runtime" >&2
        exit 1
    fi
    grep -q 'Incompatible direct call signature for lib_mem_src' "parameters-$runtime.log"
done
printf 'PASS: %d configure probes link as natively in both modes; executed calls trap, -w silences the warning, prototypes with parameters stay rejected\n' "$probes"
