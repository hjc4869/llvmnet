#!/usr/bin/env bash
# Stage the pinned libc++ 22.1.8 headers into the portable sysroot without root privileges.
# The Debian package is downloaded by URL and verified by SHA-256, so the result does not depend on
# the host's apt configuration. Override with LIBCXX_DEB_URL and LIBCXX_DEB_SHA256.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
version="${LIBCXX_DEB_VERSION:-1:22.1.8-1+b2}"
url="${LIBCXX_DEB_URL:-https://deb.debian.org/debian/pool/main/l/llvm-toolchain-22/libc++-22-dev_22.1.8-1+b2_amd64.deb}"
hash="${LIBCXX_DEB_SHA256:-70501f9a04580fe1ab71509647e598697177ae92d7453e49f19a68193dfb545e}"
downloads="${LLVMNET_VENDOR_DIR:-$root/artifacts/vendor}/downloads"
stage="$root/artifacts/vendor/libcxx"
destination="$root/sysroot/dotnet64-v1/include/c++/v1"
if [[ -f "$destination/__config" && -f "$destination/llvmnet-package-version" ]] && grep -Fxq "$version" "$destination/llvmnet-package-version"; then
    printf 'libc++ headers already staged: %s\n' "$version"
    exit 0
fi
package="$downloads/$(basename "$url")"
mkdir -p "$downloads"
if [[ ! -f "$package" ]] || [[ "$(sha256sum "$package" | cut -d' ' -f1)" != "$hash" ]]; then
    [[ "${LLVMNET_OFFLINE:-0}" == 1 ]] && { printf 'LLVMNET_OFFLINE=1 and %s is not cached\n' "$package" >&2; exit 1; }
    curl -sSfL --retry 2 -o "$package.part" "$url"
    if [[ "$(sha256sum "$package.part" | cut -d' ' -f1)" != "$hash" ]]; then
        rm -f "$package.part"
        printf '%s does not match SHA-256 %s\n' "$url" "$hash" >&2
        exit 1
    fi
    mv "$package.part" "$package"
fi
rm -rf "$stage/extracted"
mkdir -p "$stage/extracted" "$destination" "$root/sysroot/dotnet64-v1/share/libcxx"
if command -v dpkg-deb >/dev/null; then
    dpkg-deb -x "$package" "$stage/extracted"
else
    (cd "$stage/extracted" && ar x "$package" && tar -xf data.tar.* && rm -f data.tar.* control.tar.* debian-binary)
fi
configuration="$(find "$stage/extracted" -path '*/c++/v1/__config' -type f -print -quit)"
if [[ -z "$configuration" ]]; then printf 'libc++ headers are missing from the package.\n' >&2; exit 1; fi
cp -a "$(dirname "$configuration")/." "$destination/"
site="$(find "$stage/extracted" -name __config_site -type f -print -quit)"
if [[ -n "$site" ]]; then cp "$site" "$destination/__config_site"; fi
cp "$stage/extracted/usr/share/doc/libc++-22-dev/copyright" "$root/sysroot/dotnet64-v1/share/libcxx/copyright"
printf '%s\n' "$version" >"$destination/llvmnet-package-version"
printf 'Installed libc++ %s headers and notices into the portable sysroot.\n' "$version"
