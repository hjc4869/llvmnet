#!/usr/bin/env bash
# Install the llvmnet build and test prerequisites on Debian 13 (trixie). Run as root.
# Shared by .devcontainer/Dockerfile and .github/workflows/ci.yml so both use the same package set.
#   --integration   also install the tools needed by the integration tag (ffmpeg, Meson, Node.js, lld)
set -euo pipefail
integration=0
[[ "${1:-}" == --integration ]] && integration=1
export DEBIAN_FRONTEND=noninteractive
apt-get update
apt-get install -y --no-install-recommends ca-certificates curl gnupg git xz-utils file time
if [[ ! -f /etc/apt/sources.list.d/microsoft-prod.list ]]; then
    curl -fsSL https://packages.microsoft.com/keys/microsoft.asc | gpg --dearmor -o /usr/share/keyrings/microsoft-prod.gpg
    echo 'deb [arch=amd64,arm64 signed-by=/usr/share/keyrings/microsoft-prod.gpg] https://packages.microsoft.com/debian/13/prod trixie main' \
        >/etc/apt/sources.list.d/microsoft-prod.list
    apt-get update
fi
packages=(
    dotnet-sdk-10.0
    clang-22 flang-22 llvm-22 llvm-22-dev llvm-22-tools libflang-22-dev
    build-essential zlib1g-dev libzstd-dev libxml2-dev libedit-dev libffi-dev
    cmake ninja-build pkg-config perl python3 shellcheck
)
if [[ "$integration" -eq 1 ]]; then packages+=(meson ffmpeg nodejs lld-22); fi
apt-get install -y --no-install-recommends "${packages[@]}"
# NativeAOT invokes "clang" as its native compiler and linker; use the pinned LLVM release.
ln -sfn /usr/bin/clang-22 /usr/local/bin/clang
ln -sfn /usr/bin/clang++-22 /usr/local/bin/clang++
# Package lists stay in place: scripts/install-libcxx-headers.sh uses "apt-get download" without root.
