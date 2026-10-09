# M0 convergence report

Recorded 2026-10-08 on branch `converge`, based on `cfecb8f`. All implementation work was confined to `.worktrees/converge`; no push, checkout, merge, rebase, or root-checkout changes were performed.

The integrated solution and local gates pass. **M0 remains open:** the required live vLLM server is not set up, so `Qualification.VllmReady` has not exercised a live binding. The target-host operational spikes and the next GitHub CI run also need recorded evidence. Passing the unset-URL qualification no-op does not satisfy the live requirement.

## Deliverables

| # | Status | Result |
| --- | --- | --- |
| 1. Server tests | Complete | All 133 pass. `LaneDWebFactory` loads valid temporary YAML through the real loader, starts the loopback Kestrel mock and a real extractor stub, uses real temporary stores, and holds the root lock. Smoke uses the same fixture. Production required-role validation remains enforced. |
| 2. Readiness | Mock complete; live pending | Real `migrations`, `stores`, `lock`, `canary`, `extractor`, and `provider:<role>` contributions are registered. Healthy provider checks expire after five minutes and configuration changes invalidate cached observations. `/ready` is 200 against the mock in tests and Docker. Optional unconfigured rerank is explicitly non-blocking. Live qualification is implemented with operator-supplied model IDs/dimensions/limits. |
| 3. CLI bindings | Complete for M0 | Initialization, reset, keys, login, providers, sessions, doctor and durable account-epoch revocation are bound. Calibrated Argon2 policy persists atomically with account creation/reset. Initial/new keys print once; login and metadata responses contain no retained secret. All commands preserve JSON and exit statuses 0–4. |
| 4. Doctor and shell | Complete locally | Doctor reports roots/modes/owners, listener interface and actual exposure, certificates, stores/migrations, lock, capabilities/reachability, trusted services, canary, Access refresh/cache, key-ring integrity and extractor ping. Docker doctor exits 0 with every check passing. Real browser form login returns 303; signed-in shell and authenticated `/auth/me` work with real YAML. |
| 5. Problem constants | Complete | Added all eleven requested common URIs and replaced lane D's duplicate/string definitions. Privacy/provider failures use common typed problem responses. |
| 6. Gate audit and CI | Local deterministic gates complete; qualification pending | Actual mappings are below. Hardened Compose G1 passed locally, including the complete CLI flow. CI runs all deterministic projects, locked restore/build, self-contained publishes, image build and Compose smoke; live qualification has its own explicitly configured workflow. No remote CI run was initiated because this task forbids pushing. |
| 7. Documentation | Complete | README describes converged layout, exact disposable Docker and direct-host commands, JSON/exit codes and qualification. The parallel-lane freeze is replaced with a contribution note. M1 draft maps scope, planned acceptance tests, work items and M0 hooks; planned tests are clearly marked as not implemented. |

Session administration follows lane D's browser-session authority: the CLI authenticates with the account password, obtains a temporary cookie and fresh antiforgery tokens, performs the session operation, then closes its temporary session. Logout-all advances the durable epoch and invalidates keys too. There is no API-key-only session administration or persistent paired CLI session; `/auth/pair` and persistent pairing belong to M2. No unsupported endpoint was invented.

## G1–G12 evidence

Names below are actual discoverable test methods, not the plan's shorthand. Except where fully qualified, `Server:` means `SecondBrain.Server.Tests`, `Privacy:` means `SecondBrain.Core.Tests.Privacy`, `Transport:` means `SecondBrain.Core.Tests.Providers.Transport`, and `Storage:` means `SecondBrain.Storage.Tests`. Theory cases count individually in project totals.

