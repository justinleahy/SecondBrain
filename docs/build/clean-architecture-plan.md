# Clean-architecture migration plan

| | |
| --- | --- |
| **Branch** | `refactor/clean-architecture` |
| **Baseline** | `37d47fb`. `dotnet build SecondBrain.slnx` and `dotnet test SecondBrain.slnx` are green with 516 tests: 515 deterministic tests plus the unset-URL `Category=Qualification` no-op. |
| **Authority** | `spec.md` is the authority on requirements. This plan does not edit it. Section 1.4 lists the deviations from the §18 reference-stack table that need sign-off. |
| **Status** | Ready to implement, one commit per step (S0–S16). |
| **Date** | 2026-10-08 |

## 0. Summary

### Target shape

The target keeps every spec §4 component name and adds one library.

**`SecondBrain.Core` (the Engine)** becomes the Domain and Application layers:
- It holds the domain model, use cases, policies and every port.
- It does no file-system, socket, DNS or environment access.
- It contains no P/Invoke, SQL or ADO.NET, ASP.NET Data Protection, or Argon2.
- It still has no project references.

**`SecondBrain.Infrastructure` (new library, not a process)** holds the platform adapters shared by the daemon and `brain`:
- YAML loading and configuration reload
- Secrets
- Unix P/Invoke helpers
- The data-root lock
- The file key ring
- Argon2
- DNS and TCP defaults
- The extractor socket client
- Host file-system policy helpers

**`SecondBrain.Storage`** owns every SQL statement, including the auth, sources, password-policy and account-recovery statements that today live in Server and the CLI. It also owns the `DbConnection`-shaped store handles.

**`SecondBrain.Providers.OpenAICompatible`** stays the only home of vendor SDKs. It now depends on a privacy port instead of the concrete `PrivacyPolicy`.

**`SecondBrain.Server`** keeps only presentation (REST, Blazor, middleware, readiness) and the daemon composition root. It has no repositories and no SQL.

**`brain` (`SecondBrain.Cli`)** compiles only against Core, Infrastructure and Storage. Its `SecondBrain.Server` reference stays, but only for layout, so `brain serve` still finds the daemon beside it.

**`SecondBrain.Extractor`** and the mock provider reference no SecondBrain project.

Architecture tests enforce all of the above.

### What does not change

- Assembly names and project paths stay as they are: `SecondBrain.Server`, `brain`, `SecondBrain.Extractor`, `SecondBrain.Storage`, `SecondBrain.Core`, `SecondBrain.Providers.OpenAICompatible`, and `src/SecondBrain.X/SecondBrain.X.csproj`. The Dockerfile, compose, systemd units and CI publish matrix are therefore untouched.
- Persisted and external contracts stay the same:
  - Appendix A SQL and meta keys.
  - JSON shapes: `SessionTicket`, `SessionDevice`, `PasswordParameters`, `FolderSourceConfiguration` and `keyring/hmac.json`.
  - Data Protection application name and purposes.
  - Appendix B YAML keys.
  - OpenAPI schema IDs.
  - Exception messages and `brain` exit codes.
- The nine lane composition methods (`AddConfiguration` … `AddLimits`), the lane class names, `AddSecondBrainStorage`, and the global-namespace `Program` stay.
- Every one of the 516 existing tests keeps passing. Tests change only through `using` lines, the call-site edits listed in steps S3 and S7, and the namespace moves in S14 and S15.

### 0.1 Decisions settled by reading the code

| Question | Decision | Evidence in the tree |
| --- | --- | --- |
| Separate `SecondBrain.Core.Domain` assembly? | **No.** Domain and Application are namespaces inside `SecondBrain.Core`, and the layer boundary is enforced by test (AR-14). | `src/SecondBrain.Core/Domain/*` and `Authorization/*` already use only the BCL and `JsonSchema.Net`. Spec §4 defines the Engine as one project, §18 puts `JsonSchema.Net` in Core, and the M1 plan §3 states "Core continues to have no project references". |
| Where does `ConfigurationValidator` go? | **It stays in Core** behind a new `IHostPathInspector` port. | Its only host I/O is 3 × `UnixPath.Canonicalize` plus 1 × `Directory.Exists`. Its only callers are `YamlConfigurationLoader` and `Cli/Program.cs:42`, and no test calls it. |
| `EgressCanary` and `PrivacyPolicy` defaults | Both **stay in Core**. `PrivacyPolicy` now requires an `IDnsResolver`. `EgressCanary` keeps its signature but no longer creates a TCP connector itself. | Only 4 test call sites rely on the DNS default (`PrivacyPolicyTests.cs:18,27,67,167`) and 2 on the TCP default (`EgressCanaryTests.cs:25`, `TransportTests.cs:210`). DI already supplies both services (`LaneB.Domain.cs` `AddPrivacy`). |
| How to split the key ring | Core gets `IHmacKeyRing` (ActiveKid, Sign, Verify). `IKeyRing : IHmacKeyRing` moves to Infrastructure and adds `DataProtectionProvider`. **`IKeyRing` stays the registered DI service type.** | Tests replace `IKeyRing` by type (`LaneDWebFactory.cs:133-134`, `HttpTests.cs:204`, `CliConvergenceTests.cs:318`). `ConfigurationIntegrationTests` reads `ring.DataProtectionProvider`. `CliConvergenceTests.cs:323` calls `AddAuth()` without `AddKeyRing()`, so the forwarding registration must also be in `AddAuth`. |
| Where do `IStateStore`, `IIndexStore` and `IStoreHandle` go? | **Move them to `SecondBrain.Storage`** (step S12). | No Core type consumes them: the Durability ports never mention them. After S6–S9, their only users are Storage, two Server composition and readiness sites, the CLI and tests. |
| How does reset-password keep its rollback? | Throw the new `AccountNotInitializedException` inside the write callback. The CLI maps it to the same `CliPreconditionException`. | `SqliteStore.WriteWork.ExecuteAsync` rolls back and rethrows callback exceptions unwrapped. |
| Keep the CLI → Server reference? | **Yes, but only for layout** (`Aliases="ServerLayout"`). | `InstalledDaemonCommands` looks for `SecondBrain.Server` and `SecondBrain.Server.dll` beside `AppContext.BaseDirectory`. Today's CLI output already contains the `SecondBrain.Server` apphost, `.dll`, `.deps.json` and `.runtimeconfig.json`. |
| Move YAML attributes and JSON Schema out of Core? | **No** (see non-goals). | Spec §18 lists `YamlDotNet` and `JsonSchema.Net` under Core. After S4, Core's only YamlDotNet use is the 80 `[YamlMember(Alias = …)]` attributes. |
| Can the full `Microsoft.Extensions.AI` package go? | **Yes.** Core keeps only `Microsoft.Extensions.AI.Abstractions`. | Nothing uses `ChatClientBuilder` or similar builders. `AIFunctionFactory` (used in `AdapterTests.cs:38`) lives in `Microsoft.Extensions.AI.Abstractions` 10.10.1. The full package reaches the adapter only through Core. |
| Purpose literals in the host | Replace them with the `KeyRingPurposes` constants. | `"secondbrain.session"` (`CredentialService`) and `"secondbrain.antiforgery"` (`KeyRingDataProtectionProvider`) equal the existing constants exactly. |
| Where do `LoginService`, `CredentialAuthority` and `AdmissionController` go? | **To Core** (`Core.Auth`, `Core.Limits`). | None of them uses an HTTP type, and the M1 plan needs `IAdmissionController` outside the HTTP middleware. |
| `CredentialSummary` | Stays in Server. | It is an HTTP DTO used only by `AuthEndpoints` and `AuthTests`. |

## 1. Final projects and allowed references

### 1.1 Layers

```
Domain        SecondBrain.Core  (namespaces SecondBrain.Core.Domain, .Authorization)
Application   SecondBrain.Core  (all other SecondBrain.Core.* namespaces: use cases, policies, ports)
Infrastructure SecondBrain.Infrastructure | SecondBrain.Storage | SecondBrain.Providers.OpenAICompatible
Presentation / composition roots  SecondBrain.Server (daemon) | SecondBrain.Cli = brain | SecondBrain.Extractor (sandbox host)
```

All references point inward. Infrastructure projects never reference each other. Only the hosts reference more than one of them.

### 1.2 Dependency table (end state)

| Project | Layer | Allowed `ProjectReference`s | Direct packages | Must never reference |
| --- | --- | --- | --- | --- |
| `src/SecondBrain.Core` | Domain and Application | none | `Microsoft.Extensions.AI.Abstractions`, `Microsoft.Extensions.Options`, `YamlDotNet` (attributes only), `JsonSchema.Net` (Domain only), `Ulid` | Any project; `Microsoft.AspNetCore.*`; full `Microsoft.Extensions.AI`; `Microsoft.Extensions.{Configuration,DependencyInjection,Hosting,Http}*`; Dapper or SQLite; Isopoh; vendor SDKs |
| `src/SecondBrain.Infrastructure` (new) | Infrastructure (platform) | Core | `YamlDotNet`, `Microsoft.AspNetCore.DataProtection.Abstractions`, `Isopoh.Cryptography.Argon2`, `Microsoft.Extensions.Options`, `Ulid` | Storage, Providers.\*, Server, Cli; Dapper or SQLite; vendor SDKs; DI or Hosting (no composition helpers) |
| `src/SecondBrain.Storage` | Infrastructure (persistence) | Core | `Microsoft.Data.Sqlite`, `SQLitePCLRaw.bundle_e_sqlite3`, `Dapper`, `Microsoft.Extensions.Hosting` (unchanged) | Infrastructure, Providers.\*, Server, Cli |
| `src/SecondBrain.Providers.OpenAICompatible` | Infrastructure (provider adapter) | Core | `Microsoft.Extensions.AI.OpenAI`, `OpenAI`, `Microsoft.Extensions.Http` (unchanged) | Infrastructure, Storage, Server, Cli |
| `src/SecondBrain.Server` (Web SDK, global `Program`) | Presentation plus daemon composition root | Core, Infrastructure, Storage, Providers.OpenAICompatible | `Microsoft.AspNetCore.OpenApi`, `…Authentication.JwtBearer`, `Microsoft.Extensions.Hosting.Systemd`, `System.IdentityModel.Tokens.Jwt`, `Microsoft.IdentityModel.Tokens` (Isopoh removed) | Direct use of Dapper or SQLite (no SQL) |
| `src/SecondBrain.Cli` (`brain`) | Presentation (CLI) plus local-command composition | Core, Infrastructure, Storage; **Server with `Aliases="ServerLayout"` (layout only)** | `System.CommandLine`, `Microsoft.Extensions.Hosting`, `Microsoft.Extensions.Http`, `YamlDotNet`; `FrameworkReference Microsoft.AspNetCore.App`; `RestoreEnablePackagePruning=false` (unchanged) | Compile-time use of Server or Providers.\* types; Dapper |
| `src/SecondBrain.Extractor` | Sandbox host | none | `Microsoft.Extensions.Hosting`, `…Hosting.Systemd` | Any SecondBrain project |
| `tests/SecondBrain.MockProvider` | Test fixture host | none | Web SDK only | Any SecondBrain project |
| `tests/SecondBrain.Architecture.Tests` (new, S0) | Rules | Server, Cli, Extractor, MockProvider | Test SDK, xunit, runner; `FrameworkReference Microsoft.AspNetCore.App` | — |
| `tests/SecondBrain.Core.Tests` | Domain and Application tests | Core, Infrastructure (end state, after S15) | Test SDK, xunit, runner, `Microsoft.Extensions.TimeProvider.Testing` | Providers.\*, MockProvider (after S15) |
| `tests/SecondBrain.Infrastructure.Tests` (new, S14) | Platform adapter tests | Infrastructure | Test SDK, xunit, runner | — |
| `tests/SecondBrain.Providers.OpenAICompatible.Tests` (new, S15) | Adapter and transport tests | Providers.OpenAICompatible, Infrastructure, MockProvider | Test SDK, `Microsoft.AspNetCore.Mvc.Testing`, xunit, runner, `TimeProvider.Testing` | — |
| `tests/SecondBrain.Storage.Tests`, `Server.Tests`, `Deploy.Tests` | Unchanged roles | Unchanged | `Server.Tests` gains an explicit `YamlDotNet` reference (S4) | — |

