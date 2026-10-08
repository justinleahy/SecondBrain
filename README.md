<img src="assets/icon.svg" width="64" height="64" alt="">

# SecondBrain

A personal knowledge daemon with provider-neutral model roles, built on .NET 10 and a Blazor Web App with Interactive Server rendering. This repository currently contains the M0 item 1 scaffold: projects, shared contracts, a placeholder `/health` endpoint, and build workflows. The remaining M0 behavior is defined in [the build plan](docs/build/M0-foundations.md) and [specification](spec.md).

The Razor component shell compiles with Interactive Server rendering, but its services and routes are not yet mounted. Lane D adds that integration through its own composition extension; the scaffold currently serves `/health` only.

## Build and test

Install .NET SDK **10.0.105**; `global.json` pins that version with `latestPatch` roll-forward. From the repository root:

```sh
dotnet restore --locked-mode
dotnet build -warnaserror --no-restore
dotnet test --no-build --no-restore
```

The solution is `SecondBrain.slnx`. The 31 explicit package versions are managed centrally in `Directory.Packages.props`, and each project checks in `packages.lock.json`. Locked restore fails when a project's dependencies disagree with its lock file. Restore needs access to nuget.org until the NuGet package cache is populated; a lock file records versions and hashes, not package contents. A populated cache supports offline builds with `--no-restore`.

Self-contained publishes pin the bundled .NET runtime to **10.0.12** through `RuntimeFrameworkVersion`, while the SDK remains **10.0.105**. `Directory.Build.targets` also pins the SDK's implicit `Microsoft.AspNetCore.App.Internal.Assets` package to **10.0.12**, so ordinary and self-contained restores use the same Blazor asset package and lock file. That implicit package is controlled through the shared `SecondBrainRuntimeVersion` property rather than an explicit `PackageVersion` entry. Ordinary builds and tests use the installed shared runtime (10.0.5 in the development environment).

Tests use **xUnit 2** and the Visual Studio runner. To run a single project:

```sh
dotnet test tests/SecondBrain.Storage.Tests/SecondBrain.Storage.Tests.csproj --no-restore
```

Normal CI excludes tests marked `[Trait("Category", "Qualification")]`. The separate, manually triggered `qualification.yml` runs only that category; lane B supplies the live provider configuration and network access when those tests land. Providers and model ids are selected by configuration; the scaffold sets no model vendor default.

Run the placeholder daemon locally:

```sh
dotnet run --project src/SecondBrain.Server --no-restore --urls http://127.0.0.1:7171
curl --fail http://127.0.0.1:7171/health
```

CI restores in locked mode, builds with warnings as errors, tests, publishes self-contained `linux-x64` Server, CLI (`brain`), and Extractor outputs, and builds the non-root Docker image. The `compose-smoke` job remains a placeholder until lane C delivers item 14.

```sh
docker build --platform linux/amd64 --file deploy/docker/Dockerfile --tag secondbrain:dev .
docker run --rm --publish 127.0.0.1:7171:8080 secondbrain:dev
```

Both Docker base images are pinned by digest. Since MCR has no SDK `10.0.105` image tag, the build stage installs Microsoft's SDK archive and checks its pinned SHA-512 before building. The image is an item 1 placeholder; lane C supplies the rest of the deployment requirements, including Compose and systemd, in item 14.

## Parallel lane ownership

Each lane adds implementation files only within its owned directories and updates only its own composition file. Test subdirectories below `SecondBrain.Core.Tests` keep configuration and security tests separate from domain and provider tests.

| Lane | Implementation directories | Test directories | Composition file and methods |
| --- | --- | --- | --- |
| **A — Storage and durability** | `src/SecondBrain.Storage/`; new implementation files in `src/SecondBrain.Core/Storage/` and `src/SecondBrain.Core/Durability/` | `tests/SecondBrain.Storage.Tests/` | `src/SecondBrain.Server/Composition/LaneA.Storage.cs`: `AddStorage()` |
| **B — Domain and providers** | `src/SecondBrain.Core/Domain/`; new implementation files in `src/SecondBrain.Core/Providers/` and `src/SecondBrain.Core/Privacy/`; `src/SecondBrain.Providers.OpenAICompatible/`; `tests/SecondBrain.MockProvider/` | `tests/SecondBrain.Core.Tests/Domain/`, `tests/SecondBrain.Core.Tests/Providers/`, `tests/SecondBrain.Core.Tests/Privacy/` | `src/SecondBrain.Server/Composition/LaneB.Domain.cs`: `AddDomain()`, `AddProviders()`, `AddPrivacy()` |
| **C — Configuration, provisioning, deployment** | New implementation files in `src/SecondBrain.Core/Configuration/` and `src/SecondBrain.Core/Security/`; `deploy/`; `src/SecondBrain.Cli/`; `src/SecondBrain.Extractor/` | `tests/SecondBrain.Core.Tests/Configuration/`, `tests/SecondBrain.Core.Tests/Security/`; `tests/SecondBrain.Deploy.Tests/` | `src/SecondBrain.Server/Composition/LaneC.Configuration.cs`: `AddConfiguration()`, `AddKeyRing()` |
| **D — HTTP and authentication** | `src/SecondBrain.Server/Http/`, `src/SecondBrain.Server/Auth/`, `src/SecondBrain.Server/Limits/`, `src/SecondBrain.Server/Sources/`, `src/SecondBrain.Server/Components/`; new implementation files in `src/SecondBrain.Core/Authorization/` | `tests/SecondBrain.Server.Tests/` | `src/SecondBrain.Server/Composition/LaneD.Http.cs`: `AddHttpHost()`, `AddAuth()`, `AddLimits()` |

Lane A owns store creation for `brain init` (item 3b); lane C owns the CLI command shell. They exchange that integration through the orchestrator. Cross-lane changes and convergence work also go through the orchestrator.

**Frozen after item 1:** `Directory.Packages.props`, `src/SecondBrain.Server/Program.cs`, and the shared contract files created under `src/SecondBrain.Core/{Configuration,Security,Privacy,Authorization,Storage,Durability,Providers,Problems}/`. Lanes may add implementations beside those contracts, but contract edits, new package pins, and edits to `Program.cs` require the orchestrator. Each lane can update its own composition extension file. The solution, shared build configuration, workflows, and existing smoke tests are also coordinated by the orchestrator.

Created in [T3 Code](https://t3.codes).
