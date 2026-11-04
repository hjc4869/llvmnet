#!/usr/bin/env bash
# tags: core aot
# requires: python3 lld
# Native kernel packages (ADR-16 Level 3, SV-13). tests/kernels/synthetic has assembly kernels for x86-64 and AArch64,
# which Clang assembles and scripts/kernels.py makes into a package (runtimes/<rid>/native/ with a shared library for the
# JIT and a static one for NativeAOT, linked by lld), and a program that calls them directly and through a table, with C
# fallbacks it selects by llvmnet_native_kernels, and shares data with them both ways. It must print what the native
# program linked with the kernels prints: under the JIT, which loads the library from beside the image; as NativeAOT,
# which links the static library; and through its C fallbacks where the host has no library. A kernel called anyway must
# end the process with llvmnet's message, exit status 127.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
rm -rf "$test_output"
mkdir -p "$test_output"
cd "$test_output"
kernels="$root/tests/kernels/synthetic"
status() { local code=0; "$@" >/dev/null 2>&1 || code=$?; echo "$code"; }

# The package, as a project's build makes it: the kernels' objects for each identifier (the Windows and macOS ones in
# their own conventions), and the program's bitcode, whose data the kernels read.
clang-22 --target=x86_64-linux-gnu -c "$kernels/x86_64.S" -o x86_64.o
clang-22 --target=aarch64-linux-gnu -c "$kernels/aarch64.S" -o aarch64.o
clang-22 --target=x86_64-pc-windows-msvc -c "$kernels/x86_64-windows.S" -o x86_64-windows.o
clang-22 --target=arm64-apple-macos11 -c "$kernels/aarch64-macos.S" -o aarch64-macos.o
"$llvmnet" -O1 -c "$kernels/program.c" -o program.o
python3 "$root/scripts/kernels.py" --llvmnet "$llvmnet" --name synthetic --version 1.0 --library synthetic-kernels --output package \
    --runtime linux-x64 x86_64.o --runtime linux-arm64 aarch64.o --runtime win-x64 x86_64-windows.o --runtime osx-arm64 aarch64-macos.o \
    --bitcode program.o --not-leaf sk_dot_f64 >package.log
python3 -c 'import json, sys; m = json.load(open("package/manifest.json")); sys.exit(m["data"] != ["sk_counts", "sk_scale", "sk_weights"] or m["symbols"]["sk_dot_f64"] != {"leaf": False} or sorted(m["runtimes"]) != ["linux-arm64", "linux-x64", "osx-arm64", "win-x64"])'
native="package/runtimes"
llvm-nm-22 -D --defined-only "$native/linux-x64/native/libsynthetic-kernels.so" | grep -q ' T llvmnet_kernel_data_sk_weights$'
llvm-readobj-22 --coff-exports "$native/win-x64/native/synthetic-kernels.dll" | grep -q 'Name: sk_weighted$'
llvm-nm-22 -g "$native/osx-arm64/native/libsynthetic-kernels.dylib" | grep -q ' T _llvmnet_kernel_data_sk_scale$'
# The static libraries hold the objects, not an import library (lld-link writes one beside the DLL unless told not to).
llvm-nm-22 "$native/win-x64/native/synthetic-kernels.lib" | grep -q ' T sk_add_i32$'
llvm-nm-22 "$native/osx-arm64/native/libsynthetic-kernels.a" | grep -q ' T _sk_count$'

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
# An archive that holds the kernels' native object beside the program's bitcode, as ffmpeg's do: the package takes the
# native member, llvmnet links the bitcode and leaves the native member to the package, and rejects it without one.
llvm-ar-22 rcs mixed.a x86_64.o program.o
python3 "$root/scripts/kernels.py" --llvmnet "$llvmnet" --name synthetic --version 1.0 --library synthetic-kernels --output mixed \
    --runtime linux-x64 mixed.a --bitcode mixed.a >mixed.log
"$llvmnet" -O1 --native-kernels mixed mixed.a -o mixed-image/program.dll
dotnet mixed-image/program.dll | diff -u native.txt -
"$llvmnet" -O1 mixed.a -o unmixed.dll 2>unmixed.err || true
grep -q 'mixed.a(x86_64.o) is a native object file, such as assembled code; llvmnet links bitcode, and calls native code through a native kernel package' unmixed.err
# An optional kernel, which the program calls whatever the CPU (ffmpeg's ff_emms_asm), does nothing without the library.
python3 "$root/scripts/kernels.py" --llvmnet "$llvmnet" --name synthetic --version 1.0 --library synthetic-kernels --output optional \
    --runtime linux-x64 x86_64.o --bitcode program.o --optional sk_count sk_mix >optional.log
printf '#include <stdint.h>\n#include <stdio.h>\n#include <llvmnet/cpu.h>\nextern int32_t sk_counts[4];\nvoid sk_count(int32_t index);\nint main(void) { sk_count(1); printf("%%d\\n", llvmnet_native_kernels("synthetic") ? sk_counts[1] : -1); return 0; }\n' >reset.c
"$llvmnet" -O1 --native-kernels optional reset.c -o reset/reset.dll
test "$(dotnet reset/reset.dll)" = 21
rm -r reset/runtimes/linux-x64
test "$(dotnet reset/reset.dll)" = -1
"$llvmnet" -O1 --native-kernels optional "$kernels/program.c" -o optional.dll 2>optional.err || true
grep -q 'native kernel sk_mix of package synthetic is optional but returns a value' optional.err
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
