# LunaSandbox
# Supports linux/amd64 (x86-64) and linux/arm64 (Raspberry Pi 4/5)
FROM ubuntu:24.04

# Avoid interactive prompts during package installation
ENV DEBIAN_FRONTEND=noninteractive

# ── Layer 1: Core system packages (rarely changes) ──────────────────────────
# Foundation packages that almost never need updating.
RUN apt-get update && apt-get install -y --no-install-recommends \
        ca-certificates \
        curl \
        gnupg \
        wget \
    && rm -rf /var/lib/apt/lists/*

# ── Layer 2: Build essentials & native compilation (rarely changes) ──────────
# Required for npm packages with native addons (node-gyp) and pip packages
# that compile C extensions (numpy, etc.)
RUN apt-get update && apt-get install -y --no-install-recommends \
        build-essential \
        pkg-config \
    && rm -rf /var/lib/apt/lists/*

# ── Layer 3: Python runtime & package management (rarely changes) ────────────
RUN apt-get update && apt-get install -y --no-install-recommends \
        python3 \
        python3-dev \
        python3-pip \
        python3-venv \
        python-is-python3 \
        pipx \
    && rm -rf /var/lib/apt/lists/*

# ── Layer 4: Node.js from Ubuntu repos (base for upgrade) ───────────────────
RUN apt-get update && apt-get install -y --no-install-recommends \
        nodejs \
        npm \
    && rm -rf /var/lib/apt/lists/*

# ── Layer 5: Database tools (rarely changes) ────────────────────────────────
RUN apt-get update && apt-get install -y --no-install-recommends \
        sqlite3 \
        libsqlite3-dev \
    && rm -rf /var/lib/apt/lists/*

# ── Layer 6: CLI utilities & editors (occasionally updated) ──────────────────
RUN apt-get update && apt-get install -y --no-install-recommends \
        git \
        jq \
        nano \
        tar \
        unzip \
        zip \
    && rm -rf /var/lib/apt/lists/*

# ── Layer 7: Networking & sysadmin tools (occasionally updated) ──────────────
# Referenced by SystemAdmin prompts: netcat, ss, curl, wget already above.
RUN apt-get update && apt-get install -y --no-install-recommends \
        iproute2 \
        iputils-ping \
        netcat-openbsd \
        dnsutils \
    && rm -rf /var/lib/apt/lists/*

# ── Layer 8: Shell development tools ────────────────────────────────────────
# shellcheck is referenced by the Bash coding prompts.
RUN apt-get update && apt-get install -y --no-install-recommends \
        shellcheck \
    && rm -rf /var/lib/apt/lists/*

# ── Layer 9: Upgrade Node.js to latest LTS ──────────────────────────────────
# Ubuntu 24.04's apt repo ships Node.js 18.x, which is too old for many modern
# npm packages (they require >=20 or >=22). The 'n' version manager uses the
# existing npm to upgrade Node.js in-place.
RUN npm install -g n && n lts && hash -r

# ── Layer 10: .NET 10 SDK ────────────────────────────────────────────────────
# Microsoft's official install script handles both amd64 and arm64.
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

# ── Layer 11: .NET global tools ──────────────────────────────────────────────
# Separate layer so SDK updates don't re-install these tools.
RUN dotnet tool install --global dotnet-ef \
    && dotnet tool install --global dotnet-aspnet-codegenerator

ENV PATH="$PATH:/root/.dotnet/tools"

# ── Layer 12: TypeScript toolchain (npm global) ─────────────────────────────
# Separate from frontend frameworks so TS-only updates are a small layer.
RUN npm install -g \
        typescript \
        ts-node

# ── Layer 13: Frontend framework CLIs (npm global) ──────────────────────────
# These are the largest npm packages — isolated so updates don't re-download
# the TypeScript layer or linting layer.
# NOTE: create-react-app is deprecated — use `npm create vite@latest -- --template react`.
#       @vue/cli is deprecated (Vue 2 EOL) — use `npm create vue@latest` (create-vue).
RUN npm install -g \
        @angular/cli \
        create-vue

# ── Layer 14: Build tools & dev servers (npm global) ────────────────────────
RUN npm install -g \
        vite \
        next

# ── Layer 15: Linting & formatting (npm global) ─────────────────────────────
RUN npm install -g \
        eslint \
        prettier

# ── Layer 16: Python web frameworks (pre-installed for common tasks) ─────────
# Installed with --break-system-packages since this is a disposable container.
# Separate layer so Python framework version bumps don't touch anything else.
RUN pip3 install --break-system-packages --no-cache-dir \
        django \
        flask

# ── Layer 17: Python data science essentials (optional but common) ───────────
RUN pip3 install --break-system-packages --no-cache-dir \
        numpy \
        pandas \
        requests

# ── Workspace ────────────────────────────────────────────────────────────────
WORKDIR /workspace

CMD ["/bin/bash"]
