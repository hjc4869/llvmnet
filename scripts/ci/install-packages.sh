#!/usr/bin/env bash
# Install the llvmnet build and test prerequisites on Debian 13 (trixie). Run as root.
# Shared by .devcontainer/Dockerfile and .github/workflows/ci.yml so both use the same package set.
#   --integration   also install the tools needed by the integration tag (ffmpeg, Meson, Node.js, nasm, rsync)
set -euo pipefail
integration=0
[[ "${1:-}" == --integration ]] && integration=1
export DEBIAN_FRONTEND=noninteractive
apt-get update
apt-get install -y --no-install-recommends ca-certificates curl git xz-utils file time
if [[ ! -f /etc/apt/sources.list.d/microsoft-prod.list ]]; then
    # The configuration package installs the repository with Microsoft's current signing key, which trixie's apt
    # signature policy accepts.
    curl -fsSL -o /tmp/packages-microsoft-prod.deb https://packages.microsoft.com/config/debian/13/packages-microsoft-prod.deb
    dpkg -i /tmp/packages-microsoft-prod.deb
    rm /tmp/packages-microsoft-prod.deb
    apt-get update
fi
packages=(
    dotnet-sdk-10.0
    clang-22 flang-22 llvm-22 llvm-22-dev llvm-22-tools libflang-22-dev
    build-essential zlib1g-dev libzstd-dev libxml2-dev libedit-dev libffi-dev
    cmake ninja-build m4 pkg-config perl python3 shellcheck
    # scripts/kernels.py links native kernel packages for Linux, Windows and macOS (tests/native-kernels.sh).
    lld-22
    # lit's per-test timeout (tests/libcxx-conformance.sh, tests/llvm-test-suite.sh) needs psutil.
    python3-psutil
    # tests/ext-gles.sh: Mesa's EGL and GLES with its software renderer (llvmpipe), for headless runners without a GPU.
    libegl1 libgles2 libegl-mesa0 libgl1-mesa-dri
    # tests/ext-vulkan.sh: the Khronos loader and Mesa's software Vulkan driver (lavapipe).
    libvulkan1 mesa-vulkan-drivers
)
if [[ "$integration" -eq 1 ]]; then packages+=(meson ffmpeg nodejs nasm rsync); fi
apt-get install -y --no-install-recommends "${packages[@]}"
# NativeAOT invokes "clang" as its native compiler and linker; use the pinned LLVM release.
ln -sfn /usr/bin/clang-22 /usr/local/bin/clang
ln -sfn /usr/bin/clang++-22 /usr/local/bin/clang++
# Package lists stay in place: scripts/install-system-openmp.sh uses "apt-get download" without root.
