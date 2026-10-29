# The CI's Linux job (ubuntu-24.04) in a container, to run before pushing: tools/ci/linux-check.ps1 or .sh.
FROM ubuntu:24.04

ENV DEBIAN_FRONTEND=noninteractive \
    DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_ROOT=/usr/share/dotnet \
    PATH=/usr/share/dotnet:$PATH

# What the GitHub runner image has, plus the packages ci.yml installs.
RUN apt-get update && apt-get install -y --no-install-recommends \
        build-essential cmake git curl ca-certificates python3 rsync \
        libasound2-dev libfreetype-dev libfontconfig-dev ninja-build espeak-ng \
        libicu74 \
    && rm -rf /var/lib/apt/lists/*

# The .NET SDK that global.json asks for (10.0.100, latest feature band).
RUN curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
    && bash /tmp/dotnet-install.sh --channel 10.0 --install-dir /usr/share/dotnet \
    && rm /tmp/dotnet-install.sh

WORKDIR /work
