#!/usr/bin/env bash
# tags: toolchain aot
# TC-21: a program sees the argv[0] and /proc/self/exe that the native build started the same way sees: from its own
# directory, from another one, by an absolute path, through symbolic links (beside it, relative and absolute from
# another directory, a chain, a linked directory) and through PATH. tests/c/launcher-identity.c checks readlink,
# realpath, stat, lstat, access, open and posix_spawn of /proc/self/exe and that the launcher's variables are not in
# the environment. Each build gets a tree of the same layout, under a path with a space; outputs name the tree <root>.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
unset LLVMNET_ARGV0 LLVMNET_EXE
fixture="$root/tests/c/launcher-identity.c"
rm -rf "$test_output"
mkdir -p "$test_output"
base="$(cd "$test_output" && pwd -P)/with space"

# layout <tree>: the links around <tree>/bin/prog and a working directory beside it.
layout() {
    local tree="$1"
    mkdir -p "$tree/links" "$tree/work"
    ln -s prog "$tree/bin/alias"
    ln -s "$tree/bin/prog" "$tree/links/absolute"
    ln -s ../bin/prog "$tree/links/relative"
    ln -s relative "$tree/links/chain"
    ln -s bin "$tree/linked-bin"
}

# starts <tree> [argument]: every way of starting the program, with the tree's root replaced by <root>.
starts() {
    local tree="$1"; shift
    {
        echo "== ./prog"; (cd "$tree/bin" && ./prog "$@")
        echo "== ./alias"; (cd "$tree/bin" && ./alias "$@")
        echo "== ../bin/prog"; (cd "$tree/work" && ../bin/prog "$@")
        echo "== <root>/bin/prog"; (cd "$tree/work" && "$tree/bin/prog" "$@")
        for link in absolute relative chain; do
            echo "== ../links/$link"; (cd "$tree/work" && "../links/$link" "$@")
        done
        echo "== ../linked-bin/prog"; (cd "$tree/work" && ../linked-bin/prog "$@")
        echo "== relative on PATH"; (cd "$tree/work" && PATH="$tree/links:$PATH" relative "$@")
    } | sed "s|$tree|<root>|g"
}

mkdir -p "$base/native/bin"
clang-22 -O1 -w "$fixture" -o "$base/native/bin/prog"
layout "$base/native"
starts "$base/native" >"$test_output/native.txt"
starts "$base/native" names >"$test_output/native-names.txt"
grep -qx 'argv\[0\]: \.\./links/chain' "$test_output/native.txt"
grep -qx 'readlink: <root>/bin/prog' "$test_output/native.txt"

# A script started through PATH gets the path the shell found as $0, where a native program gets the name it was
# started by (docs/user/deployment.md): the one expected difference.
through_path() { sed '/^== relative on PATH$/,/^== /s|^argv\[0\]: <root>/links/relative$|argv[0]: relative|' "$1"; }

compare() {
    local name="$1" expected="$2" actual="$3"
    if ! diff -u "$expected" "$actual"; then
        printf 'FAIL: %s does not report what the native build reports\n' "$name" >&2
        exit 1
    fi
}

mkdir -p "$base/portable/bin"
"$llvmnet" --runtime=portable -O1 -w "$fixture" -o "$base/portable/bin/prog"
layout "$base/portable"
starts "$base/portable" >"$test_output/portable.txt"
grep -qx 'argv\[0\]: <root>/links/relative' "$test_output/portable.txt"
through_path "$test_output/portable.txt" >"$test_output/portable-compared.txt"
compare "the portable launcher" "$test_output/native.txt" "$test_output/portable-compared.txt"

# System mode reads /proc/self/exe through glibc from the kernel, which names the dotnet host; argv[0] and the
# environment come from the runtime.
mkdir -p "$base/system/bin"
"$llvmnet" --runtime=system -O1 -w "$fixture" -o "$base/system/bin/prog"
layout "$base/system"
starts "$base/system" names | through_path /dev/stdin >"$test_output/system-names.txt"
compare "the system-mode launcher" "$test_output/native-names.txt" "$test_output/system-names.txt"

# A NativeAOT executable is a native program: its own argv[0], and the host layer's /proc/self/exe is the executable.
mkdir -p "$base/aot/bin"
"$llvmnet" --runtime=portable -O1 -w --nativeaot "$fixture" -o "$base/aot/bin/prog"
layout "$base/aot"
starts "$base/aot" >"$test_output/aot.txt"
compare "the NativeAOT executable" "$test_output/native.txt" "$test_output/aot.txt"

printf 'PASS: argv[0] and /proc/self/exe match native through the launcher (portable and system mode) and as NativeAOT\n'
