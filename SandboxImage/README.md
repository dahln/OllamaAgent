# LunaSandbox

A multi-architecture Docker image (linux/amd64 + linux/arm64) used as the isolated execution sandbox for each OllamaAgent task.

## What's inside

| Layer | Tool(s) | Notes |
|---|---|---|
| 1 | Ubuntu 24.04, ca-certificates, curl, gnupg, wget | Core OS & HTTPS/download foundations |
| 2 | build-essential, pkg-config | C/C++ compiler, make – needed for native npm/pip extensions |
| 3 | Python 3, pip3, venv, pipx | Python runtime & package management |
| 4 | Node.js, npm | Base Node.js from Ubuntu repos (upgraded in Layer 9) |
| 5 | SQLite 3, libsqlite3-dev | Lightweight database with development headers |
| 6 | git, jq, nano, tar, unzip, zip | CLI utilities, editors, archive tools |
| 7 | iproute2, iputils-ping, netcat, dnsutils | Networking & sysadmin diagnostics |
| 8 | shellcheck | Shell script linting |
| 9 | Node.js LTS (via `n`) | Upgrades Node.js 18→LTS in-place |
| 10 | .NET 10 SDK | Installed via official Microsoft install script |
| 11 | dotnet-ef, dotnet-aspnet-codegenerator | .NET global tools |
| 12 | TypeScript, ts-node | TypeScript compiler & runner |
| 13 | @angular/cli, create-react-app, @vue/cli | Frontend framework scaffolding CLIs |
| 14 | vite, next | Build tools & dev servers |
| 15 | eslint, prettier | Linting & code formatting |
| 16 | Django, Flask | Python web frameworks |
| 17 | numpy, pandas, requests | Python data science & HTTP essentials |

The default working directory inside the container is `/workspace`.

---

## Building the image locally

```bash
# From the repository root
docker buildx build \
  --platform linux/amd64,linux/arm64 \
  -t lunasandbox:latest \
  ./SandboxImage
```

For a single-arch local build (faster):

```bash
docker build -t lunasandbox:latest ./SandboxImage
```

---

## Automated builds via GitHub Actions

The workflow at `.github/workflows/build-sandbox-image.yml` triggers automatically when:

- A commit is pushed to `main` that changes the `Dockerfile` or the workflow file itself.
- The workflow is triggered manually from the **Actions** tab (**Run workflow** button).

The workflow builds a multi-arch image for `linux/amd64` **and** `linux/arm64` and pushes it to the **GitHub Container Registry (GHCR)**:

```
ghcr.io/<owner>/lunasandbox:latest
ghcr.io/<owner>/lunasandbox:sha-<short-sha>
```

Replace `<owner>` with the GitHub username or organisation that owns this repository (e.g. `dahln`).

### Required permissions

No secrets need to be configured manually. The workflow uses the built-in `GITHUB_TOKEN` which is automatically available in every GitHub Actions run. The job's `permissions` block already requests `packages: write` so the token can push to GHCR.

### Making the package public

By default a newly published GHCR package is **private**. To make it publicly pullable:

1. Go to **github.com/\<owner\>/lunasandbox** (the package page).  
   Direct link: `https://github.com/users/<owner>/packages/container/lunasandbox`
2. Click **Package settings** (bottom-right of the package page).
3. Scroll to **Danger Zone → Change visibility** and set it to **Public**.

---

## Pulling the image on a host machine

Because the image is on GHCR (not Docker Hub), use the full registry path:

```bash
# Pull the latest image
docker pull ghcr.io/dahln/lunasandbox:latest

# Pull a specific commit build
docker pull ghcr.io/dahln/lunasandbox:sha-<short-sha>
```

---

## Running the sandbox manually

```bash
docker run --rm -it \
  -v "$(pwd)/workspace:/workspace" \
  ghcr.io/dahln/lunasandbox:latest
```

---

## Using this image with OllamaAgent

Update the `DockerService` in the main application to reference the GHCR image instead of a generic Ubuntu image:

```csharp
// In Services/DockerService.cs, change the image name to:
private const string SandboxImage = "ghcr.io/dahln/lunasandbox:latest";
```

OllamaAgent will check that the image is present locally before accepting any tasks.
