#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
version="${LIBCXX_DEB_VERSION:-1:22.1.8-1+b2}"
stage="$root/artifacts/vendor/libcxx"
destination="$root/sysroot/dotnet64-v1/include/c++/v1"
if [[ -f "$destination/__config" && -f "$destination/llvmnet-package-version" ]] && grep -Fxq "$version" "$destination/llvmnet-package-version"; then
    printf 'libc++ headers already staged: %s\n' "$version"
    exit 0
fi
mkdir -p "$stage" "$destination" "$root/sysroot/dotnet64-v1/share/libcxx"
(
    cd "$stage"
    apt-get download "libc++-22-dev=$version"
)
packages=("$stage"/libc++-22-dev_*.deb)
if [[ ${#packages[@]} -ne 1 ]]; then printf 'Expected one pinned libc++ package in %s\n' "$stage" >&2; exit 1; fi
dpkg-deb -x "${packages[0]}" "$stage/extracted"
configuration="$(find "$stage/extracted" -path '*/c++/v1/__config' -type f -print -quit)"
if [[ -z "$configuration" ]]; then printf 'libc++ headers are missing from the package.\n' >&2; exit 1; fi
cp -a "$(dirname "$configuration")/." "$destination/"
site="$(find "$stage/extracted" -name __config_site -type f -print -quit)"
if [[ -n "$site" ]]; then cp "$site" "$destination/__config_site"; fi
cp "$stage/extracted/usr/share/doc/libc++-22-dev/copyright" "$root/sysroot/dotnet64-v1/share/libcxx/copyright"
dpkg-deb -f "${packages[0]}" Version > "$destination/llvmnet-package-version"
printf 'Installed libc++ %s headers and notices into the portable sysroot.\n' "$version"