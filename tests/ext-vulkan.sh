#!/usr/bin/env bash
# tags: core aot
# requires: python3 vulkan
# NX-10: the vulkan native extension (extensions/vulkan) against Mesa's lavapipe, through the Khronos loader. The
# extension is generated from the pinned vk.xml into a toolchain root of its own; tests/extensions/vulkan.c, linked with
# -lvulkan, creates an instance with allocation callbacks and two debug messengers (one in the instance's pNext chain,
# one created through vkGetInstanceProcAddr), fills a buffer on the device and reads it back, and calls a function
# through vkGetDeviceProcAddr's pointer. The native build, the portable image under the JIT on lavapipe and on the
# default device, the NativeAOT executable and the system-mode image must print tests/extensions/vulkan.expected; on the
# same driver the portable image sees the allocations and the loader's messages the native build sees.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
rm -rf "$test_output"
mkdir -p "$test_output"
fixtures="$root/tests/extensions"
expected="$fixtures/vulkan.expected"
toolchain="$test_output/toolchain"
extension_root "$toolchain"
bash "$root/scripts/build-extensions.sh" --sysroot "$toolchain/sysroot/dotnet64-v2" --build-dir "$test_output/ext-build" vulkan \
    2>"$test_output/build.log" || { cat "$test_output/build.log" >&2; exit 1; }
grep -q 'verified [0-9]* stubs against the C prototypes llvmnet compiled' "$test_output/build.log" || { cat "$test_output/build.log" >&2; exit 1; }
include="$toolchain/sysroot/dotnet64-v2/ext/vulkan/include"
lavapipe=(env VK_DRIVER_FILES=/usr/share/vulkan/icd.d/lvp_icd.json VK_ICD_FILENAMES=/usr/share/vulkan/icd.d/lvp_icd.json)
# run <label> <command...>: standard output must be the expected lines; standard error keeps the device and counts.
run() {
    local label="$1"
    shift
    if ! "$@" >"$test_output/$label.txt" 2>"$test_output/$label.err"; then
        printf 'FAIL: %s exited with an error\n' "$label" >&2
        cat "$test_output/$label.txt" "$test_output/$label.err" >&2
        exit 1
    fi
    diff -u "$expected" "$test_output/$label.txt" || { printf 'FAIL: %s differs\n' "$label" >&2; cat "$test_output/$label.err" >&2; exit 1; }
    grep -m1 '^device:' "$test_output/$label.err" | sed "s/^/$label: /"
}
clang-22 -O1 -I"$include" "$fixtures/vulkan.c" -l:libvulkan.so.1 -o "$test_output/vulkan-native"
run native-lavapipe "${lavapipe[@]}" "$test_output/vulkan-native"
export LLVMNET_ROOT="$toolchain"
"$llvmnet" -O1 "$fixtures/vulkan.c" -lvulkan -o "$test_output/jit/vulkan.dll"
run jit-lavapipe "${lavapipe[@]}" dotnet "$test_output/jit/vulkan.dll"
run jit-default dotnet "$test_output/jit/vulkan.dll"
# On one driver, the portable program makes the native program's allocations through its callbacks and hears the
# loader's messages through the messenger chained to the instance, which only patched pointers can reach.
for line in '^allocation callbacks:' '^messages:'; do
    diff <(grep "$line" "$test_output/native-lavapipe.err") <(grep "$line" "$test_output/jit-lavapipe.err") ||
        { printf 'FAIL: the portable image differs from native in %s\n' "$line" >&2; exit 1; }
done
grep -q '^messages: [1-9][0-9]* to the chained messenger' "$test_output/jit-lavapipe.err" || { cat "$test_output/jit-lavapipe.err" >&2; exit 1; }
mkdir -p "$test_output/aot"
"$llvmnet" -O1 --nativeaot "$fixtures/vulkan.c" -lvulkan -o "$test_output/aot/vulkan" >"$test_output/aot.log" 2>&1 ||
    { cat "$test_output/aot.log" >&2; exit 1; }
run aot-lavapipe "${lavapipe[@]}" "$test_output/aot/vulkan"
"$llvmnet" --runtime=system -O1 -I"$include" "$fixtures/vulkan.c" --extension=vulkan -o "$test_output/system/vulkan.dll"
run system-lavapipe "${lavapipe[@]}" dotnet "$test_output/system/vulkan.dll"
printf 'PASS: the vulkan extension computes, calls back and allocates through the program as the native build does (JIT, NativeAOT, system mode)\n'
