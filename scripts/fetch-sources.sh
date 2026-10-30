#!/usr/bin/env bash
# Download, verify and extract pinned upstream sources listed in scripts/sources.lock.
#
#   scripts/fetch-sources.sh                 fetch the integration set (ffmpeg dav1d libxml2 zlib)
#   scripts/fetch-sources.sh musl llvm-project
#   scripts/fetch-sources.sh --all
#   scripts/fetch-sources.sh --path ffmpeg   print the extracted directory, fetching it first if needed
#   scripts/fetch-sources.sh --list
#
# LLVMNET_VENDOR_DIR moves the cache (default artifacts/vendor). LLVMNET_OFFLINE=1 refuses to download.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
lock="$root/scripts/sources.lock"
vendor="${LLVMNET_VENDOR_DIR:-$root/artifacts/vendor}"
downloads="$vendor/downloads"
sources="$vendor/src"
integration=(ffmpeg dav1d libxml2 zlib)

entry() {
    local name="$1" line
    line="$(awk -F '\t' -v name="$name" '!/^#/ && $1 == name { print; exit }' "$lock")"
    if [[ -z "$line" ]]; then printf 'Unknown source %s; see %s\n' "$name" "$lock" >&2; return 1; fi
    printf '%s\n' "$line"
}

verify() {
    local file="$1" expected="$2" actual
    actual="$(sha256sum "$file" | cut -d' ' -f1)"
    [[ "$actual" == "$expected" ]]
}

# A source without release archives is pinned to a commit: its archive column reads <name>.tar@<commit>, the URL is
# the repository, and the hash is that of `git archive --format=tar` of the commit, which is deterministic.
download() {
    local url="$1" archive="$2" destination="$3" commit clone
    if [[ "$archive" != *.tar@* ]]; then
        curl -sSfL --retry 2 --connect-timeout 20 -o "$destination" "$url"
        return
    fi
    commit="${archive#*.tar@}"
    clone="$(mktemp -d "$downloads/.clone.XXXXXXXX")"
    git clone --quiet --no-checkout "$url" "$clone" &&
        git -C "$clone" archive --format=tar --prefix=source/ "$commit" >"$destination"
    local status=$?
    rm -rf "$clone"
    return "$status"
}

fetch() {
    local name="$1" version hash archive urls url target stamp temporary
    IFS=$'\t' read -r name version hash archive urls < <(entry "$name")
    target="$sources/$name-$version"
    stamp="$target/.llvmnet-source"
    if [[ -f "$stamp" && "$(<"$stamp")" == "$hash" ]]; then
        printf '%s\n' "$target"
        return 0
    fi
    mkdir -p "$downloads" "$sources"
    if [[ ! -f "$downloads/$archive" ]] || ! verify "$downloads/$archive" "$hash"; then
        if [[ "${LLVMNET_OFFLINE:-0}" == 1 ]]; then
            printf 'LLVMNET_OFFLINE=1 and %s is not cached in %s\n' "$archive" "$downloads" >&2
            return 1
        fi
        rm -f "$downloads/$archive"
        for url in $urls; do
            printf 'Downloading %s\n' "$url" >&2
            if download "$url" "$archive" "$downloads/$archive.part" && verify "$downloads/$archive.part" "$hash"; then
                mv "$downloads/$archive.part" "$downloads/$archive"
                break
            fi
            printf 'Download from %s failed or did not match SHA-256 %s\n' "$url" "$hash" >&2
            rm -f "$downloads/$archive.part"
        done
        if [[ ! -f "$downloads/$archive" ]]; then
            printf 'Cannot obtain %s %s from any listed URL.\n' "$name" "$version" >&2
            return 1
        fi
    fi
    temporary="$(mktemp -d "$sources/.$name.XXXXXXXX")"
    trap 'rm -rf "$temporary"' RETURN
    tar -xf "$downloads/$archive" -C "$temporary" --strip-components=1 --no-same-owner
    printf '%s\n' "$hash" >"$temporary/.llvmnet-source"
    rm -rf "$target"
    mv "$temporary" "$target"
    printf 'Extracted %s %s into %s\n' "$name" "$version" "$target" >&2
    printf '%s\n' "$target"
}

case "${1:-}" in
    --list)
        awk -F '\t' '!/^#/ && NF { printf "%-14s %-8s %s\n", $1, $2, $4 }' "$lock"
        ;;
    --path)
        [[ $# -eq 2 ]] || { printf 'Usage: %s --path <name>\n' "$0" >&2; exit 2; }
        fetch "$2"
        ;;
    --all)
        while read -r name; do fetch "$name" >/dev/null; done < <(awk -F '\t' '!/^#/ && NF { print $1 }' "$lock")
        ;;
    -h|--help)
        sed -n '2,10p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
        ;;
    *)
        names=("$@")
        [[ ${#names[@]} -gt 0 ]] || names=("${integration[@]}")
        for name in "${names[@]}"; do fetch "$name" >/dev/null; done
        ;;
esac
