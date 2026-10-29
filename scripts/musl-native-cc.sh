#!/usr/bin/env bash
# Native oracle for musl tests: compile and statically link C sources against Debian's musl 1.2.5
# (the same release the portable libc port uses), staged without root under artifacts/vendor/musl-native.
#   scripts/musl-native-cc.sh -O1 test.c -o test
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
stage="${LLVMNET_VENDOR_DIR:-$root/artifacts/vendor}/musl-native"
url=http://deb.debian.org/debian/pool/main/m/musl/musl-dev_1.2.5-3.1~deb13u1_amd64.deb
hash=a3f7d47e452129bb65aae201d6c115abf4d5d3efb136b38b7c6b4fa03ec065e9
if [[ ! -f "$stage/root/usr/lib/x86_64-linux-musl/libc.a" ]]; then
    mkdir -p "$stage"
    curl -sSfL -o "$stage/musl-dev.deb.part" "$url"
    [[ "$(sha256sum "$stage/musl-dev.deb.part" | cut -d' ' -f1)" == "$hash" ]] || { printf 'musl-dev package hash mismatch\n' >&2; exit 1; }
    mv "$stage/musl-dev.deb.part" "$stage/musl-dev.deb"
    dpkg-deb -x "$stage/musl-dev.deb" "$stage/root"
fi
lib="$stage/root/usr/lib/x86_64-linux-musl"
resource="$(clang-22 -print-resource-dir)"
exec clang-22 -static -nostdinc -nostdlib -isystem "$stage/root/usr/include/x86_64-linux-musl" -isystem "$resource/include" \
    "$lib/crt1.o" "$lib/crti.o" "$@" "$lib/libc.a" "$(gcc -print-libgcc-file-name)" "$lib/crtn.o"
