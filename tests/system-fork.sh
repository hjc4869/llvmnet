#!/usr/bin/env bash
# tags: core aot
# fork, vfork and _Fork in system mode. A forked child goes on running translated code, so it needs a private copy of
# the code the runtime generated. Under the JIT, CoreCLR's W^X maps that code from shared memory, which a child would
# share with its parent (a child's close() ran as dup2 and parents crashed or hung): the calls fail with ENOSYS there.
# With DOTNET_EnableWriteXorExecute=0 and under NativeAOT the host's fork runs, and the output equals native.
# posix_spawn, whose child runs no translated code, works in every case.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
rm -rf "$test_output"
mkdir -p "$test_output"
cd "$test_output"
fixture="$root/tests/c/system-fork.c"
clang-22 -O1 "$fixture" -o native
./native >native.txt
grep -qx "fork: read 'child says hi', exit 0" native.txt
grep -qx "posix_spawn: read 'child says hi', exit 0" native.txt
"$llvmnet" --runtime=system -O1 "$fixture" -o fork.dll
# A program that forks keeps the runtime's tiering delay: a child forked while tier-1 compilation runs inherits its locks.
if grep -q CallCountingDelayMs fork.runtimeconfig.json; then printf 'FAIL: fork.runtimeconfig.json sets CallCountingDelayMs\n' >&2; exit 1; fi
sed -E 's/^(fork|vfork|_Fork|fork worker): .*/\1: ENOSYS/' native.txt >jit-expected.txt
for run in 1 2 3; do
    timeout 60 dotnet fork.dll >jit.txt
    diff -u jit-expected.txt jit.txt
    DOTNET_EnableWriteXorExecute=0 timeout 60 dotnet fork.dll >jit-private.txt
    diff -u native.txt jit-private.txt
done
"$llvmnet" --runtime=system --nativeaot -O1 "$fixture" -o fork-aot >aot.log
timeout 60 ./fork-aot >aot.txt
diff -u native.txt aot.txt
printf 'PASS: system-mode fork refused while JIT code is shared memory, native results with private code and NativeAOT; posix_spawn everywhere\n'
