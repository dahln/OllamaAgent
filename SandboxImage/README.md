# LunaSandbox

A multi-architecture Docker image (linux/amd64 + linux/arm64) used as the isolated execution sandbox for each OllamaAgent task.

## What's inside

| Tool | Notes |
|---|---|
| Ubuntu 24.04 | Base OS |
| .NET 10 SDK | Installed via the official Microsoft install script |
| Node.js & npm | From Ubuntu's default package repository |
| Python 3 | Includes `pip` and `venv` |
| SQLite 3 | For lightweight database operations |
| nano | Text editor |

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

If the package is still private, authenticate first:

```bash
# Create a Personal Access Token (PAT) with read:packages scope at
# https://github.com/settings/tokens/new
echo "<YOUR_PAT>" | docker login ghcr.io -u <your-github-username> --password-stdin
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
