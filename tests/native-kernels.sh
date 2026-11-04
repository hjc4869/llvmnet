#!/usr/bin/env bash
# tags: core aot
# Native kernel packages (ADR-16 Level 3, SV-13). tests/kernels/synthetic has assembly kernels for x86-64 and AArch64,
# which Clang assembles here into a package (runtimes/<rid>/native/ with a shared library for the JIT and a static one
# for NativeAOT; linux-arm64 when the AArch64 cross linker is installed), and a program that calls them directly and
# through a table, with C fallbacks it selects by llvmnet_native_kernels. It must print what the native program linked
# with the kernels prints: under the JIT, which loads the library from beside the image; as NativeAOT, which links the
# static library; and through its C fallbacks where the host has no library. A kernel called anyway must end the process
# with llvmnet's message, exit status 127.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
rm -rf "$test_output"
mkdir -p "$test_output"
cd "$test_output"
kernels="$root/tests/kernels/synthetic"
status() { local code=0; "$@" >/dev/null 2>&1 || code=$?; echo "$code"; }

# The package, as a project's build makes it.
identifiers=(x86_64:linux-x64)
command -v aarch64-linux-gnu-ld >/dev/null && identifiers+=(aarch64:linux-arm64)
runtimes=""
for entry in "${identifiers[@]}"; do
    arch="${entry%%:*}" identifier="${entry#*:}"
    native="package/runtimes/$identifier/native"
    mkdir -p "$native"
    clang-22 --target="$arch-linux-gnu" -c "$kernels/$arch.S" -o "$arch.o"
    llvm-ar-22 rcs "$native/libsynthetic-kernels.a" "$arch.o"
    clang-22 --target="$arch-linux-gnu" -shared -nostdlib -fuse-ld=bfd -Wl,-soname,libsynthetic-kernels.so "$arch.o" -o "$native/libsynthetic-kernels.so"
    runtimes+="${runtimes:+, }\"$identifier\": {\"shared\": \"runtimes/$identifier/native/libsynthetic-kernels.so\", \"static\": \"runtimes/$identifier/native/libsynthetic-kernels.a\"}"
done
printf '{"name": "synthetic", "version": "1.0", "library": "synthetic-kernels", "runtimes": {%s}, "symbols": {"sk_add_i32": {}, "sk_dot_f64": {}, "sk_mix": {}}}\n' "$runtimes" >package/manifest.json

clang-22 -O1 "$kernels/program.c" x86_64.o -o native
./native >native.txt
"$llvmnet" -O1 --native-kernels package "$kernels/program.c" -o jit/program.dll
test -f jit/runtimes/linux-x64/native/libsynthetic-kernels.so
dotnet jit/program.dll | diff -u native.txt -
dotnet "$checks" inspect jit/program.dll | grep -q '^llvmnet.kernels: synthetic/1.0 (linux-x64'
# Without this host's library the program takes its C paths, and a kernel called anyway ends the process.
mv jit/runtimes/linux-x64 hidden
dotnet jit/program.dll | diff -u native.txt -
code=$(status dotnet jit/program.dll kernels)
test "$code" -eq 127
dotnet jit/program.dll kernels 2>missing.err || true
grep -q '^llvmnet: native kernel sk_add_i32 of package synthetic was called, but its linux-x64 library .*libsynthetic-kernels.so does not load' missing.err
mv hidden jit/runtimes/linux-x64
printf '#include <stdio.h>\n#include <llvmnet/cpu.h>\nint main(void) { printf("%%d %%d\\n", llvmnet_native_kernels("synthetic"), llvmnet_native_kernels("other")); return 0; }\n' >available.c
"$llvmnet" -O1 --native-kernels package available.c -o available/available.dll
test "$(dotnet available/available.dll)" = "1 0"
"$llvmnet" -O1 --native-kernels package "$kernels/program.c" --nativeaot -o aot/program >aot.log 2>&1
./aot/program | diff -u native.txt -
if ldd aot/program | grep -q synthetic; then printf 'FAIL: the NativeAOT executable loads the kernel library instead of linking it\n' >&2; exit 1; fi
"$llvmnet" -O1 --native-kernels package "$kernels/program.c" --aot-project project >/dev/null
grep -q "<DirectPInvoke Include=\"synthetic-kernels\" Condition=\"'\$(RuntimeIdentifier)' == 'linux-x64'\"" project/NativeHost.csproj
test -f project/kernels/synthetic/linux-x64/libsynthetic-kernels.a
printf 'PASS: native kernels run under the JIT and NativeAOT, the C paths without the library, and a kernel called anyway stops the process\n'

# What a package cannot do: system mode, kernels with structures by value, a manifest without its libraries.
code=$(status "$llvmnet" --runtime=system --native-kernels package "$kernels/program.c" -o system.dll)
test "$code" -eq 2
printf 'struct triple { long a, b, c; };\nlong sk_add_i32(struct triple t);\nint main(void) { return (int)sk_add_i32((struct triple){1, 2, 3}); }\n' >aggregate.c
"$llvmnet" -O1 --native-kernels package aggregate.c -o aggregate.dll 2>aggregate.err || true
grep -q 'native kernel sk_add_i32 of package synthetic passes a structure by value (byval)' aggregate.err
mkdir -p broken
printf '{"name": "broken", "version": "1", "library": "broken", "runtimes": {"linux-x64": {"shared": "missing.so"}}, "symbols": {"f": {}}}\n' >broken/manifest.json
"$llvmnet" -O1 --native-kernels broken available.c -o broken.dll 2>broken.err || true
grep -q 'broken/manifest.json: the linux-x64 shared library .*missing.so does not exist' broken.err
printf 'PASS: system mode, structure parameters and missing libraries are rejected with their reasons\n'
