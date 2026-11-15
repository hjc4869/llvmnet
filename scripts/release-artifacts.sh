#!/usr/bin/env bash
# Build the release artifacts (TC-11) into a directory: the thin and the bundled-LLVM toolchain packages as
# reproducible tar.xz files, the dotnet tool package of the thin variant, and SHA256SUMS. A tag's CI workflow
# (.github/workflows/release.yml) runs this, signs the files and attaches them to the release.
#
# Usage: scripts/release-artifacts.sh <out-dir> [version]
#   version defaults to the tag or describe of HEAD ("v0.3.0" gives 0.3.0; a dev build gives 0.3.0-dev+<commit>)
#   LLVMNET_RELEASE_VARIANTS   space-separated subset of "thin llvm tool portable portable-<rid>" (default: thin llvm
#                              tool); portable is the toolchain for every .NET 10 host as .tar.xz and .zip, from the
#                              llvmnet-built LLVM (scripts/selfhost/, LLVMNET_PORTABLE_LIBRARY and LLVMNET_PORTABLE_STAGE);
#                              portable-<rid> (win-x64, osx-arm64, linux-x64, linux-arm64) is the same toolchain
#                              precompiled for that host (ReadyToRun), a .zip for Windows and a .tar.xz otherwise
#   SOURCE_DATE_EPOCH          the timestamp recorded in the archives (default: the commit time of HEAD)
# Reproducibility: the same commit on the same toolchain produces byte-identical archives (tests/release.sh checks).
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out="${1:?usage: release-artifacts.sh <out-dir> [version]}"
out="$(realpath -m "$out")"
version="${2:-}"
if [[ -z "$version" ]]; then
    version="$(git -C "$root" describe --tags --exact-match 2>/dev/null || true)"
    if [[ -n "$version" ]]; then
        version="${version#v}"
    else
        prefix="$(sed -n 's:.*<VersionPrefix>\(.*\)</VersionPrefix>.*:\1:p' "$root/Directory.Build.props")"
        suffix="$(sed -n 's:.*<VersionSuffix>\(.*\)</VersionSuffix>.*:\1:p' "$root/Directory.Build.props")"
        version="$prefix${suffix:+-$suffix}+$(git -C "$root" rev-parse --short=12 HEAD)"
    fi