| Gate | Actual test or executable check | Local result |
| --- | --- | --- |
| G1 | `deploy/tests/compose-smoke.sh`; CI job `compose-smoke`, step `Deploy.ComposeSmoke — initialize and serve as non-root` | **PASS** locally, exit 0; next remote CI run pending |
| G2 | `Server: Ready.MockProviderGreen`; `Server: Qualification.VllmReady` | **PASS mock; LIVE PENDING**. Qualification returns without probing when URL is unset. |
| G3 | `Privacy: PrivacyPolicyTests.RefusesHostedAtStartup`, `RefusesHostedAtReload`, `RefusesHostedAtRequest`; `Transport: RequestPolicyDetectsBindingChangeDuringDnsAwaitBeforeSend` | **PASS** |
| G4 | `Transport: RedirectIsFailure`, `RedirectNeverContactsEvenAnAllowedSecondListener` | **PASS** |
| G5 | `Privacy: EgressCanaryTests.CanaryBlocksProviderCallsAndNextFailureClearsState`; `Transport: SuccessfulCanaryBlocksActualProviderTransportAndLaterFailureClearsIt` | **PASS** |
| G6 | `Server: AuthTests.Authz_ScopeMatrix` | **PASS**; table covers every registered M0 endpoint policy, including new admin diagnostics |
| G7 | `Server: SourcesTests.RejectsOutsideRoots`, `DeniesDataRoot`, `SymlinkOutsideRootsIsDenied`, `ProtectedDirectoriesAreDeniedEvenWhenAllowed` | **PASS**; protected-root theory exercises eight paths |
| G8 | `Server: LimitsTests.PerCredentialRate`, `GlobalCapacity503` | **PASS**; rejection leaves no queued job or reservation |
| G9 | `Storage: Migrations.CrashBetweenStoresRecovers` | **PASS** |
| G10 | `Storage: JournalTests.AppliedIsFinalizedOnce`, `PreparedTempIsDiscardedAndSecondRestartChangesNothing` | **PASS**; applied theory exercises two crash points |
| G11 | `Server: AuthTests.Sessions_Expiry`, `Sessions_IdleSlidingDoesNotExtendAbsoluteExpiry`, `Sessions_RotationAfterLoginAndStepUp`, `Sessions_LogoutAndListRevoke`, `Sessions_LogoutAllEpochRejectsEveryCredential`, `Sessions_StepUpWindowAndAntiforgery`, `Sessions_GenerationAndEpochRejected`, `Sessions_SecureCookieCannotBeReplayedOverHttp`, `Sessions_BlazorCookiePath` | **PASS** |
| G12 | `Server: AccessTests.NoAssertionNoLogin`, `JwksRefresh` | **PASS**; local JWKS assertion validation, unknown-kid refresh and last-good-cache failure behavior |

The targeted gate runs passed Core **8/8**, Storage **4/4**, Server **29/29**; the Server count includes the unset-URL qualification no-op. Full-project runs additionally cover readiness cache expiry, reload races, invalid reload retention, real Data Protection composition, fixture teardown after faults and bound CLI administration.

## Verification and totals

Executed from `/Users/justinleahy/.t3/projects/secondbrain/.worktrees/converge`:

```sh
dotnet restore --locked-mode --disable-build-servers
dotnet build -warnaserror --no-restore --disable-build-servers
dotnet test --no-build --no-restore --disable-build-servers -v minimal \
  --logger trx --results-directory /tmp/secondbrain-converge-verification
SECONDBRAIN_SMOKE_KEEP=1 deploy/tests/compose-smoke.sh
```

The build reports **0 warnings, 0 errors**. All test projects pass:

| Test project | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| SecondBrain.Core.Tests | 207 | 0 | 0 |
| SecondBrain.Storage.Tests | 67 | 0 | 0 |
| SecondBrain.Deploy.Tests | 109 | 0 | 0 |
| SecondBrain.Server.Tests | 133 | 0 | 0 |
| **Total** | **516** | **0** | **0** |

One of the 516 is the explicit unset-URL qualification no-op; CI excludes it and runs 515 deterministic cases. `SecondBrain.MockProvider` is a fixture executable, not another xUnit project. Bound CLI tests live in Deploy.Tests; its targeted CLI subset passes 53/53. An additional fresh complete Deploy run after Compose changes passed 109/109.

Locked `linux-x64` CLI restore and self-contained Release publish also pass:

```sh
dotnet restore src/SecondBrain.Cli/SecondBrain.Cli.csproj --locked-mode \
  --runtime linux-x64 -p:RuntimeIdentifier=linux-x64 -p:SelfContained=true --disable-build-servers
dotnet publish src/SecondBrain.Cli/SecondBrain.Cli.csproj --configuration Release \
  --runtime linux-x64 --self-contained true --no-restore -warnaserror \
  -p:ContinuousIntegrationBuild=true --disable-build-servers \
  --output /tmp/secondbrain-converge-publish-cli
```