`Core.Tests` keeps an Infrastructure reference on purpose. Six existing privacy tests use the real `SystemDnsResolver` and `TcpCanaryConnector`, and swapping in fakes would change what they test. The dependency rule applies to production assemblies.

### 1.3 Where each type lives at the end

| Assembly | Namespace | Contents |
| --- | --- | --- |
| Core | `.Domain` | `ContentTypeDefinition`, `Document`, `DocumentModel`, `DocumentSnapshot`, `TypeRegistry` (unchanged) |
| Core | `.Authorization` | `EndpointPolicy`, `M0ScopePolicy`, `IScopePolicy`, `Scope`, `ScopeMatrix` (unchanged) |
| Core | `.Auth` (new) | `AuthRecords` (`CredentialRecord`, `AccountRecord`, `LoginAttempt`, `AuthenticatedCredential`, `SessionTicket`, `SessionDevice`, `AuthTime` now public, `AuthorityChangedException`), `IAuthRepository`, `PasswordParameters`, `IPasswordHasher`, `IPasswordPolicyStore`, `PasswordPolicy`, `CredentialFactory` (+`CreatedCredential`, `InitializationRecords`, `IAdminCredentialFactory`), `CredentialAuthority` (+`ICredentialAuthority`), `LoginService` (+`LoginDecision`), `IAccountRecoveryStore` (+`AccountNotInitializedException`) |
| Core | `.Configuration` | All `*Options` types (with their `[YamlMember]` attributes), `ConfigurationException`, `ConfigurationValidator`, `ISecretResolver`, `IHostPathInspector` (new) |
| Core | `.Durability` | Unchanged ports (`ICrashPoints`, `IMutationJournal`, `IPublicationCoordinator`, …) |
| Core | `.Limits` (new) | `AdmissionResource`, `AdmissionPolicy`, `AdmissionSnapshot`, `AdmissionDecision`, `IAdmissionController`, `IDiskCapacity`, `AdmissionController` |
| Core | `.Privacy` | `PrivacyPolicy`, `EgressCanary`, `ICanaryConnector`, `IDnsResolver`, `IPrivacyPolicy`, `IPrivacyReadiness`, `IProviderEgressPolicy` (new), `PrivacyPolicyException`, `DEPLOYMENT.md` |
| Core | `.Problems`, `.Providers` | `ProblemTypes`; `IProviderBinding`, `ModelCapabilities`, `ModelLimits`, `ModelRole`, and `ProviderServices` (minus the environment resolver) |
| Core | `.Security` | `IHmacKeyRing` (new), `IAccountEpochRevoker`, `KeyRingPurposes` |
| Core | `.Sources` (new) | `FolderSourceConfiguration`, `SourceRecord`, `ISourceRepository`, `SourcePathValidation`, `ISourcePathValidator` |
| Core | `.Storage` | `IStorageStatus`, `IMigrationRunner` (+records), `IStoreInitializer` (+records) |
| Infrastructure | `.Configuration` | `YamlConfigurationLoader` (+`ConfigurationSnapshot`), `ReloadingConfiguration`, `SecretResolver`, `SecondBrainOptionsMonitor` |
| Infrastructure | `.Security` | `UnixPath`, `UnixSecurity`, `UnixAccounts`, `RootSecurityValidator` (+`RootSecurityCheck`), `DataRootLock` (+`DataRootLockedException`), `IKeyRing`, `FileKeyRing`, `PasswordHasher` (+`Argon2Calibration`) |
| Infrastructure | `.Network`, `.Extraction`, `.FileSystem` | `SystemDnsResolver`, `TcpCanaryConnector`; `ExtractorPing`; `UnixHostPathInspector`, `SourcePathValidator`, `DiskCapacity` |
| Storage | `SecondBrain.Storage` | `IStoreHandle` (+`IReadConnectionLease`), `IStateStore`, `IIndexStore`, `IStoreSnapshot`, the existing `Sqlite*` stores and `StorageServiceCollectionExtensions` |
| Storage | `.Auth` (new), `.Sources` (new) | `AuthRepository`, `SqlitePasswordPolicyStore`, `SqliteAccountRecoveryStore`; `SqliteSourceRepository` |
| Storage | `.Connections`, `.Durability`, `.Initialization`, `.Migrations` | Unchanged. `PublicationCoordinator` and `MutationJournal` stay together. |
| Providers.OpenAICompatible | unchanged | `BoundClients`, `ModelCatalog` (`mock-chat` and `mock-embed` kept), `OpenAICompatibleBinding`, `PolicyHttpClientFactory`, `ProviderRegistry`, `EnvironmentProviderCredentialResolver` (moved in) |
| Server | `.Auth` | `AuthEndpoints`, `CredentialAuthenticationHandler`, `CredentialService`, `CredentialSummary`, `RequestAuthorizationMiddleware`, `SessionCircuitSecurity`, `Calibration.md` |
| Server | `.Limits`, `.Sources`, `.Http`, `.Composition`, `.Components` | `AdmissionMiddleware`, `LimitsServices`; `SourceEndpoints`, `SourcesServices`, `CreateSourceRequest`; `Http/*` (unchanged); `Lane{A,B,C,D}*` (unchanged names); Razor components |

### 1.4 Spec alignment

**Requirements preserved:**
- §2.7: no new process.
- §2.8 and §13: vendor SDKs appear only in `Providers.*` (AR-10), and `new SecondBrainOptions()` still has no provider (`Core.Tests` `SmokeTests`).
- §4: names and roles unchanged.
- §5.1: credential, session and source persistence now lives in Storage.
- §13.5 and SEC-15: one policy-owned `SocketsHttpHandler`, unchanged.
- §14 and Appendix B: keys pinned by `yaml-aliases.txt`.
- §15: purposes, formats, umask timing, lock and validation unchanged.
- §16: `/health` still resolves neither the stores nor the key ring.
- SEC-20: the Extractor loses Core.

**Deviations from the §18 reference-stack table (record sign-off; `spec.md` is not edited):**
1. A new library, `SecondBrain.Infrastructure`, sits under the existing Daemon and CLI roles.
2. `Isopoh.Cryptography.Argon2` and `Microsoft.AspNetCore.DataProtection.Abstractions` are referenced from Infrastructure, not Server. Server still ships both through that reference.
3. The CLI still compiles against Storage and Infrastructure for `brain init`, reset-password and `rotate-keys --revoke-all`. This is pre-existing; spec §4 allows those local commands.

**Deviation from the M0 plan §3:** the data-root lock lives in Infrastructure, not Storage. `FileKeyRing` also locks `keyring/` with it, and the CLI acquires it, so Storage would be the wrong owner.

### 1.5 Observable but non-contractual differences

These are allowed. Nothing in the repo (tests, `deploy/`, CI) depends on them.

1. When daemon startup fails, the runtime prints the exception's full type name in its `Unhandled exception. <FQN>` line, and `brain serve` repeats that name in its "Daemon startup failed (…)" diagnostic. For moved types the namespace changes, for example `SecondBrain.Infrastructure.Security.DataRootLockedException`. Exit codes do not change, because `InstalledDaemonCommands` matches only the simple type name `DataRootLockedException` and fixed message text (AR-17).
2. The logger category for configuration-reload warnings becomes `SecondBrain.Infrastructure.Configuration.ReloadingConfiguration`. No log filter in the repo uses categories.
3. Publish outputs change:
   - Server and CLI gain `SecondBrain.Infrastructure.dll`.
   - Server and CLI lose the unused full `Microsoft.Extensions.AI.dll` and its unused transitive packages after S13.
   - The Extractor and the mock no longer ship `SecondBrain.Core.dll`, YamlDotNet, JsonSchema.Net, M.E.AI or DataProtection.
4. Passing an explicit `null` `IDnsResolver` to `PrivacyPolicy`, or `null` `ICanaryConnector` to `EgressCanary`, now throws `ArgumentNullException` instead of silently using system DNS or TCP. No caller does this after S3.

## 2. Namespace and placement conventions

1. **Namespace = assembly root + folder path.** For example, `src/SecondBrain.Infrastructure/Security/FileKeyRing.cs` → `SecondBrain.Infrastructure.Security`. New store abstractions go at the Storage project root (`namespace SecondBrain.Storage`), beside `SqliteStateStore.cs`.
2. **Layers inside Core are namespaces.**
   - Domain: `SecondBrain.Core.Domain` and `SecondBrain.Core.Authorization`. These may use only the BCL, `Ulid` and (Domain only) `Json.Schema`.
   - Application: every other `SecondBrain.Core.*` namespace. These may use Domain.
