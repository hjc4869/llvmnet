# Reproducible llvmnet build and test environment: Debian 13, LLVM/Clang/Flang 22.1.8, .NET 10 SDK.
#   docker build -f .devcontainer/Dockerfile -t llvmnet-dev .
#   docker run --rm -v "$PWD:/src" -w /src llvmnet-dev tests/run.sh --tag core
FROM debian:trixie
COPY scripts/ci/install-packages.sh /tmp/install-packages.sh
RUN bash /tmp/install-packages.sh --integration && rm /tmp/install-packages.sh
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
RUN useradd --create-home --uid 1000 builder
USER builder
