#!/usr/bin/env bash
# tags: core aot
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
output="$root/artifacts/tests/debug-trap"
mkdir -p "$output"
clang-22 -O1 "$root/tests/c/debug-trap.c" -o "$output/native"
"$output/native" > "$output/native.txt"
"$root/bin/llvmnet" --runtime=system -O1 "$root/tests/c/debug-trap.c" -o "$output/trap.dll"
dotnet "$output/trap.dll" > "$output/jit.txt"
diff -u "$output/native.txt" "$output/jit.txt"
"$root/bin/llvmnet" --runtime=system --nativeaot -O1 "$root/tests/c/debug-trap.c" -o "$output/trap-aot"
"$output/trap-aot" > "$output/aot.txt"
diff -u "$output/native.txt" "$output/aot.txt"
clang-22 "$root/tests/ir/compiler-barrier.ll" -o "$output/barrier-native"
"$output/barrier-native"
cp "$root/tests/ir/compiler-barrier.ll" "$output/barrier.input"
for runtime in system portable; do
	"$root/bin/llvmnet" --runtime="$runtime" -x ir -O0 -c "$output/barrier.input" -o "$output/barrier-$runtime.bc"
	"$root/bin/llvmnet" --runtime="$runtime" "$output/barrier-$runtime.bc" -o "$output/barrier-$runtime.dll"
	dotnet "$output/barrier-$runtime.dll"
	"$root/bin/llvmnet" --runtime="$runtime" --nativeaot "$output/barrier-$runtime.bc" -o "$output/barrier-$runtime-aot"
	"$output/barrier-$runtime-aot"
done
for fixture in asm-instruction asm-output asm-comment-instruction; do
	tag_ir portable "$root/tests/ir/rejected/$fixture.ll" "$output/$fixture.bc"
	if "$root/bin/llvmnet" "$output/$fixture.bc" -o "$output/rejected.dll" > "$output/$fixture.log" 2>&1; then
		printf 'FAIL: unsupported assembly was accepted: %s\n' "$fixture" >&2
		exit 1
	fi
	grep -q 'Inline assembly with instructions or outputs is not supported' "$output/$fixture.log"
done
	clang-22 -O1 "$root/tests/c/cpu-query.c" -o "$output/cpu-native"
	"$output/cpu-native" > "$output/cpu-native.txt"
	"$root/bin/llvmnet" --runtime=system -O1 "$root/tests/c/cpu-query.c" -o "$output/cpu.dll"
	dotnet "$output/cpu.dll" > "$output/cpu-jit.txt"
	diff -u "$output/cpu-native.txt" "$output/cpu-jit.txt"
	"$root/bin/llvmnet" --runtime=system --nativeaot -O1 "$root/tests/c/cpu-query.c" -o "$output/cpu-aot"
	"$output/cpu-aot" > "$output/cpu-aot.txt"
	diff -u "$output/cpu-native.txt" "$output/cpu-aot.txt"
	# Portable code's CPU queries answer from the runtime (SV-12): the host's CPUID on x64, nothing where .NET runs no
	# x86 instructions (here with hardware intrinsics off).
	"$root/bin/llvmnet" --runtime=portable -O1 "$root/tests/c/cpu-query.c" -o "$output/cpu-portable.dll"
	dotnet "$output/cpu-portable.dll" > "$output/cpu-portable.txt"
	diff -u "$output/cpu-native.txt" "$output/cpu-portable.txt"
	DOTNET_EnableHWIntrinsic=0 dotnet "$output/cpu-portable.dll" > "$output/cpu-portable-none.txt"
	printf 'vendor=00000000 00000000 00000000 maximum=0\n' | diff -u - "$output/cpu-portable-none.txt"
	printf 'PASS: system traps and exact CPU queries in JIT/NativeAOT, portable CPU queries from the runtime; other assembly rejected\n'