3. **Ports live in Core**, in the namespace of the capability they serve. They are named `I<Noun>` (`IAuthRepository`, `IPasswordPolicyStore`, `IAccountRecoveryStore`, `IHostPathInspector`, `IHmacKeyRing`, `IProviderEgressPolicy`).
4. **Adapters are named for their technology** (`Sqlite*`, `Unix*`, `File*`, `Tcp*`, `System*`, `Yaml*`). They live in the outer assembly that owns that technology:
   - Storage for SQLite.
   - Providers.\* for vendor SDKs.
   - Infrastructure for everything else.
5. **One top-level public type per file** when a file is touched by a move. Split multi-type files on move, but leave untouched files alone.
6. **Composition stays in the hosts.**
   - Server: `Lane*` classes and `Add*` methods.
   - CLI: `CliServices` and `LocalInitializationCommands`.
   - Storage: keeps `AddSecondBrainStorage`.
   - Infrastructure exposes no DI extensions (AR-16).
7. **Persisted or external contract types keep their simple names, JSON property names and messages.** Only their namespaces may change, because OpenAPI schema IDs and the CLI's stderr matching rely on simple names.
8. **No relative namespace qualification** (`Core.Storage.X`, `Auth.X`) in touched code. When a referenced type moves, rewrite the existing relative references at `LaneD.Http.cs:20` and `ProblemResponses.cs:60`.
9. **Test namespaces** follow the pattern `SecondBrain.<Assembly>.Tests[.<Folder>]`. A test that moves takes its new project's namespace and keeps its class and method names.

## 3. Implementation steps

### 3.0 Rules for every step

- **One step is one commit**, and every commit passes Gate A (§4.1).
- **Use `git mv` for moves.** Bodies, SQL, P/Invoke signatures and flags, exception types and messages stay **verbatim**. The only allowed edits are:
  - the namespace line;
  - `using` lines;
  - the edits each step lists explicitly.
- **Expect missing implicit usings.** Moving a file from the Web SDK (Server) to a class library loses `Microsoft.AspNetCore.*` and `Microsoft.Extensions.*` implicit usings. Add explicit `using`s and change nothing else.
- **Namespaces can disappear.** `SecondBrain.Core.Extraction` disappears in S3, so a stale `using` becomes CS0234. Delete it.
- **Regenerate lock files when references change.** When a `ProjectReference` or `PackageReference` changes, run `dotnet restore SecondBrain.slnx --force-evaluate`. Commit **every** changed `packages.lock.json` in the same commit.
- **Add new projects to the solution properly.** Add them to `SecondBrain.slnx` under the right folder. They inherit `Directory.Build.props` (`TreatWarningsAsErrors`, `AnalysisLevel=latest`, lock files, `linux-x64`) and must use central versions only.
- **Golden files** (created in S1):
  - `composition.txt` may change only by the registrations the step lists under **DI**.
  - `openapi.json` and `yaml-aliases.txt` must never change in this plan.
- **Do not run `dotnet format` or any repo-wide cleanup.**

---

### S0. Architecture test project with baseline rules

**Goal:** make the dependency rule executable before any code moves.

**Create**
- `tests/SecondBrain.Architecture.Tests/SecondBrain.Architecture.Tests.csproj`. Copy the shape of `Deploy.Tests`:
  - `IsTestProject`, `IsPackable=false`, `FrameworkReference Microsoft.AspNetCore.App`.
  - `ProjectReference`s to `src/SecondBrain.Server`, `src/SecondBrain.Cli`, `src/SecondBrain.Extractor` and `tests/SecondBrain.MockProvider`.
  - Packages `Microsoft.NET.Test.Sdk`, `xunit`, `xunit.runner.visualstudio`, with the same asset flags as the other test projects.
  - `System.Reflection.Metadata` is in the shared framework, so no new package is needed.
- `Support/AssemblyFacts.cs` reads `Assembly.Location` through `PEReader`/`MetadataReader` and returns:
  - assembly-reference names;
  - type references (`Namespace.Name`, nested types as `Outer+Inner`);
  - methods carrying `MethodAttributes.PinvokeImpl`.
- `Support/Repository.cs`:
  - Finds the repo root (the directory containing `spec.md` and `deploy/`, the same rule as `DeploymentArtifactsTests`).
  - Enumerates `src/**/*.cs`, excluding `bin` and `obj`.
  - Parses csproj XML: `ProjectReference` `Include` plus `Aliases`, and `PackageReference` `Include`.
- `Support/Layers.cs` holds one anchor type per assembly:
  - Core: `SecondBrain.Core.Problems.ProblemTypes`
  - Storage: `SecondBrain.Storage.StorageServiceCollectionExtensions`
  - Providers: `SecondBrain.Providers.OpenAICompatible.ModelCatalog`
  - Server: `global::Program`
  - brain: `SecondBrain.Cli.Program`
  - Extractor: `SecondBrain.Extractor.Program`
  - MockProvider: `SecondBrain.MockProvider.Program`

  The Infrastructure anchor is added in S3.
- `DependencyRulesTests.cs`, `SourceRulesTests.cs`, `DaemonStderrContractTests.cs`, implementing the rules below.

**Edit:** add the project to `SecondBrain.slnx` (`/tests/`) and commit its lock file.

**Rules switched on:**
- AR-01
- AR-03 (Storage and Providers part; the Server allow-set already includes Infrastructure)
- AR-10
- AR-14
- AR-17

**Done when:** the new tests pass against unmodified production code. If AR-10 fails at baseline, stop and report it as a pre-existing provider-neutrality violation. Do not add it to an allow-list.

### S1. Characterization tests (tests only)

**Goal:** pin DI composition, the HTTP contract and the persisted contracts. Golden files are generated from **unmodified** production code. Mark each golden file `CopyToOutputDirectory="PreserveNewest"` in its test csproj.

**Create**
1. **Composition snapshot:** `tests/SecondBrain.Server.Tests/CompositionSnapshotTests.cs` and `Golden/composition.txt`.
   - Build `WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing", ApplicationName = typeof(global::Program).Assembly.GetName().Name })`.
   - Copy `builder.Services` before the calls.
   - Call the nine `Add*()` methods in `Program.cs` order.
   - Keep the descriptors that were added (compare by reference) **and** whose service, implementation or instance type, or any of their generic arguments, comes from a `SecondBrain.*` or `brain` assembly. Also keep added descriptors whose service type is `TimeProvider`, `IDataProtectionProvider`, `IHostedService` or `IStartupFilter`.
   - Write one line per kept descriptor, in order: `Lifetime ServiceType[key] -> ImplType | instance:Type | factory`.
   - Use `Type.Name`, rendering generic arguments by `Name`, so later namespace moves do not churn the file.
   - For keyed descriptors, read the `Keyed*` properties; the non-keyed accessors throw.
   - Do not build the provider. `AddKeyRing` sets umask 0077 at registration, as `Program` already does in this test process.
2. **OpenAPI snapshot:** `OpenApiSnapshotTests.cs` and `Golden/openapi.json`.
   - Fetch `GET /v1/openapi.json` through `LaneDWebFactory.CreatePrivateClient()`, as `HttpTests.OpenApiContainsEndpointRequestSchemas` does.
   - The golden contains a sorted `METHOD path` list and `components.schemas`, with object keys sorted recursively.
3. **Base64Url equivalence:** `tests/SecondBrain.Server.Tests/Base64UrlCompatibilityTests.cs`.
   - For every length from 0 to 64, and for 10,000 random 32-byte buffers, assert `WebEncoders.Base64UrlEncode(b) == System.Buffers.Text.Base64Url.EncodeToString(b)`.
4. **Key-ring purposes:** `tests/SecondBrain.Core.Tests/Security/KeyRingPurposesTests.cs` asserts the exact values `secondbrain.session`, `secondbrain.cursor`, `secondbrain.antiforgery`, `secondbrain.capability` and `secondbrain` (`ApplicationName`).
5. **YAML key contract:** `tests/SecondBrain.Core.Tests/Configuration/YamlKeyContractTests.cs` and `tests/SecondBrain.Core.Tests/Golden/yaml-aliases.txt`.
   - Reflect over the Core assembly's public types.
   - Emit `Type.Property=alias` for every public instance property with `[YamlMember]`, sorted.
   - Compare with the golden, which has one line per attribute (80 `[YamlMember]` attributes at baseline) and includes `api_key`, `requests_per_min`, `adapter`/`base_url` and `kind`/`endpoint`.
6. **Store-handle identity:** add a test to `tests/SecondBrain.Storage.Tests/CompositionTests.cs`: `StoreHandlesAreSingleUndecoratedInstances`.
   - `IStateStore` resolves to the same instance as `SqliteStateStore`, twice in a row; likewise `IIndexStore` and `SqliteIndexStore`.
   - This matters because `PublicationCoordinator` and `MutationJournal` key static locks by instance.

**Rules switched on:** none. **DI:** none.

### S2. Drop the unused Core references from the sandbox and the mock

**Edit**
- `src/SecondBrain.Extractor/SecondBrain.Extractor.csproj`: remove `<ProjectReference Include="../SecondBrain.Core/…">`.
- `tests/SecondBrain.MockProvider/SecondBrain.MockProvider.csproj`: remove the same reference and delete the now-empty `ItemGroup`.

**Lock files:** these change for Extractor, MockProvider, and every project that consumes them (`Core.Tests`, `Server.Tests`, `Deploy.Tests`, `Architecture.Tests`).

**Rules switched on:** AR-08.

**Extra verification**
- `dotnet publish src/SecondBrain.Extractor/SecondBrain.Extractor.csproj -c Release -r linux-x64 --self-contained false -o /tmp/sb-extractor` produces no `SecondBrain.Core.dll`, `YamlDotNet.dll`, `JsonSchema.Net.dll`, `Microsoft.Extensions.AI*.dll` or `Microsoft.AspNetCore.DataProtection*.dll`.
- `ExtractorTests` and `ExtractorEnvironmentTests` are green.

### S3. Create `SecondBrain.Infrastructure`; move the network and extractor-client defaults out of Core

**Create**
- `src/SecondBrain.Infrastructure/SecondBrain.Infrastructure.csproj`: plain `Microsoft.NET.Sdk`, `ProjectReference` to Core only, no packages yet. Add it to `SecondBrain.slnx` under `/src/`.
- `src/SecondBrain.Core/Privacy/ICanaryConnector.cs`, split out of `EgressCanary.cs` with its namespace unchanged.

