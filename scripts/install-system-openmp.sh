#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
version="${OPENMP_DEB_VERSION:-1:22.1.8-1+b2}"
destination="$(realpath -m "${1:-$root/artifacts/system-openmp}")"
if [[ -f "$destination/include/omp.h" && -f "$destination/lib/libomp.so.5" && -f "$destination/package-version" ]] && grep -Fxq "$version" "$destination/package-version"; then
    printf 'System OpenMP already staged: %s\n' "$version"
    exit 0
fi
mkdir -p "$root/artifacts/vendor/openmp" "$destination/include" "$destination/lib" "$destination/share"
stage="$(mktemp -d "$root/artifacts/vendor/openmp/package.XXXXXXXX")"
pushd "$stage" >/dev/null
apt-get download "libomp-22-dev=$version" "libomp5=$version"
popd >/dev/null
for package in "$stage"/*.deb; do
    dpkg-deb -x "$package" "$stage/extracted"
    sha256sum "$package" >> "$destination/packages.sha256"
done
header="$(find "$stage/extracted" -name omp.h -type f -print -quit)"
library="$(find "$stage/extracted" -name libomp.so.5 -print -quit)"
if [[ -z "$header" || -z "$library" ]]; then
    printf 'OpenMP headers or runtime are missing from the packages.\n' >&2
    exit 1
fi
cp "$header" "$destination/include/omp.h"
find "$stage/extracted" -name 'omp*.mod' -type f -exec cp -t "$destination/include" {} +
cp -L "$library" "$destination/lib/libomp.so.5"
ln -sfn libomp.so.5 "$destination/lib/libomp.so"
cp "$stage/extracted/usr/share/doc/libomp-22-dev/copyright" "$destination/share/copyright"
printf '%s\n' "$version" > "$destination/package-version"
printf 'Staged LLVM OpenMP %s for explicit system-ABI use: %s\n' "$version" "$destination"