The Docker build publishes Server, CLI, Extractor and the separate mock fixture target on Linux amd64. Bash syntax, both workflow YAML files and `git diff --check` pass. Docker Compose configuration and the actual image/service behavior pass in G1.

G1 created disposable roots and a real mock-backed YAML, provisioned the ownership boundary, initialized both stores and the account as UID1654, served with UID1654 and isolated extractor UID1655, and verified health/readiness, login, create/list/revoke keys, provider list/test, session list/revoke/logout-all, rejected stale cookies/keys, fresh login and doctor JSON exit 0. It also verified read-only serving rootfs, zero effective capabilities and public canary blockage. A T3 browser then loaded `/login`, submitted the real form, rendered the signed-in page, fetched framework JS and received `/auth/me` 200. The retained stack, named volume and temporary roots were removed after this verification.

Local evidence files (ephemeral, not repository artifacts): `/tmp/secondbrain-converge-final-build.log`, `/tmp/secondbrain-converge-verification.log`, `/tmp/secondbrain-converge-verification/*.trx`, `/tmp/secondbrain-convergence-gates/*-gates.trx`, `/tmp/secondbrain-convergence-gates/deploy-final.trx`, `/tmp/secondbrain-converge-publish-cli.log`, and `/tmp/secondbrain-convergence-compose-smoke-final.log`.

## Package, contract and deployment changes

No package version changed in `Directory.Packages.props`. Regenerated lock files reflect project references only: CLI now references Storage and Server to reuse durable initialization, credential generation and calibrated hashing; Deploy.Tests references MockProvider; Server.Tests references Extractor. Core still has no project references.

- Added `ExtractorOptions.SocketPath` and `SECONDBRAIN_EXTRACTOR_SOCKET`, plus the bounded shared ping client, so readiness and doctor probe the actual configured Unix socket.
- Added persisted password-parameter metadata to `StoreInitializationRequest`, so initialization/reset and daemon verification use the same calibrated policy.
- Added a pre-publication reload-validation event and `IProviderRegistry.IsCurrentConfiguration`, so bad provider/privacy reloads retain the accepted snapshot and readiness cannot reuse an observation from a different configuration.
- Exposed `DataRootLock.IsHeld` and Access refresh state for truthful diagnostics. Added admin scope-matrix entries for provider/diagnostic endpoints.
- Added requested ProblemTypes constants: `authentication-required`, `step-up-required`, `antiforgery-rejected`, `invalid-request`, `source-path-rejected`, `not-found`, `method-not-allowed`, `conflict`, `request-too-large`, `unsupported-media-type`, `internal-error`.
- Registered the raw persisted Data Protection provider separately from the HTTP purpose wrapper, fixing a real composition recursion when the key ring and credential services resolve each other.
- Replaced the Compose host-mounted socket directory with a provisioned Linux named runtime volume. Docker Desktop rejected Unix socket chmod on its host file share; the new volume preserves required modes and identity isolation. The mock image/Compose override is test-only and is absent from production serving targets.

## Remaining qualifications and boundaries

1. **Live vLLM blocks M0 closure.** The operator confirmed it is not set up. Supply its URL, chat/enrich model IDs, embedding model ID/dimensions and reviewed limits; run the qualification workflow once on a runner with network access. This gate checks configured-role transport/reachability and `/ready`; broader model-quality or native-tool inference evaluation belongs to later milestones. Provider health testing follows configured roles; unused provider declarations have no role observation, and testing such a provider returns an empty result rather than an inference qualification.
2. **Remote CI evidence is pending.** The workflow is configured and its local equivalents pass; this task prohibits pushing, so no remote run is claimed.
3. **Target Linux operational spikes remain.** Verify live systemd socket activation, target cgroup-BPF egress enforcement while DNS/Access/vLLM still work, Linux arm64 descriptor ABI, and HTTPS from an actual tailnet peer with its certificate. The existing lane-C evidence covers systemd syntax verification and descriptor passing on macOS arm64/Linux amd64; Docker proved the Linux amd64 extractor ping and firewall behavior here. Docker success does not prove target-host systemd behavior.
4. **External integrations require setup.** Live Cloudflare Access/tunnel and native macOS Keychain/Linux Secret Service were not exercised against developer accounts. Local JWKS and private file-credential fallback are tested. Direct macOS multi-UID root preparation was not run because passwordless sudo is unavailable; the full local end-to-end run was performed in Docker.
5. **M0 hooks remain deliberate stubs.** The extractor implements ping/descriptor probes, source registration has no watcher, and no ingest/search/chat projections are promised. M1 work items explicitly cover parsing/supervision, durable admission, source reconciliation, cursors and lineage/purge; M2 covers paired CLI sessions, assistant approval and MCP. No outstanding TODO affecting the security guarantees was found without that milestone linkage.