**Move**
| From | To | Namespace |
| --- | --- | --- |
| `src/SecondBrain.Core/Extraction/ExtractorPing.cs` | `src/SecondBrain.Infrastructure/Extraction/ExtractorPing.cs` | `SecondBrain.Infrastructure.Extraction` (`SecondBrain.Core.Extraction` disappears) |
| `SystemDnsResolver` (from `Core/Privacy/IDnsResolver.cs`) | `src/SecondBrain.Infrastructure/Network/SystemDnsResolver.cs` | `SecondBrain.Infrastructure.Network` (`IDnsResolver` stays in Core) |
| `TcpCanaryConnector` (from `Core/Privacy/EgressCanary.cs`) | `src/SecondBrain.Infrastructure/Network/TcpCanaryConnector.cs` | `SecondBrain.Infrastructure.Network` |
| `EnvironmentProviderCredentialResolver` (from `Core/Providers/ProviderServices.cs`) | `src/SecondBrain.Providers.OpenAICompatible/EnvironmentProviderCredentialResolver.cs` | `SecondBrain.Providers.OpenAICompatible` (its only default user is `ProviderRegistry.ValidateConfiguration`) |

**Edit (production)**
- `PrivacyPolicy` constructor becomes `(IOptionsMonitor<SecondBrainOptions> options, IDnsResolver resolver, TimeProvider? timeProvider = null)`. Add `ArgumentNullException.ThrowIfNull(resolver)` and remove the `?? new SystemDnsResolver()` default.
- `EgressCanary` keeps its exact signature, but `_connector = connector ?? throw new ArgumentNullException(nameof(connector));`.
- `Server.csproj` and `Cli.csproj` gain a `ProjectReference` to Infrastructure.
- `Server/Http/Readiness.cs` and `Cli/Commands/DoctorService.cs`: replace `using SecondBrain.Core.Extraction;` with `using SecondBrain.Infrastructure.Extraction;`.
- `Server/Composition/LaneB.Domain.cs`: add `using SecondBrain.Infrastructure.Network;`.

**Tests**
- `Core.Tests.csproj` gains a `ProjectReference` to `src/SecondBrain.Infrastructure`.
- `PrivacyPolicyTests.cs` lines 18, 27, 67 and 167 pass `new SystemDnsResolver()` as the second argument, which keeps the real-DNS semantics.
- `EgressCanaryTests.cs:25` becomes `new EgressCanary(policy, monitor, time, new TcpCanaryConnector())`.
- `TransportTests.cs:210` becomes `new EgressCanary(context.Privacy, context.Options, connector: new TcpCanaryConnector())`.
- `ProviderTestContext.cs` gains `using SecondBrain.Infrastructure.Network;`.
- `Architecture.Tests` gains the Infrastructure anchor `SecondBrain.Infrastructure.Network.SystemDnsResolver`.

**DI:** registrations are unchanged; `composition.txt` does not change.

**Rules switched on:**
- AR-03 (Infrastructure ⊆ {Core})
- AR-06a (Core: no `System.Net.Dns`, `Socket`, `TcpClient`, `UdpClient`, `NetworkStream`, `UnixDomainSocketEndPoint`)
- AR-16

### S4. Move configuration infrastructure out of Core behind `IHostPathInspector`

**Create**
- `src/SecondBrain.Core/Configuration/IHostPathInspector.cs`:
  ```csharp
  public interface IHostPathInspector
  {
      string Canonicalize(string path);   // realpath semantics
      bool DirectoryExists(string path);
  }
  ```
- `src/SecondBrain.Core/Configuration/ISecretResolver.cs`: the interface, split from `SecretResolver.cs` with its namespace unchanged.
- `src/SecondBrain.Infrastructure/FileSystem/UnixHostPathInspector.cs`: `public static UnixHostPathInspector Instance { get; } = new();`. `Canonicalize` delegates to `UnixPath.Canonicalize`, which is still in `SecondBrain.Core.Security` until S5. `DirectoryExists` delegates to `Directory.Exists`.

**Move** (namespace becomes `SecondBrain.Infrastructure.Configuration`)
- `Core/Configuration/YamlConfigurationLoader.cs` (+`ConfigurationSnapshot`). The only code edit is `ConfigurationValidator.Validate(options)` → `ConfigurationValidator.Validate(options, UnixHostPathInspector.Instance)`.
- `Core/Configuration/ReloadingConfiguration.cs`, verbatim.
- `Core/Configuration/SecretResolver.cs` (the class only). `Expand` and `ReferencePattern` stay `internal`, since their caller is now in the same assembly.
- `SecondBrainOptionsMonitor` moves from `Server/Composition/LaneC.Configuration.cs` to `src/SecondBrain.Infrastructure/Configuration/SecondBrainOptionsMonitor.cs`, with the same public constructor.

**Edit (production)**
- `ConfigurationValidator.Validate(SecondBrainOptions options, IHostPathInspector paths)`:
  - Replace the three `UnixPath.Canonicalize(…)` calls with `paths.Canonicalize(…)` and `Directory.Exists(root)` with `paths.DirectoryExists(root)`.
  - Drop `using SecondBrain.Core.Security;`.
  - Keep the check order and messages identical. `IsLocal` and `IsPrivateInterface` are untouched.
- `src/SecondBrain.Cli/Program.cs:42` becomes `ConfigurationValidator.Validate(options, UnixHostPathInspector.Instance);`.
- Update usings in `LaneB.Domain.cs` (`ProviderStartup` uses `ReloadingConfiguration`), `LaneC.Configuration.cs` and `CliServices.cs`.
- `Directory.Packages.props`: add `<PackageVersion Include="Microsoft.Extensions.Options" Version="10.0.12" />`. This is the version already resolved transitively; record that reason in the commit message.
- `Infrastructure.csproj`: add `PackageReference`s `YamlDotNet` and `Microsoft.Extensions.Options`.
- `Cli.csproj`: add `PackageReference YamlDotNet`, because `ProvisioningService` uses `YamlDotNet.RepresentationModel` directly.
- `tests/SecondBrain.Server.Tests.csproj`: add `PackageReference YamlDotNet`, because `LaneDWebFactory` uses the serializer directly.

**Tests:** update usings in `Core.Tests/Configuration/ConfigurationTests.cs`, `Server.Tests/Support/LaneDWebFactory.cs`, `Server.Tests/ReadyTests.cs`, `Deploy.Tests/CliConvergenceTests.cs` and `Deploy.Tests/ConfigurationIntegrationTests.cs`.

**DI:** unchanged.

