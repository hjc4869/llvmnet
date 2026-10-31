#!/usr/bin/env bash
# tags: core libc aot
# requires: python3
# Time zones without zoneinfo (LC-09): where the host has no zoneinfo files, the host layer serves TZif images
# generated from .NET's TimeZoneInfo. tests/c/zoneinfo.c converts instants between 1902 and 2037 in several zones;
# the portable build must print the same with the generated images (LLVMNET_ZONEINFO=managed) as with the host's
# files, which in turn must match the native build. The one documented difference: TimeZoneInfo holds offsets in
# whole minutes, so the local-mean-time offsets with seconds that tzdata has for the years before standard time
# (America/St_Johns until 1935, Asia/Tehran until 1946) are rounded; the instants native resolves to such an offset
# are left out of the comparison with the generated images.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
rm -rf "$test_output"
mkdir -p "$test_output"
source_file="$root/tests/c/zoneinfo.c"
# The first three zones also get an hourly sweep of 2024: a northern, a southern and a half-hour zone with daylight
# time. The others cover abolished daylight time, a date-line jump, 45-minute offsets and a bare UTC.
zones=()
for zone in Europe/Berlin America/New_York Australia/Lord_Howe America/St_Johns Asia/Kolkata America/Sao_Paulo \
        Pacific/Auckland Asia/Tehran Pacific/Apia Asia/Kathmandu Europe/Moscow Africa/Casablanca UTC; do
    if [[ -f "/usr/share/zoneinfo/$zone" ]]; then zones+=("$zone"); fi
done
if [[ ${#zones[@]} -lt 3 ]]; then printf 'SKIP: no zoneinfo on this host\n'; exit 0; fi
clang-22 -O1 -w "$source_file" -o "$test_output/native"
env -u TZ "$test_output/native" "${zones[@]}" >"$test_output/native.txt"
"$llvmnet" --runtime=portable -O1 -w "$source_file" -o "$test_output/zoneinfo.dll"
env -u TZ dotnet "$test_output/zoneinfo.dll" "${zones[@]}" >"$test_output/host.txt"
diff -u "$test_output/native.txt" "$test_output/host.txt"
# Every instant is a multiple of an hour, so a local time with seconds means an offset with seconds.
whole_minutes() {
    python3 - "$test_output/native.txt" "$1" <<'PYEOF'
import re, sys
native, other = sys.argv[1:3]
def lines(path):
    zone = None
    for line in open(path):
        if line.startswith('zone '): zone = line
        yield (zone, line.split()[0] if line[:1] in '-0123456789' else line), line
seconds = {key for key, line in lines(native) if re.match(r'^\S+ \S+ \d\d:\d\d:(?!00)\d\d ', line)}
for path in (native, other):
    with open(path + '.minutes', 'w') as output:
        output.writelines(line for key, line in lines(path) if key not in seconds)
PYEOF
    diff -u "$test_output/native.txt.minutes" "$1.minutes"
}
env -u TZ LLVMNET_ZONEINFO=managed dotnet "$test_output/zoneinfo.dll" "${zones[@]}" >"$test_output/managed.txt"
whole_minutes "$test_output/managed.txt"
"$llvmnet" --runtime=portable -O1 -w --nativeaot "$source_file" -o "$test_output/zoneinfo-aot" >"$test_output/aot.log"
env -u TZ LLVMNET_ZONEINFO=managed "$test_output/zoneinfo-aot" "${zones[@]}" >"$test_output/managed-aot.txt"
whole_minutes "$test_output/managed-aot.txt"
# A zone .NET does not know is ENOENT to musl, which then behaves as it does for a missing file (UTC).
env -u TZ LLVMNET_ZONEINFO=managed dotnet "$test_output/zoneinfo.dll" No/Such_Zone >"$test_output/unknown.txt"
env -u TZ LLVMNET_ZONEINFO=host dotnet "$test_output/zoneinfo.dll" No/Such_Zone >"$test_output/unknown-host.txt"
diff -u "$test_output/unknown-host.txt" "$test_output/unknown.txt"
grep -q '^zone No/Such_Zone' "$test_output/unknown.txt"
printf 'PASS: %d conversions in %d zones match native with the host zoneinfo, %d with generated images (JIT and NativeAOT)\n' \
    "$(grep -c '^[-0-9]' "$test_output/native.txt")" "${#zones[@]}" "$(grep -c '^[-0-9]' "$test_output/native.txt.minutes")"