The software convergence deliverables are implemented and locally verified. The live qualification, remote CI evidence and target-host spike results must be recorded before claiming the full M0 definition of done.

## Addendum: clean-architecture refactor

Recorded 2026-10-08 on branch `refactor/clean-architecture`, from baseline `37d47fb` (steps S0–S16 of [clean-architecture-plan.md](clean-architecture-plan.md)). The evidence above is the original convergence record and is unchanged. The refactor changes no behaviour, contract, exit code or deployment artifact; it adds `SecondBrain.Infrastructure`, moves all SQL into Storage, removes the CLI's compile-time use of Server (its Server reference is now layout-only), and adds architecture tests.

Five test classes moved to new projects. Their class and method names are unchanged, so read the gate names above through this map; for example `Transport:` now means `SecondBrain.Providers.OpenAICompatible.Tests.Transport`.

| Before | After |
| --- | --- |
| `SecondBrain.Core.Tests.Configuration.ConfigurationTests` | `SecondBrain.Infrastructure.Tests.Configuration.ConfigurationTests` |
| `SecondBrain.Core.Tests.Security.KeyRingAndRootTests` | `SecondBrain.Infrastructure.Tests.Security.KeyRingAndRootTests` |
| `SecondBrain.Core.Tests.Providers.Ready` | `SecondBrain.Providers.OpenAICompatible.Tests.Ready` |
| `SecondBrain.Core.Tests.Providers.AdapterTests` | `SecondBrain.Providers.OpenAICompatible.Tests.AdapterTests` |
| `SecondBrain.Core.Tests.Providers.Transport` (G3, G4 and G5 evidence) | `SecondBrain.Providers.OpenAICompatible.Tests.Transport` |

`SecondBrain.Core.Tests.Privacy.*` (G3 and G5 evidence), `SecondBrain.Core.Tests.Domain.*`, `SecondBrain.Server.Tests.*` (including `Qualification`), `SecondBrain.Storage.Tests.*` and `SecondBrain.Deploy.Tests.*` did not move.

Totals after the refactor (`dotnet test SecondBrain.slnx --configuration Release`, every test passing):

| Test project | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| SecondBrain.Architecture.Tests (new) | 58 | 0 | 0 |
| SecondBrain.Core.Tests | 104 | 0 | 0 |
| SecondBrain.Infrastructure.Tests (new) | 71 | 0 | 0 |
| SecondBrain.Providers.OpenAICompatible.Tests (new) | 34 | 0 | 0 |
| SecondBrain.Storage.Tests | 100 | 0 | 0 |
| SecondBrain.Deploy.Tests | 109 | 0 | 0 |
| SecondBrain.Server.Tests | 137 | 0 | 0 |
| **Total** | **613** | **0** | **0** |

All 516 baseline tests are still present and passing, under the names above. The other 97 are new: 58 architecture rules, 33 Storage characterization tests for the relocated auth, password-policy, account-recovery and sources SQL plus composition, 4 Server guards (composition and OpenAPI golden files, Base64Url equivalence), and 2 Core guards (YAML key golden file, `KeyRingPurposes` values). CI's `Category!=Qualification` filter runs 612; the extra one is still the unset-URL qualification no-op, which does not count as live qualification. The M0 closure items in "Remaining qualifications and boundaries" are unchanged.
