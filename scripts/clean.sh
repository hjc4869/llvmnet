#!/usr/bin/env bash
# Delete disposable build and test output under artifacts/. See docs/dev/artifacts.md.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
artifacts="$root/artifacts"
mode=disposable
dry_run=0
for option in "$@"; do
    case "$option" in
        --all) mode=all ;;
        --downloads) mode=downloads ;;
        -n|--dry-run) dry_run=1 ;;
        -h|--help)
            printf 'Usage: %s [--dry-run] [--downloads | --all]\n' "$0"
            printf '  default      test output, dependency/ffmpeg builds, packaged toolchains\n'
            printf '  --downloads  also the pinned source cache under artifacts/vendor\n'
            printf '  --all        everything under artifacts/, including SPEC kits and evidence\n'
            exit 0 ;;
        *) printf 'Unknown option: %s\n' "$option" >&2; exit 2 ;;
    esac
done
[[ -d "$artifacts" ]] || exit 0
remove() {
    local path
    for path in "$@"; do
        path="${path%/}"
        [[ -e "$path" ]] || continue
        case "$(realpath -m "$path")" in "$artifacts"/*) ;; *) printf 'Refusing to delete %s\n' "$path" >&2; exit 1 ;; esac
        if [[ "$dry_run" -eq 1 ]]; then printf 'would remove %s\n' "$path"; else rm -rf -- "$path"; fi
    done
}
shopt -s nullglob
if [[ "$mode" == all ]]; then
    remove "$artifacts"/*
    exit 0
fi
# Trailing slashes restrict the globs to directories, so cited top-level logs survive.
remove "$artifacts/tests" "$artifacts/sysroot-build" "$artifacts"/deps/ "$artifacts"/deps-*/ \
    "$artifacts"/ffmpeg-*/ "$artifacts"/*-toolchain/ "$artifacts"/*-toolchain-*/
if [[ "$mode" == downloads ]]; then
    remove "$artifacts/vendor"
fi