**Rules switched on:**
- AR-05 (Core's YamlDotNet type references are exactly `{YamlDotNet.Serialization.YamlMemberAttribute}`; `using Json.Schema` appears only in `src/SecondBrain.Core/Domain`)
- AR-06b (Core: no `FileSystemWatcher`, `PosixSignalRegistration`)

**Docs:** in `deploy/LANE-C-HANDOFF.md`, `YamlConfigurationLoader`, `ReloadingConfiguration` and `SecretResolver` now live in `SecondBrain.Infrastructure.Configuration`.

### S5. Move the security primitives and the key ring out of Core; introduce `IHmacKeyRing`

**Create**
- `src/SecondBrain.Core/Security/IAccountEpochRevoker.cs` and `src/SecondBrain.Core/Security/KeyRingPurposes.cs`, split out of `FileKeyRing.cs` with the namespace unchanged and the constant values unchanged.
- `src/SecondBrain.Infrastructure/Security/IKeyRing.cs`, keeping the existing XML docs:
  ```csharp
  public interface IKeyRing : IHmacKeyRing { IDataProtectionProvider DataProtectionProvider { get; } }
  ```

**Move**
- `git mv src/SecondBrain.Core/Security/IKeyRing.cs src/SecondBrain.Core/Security/IHmacKeyRing.cs`. Rename the interface to `IHmacKeyRing` and keep `ActiveKid`, `Sign` and `Verify`. Delete the `DataProtectionProvider` member and the DataProtection `using`.
- `Core/Security/{UnixPath, UnixSecurity, UnixAccounts, RootSecurityValidator (+RootSecurityCheck), DataRootLock (+DataRootLockedException)}.cs` → `src/SecondBrain.Infrastructure/Security/`, namespace `SecondBrain.Infrastructure.Security`. Keep these verbatim, including stat offsets, open flags and messages.
- `Core/Security/FileKeyRing.cs` (`FileKeyRing` + `RingDocument`) → `src/SecondBrain.Infrastructure/Security/FileKeyRing.cs`. The constructor, the `hmac.json` format and the messages are unchanged.

**Edit (production)**
- `UnixHostPathInspector`: change the `using` to `SecondBrain.Infrastructure.Security`.
- `Core.csproj`: remove `Microsoft.AspNetCore.DataProtection.Abstractions`.
- `Infrastructure.csproj`: add `Microsoft.AspNetCore.DataProtection.Abstractions` and `Ulid`.
- Add `using SecondBrain.Infrastructure.Security;` to:
  - Server: `Composition/LaneC.Configuration.cs`, `Http/Readiness.cs`, `Http/KeyRingDataProtectionProvider.cs`, `Http/AdministrationEndpoints.cs`, `Auth/CredentialService.cs`, `Auth/CredentialFactory.cs`.
  - CLI: `Program.cs`, `CliServices.cs`, `Commands/DoctorService.cs`, `Commands/LocalInitializationCommands.cs`.

**Tests:** update usings in:
- `Core.Tests`: `Security/KeyRingAndRootTests.cs`, `Configuration/ConfigurationTests.cs` (for `UnixPath`).
- `Server.Tests`: `Support/TestKeyRing.cs`, `AuthRaceTests.cs` (`RotatingOnReadKeyRing`), `HttpTests.cs`, `Support/LaneDWebFactory.cs`.
- `Deploy.Tests`: `CliConvergenceTests.cs`, `CliTests.cs`, `ConfigurationIntegrationTests.cs`.

**DI:** unchanged. `IKeyRing`, `FileKeyRing` and `DataRootLock` remain the registered types.

**Rules switched on:**
- AR-04a (Core: no `Microsoft.AspNetCore.*` assembly reference)
- AR-06c: Core has no type reference to:
  - the I/O types `File`, `Directory`, `FileInfo`, `DirectoryInfo`, `FileSystemInfo`, `FileStream` and `DriveInfo`;
  - the interop types `Marshal`, `NativeLibrary` and `SafeHandle`, or anything in `Microsoft.Win32.SafeHandles.*`;
  - `System.Environment` or `System.Diagnostics.Process`.
- AR-07 (P/Invoke only in Infrastructure, Storage, Extractor and brain)

**Docs:** in `deploy/LANE-C-HANDOFF.md`, `FileKeyRing` now lives in `SecondBrain.Infrastructure.Security`.

### S6. Move auth contracts to Core and auth persistence to Storage

**Create**
- `src/SecondBrain.Core/Auth/IPasswordPolicyStore.cs`: `Task<string?> ReadPasswordParametersJsonAsync(CancellationToken cancellationToken = default);`.
- `src/SecondBrain.Storage/Auth/SqlitePasswordPolicyStore.cs` (`IStateStore state`): the exact read-lease and command from `PasswordPolicy.LoadAsync`, `SELECT value FROM meta WHERE key='password_parameters'`, returning `as string`.
- `src/SecondBrain.Server/Auth/CredentialSummary.cs`: the HTTP DTO, extracted from `AuthRecords.cs` and kept in `SecondBrain.Server.Auth`.

**Move**
| From | To | Notes |
| --- | --- | --- |
| `Server/Auth/AuthRecords.cs` (all except `CredentialSummary`) | `src/SecondBrain.Core/Auth/AuthRecords.cs` | Namespace `SecondBrain.Core.Auth`. `AuthTime` goes from `internal` to `public`, because Storage and Server use it. |
| `IAuthRepository` (from `Server/Auth/AuthRepository.cs`) | `src/SecondBrain.Core/Auth/IAuthRepository.cs` | Members unchanged. |
| `AuthRepository` | `src/SecondBrain.Storage/Auth/AuthRepository.cs` | Namespace `SecondBrain.Storage.Auth`. SQL and constructor `(IStateStore, TimeProvider)` verbatim. |
| `PasswordParameters`, `IPasswordHasher` (from `Server/Auth/PasswordHasher.cs`) | `src/SecondBrain.Core/Auth/PasswordParameters.cs`, `IPasswordHasher.cs` | `PasswordHasher` and `Argon2Calibration` stay in Server until S7. |
| `Server/Auth/PasswordPolicy.cs` | `src/SecondBrain.Core/Auth/PasswordPolicy.cs` | Becomes `public static PasswordParameters Load(IPasswordPolicyStore store) => LoadAsync(store).GetAwaiter().GetResult();`. The null → `new PasswordParameters()`, the deserialization, the range checks and the `"Stored password policy is invalid."` messages stay verbatim. |

**Edit (production)**
- `LaneD.Http.cs:20` becomes `services.TryAddSingleton(provider => PasswordPolicy.Load(new SqlitePasswordPolicyStore(provider.GetRequiredService<IStateStore>())));`. Add usings `SecondBrain.Core.Auth`, `SecondBrain.Core.Storage` and `SecondBrain.Storage.Auth`. This removes the relative `Core.Storage.IStateStore`.
- `ProblemResponses.cs:60`: `catch (Auth.AuthorityChangedException)` becomes `catch (AuthorityChangedException)`, with `using SecondBrain.Core.Auth;`.
- Add `using SecondBrain.Core.Auth;` to:
  - Server: `Auth/AuthEndpoints.cs`, `CredentialAuthenticationHandler.cs`, `CredentialFactory.cs`, `CredentialService.cs`, `LoginService.cs`, `PasswordHasher.cs`, `RequestAuthorizationMiddleware.cs`, `SessionCircuitSecurity.cs`.
  - CLI: `LocalInitializationCommands.cs` (also `using SecondBrain.Storage.Auth;`) and `CliServices.cs`.

**Tests**
- Update usings in `Server.Tests` (`AuthTests`, `AuthRaceTests`, `SessionCircuitTests`, `SourcesTests`, `LimitsTests`, `Support/LaneDWebFactory`) and in `Deploy.Tests/CliConvergenceTests`.
- New `tests/SecondBrain.Storage.Tests/AuthRepositoryTests.cs`. Use a migrated real store, seeded through `StoreInitializer` or with `meta.account_epoch='1'` exactly as `LaneDWebFactory.CreateHost` does. Write one characterization test per public method, including:
  - `AddAsync` with a stale epoch throws `AuthorityChangedException` and inserts nothing.
  - `RotateAsync` revokes the old credential and inserts the new one atomically.
  - `TouchAsync` honours all three expiries.
  - `SaveAccountAsync(invalidate: true)` bumps the epoch.
  - `RecordLoginAsync` prunes attempts older than one day.
- New `SqlitePasswordPolicyStoreTests.cs`: a missing key returns `null`, and stored JSON comes back verbatim.

**DI:** the `IAuthRepository` implementation has the same simple name, so `composition.txt` does not change.

**Docs:**
- `src/SecondBrain.Server/Http/LaneD-Integration.md`: `IAuthRepository` is in `SecondBrain.Core.Auth`, implemented in `SecondBrain.Storage.Auth`.
- `src/SecondBrain.Server/Auth/Calibration.md`: update the `IAuthRepository` reference.

### S7. Move the auth use cases into Core and Argon2 into Infrastructure

**Move**
- `Server/Auth/CredentialFactory.cs` → `src/SecondBrain.Core/Auth/CredentialFactory.cs` (+`CreatedCredential`, `InitializationRecords`, `IAdminCredentialFactory`). Exactly three edits:
  - The constructor takes `IHmacKeyRing keyRing` instead of `IKeyRing keyRing`.
  - Both `WebEncoders.Base64UrlEncode(secret)` calls become `Base64Url.EncodeToString(secret)` (`using System.Buffers.Text;`; drop `Microsoft.AspNetCore.WebUtilities`).
  - `internal CreateSession` becomes `public`, because `CredentialService` stays in Server.
- `ICredentialAuthority` and `CredentialAuthority` (from `Server/Auth/CredentialService.cs`) → `src/SecondBrain.Core/Auth/CredentialAuthority.cs`.
- `Server/Auth/LoginService.cs` (+`LoginDecision`) → `src/SecondBrain.Core/Auth/LoginService.cs`.
- `Server/Auth/PasswordHasher.cs` (`PasswordHasher`, `Argon2Calibration`) → `src/SecondBrain.Infrastructure/Security/PasswordHasher.cs`. The class name, the static `Calibrate` and the Argon2 configuration are verbatim.

**Edit (production)**
- `Server.csproj`: remove `PackageReference Isopoh.Cryptography.Argon2`. `Infrastructure.csproj`: add it.
- `CredentialService`: `CreateProtector("secondbrain.session")` becomes `CreateProtector(KeyRingPurposes.Session)`.
- `KeyRingDataProtectionProvider`: `"secondbrain.antiforgery"` becomes `KeyRingPurposes.Antiforgery`. `KeyRingPurposesTests` from S1 proves the strings are identical.
- `LaneC.AddKeyRing`: directly after `services.AddSingleton<IKeyRing>(…)`, add `services.TryAddSingleton<IHmacKeyRing>(provider => provider.GetRequiredService<IKeyRing>());`, with `using Microsoft.Extensions.DependencyInjection.Extensions;`.
- `LaneD.AddAuth`: add the same `TryAddSingleton<IHmacKeyRing>` line. This is required because `CliConvergenceTests` composes `AddAuth` without `AddKeyRing`. The forwarder resolves lazily, so the test overrides `RemoveAll<IKeyRing>()`/`AddSingleton<IKeyRing>` keep working, and `/health` still never opens the ring.
- Update usings in:
  - Server: `AuthEndpoints`, `SessionCircuitSecurity`, `LaneD.Http`.
  - CLI: `LocalInitializationCommands` and `CliServices` (`PasswordHasher` from `SecondBrain.Infrastructure.Security`; `CredentialFactory` and `IAdminCredentialFactory` from `SecondBrain.Core.Auth`).

**Tests**
- **`tests/SecondBrain.Deploy.Tests/CliConvergenceTests.cs:309`** becomes `ApplicationName = typeof(global::Program).Assembly.GetName().Name,`. The value stays `"SecondBrain.Server"`. This must land in this commit, otherwise Razor and static-asset resolution breaks.
- Update usings in `Server.Tests` (`AuthTests`, `AuthRaceTests`, `LimitsTests`, `SourcesTests`, `SessionCircuitTests`, `Support/LaneDWebFactory`) and `Deploy.Tests/CliConvergenceTests` (for `PasswordHasher`).

**DI:** add exactly one line, `Singleton IHmacKeyRing -> factory`, at the `AddKeyRing` position in `composition.txt`. `AddAuth`'s `TryAdd` is a no-op in the full composition.

**Rules switched on:** AR-11 (only Infrastructure references `Isopoh.*`).

**Docs:**
- `Calibration.md`: `PasswordHasher` is in `SecondBrain.Infrastructure.Security`; `PasswordParameters` is in `SecondBrain.Core.Auth`.
- `LaneD-Integration.md`: `SecondBrain.Core.Auth.IAdminCredentialFactory`.

### S8. Remove SQL and Server types from the CLI; make the Server reference layout-only

**Create**
- `src/SecondBrain.Core/Auth/IAccountRecoveryStore.cs`:
  ```csharp
  public interface IAccountRecoveryStore
  {
      Task<string?> FindInitialAdminCredentialIdAsync(CancellationToken cancellationToken = default);
      Task<long> ResetPasswordAsync(AccountRecord account, string passwordParametersJson, CancellationToken cancellationToken = default);
  }
  public sealed class AccountNotInitializedException() : InvalidOperationException("The account is not initialized.");
  ```
- `src/SecondBrain.Storage/Auth/SqliteAccountRecoveryStore.cs` (`IStateStore state`):
  - `FindInitialAdminCredentialIdAsync` runs the two queries from `LocalInitializationCommands.InitializeAsync` on one read lease: the meta key first, then the oldest admin `api_key`.
  - `ResetPasswordAsync` is the `QueueWriteAsync` callback from `LocalInitializationCommands.ResetPasswordAsync` with the SQL verbatim. The only change is `if (changed != 1) throw new AccountNotInitializedException();` inside the callback, so the rollback path is identical.

**Edit (production)**
- `src/SecondBrain.Cli/Commands/LocalInitializationCommands.cs`:
  - Remove `using Dapper;` and `using SecondBrain.Server.Auth;`.
  - `InitializeAsync` uses `FindInitialAdminCredentialIdAsync` and keeps the same `CliPreconditionException` when the result is null.
  - `ResetPasswordAsync` wraps the store call in `try { … } catch (AccountNotInitializedException) { throw new CliPreconditionException("The account is not initialized."); }`, without an inner exception.
  - Everything else is unchanged: the pre-check message `"…run brain init first."`, calibration, key ring, JSON payloads and `JsonSerializer.Serialize(parameters)` as the parameters string.
- `src/SecondBrain.Cli/CliServices.cs`: remove `using SecondBrain.Server.Auth;`.
- `src/SecondBrain.Cli/SecondBrain.Cli.csproj`:
  ```xml
  <!-- Layout only: copies the runnable daemon (apphost, dll, deps, runtimeconfig) beside brain so
       `brain serve` finds it under systemd and in the CI artifact. Never write `extern alias ServerLayout`. -->
  <ProjectReference Include="../SecondBrain.Server/SecondBrain.Server.csproj" Aliases="ServerLayout" />
  ```
- `.github/workflows/ci.yml`, publish job: add a step after "Publish self-contained binary". Before committing, run the self-contained CLI publish on the baseline commit and confirm these four files exist.
  ```yaml
      - name: Check CLI artifact carries the daemon
        if: matrix.project == 'Cli'
        run: |
          for f in SecondBrain.Server SecondBrain.Server.dll SecondBrain.Server.deps.json SecondBrain.Server.runtimeconfig.json; do
            test -f "artifacts/Cli/$f" || { echo "missing $f"; exit 1; }
          done
  ```

**Tests:** new `tests/SecondBrain.Storage.Tests/SqliteAccountRecoveryStoreTests.cs`:
- The lookup prefers `meta.initial_admin_credential_id`, falls back to the oldest admin key, and returns null when neither exists.
- Reset updates the account, upserts `meta.password_parameters`, increments `meta.account_epoch` and returns the new epoch.
- Reset with no account throws `AccountNotInitializedException` and leaves `meta` unchanged.

`CliConvergenceTests` and `CliTests` already cover CLI exit codes and messages end to end.

**DI:** daemon unchanged.

**Rules switched on:**
- AR-09 (brain's `SecondBrain.*` assembly references ⊆ {Core, Infrastructure, Storage}; the CLI's Server `ProjectReference` has `Aliases="ServerLayout"`; no `extern alias` anywhere in `src/SecondBrain.Cli`)
- AR-03b (exact `ProjectReference` sets per csproj, as in §1.2)
- AR-12a (brain has no Dapper or SQLite assembly reference)

**Docs:**
- `src/SecondBrain.Cli/README.md` line 27: the local adapter uses Core, Infrastructure and Storage, and Server is referenced only for layout.
- `README.md`: the sentence "CLI references Storage and Server to reuse credential generation…".

**Gate B** runs after this step.

### S9. Sources

**Create:** `src/SecondBrain.Server/Sources/CreateSourceRequest.cs`, split out of `SourceModels.cs`. Its namespace (`SecondBrain.Server.Sources`) and attributes are unchanged.

**Move**
- `Server/Sources/SourceModels.cs` (the rest: `FolderSourceConfiguration`, `SourceRecord`, `ISourceRepository`, `SourcePathValidation`, `ISourcePathValidator`) → `src/SecondBrain.Core/Sources/SourceModels.cs`, namespace `SecondBrain.Core.Sources`. `JsonPropertyName` attributes and type names are unchanged, because they define `config_json` and the OpenAPI IDs.
- `Server/Sources/SqliteSourceRepository.cs` → `src/SecondBrain.Storage/Sources/SqliteSourceRepository.cs`, namespace `SecondBrain.Storage.Sources`, SQL verbatim.
- `Server/Sources/SourcePathValidator.cs` → `src/SecondBrain.Infrastructure/FileSystem/SourcePathValidator.cs`, namespace `SecondBrain.Infrastructure.FileSystem`. `ProtectedRoots` and the managed resolver are verbatim and are **not** merged with `UnixPath`.

**Edit:** usings in `SourceEndpoints.cs` and `SourcesServices.cs`.

**Tests**
- Update usings in `Server.Tests/SourcesTests.cs`, which constructs `SourcePathValidator` directly.
- New `tests/SecondBrain.Storage.Tests/SqliteSourceRepositoryTests.cs`: an add/list round trip that asserts the stored `config_json` keys are exactly `path, include, exclude, recursive, mode, enrich, default_type, type_map, watch, poll_interval_s`.

**DI:** unchanged. `openapi.json` is also unchanged, which proves the schema IDs survived the move.

**Rules switched on:**
- AR-12 (only Storage references `Dapper`, `Microsoft.Data.Sqlite`, `SQLitePCLRaw.*`)
- AR-15 (no SQL literals outside `src/SecondBrain.Storage`)

**Docs:** `LaneD-Integration.md`: `ISourceRepository` is in `SecondBrain.Core.Sources`, implemented in `SecondBrain.Storage.Sources`.

### S10. Limits

**Move**
- `Server/Limits/AdmissionPolicy.cs` → `src/SecondBrain.Core/Limits/AdmissionPolicy.cs`, namespace `SecondBrain.Core.Limits`. This file holds `AdmissionResource`, `AdmissionPolicy` (still usable as endpoint metadata), `AdmissionSnapshot`, `AdmissionDecision`, `IAdmissionController` and `IDiskCapacity`.
- `AdmissionController` and its private types (from `Server/Limits/AdmissionController.cs`) → `src/SecondBrain.Core/Limits/AdmissionController.cs`. The constructor is unchanged; `LimitsTests` constructs it directly.
- `DiskCapacity` → `src/SecondBrain.Infrastructure/FileSystem/DiskCapacity.cs`.

**Edit:** usings in `AdmissionMiddleware.cs`, `LimitsServices.cs` and `Auth/SessionCircuitSecurity.cs`.

**Tests:** usings in `LimitsTests.cs` and `SessionCircuitTests.cs`.

**DI:** unchanged. **Rules:** AR-06 already bans `DriveInfo` in Core.

**Docs:** M1 plan §3, `IAdmissionController` row: it is now `SecondBrain.Core.Limits` and directly usable by background dispatchers.

### S11. `IProviderEgressPolicy`: the adapter depends on a port, not the concrete policy

**Create:** `src/SecondBrain.Core/Privacy/IProviderEgressPolicy.cs`:
```csharp
public interface IProviderEgressPolicy
{
    bool IsLocalEndpoint(Uri? endpoint);
    void ValidateRequest(ModelRole role, IProviderBinding binding, Uri requestUri, bool discovery = false);
    void VerifyResolvedAddresses(Uri endpoint, IReadOnlyList<IPAddress> addresses);
    void VerifyConnectionAddresses(string host, int port, IReadOnlyList<IPAddress> addresses);
    IDisposable RegisterConfigurationValidator(Action<SecondBrainOptions> validator);
}
```

**Edit**
- `PrivacyPolicy` gains `IProviderEgressPolicy` in its implemented-interface list. Its members already match.
- In the adapter, change `PrivacyPolicy` to `IProviderEgressPolicy` (field and constructor parameter types only) in:
  - `PolicyHttpClientFactory`, including the nested `RequestPolicyHandler`;
  - `OpenAICompatibleBinding`;
  - `ProviderRegistry`. Its static `ValidateConfiguration` is unchanged.
- `LaneB.AddPrivacy`: `services.TryAddSingleton<IProviderEgressPolicy>(provider => provider.GetRequiredService<PrivacyPolicy>());`. `ProviderStartup` keeps the concrete `PrivacyPolicy` for `ValidateConfiguration`.

**Tests:** no edits. `ProviderTestContext` and `TransportTests.cs:192` pass a `PrivacyPolicy`, which converts implicitly.

**DI:** add one line to `composition.txt`.

**Rules switched on:** AR-13.

### S12. Move the store handles to Storage

**Move:** `src/SecondBrain.Core/Storage/{IStoreHandle.cs (+IReadConnectionLease), IStateStore.cs, IIndexStore.cs, IStoreSnapshot.cs}` → `src/SecondBrain.Storage/`, namespace `SecondBrain.Storage`. `Core.Storage` keeps `IStorageStatus`, `IMigrationRunner` (+records) and `IStoreInitializer` (+records).

**Edit (production)**
- Storage sources compile unchanged, because their namespaces are `SecondBrain.Storage` or nested under it.
- Add `using SecondBrain.Storage;` to `Server/Http/Readiness.cs` and `Server/Composition/LaneD.Http.cs`.
- The CLI's `LocalInitializationCommands.cs` already imports `SecondBrain.Storage`.

**Tests:** add `using SecondBrain.Storage;` to `Server.Tests/Support/TestStateStore.cs`, `RealStoreAccessor.cs` and `LaneDWebFactory.cs`, and to `Deploy.Tests/CliConvergenceTests.cs` if the compiler asks. `Storage.Tests` needs no edits, because of the nested namespace.

**DI:** unchanged. Never wrap or decorate `IStateStore`; S1's identity test guards this.

**Rules switched on:** AR-06d (Core has no type reference in `System.Data.*`).

**Docs:** M1 plan §3, rows for `IStateStore`, `IIndexStore` and queued writers: these handles are now `SecondBrain.Storage` infrastructure. M1 code in Core declares repository and projection ports in `SecondBrain.Core.*`, and Storage implements them. The statement "Core continues to have no project references" stays true.

### S13. Trim Core's packages

**Edit:** set `src/SecondBrain.Core/SecondBrain.Core.csproj` to exactly:
```xml
<ItemGroup>
  <PackageReference Include="Microsoft.Extensions.AI.Abstractions" />
  <PackageReference Include="Microsoft.Extensions.Options" />
  <PackageReference Include="YamlDotNet" />
  <PackageReference Include="JsonSchema.Net" />
  <PackageReference Include="Ulid" />
</ItemGroup>
```
This removes `Microsoft.Extensions.AI` and `Microsoft.Extensions.Options.ConfigurationExtensions`. Leave the now-unused `PackageVersion` entries in place; deleting package versions is out of scope.

**Lock files:** most of them change.

**Rules switched on:**
- AR-02 (Core has no `ProjectReference` and exactly these five `PackageReference`s)
- AR-04 (the full Core assembly-reference deny list)

**Extra verification:**
- `grep -rn "ChatClientBuilder\|EmbeddingGeneratorBuilder\|UseFunctionInvocation\|TensorPrimitives" src tests` returns nothing.
- The publish diff for Server and CLI shows only the losses expected in §1.5.

**Gate B** runs after this step.

### S14. Move the infrastructure tests into `tests/SecondBrain.Infrastructure.Tests`

**Create:** `tests/SecondBrain.Infrastructure.Tests/SecondBrain.Infrastructure.Tests.csproj`, with a `ProjectReference` to `src/SecondBrain.Infrastructure` and the standard test packages. Add it to `SecondBrain.slnx` and commit its lock file.

**Move** (bodies unchanged; namespaces follow §2.9)
- `tests/SecondBrain.Core.Tests/Configuration/ConfigurationTests.cs` → `tests/SecondBrain.Infrastructure.Tests/Configuration/ConfigurationTests.cs` (`SecondBrain.Infrastructure.Tests.Configuration`).
- `tests/SecondBrain.Core.Tests/Security/KeyRingAndRootTests.cs` → `tests/SecondBrain.Infrastructure.Tests/Security/KeyRingAndRootTests.cs` (`SecondBrain.Infrastructure.Tests.Security`).

**Verify:** the test-name inventory (§4.4) maps one-to-one through Appendix A.

### S15. Move the adapter tests into `tests/SecondBrain.Providers.OpenAICompatible.Tests`

**Create:** `tests/SecondBrain.Providers.OpenAICompatible.Tests/…csproj`:
- `ProjectReference`s to `src/SecondBrain.Providers.OpenAICompatible`, `src/SecondBrain.Infrastructure` and `tests/SecondBrain.MockProvider`.
- Packages: Test SDK, `Microsoft.AspNetCore.Mvc.Testing`, xunit, runner, `Microsoft.Extensions.TimeProvider.Testing`.
- Add it to the slnx and commit its lock file.

**Move:** `tests/SecondBrain.Core.Tests/Providers/{AdapterTests.cs, TransportTests.cs, ProviderTestContext.cs}` → the new project root, namespace `SecondBrain.Providers.OpenAICompatible.Tests`. The classes `Ready`, `AdapterTests` and `Transport` keep their names.

**Edit:** `tests/SecondBrain.Core.Tests/SecondBrain.Core.Tests.csproj`:
- Remove the `ProjectReference`s to Providers.OpenAICompatible and MockProvider.
- Remove `Microsoft.AspNetCore.Mvc.Testing` if the build confirms it is unused.

**Rules switched on:** AR-19 (`Core.Tests` `ProjectReference`s ⊆ {Core, Infrastructure}).

### S16. Documentation sweep and final verification

**Edit**
- `README.md`, layout table (lines 34–40) and the CLI sentence:
  - Add an Infrastructure row.
  - Core becomes "domain model, application use cases, policies and ports; no project references; no I/O".
  - Mention `tests/SecondBrain.Architecture.Tests`.
- `docs/build/M0-foundations.md` §3: add one note under the layout block pointing to this plan. The historical plan is not rewritten.
- `docs/build/M1-ingest-and-search.md` §3, if S10 and S12 have not already done it:
  - The key-ring row names the Core-facing `IHmacKeyRing`, with `IKeyRing`, `FileKeyRing` and `DataRootLock` in `SecondBrain.Infrastructure.Security`.
  - Store handles are in `SecondBrain.Storage`; `IAdmissionController` is in `SecondBrain.Core.Limits`.
  - Add the port rule: new M1 persistence and file-system needs are Core ports with Storage or Infrastructure adapters.
  - Add a deferred-seam note: an `IPayloadProtection` port for cursor signing (M1 item 8) and an `IExtractorClient` port (M1 item 3) are added with their first consumer.
- `docs/build/M0-convergence-report.md`: **append** an "Addendum: clean-architecture refactor" with the Appendix A FQN map and the new per-project totals. Leave the original evidence untouched.
- `deploy/LANE-C-HANDOFF.md`, `src/SecondBrain.Server/Http/LaneD-Integration.md`, `src/SecondBrain.Server/Auth/Calibration.md`, `src/SecondBrain.Cli/README.md`, and `src/SecondBrain.Providers.OpenAICompatible/INTEGRATION.md` (whose lock-file paragraph should mention Infrastructure): fix any remaining stale names. This command must print nothing:
  `grep -rnE "SecondBrain\.Server\.Auth\.(IAuthRepository|IAdminCredentialFactory|PasswordHasher)|SecondBrain\.Core\.(Extraction|Security\.(FileKeyRing|DataRootLock|IKeyRing)|Storage\.IStateStore)" README.md docs deploy src tests`

**Run** Gate B and the final acceptance checklist (§4.5).

---

## 4. Verification checklist

### 4.1 Gate A (every step, in this order)

```sh
dotnet restore SecondBrain.slnx --force-evaluate            # only if a reference or package changed
git add '**/packages.lock.json'                             # stage the regenerated locks
dotnet restore SecondBrain.slnx --locked-mode --runtime linux-x64 -p:RuntimeIdentifier=linux-x64 -p:SelfContained=false   # Docker form
for p in Server Cli Extractor; do
  dotnet restore src/SecondBrain.$p/SecondBrain.$p.csproj --locked-mode --runtime linux-x64 -p:RuntimeIdentifier=linux-x64 -p:SelfContained=true  # CI publish form
done
dotnet restore SecondBrain.slnx --locked-mode
git diff --exit-code -- '**/packages.lock.json'             # no restore form changed a staged lock file
dotnet build SecondBrain.slnx --configuration Release --no-restore -warnaserror -p:ContinuousIntegrationBuild=true
dotnet test SecondBrain.slnx --configuration Release --no-build --no-restore --filter 'Category!=Qualification'
dotnet test SecondBrain.slnx --configuration Release --no-build --no-restore   # all, incl. the unset-URL qualification no-op
```

Gate A passes when:
- Restore and build succeed with 0 warnings.
- Every pre-existing test passes (516 in total, 515 under the CI filter).
- Every new test passes.
- `composition.txt` changes only as the step's **DI** section says, and `openapi.json` and `yaml-aliases.txt` are unchanged.

### 4.2 Gate B (after S8, S13 and S16)

```sh
dotnet publish src/SecondBrain.Cli/SecondBrain.Cli.csproj -c Release -r linux-x64 --self-contained true \
  -warnaserror -p:ContinuousIntegrationBuild=true -o /tmp/sb-cli
ls /tmp/sb-cli/SecondBrain.Server /tmp/sb-cli/SecondBrain.Server.dll \
   /tmp/sb-cli/SecondBrain.Server.deps.json /tmp/sb-cli/SecondBrain.Server.runtimeconfig.json   # systemd layout
for p in Server Cli Extractor; do
  dotnet publish src/SecondBrain.$p/SecondBrain.$p.csproj -c Release -r linux-x64 --self-contained true -o /tmp/sb-$p
done   # then diff the file lists against the same commands on 37d47fb; only §1.5 item 3 differences are allowed
docker build --platform linux/amd64 --file deploy/docker/Dockerfile --tag secondbrain:ci .
SECONDBRAIN_PRIVATE_ADDRESS=127.0.0.1 deploy/tests/compose-smoke.sh   # G1: init --provision && serve as non-root, /ready green against the mock
```

On a Linux host, also run `/tmp/sb-cli/brain serve --config <test config>` with `SECONDBRAIN_SERVER_PATH` **unset**. It must not report "The daemon binary is missing".

### 4.3 Architecture rules (`tests/SecondBrain.Architecture.Tests`)

| ID | Rule | Asserted by | On from |
| --- | --- | --- | --- |
| AR-01 | `SecondBrain.Core` has no `ProjectReference` and no `SecondBrain.*` assembly reference. | csproj, metadata | S0 |
| AR-02 | `SecondBrain.Core.csproj` `PackageReference`s are exactly {`Microsoft.Extensions.AI.Abstractions`, `Microsoft.Extensions.Options`, `YamlDotNet`, `JsonSchema.Net`, `Ulid`}. | csproj | S13 |
| AR-03 | `SecondBrain.*` assembly references: Infrastructure ⊆ {Core}; Storage ⊆ {Core}; Providers.OpenAICompatible ⊆ {Core}; Server ⊆ {Core, Infrastructure, Storage, Providers.OpenAICompatible}. **AR-03b:** every src csproj's `ProjectReference` set equals §1.2 exactly. | metadata; csproj | S0 (S3 adds Infrastructure); S8 |
| AR-04 | Core has no assembly reference to `Microsoft.AspNetCore.*`, `Microsoft.Extensions.AI` (exact name), `Microsoft.Extensions.Configuration*`, `…DependencyInjection*`, `…Hosting*`, `…Http`, `Microsoft.Data.*`, `Dapper`, `SQLitePCLRaw.*`, `Isopoh.*`, `OpenAI` or `Microsoft.Extensions.AI.OpenAI`. | metadata | S5 (`AspNetCore`), S13 (full list) |
| AR-05 | Core's type references into YamlDotNet are exactly `{YamlDotNet.Serialization.YamlMemberAttribute}`. `using Json.Schema` appears only under `src/SecondBrain.Core/Domain`. | metadata; source | S4 |
| AR-06 | Core has no type reference to:<br>• `System.IO.{File, Directory, FileInfo, DirectoryInfo, FileSystemInfo, FileStream, FileSystemWatcher, DriveInfo}`<br>• `System.Net.Dns`<br>• `System.Net.Sockets.{Socket, TcpClient, UdpClient, NetworkStream, UnixDomainSocketEndPoint}`<br>• `System.Net.Http.{HttpClient, HttpMessageHandler, SocketsHttpHandler}`<br>• `System.Runtime.InteropServices.{Marshal, NativeLibrary, PosixSignalRegistration, SafeHandle}` or `Microsoft.Win32.SafeHandles.*`<br>• `System.Environment` or `System.Diagnostics.Process`<br>• anything in `System.Data.*` | metadata | S3 (network), S4 (watcher and signal), S5 (I/O, interop, environment), S12 (`System.Data`) |
| AR-07 | Methods with `PinvokeImpl` exist only in Infrastructure, Storage, Extractor and brain. | metadata | S5 |
| AR-08 | Extractor and MockProvider have no `ProjectReference` and no `SecondBrain.*` assembly reference. | csproj, metadata | S2 |
| AR-09 | brain's `SecondBrain.*` assembly references ⊆ {Core, Infrastructure, Storage}, so there is no Server or Providers reference. `Cli.csproj`'s Server `ProjectReference` has `Aliases="ServerLayout"`. There is no `extern alias` in `src/SecondBrain.Cli`. | metadata, csproj, source | S8 |
| AR-10 | Only `SecondBrain.Providers.*` assemblies reference the `OpenAI` and `Microsoft.Extensions.AI.OpenAI` assemblies, and only `src/SecondBrain.Providers.*` csproj files reference those packages. This is provider neutrality, spec §2.8. | metadata, csproj | S0 |
| AR-11 | Only Infrastructure references `Isopoh.*`. | metadata | S7 |
| AR-12 | Only Storage references `Dapper`, `Microsoft.Data.Sqlite` and `SQLitePCLRaw.*`. (**AR-12a:** brain only, from S8.) | metadata | S9 |
| AR-13 | Providers.OpenAICompatible has no type reference to `SecondBrain.Core.Privacy.PrivacyPolicy`. | metadata | S11 |
| AR-14 | Files under `src/SecondBrain.Core/{Domain,Authorization}` have no `using` of `SecondBrain.*` outside those two namespaces, no `using Microsoft.*`, and no fully qualified `SecondBrain.Core.<other>` names. | source | S0 |
| AR-15 | No SQL appears in `src/**/*.cs` outside `src/SecondBrain.Storage`. The check is case-sensitive and line-based, so it also covers raw `"""` literals: no line may match `\b(SELECT\b.*\bFROM\b\|INSERT INTO\b\|UPDATE [a-z_]+ SET\b\|DELETE FROM\b\|CREATE TABLE\b)`. At baseline the regex matches only `LocalInitializationCommands`, `AuthRepository`, `PasswordPolicy` and `SqliteSourceRepository`, and all four have moved by S9. | source | S9 |
| AR-16 | Infrastructure has no assembly reference to `Microsoft.Extensions.DependencyInjection*` or `Microsoft.Extensions.Hosting*`, so composition stays in the hosts. | metadata | S3 |
| AR-17 | `brain serve` stderr contract:<br>• A type named exactly `DataRootLockedException` exists in some src assembly.<br>• Each of these literals appears in a `src/**/*.cs` file outside `src/SecondBrain.Cli`: `data root is already locked` (case-insensitive), `Root mode is incorrect`, `Root ownership is incorrect`, `Key ring must be daemon-owned`, `must run as a non-root`. | reflection, source | S0 |
| AR-18 | Assembly names are `SecondBrain.Server`, `brain`, `SecondBrain.Extractor` and `SecondBrain.Storage`. | existing `Deploy.Tests` and `Storage.Tests` `SmokeTests` | baseline |
| AR-19 | `tests/SecondBrain.Core.Tests` `ProjectReference`s ⊆ {Core, Infrastructure}. | csproj | S15 |

### 4.4 Behaviour guards

- **Every existing test** passes. The tests that are characterizations of this refactor are:
  - `Deploy.Tests` (CLI exit codes, messages, the JSON envelope, convergence, deploy artifacts).
  - `Server.Tests` (the HTTP contract, auth, sessions, limits, sources, readiness, and the Data Protection purpose separation in `HttpTests`).
  - `Storage.Tests` (`CompositionTests` idempotency and `ValidateOnBuild`).
  - `Core.Tests` `SmokeTests` (no default provider).
- **The S1 guards:**
  - `composition.txt`, which may change only by the S7 and S11 lines.
  - `openapi.json` and `yaml-aliases.txt`, which never change.
  - Base64Url equivalence, `KeyRingPurposes` values and store-handle identity.
- **The S6, S8 and S9 Storage characterization tests** for the relocated SQL.
- **Test inventory.** Run `dotnet test SecondBrain.slnx --no-build --list-tests > /tmp/tests-<step>.txt` before S0 and after each step. Apply the Appendix A namespace map, then confirm that every baseline name is still present.
- **No deploy change.** `git diff 37d47fb..HEAD --stat -- deploy/ ':!deploy/LANE-C-HANDOFF.md'` is empty. In `.github/`, only `ci.yml` changes, by the single S8 step.

### 4.5 Final acceptance

1. Gate A and Gate B are green on the final commit, and AR-01 through AR-19 are all on.
2. Every baseline test passes: 516 in total and 515 under the CI filter, with new names only through Appendix A.
3. Spec §19 gate G1 (compose smoke) and G2 (`/ready` green against the mock) pass. CI jobs `build-test`, `publish` (including the new CLI layout check), `docker` and `compose-smoke` pass on the pushed branch.
4. `docs/build/M0-convergence-report.md` has the addendum. README, the M1 plan §3 and the in-tree integration notes name the new locations.
5. `spec.md` is unchanged. The §1.4 deviations are recorded with sign-off in the PR description.

## 5. Non-goals

1. **No renames** of spec §4 projects, assemblies, executables, publish paths or deploy artifacts. No new processes or hosts (§2.7).
2. **No separate Domain assembly.** Domain stays a namespace layer in Core, enforced by AR-14. Splitting it out later is a mechanical move.
3. **YamlDotNet and JsonSchema.Net stay in Core**, following spec §18:
   - No YAML attribute-override table.
   - No `IPropertySchemaCompiler` port.
   - The `TypeRegistry` and `ContentTypeDefinition` constructors are unchanged.
   - Revisit only with a spec §18 amendment.
4. **No adapter-agnostic provider runtime.** That means no `SecondBrain.Providers` project, `IProviderAdapter` or `IModelCatalog`. `ProviderRegistry` and `PolicyHttpClientFactory` stay in the OpenAI-compatible adapter, and only the `openai_compatible` and `openai-compatible` kinds are accepted. Do this when the second §13.4 adapter lands, together with the shared adapter contract suite. `mock-chat`, `mock-embed`, `gpt-4o` and `text-embedding-3-small` stay in `ModelCatalog`.
5. **No CLI purity work.** `brain` keeps its Storage, Infrastructure and layout-only Server references, `FrameworkReference` and package-pruning setting. No replacement publish-layout target.
6. **No merging of behaviour-sensitive duplicates.** These are moved verbatim and kept separate:
   - private-address checks (`ConfigurationValidator` allows IPv6 link-local, `ConfiguredKestrelOptions` rejects it, and `DoctorService` has its own);
   - `in_process` kind casing;
   - directory-fsync open flags (`UnixSecurity`, `MigrationFiles`, `ManagedMutationFiles`);
   - `realpath` versus the `SourcePathValidator` resolver;
   - the extractor wire protocol (`ExtractorPing` versus `ExtractorProtocol`);
   - scope vocabularies;
   - problem-URI literals in the adapter;
   - `DoctorService`'s parsing of `hmac.json`;
   - key-ring composition in `LaneC` versus `CliServices`, whose umask timing differs;
   - the initial-admin lookup SQL, which now exists in both `StoreInitializer` and `SqliteAccountRecoveryStore`.
7. **No contract changes.** This covers SQL, meta keys, persisted JSON, Data Protection purposes, YAML keys, OpenAPI schema IDs, HTTP routes, exception messages, CLI JSON envelope and exit codes.
8. **No composition reshaping.** `Lane*` class names, `Add*` method names, `AddLaneD*` helpers and the `IStartupFilter` pipeline all stay. `Program.cs` is unchanged.
9. **No entity/DTO separation.** `CredentialRecord` and `AccountRecord` stay Dapper-shaped rows with string timestamps. `SourceRecord` and `FolderSourceConfiguration` remain the shared persisted and HTTP shape. `DocumentPublication` stays stringly typed.
10. **No package version changes**, except adding `Microsoft.Extensions.Options` 10.0.12, the already-resolved version. Unused `PackageVersion` entries are not removed.
11. **Unused `Microsoft.Extensions.Hosting.Systemd` stays in Server.** `StorageStartupService` keeps its Hosting dependency, and `PublicationCoordinator`'s static per-store locks are untouched.
12. **No M1 functionality and no speculative seams.** `IPayloadProtection`, `IExtractorClient` and a shared `Extraction.Protocol` are added with their first M1 consumer.
13. **No `InternalsVisibleTo`.** The only API widenings are `AuthTime` and `CredentialFactory.CreateSession`.

## 6. Accepted residue and risks to watch

**Residue the Application layer keeps on purpose:**
- `PrivacyPolicy` still resolves DNS synchronously, through the port, in its constructor and on reload.
- `ProviderRequestException` still derives from `HttpRequestException`.
- `TimeOptions` defaults to `TimeZoneInfo.Local.Id`.
- Option defaults still contain deployment paths.
- `NoOpCrashPoints` stays in Core.

**Risks**
- **CLI layout regressions are invisible to tests.** Deploy tests run `brain.dll` from an output folder that already contains Server through their own reference. The S8 CI step and Gate B are the only guards, so do not remove them.
- **Forgetting the `IHmacKeyRing` forwarder in `AddAuth`** fails loudly: `CliConvergenceTests` cannot resolve `CredentialFactory`. Forgetting `CliConvergenceTests.cs:309` fails loudly too, through Razor and static-asset errors.
- **A missed lock file** fails CI (`--locked-mode` plus `git diff`) and the Docker restore. Always commit the lock files with the step.
- **New `public` members in libraries can trip analyzers.** Fix them in place without changing behaviour.

## Appendix A. Test relocation map (S14, S15)

| Before | After |
| --- | --- |
| `SecondBrain.Core.Tests.Configuration.ConfigurationTests` | `SecondBrain.Infrastructure.Tests.Configuration.ConfigurationTests` |
| `SecondBrain.Core.Tests.Security.KeyRingAndRootTests` | `SecondBrain.Infrastructure.Tests.Security.KeyRingAndRootTests` |
| `SecondBrain.Core.Tests.Providers.Ready` | `SecondBrain.Providers.OpenAICompatible.Tests.Ready` |
| `SecondBrain.Core.Tests.Providers.AdapterTests` | `SecondBrain.Providers.OpenAICompatible.Tests.AdapterTests` |
| `SecondBrain.Core.Tests.Providers.Transport` (G3, G4 and G5 evidence) | `SecondBrain.Providers.OpenAICompatible.Tests.Transport` |

These test classes do not move:
- `SecondBrain.Core.Tests.Privacy.*` (G3 and G5 evidence)
- `SecondBrain.Core.Tests.Domain.*`
- `SecondBrain.Server.Tests.*`, including `Qualification` in `ReadyTests.cs`, which `qualification.yml` and the README rely on
- `SecondBrain.Storage.Tests.*`
- `SecondBrain.Deploy.Tests.*`

## Appendix B. Suggested PR grouping (one commit per step)

| PR | Steps | Theme |
| --- | --- | --- |
| A | S0–S2 | Guard rails; sandbox and mock decoupling |
| B | S3–S5 | Platform code out of Core (`SecondBrain.Infrastructure`) |
| C | S6–S8 | Auth into Core and Storage; CLI decoupled (Gate B) |
| D | S9–S13 | Sources, limits, egress port, store handles, Core packages (Gate B) |
| E | S14–S16 | Tests follow code; documentation; final acceptance (Gate B) |
