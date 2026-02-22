# LunaSandbox
# Supports linux/amd64 (x86-64) and linux/arm64 (Raspberry Pi 4/5)
FROM ubuntu:24.04

# Avoid interactive prompts during package installation
ENV DEBIAN_FRONTEND=noninteractive

# ── System packages ─────────────────────────────────────────────────────────
RUN apt-get update && apt-get install -y --no-install-recommends \
        ca-certificates \
        curl \
        gnupg \
        nano \
        nodejs \
        npm \
        python3 \
        python3-pip \
        python3-venv \
        sqlite3 \
        wget \
    && rm -rf /var/lib/apt/lists/*

# ── .NET 10 SDK ──────────────────────────────────────────────────────────────
# Microsoft's official install script handles both amd64 and arm64
RUN curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
    && chmod +x /tmp/dotnet-install.sh \
    && /tmp/dotnet-install.sh --channel 10.0 --install-dir /usr/share/dotnet \
    && ln -sf /usr/share/dotnet/dotnet /usr/local/bin/dotnet \
    && rm /tmp/dotnet-install.sh

# Suppress .NET welcome / telemetry noise in the sandbox
ENV DOTNET_NOLOGO=1
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1
ENV DOTNET_ROOT=/usr/share/dotnet
ENV PATH="$PATH:/usr/share/dotnet"

# ── .NET global tools ────────────────────────────────────────────────────────
RUN dotnet tool install --global dotnet-ef \
    && dotnet tool install --global dotnet-aspnet-codegenerator

ENV PATH="$PATH:/root/.dotnet/tools"

# ── Node.js / npm global tools ───────────────────────────────────────────────
RUN npm install -g \
        typescript \
        ts-node \
        @angular/cli \
        create-react-app \
        @vue/cli \
        vite \
        next \
        eslint \
        prettier

# ── Workspace ────────────────────────────────────────────────────────────────
WORKDIR /workspace

CMD ["/bin/bash"]
