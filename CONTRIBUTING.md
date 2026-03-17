# Contributing to Squad Monitor

Thank you for your interest in contributing to Squad Monitor!

## Development Setup

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (preview)
- [GitHub CLI](https://cli.github.com/) (`gh`) — authenticated via `gh auth login`

### Build & Run

```bash
dotnet restore
dotnet build
dotnet run
```

## Versioning

This project follows [Semantic Versioning (SemVer)](https://semver.org/):

- **MAJOR** — breaking changes (CLI flags removed, config format changed)
- **MINOR** — new features (new dashboard panel, new flag)
- **PATCH** — bug fixes and minor improvements

The version is managed in `squad-monitor.csproj` via the `<VersionPrefix>` property.

### How to Bump the Version

1. Edit `squad-monitor.csproj` and update `<VersionPrefix>`:
   ```xml
   <VersionPrefix>1.1.0</VersionPrefix>
   ```
2. Commit with a message like: `chore: bump version to 1.1.0`
3. Create a GitHub Release with tag `v1.1.0` — the publish pipeline handles the rest.

### Pre-release Versions

Pre-release packages are automatically created in two scenarios:

| Scenario | Version Format | Example |
|----------|---------------|---------|
| CI on PR / feature branch | `{VersionPrefix}-preview.{run}` | `1.0.0-preview.42` |
| Manual dispatch | Any version you specify | `1.1.0-beta.1` |
| Release with pre-release tag | Tag value (minus `v` prefix) | `1.1.0-rc.1` |

To install a pre-release version:

```bash
dotnet tool install -g squad-monitor --version "1.1.0-preview.*"
```

## Release Process

### 1. Prepare the Release

- Ensure all changes are merged to `main`
- Update `<VersionPrefix>` in `squad-monitor.csproj` if needed
- Verify CI passes on `main`

### 2. Create a GitHub Release

1. Go to **Releases** → **Draft a new release**
2. Create a tag matching the version: `v1.0.0` (stable) or `v1.1.0-rc.1` (pre-release)
3. Write release notes (GitHub can auto-generate these)
4. Check **"Set as pre-release"** for non-stable versions
5. Click **Publish release**

### 3. What Happens Automatically

The [publish-nuget](.github/workflows/publish-nuget.yml) workflow will:

1. Build the project
2. Pack the NuGet package with the version from the release tag
3. Publish to **NuGet.org** (if `NUGET_API_KEY` secret is configured)
4. Publish to **GitHub Packages** (always, uses `GITHUB_TOKEN`)
5. Attach the `.nupkg` file to the GitHub Release

### 4. Manual Publish (Emergency / Re-publish)

Use **Actions** → **Publish to NuGet** → **Run workflow** and specify the version manually.

## CI Pipeline

Every push to `main` and every pull request triggers the [CI workflow](.github/workflows/ci.yml):

- Builds the project
- Runs tests (if any exist)
- Packs the NuGet package (verifies packaging works)
- On PRs: creates a pre-release package artifact (`-preview.{run}`)

## Pull Requests

1. Fork or create a feature branch
2. Make your changes
3. Ensure `dotnet build` succeeds
4. Open a PR against `main`
5. CI will validate the build and packaging automatically

## License

By contributing, you agree that your contributions will be licensed under the [MIT License](LICENSE).
