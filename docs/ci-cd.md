# CI/CD guide

The repository ships a working GitHub Actions workflow (`.github/workflows/ci.yml`)
with two jobs, plus `build.sh` / `build.ps1` for local use.

## What runs on every push/PR

**Job 1 — `logic` (ubuntu-latest)**

1. `actions/setup-dotnet@v4` with .NET 8.0.x.
2. `dotnet restore` → `dotnet build -c Release` → `dotnet test -c Release`
   — all 62 tests (SDK core, interop contracts, InfGen, four samples) pass on
   Linux in under a second; no Windows or WDK needed.
3. `dotnet pack -c Release -o artifacts/` — the five SDK packages plus the
   four sample packages.
4. **InfGen smoke test** — install the packed tool from the local artifacts
   feed (`--prerelease --add-source ./artifacts`), generate the VirtualAudio
   INF from its manifest, and lint it. This proves the tool package and the
   manifest→INF pipeline in one step.
5. **Template smoke test** — `dotnet new install` the packed templates
   package, instantiate `driver-solution` as a new project, wire the sln,
   restore against the artifacts feed + nuget.org, and run the generated
   project's tests. This is an end-to-end proof that the published packages
   are consumable by a fresh project.
6. Upload `artifacts/*.nupkg` as a workflow artifact.

**Job 2 — `windows` (windows-latest)**

Build + test on Windows to catch any OS-specific compile issue. It stops
there, on purpose:

> GitHub-hosted Windows runners do not ship the WDK, so the native KMDF shim,
> InfVerif, Inf2Cat and signtool cannot run in public CI. Run those steps on a
> private runner with the WDK installed, or locally (see below).

## Local pipeline (the parts CI can't do)

```powershell
# On a machine with VS + WDK:
.\build.ps1 -Configuration Release          # managed: build, test, pack

# Native shim (KMDF):
#   open shim/WdsKmdfShim in VS → build x64/arm64

# Package validation:
InfVerif /drive: .                          # INF rule check
Inf2Cat /driver:. /os:10_X64                # catalog
signtool sign /fd SHA256 /a /f test.pfx .\WdsKmdfShim.cat

# Deploy into a VM and drive it:
.\tools\Install-Driver.ps1 -InfPath .\WdsKmdfShim.inf -CertPath .\test.cer
```

## Suggested repo setup for teams

1. **Branch protection** — require the `logic` job to pass; it gates every
   behavioral change in the driver logic.
2. **Private WDK runner** — one machine or VM with VS + WDK + HLK for the
   native/signing stages; trigger it with `workflow_dispatch` or
   `repository_dispatch` from the same pipeline.
3. **Package feed** — push `artifacts/*.nupkg` to a private Azure Artifacts /
   GitHub Packages feed; drivers consume `WindowsDriver.Sdk` etc. with
   `--prerelease` until you ship a stable version.
4. **Versioning** — the SDK version lives in `Directory.Build.props` (single
   source); bump it and the template's default `sdkVersion` symbol together
   when packaging.
5. **Determinism** — the tests never touch wall-clock time (the fake clock
   starts at a fixed date); do not add real-time waits, or CI becomes flaky.

## Security notes

- CI uses no secrets; signing happens on the private runner (or never, for
  test-signed development).
- The InfGen/template smoke tests download only from your artifacts folder and
  nuget.org; add package pinning (`global.json` pins the SDK already) if your
  threat model needs it.
- Kernel packages are machine-impacting: test them in VMs with snapshots, not
  on the build host.
