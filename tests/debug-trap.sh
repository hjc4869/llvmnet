#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output="$root/artifacts/tests/debug-trap"
mkdir -p "$output"
dotnet build "$root/src/LlvmNet.Compiler" -c Release --nologo
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
	if "$root/bin/llvmnet" "$root/tests/ir/rejected/$fixture.ll" -o "$output/rejected.dll" > "$output/$fixture.log" 2>&1; then
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
	if "$root/bin/llvmnet" --runtime=portable -O1 "$root/tests/c/cpu-query.c" -o "$output/cpu-portable.dll" > "$output/cpu-portable.log" 2>&1; then
		printf 'FAIL: portable ABI accepted host CPU-query assembly\n' >&2
		exit 1
	fi
	grep -q 'Inline assembly with instructions or outputs is not supported' "$output/cpu-portable.log"
	printf 'PASS: system traps and exact CPU queries in JIT/NativeAOT; other assembly and portable CPU queries rejected\n'