fi
variants="${LLVMNET_RELEASE_VARIANTS:-thin llvm tool}"
export SOURCE_DATE_EPOCH="${SOURCE_DATE_EPOCH:-$(git -C "$root" log -1 --format=%ct)}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
mkdir -p "$out"
work="$out/work"
rm -rf "$work"
mkdir -p "$work"
# tar and xz settings that make the archive a function of its contents: sorted names, fixed owner and times, no
# atime/ctime headers, a fixed block size so the thread count does not change the xz stream.
pack() {
    local name="$1" prefix="$2"
    tar --sort=name --owner=0 --group=0 --numeric-owner --mtime="@$SOURCE_DATE_EPOCH" \
        --pax-option=exthdr.name=%d/PaxHeaders/%f,delete=atime,delete=ctime \
        -C "$(dirname "$prefix")" -cf - "$(basename "$prefix")" |
        xz -T0 --block-size=32MiB -6 >"$out/$name.tar.xz"
    printf '%s\n' "$name.tar.xz"
}
# The same tree as a zip for Windows, made a function of its contents as the archives are: sorted entries, the commit time,
# Unix modes kept (launchers stay executable when a Unix host unpacks it).
zip_pack() {
    local name="$1" prefix="$2"
    python3 - "$prefix" "$out/$name.zip" "$SOURCE_DATE_EPOCH" <<'PYTHON'
import os, stat, sys, time, zipfile
prefix, destination, epoch = sys.argv[1], sys.argv[2], int(sys.argv[3])
stamp = time.gmtime(max(epoch, 315532800))[:6]
base = os.path.basename(prefix)
entries = []
for directory, directories, files in os.walk(prefix):
    directories.sort()
    for file in files:
        entries.append(os.path.join(directory, file))
with zipfile.ZipFile(destination, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
    for path in sorted(entries):
        if os.path.islink(path):
            raise SystemExit(f"{path} is a symbolic link, which a zip for Windows cannot hold")
        info = zipfile.ZipInfo(os.path.join(base, os.path.relpath(path, prefix)).replace(os.sep, "/"), date_time=stamp)
        info.compress_type = zipfile.ZIP_DEFLATED
        info.external_attr = (stat.S_IFREG | (0o755 if os.access(path, os.X_OK) else 0o644)) << 16
        with open(path, "rb") as source:
            archive.writestr(info, source.read())
PYTHON
    printf '%s\n' "$name.zip"
}
for variant in $variants; do
    case "$variant" in
        thin)
            prefix="$work/llvmnet-$version-linux-x64"
            bash "$root/scripts/package-toolchain.sh" "$prefix" --self-contained >"$work/thin.log"
            pack "llvmnet-$version-linux-x64" "$prefix"
            ;;
        llvm)
            prefix="$work/llvmnet-$version-linux-x64-llvm"
            bash "$root/scripts/package-toolchain.sh" "$prefix" --self-contained --bundle-llvm >"$work/llvm.log"
            pack "llvmnet-$version-linux-x64-llvm" "$prefix"
            ;;
        portable)
            prefix="$work/llvmnet-$version-portable"
            bash "$root/scripts/package-toolchain.sh" "$prefix" --portable >"$work/portable.log"
            pack "llvmnet-$version-portable" "$prefix"
            zip_pack "llvmnet-$version-portable" "$prefix"
            ;;
        portable-*)
            rid="${variant#portable-}"
            prefix="$work/llvmnet-$version-portable-$rid"
            bash "$root/scripts/package-toolchain.sh" "$prefix" --portable --readytorun="$rid" >"$work/$variant.log"
            if [[ "$rid" == win-* ]]; then zip_pack "llvmnet-$version-portable-$rid" "$prefix"; else pack "llvmnet-$version-portable-$rid" "$prefix"; fi
            rm -rf "$prefix"
            ;;
        tool)
            # The dotnet tool: the compiler, its LLVM bridge and the sysroots in one package; LLVM 22 comes from PATH.
            bash "$root/scripts/ensure-built.sh"
            LLVMNET_SKIP_BUILD=1 bash "$root/scripts/build-extensions.sh" 2>"$work/extensions.log"
            dotnet pack "$root/src/LlvmNet.Compiler" -c Release --no-build --nologo -v quiet -o "$work/tool" \
                -p:PackLlvmnetTool=true -p:PackageVersion="${version%%+*}"
            # NuGet stamps the package's generated entries with the build time; rewriting the zip with the commit time
            # and a fixed entry order makes it a function of its contents like the archives.
            for package in "$work"/tool/llvmnet.*.nupkg; do
                python3 - "$package" "$out/$(basename "$package")" "$SOURCE_DATE_EPOCH" <<'PYTHON'
import sys, time, zipfile
source, destination, epoch = sys.argv[1], sys.argv[2], int(sys.argv[3])
stamp = time.gmtime(max(epoch, 315532800))[:6]
with zipfile.ZipFile(source) as original, zipfile.ZipFile(destination, "w", zipfile.ZIP_DEFLATED) as normalized:
    for entry in sorted(original.infolist(), key=lambda entry: entry.filename):
        info = zipfile.ZipInfo(entry.filename, date_time=stamp)
        info.compress_type = zipfile.ZIP_DEFLATED
        info.external_attr = entry.external_attr
        normalized.writestr(info, original.read(entry))
PYTHON
                printf '%s\n' "$(basename "$package")"
            done
            ;;
        *) printf 'Unknown variant %s\n' "$variant" >&2; exit 2 ;;
    esac
done
rm -rf "$work"
(cd "$out" && shopt -s nullglob && sha256sum -- *.tar.xz *.zip *.nupkg >SHA256SUMS)
printf 'Release artifacts for %s in %s:\n' "$version" "$out"
cat "$out/SHA256SUMS"
