# M0 convergence report

## M0 closure (2026-10-09)

**M0 is closed for the reference Fedora x86_64 / Tailscale deployment.** The operator explicitly chose to close this working deployment and track the remaining integration qualifications as follow-ups. This is an accepted scope change: the Access portion of the original systemd egress spike remains unproved, and the live vLLM qualification is accepted from the local reference-host run rather than the remote workflow. None of the deferred checks is counted as passing.

| Acceptance evidence | Result |
| --- | --- |
| Deterministic gates and deployment CI | All six jobs passed on implementation/evidence revision `8c7d24c`: locked build and deterministic tests, all three linux-x64 publishes, Docker build and G1 hardened Compose smoke. [GitHub Actions run 37947261649](https://github.com/justinleahy/SecondBrain/actions/runs/37947261649). The deterministic suite contains 750 passing cases; live qualification is separate. |
| G2 live vLLM | `Qualification.VllmReady` passed twice on the RTX 5090 host with chat/enrich and embed on separate trusted loopback providers. `/ready` returned 200 through the production configuration and policy-owned transport; see the live qualification addendum below. |
| Reference-host operation | systemd socket activation; cgroup-BPF public canary blockage with ready vLLM roles; `brain doctor`; native Linux Secret Service; and HTTPS 200 from the actual iPhone tailnet peer. See the target-host addendum below. |
| M1 handoff | [M1 ingest/search plan](M1-ingest-and-search.md) is drafted. Extractor parsing/supervision, source reconciliation, durable admission, cursors and lineage/purge have M1 work items; pairing and assistant approval remain M2. |

### Tracked follow-ups

These remain open after M0 closure and carry their own acceptance checks:

| Issue | Remaining qualification |
| --- | --- |
| [#2 — Remote live qualification runner](https://github.com/justinleahy/SecondBrain/issues/2) | Run the workflow on a trusted runner that can reach vLLM; the accepted local result does not prove the remote workflow. |
| [#3 — Cloudflare Access and Tunnel](https://github.com/justinleahy/SecondBrain/issues/3) | Production JWKS refresh through the target-host egress policy, failed refresh with valid cached keys after removing the allowance, and the real Access/Tunnel login path. Local G12 fixture tests are already passing. |
| [#4 — macOS Keychain](https://github.com/justinleahy/SecondBrain/issues/4) | Exercise native credential storage, authenticated CLI use and cleanup on macOS. |
| [#5 — Linux arm64 descriptor ABI](https://github.com/justinleahy/SecondBrain/issues/5) | Run native descriptor, framing and cleanup checks on Linux arm64; Linux amd64 and macOS arm64 evidence already exists. |
| [#6 — Certificate lifecycle](https://github.com/justinleahy/SecondBrain/issues/6) | Verify renewal adoption and missing/invalid/expired-certificate behavior. The current listener loads the certificate at startup; automatic reload is not claimed. Complete before the reference certificate expires on 2026-11-25. |

The [M0 build plan](M0-foundations.md#accepted-closure-scope-2026-10-09) records the same acceptance decision. Earlier sections below are chronological evidence; their statements that M0 was open describe the state at those times and are superseded by this scoped closure.

## Historical convergence record (2026-10-08)

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

## Addendum: M0 review fixes

Recorded 2026-10-08 against the uncommitted working tree on local `main`, based on `bf54691`. This addendum covers the ten distinct findings from the M0 completeness review and the Daybreak Blue security review. Everything above it is historical evidence and is unchanged, including the earlier test totals and the G1 result. **This addendum does not close M0.** An independent read-only review of the ten fixes, made before the final A3 cursor correction, reported no concrete bypass or regression; it was a static review and ran nothing. The D2 configuration-symlink follow-up is complete. A later source and spec check found that the A3 cursor did not meet §7.3 and SEC-32; that has been corrected as described in the A3 row. The independent reviewer did not examine that correction; the parent inspected the corrected cursor source and its focused regression tests. Final local integrated verification on the corrected tree passed, as recorded under "Verification status". Items 1–4 of "Remaining qualifications and boundaries" (live vLLM, remote CI, target Linux spikes, external integrations) are still pending, and item 5's M0 stubs are unchanged.

| ID | Finding | Fix as implemented | Focused regression tests |
| --- | --- | --- | --- |
| P1 | DNS pin reload race (security, Medium): pins were checked before connect, and the post-connect check validated the binding and origin but not the connected address. A same-origin reload that repinned during the connect could send to a removed address. | The connect callback wraps the socket in a policy-checked stream. Before HTTP sees the socket, and again before every write (including a TLS ClientHello), it checks the dialed address against the current pins. It also checks the writing request's origin and policy, not only the request that opened the connection. Requests are exactly HTTP/1.1. There is still one policy-owned transport with proxies and redirects disabled, and `local_only` fails closed. | `Providers: ConnectionPinRaces.RepinAcceptedDuringConnectionNeverSendsToRemovedAddress`, `OriginRemovedAfterConnectRefusesBeforeFirstWrite`, `UnchangedPinReloadDuringConnectionStillSends`, `PolicyTransportSendsExactlyHttp11` |
| P2 | Production model configuration: an unknown model resolved with no tools capability, so an ordinary vLLM chat id was rejected even with explicit limits. Qualification only passed because it injected a catalog unavailable in production. | `models.<role>.capabilities` (`tools`, `streaming`, `structured_output`) describes only models absent from the catalog. A declaration for a catalog model must agree with the catalog. Nothing is inferred from an endpoint. Declarations never replace the required limits, and a fallback carries its own declaration. Qualification now writes the declarations to YAML and loads them through the production configuration path, with no catalog injection. | `Providers: ConfiguredModelCapabilities.UnknownChatModelWithDeclaredToolsIsAcceptedThroughConfiguration`, `UnknownChatModelWithoutDeclaredToolsIsRejectedAndReloadKeepsClients`, `DeclarationsContradictingTheCatalogAreRejected`, `DeclarationsAgreeingWithTheCatalogAreAccepted`, `EmbeddingDeclarationsDoNotReplaceEmbeddingLimits`, `FallbackCarriesItsOwnDeclaration`, `NullCapabilitiesAreRejectedByConfigurationValidation`; `Server: Qualification.VllmReady` (rewritten; still not live) |
| P3 | Unbounded model discovery (security, Low): `/models` was buffered and parsed without byte, count, or id-length bounds. | The response read is bounded to 8 MiB, 10,000 entries, and 512-character ids. The timeout covers the body read. Exceeding a bound is a non-retryable `provider-malformed` failure, and valid lists are unchanged. | `Providers: BoundedDiscovery.OversizedModelListIsTypedMalformed`, `ModelListAtTheByteLimitIsAccepted`, `ModelCountIsBounded`, `ModelIdLengthIsBounded`, `SlowBodyAfterHeadersIsTypedTimeout` |
| A1 | Anonymous oversized login (security, Medium): JSON and form login bodies were parsed under the 100 MiB global limit before the 1,024-byte password check, and anonymous requests skipped admission. | `PasswordRequestMiddleware` covers `/auth/login` and `/auth/step-up`, with and without `/v1`. It runs after the Host, Origin, and Access gates and before authentication and antiforgery. It admits at most 8 requests globally and 4 per source without queueing (429 with `Retry-After`). It buffers at most 16 KiB whether or not Content-Length is declared or the body is chunked (413), and allows 10 seconds for the body (408). Login rejects content types other than JSON or a form with 415. A fully escaped 1,024-byte password still fits. | `Server: AuthHardeningTests.Login_OversizedBodyIsRejectedBeforeParsingOrVerification`, `StepUp_OversizedBodyIsRejectedBeforeAntiforgeryAndBinding`, `Login_BadOriginStillWinsOverAnOversizedBody`, `Login_MaximumPasswordFitsEveryRepresentation`, `Login_UnsupportedContentTypeIsTyped415`, `Login_PerSourceAdmissionRejectsWithoutWaitingAndReleases`, `Login_GlobalAdmissionBoundsDistinctSources`, `Login_StalledBodyTimesOutAndReleasesAdmission`; `Deploy: CliConvergenceTests.Cli_DaemonBoundsLoginBodiesOnKestrel` |
| A2 | Revocation without step-up: logout-all and deleting another session had no SEC-10 step-up. | Both now require a fresh step-up. Ordinary logout, and revoking the caller's own session, do not. API-key behavior is unchanged. The CLI's temporary-session `sessions revoke` and `revoke-all` reuse the password they already read to step up, then refresh the antiforgery token before revoking. The existing G11 test now asserts that an unstepped call returns 403. | `AuthHardeningTests.LogoutAll_RequiresFreshStepUp`, `SessionRevocation_OtherNeedsStepUpButSelfDoesNot`; `AuthTests.Sessions_LogoutAllEpochRejectsEveryCredential` and the session revoke flow (updated) |
| A3 | Credential listing truncation: the oldest 1,000 rows were selected before the active-session filter, so historical rows hid new sessions and key listings were truncated. | `GET /auth/sessions` and `GET /keys` (and their `/v1` aliases) return keyset pages as a bare array: `limit` 1–500, default 100; an opaque `after` cursor; `Link: rel="next"`. The session-activity filter runs in SQL before the limit. Key listings keep their full history. The first candidate's cursor was an unauthenticated Base64url `(created_at, id)` position, which did not meet §7.3 or SEC-32. The correction makes the cursor an ASP.NET Core Data Protection envelope from the key ring's provider, under `KeyRingPurposes.Cursor` with the subpurpose `credentials.list.v1`. It carries the version, the persisted `meta.instance_id`, the logical operation (`sessions.list` or `keys.list`, the same for the `/v1` alias), the normalized query hash, the sort and `(created_at, id)` position, the processing generation (the empty set, because credential rows depend on none), the caller's credential id, generation, and account epoch, the issue time, and a 15-minute expiry. Every page still passes normal authentication and authorization, and the cursor grants nothing. The token length (2,048 characters) is bounded before decryption, and the decrypted payload (1,024 bytes) before parsing. A malformed, altered, expired, or misapplied cursor gets the same typed `400 invalid-request`. Unsigned cursors from the first candidate are rejected. Core's `IAuthRepository` gains `GetInstanceIdAsync`, which Storage implements as a `meta` read; DI registrations are unchanged. `brain keys list` and `brain sessions list` follow every page, but only on the same origin and path. A rejected link, a repeated link, or more than 1,000 pages is a hard failure with no partial result. | `AuthHardeningTests.Sessions_ListingPagesPastHistoricalRowsAndRevokesALaterPage`, `Keys_ListingKeepsHistoryAndReachesNewKeysPastTheOldCap`, `Listing_RejectsMalformedPagingParameters`; `Server: CredentialCursorTests.UnsignedKeysetPositionIsRejected`, `TamperedOrTruncatedCursorIsRejected`, `MalformedOrOverlongCursorIsTyped400`, `CursorExpiresFifteenMinutesAfterIssue`, `CursorIsBoundToItsListingOperation`, `CursorIsBoundToTheIssuingCredential`, `CursorIsRejectedAfterTheCredentialGenerationOrAccountEpochChanges`, `CursorIsBoundToTheInstance`, `CursorEnvelopeUnderAnotherPurposeIsRejected`, `CursorsContinueAcrossRouteAliasesToTheEnd`; `Storage: AuthRepositoryTests.ListPageAsyncFiltersByKindAndPagesByCreatedAtThenId`, `ListPageAsyncActiveOnlyFiltersHistoricalRowsBeforeTheLimit`, `GetInstanceIdAsyncReadsTheMetaInstanceIdAndRefusesAnUninitializedStore`; `CliConvergenceTests.Cli_ListingsTraverseEveryPagePastHistoricalRows`, `Cli_ListingRefusesNextLinksOutsideTheListing`, `Cli_ListingFailsInsteadOfLoopingOrTruncating`, `Cli_ListingFollowsSameOriginNextLinksToTheEnd` |
| A4 | Login history cap: the oldest 1,000 attempts in the hour were replayed, so newer failures could be ignored after enough successes. | Lockout is rebuilt from at most the newest 1,000 attempts after the source's latest success in the window, ordered by `(at, rowid)`. If that tail fills the cap, the source fails closed into a fixed 15-minute lock from its newest failure. Exponential delays, the non-renewable fixed lock, the global Argon2 semaphore, and `--reset-password` recovery are unchanged. | `AuthHardeningTests.Lockout_HoldsAfterManySuccessesAboveTheHistoryCap`, `Lockout_ExponentialDelayStillAppliesAboveTheHistoryCap`, `Lockout_FailsClosedWhenTheTailFillsTheCap`; `AuthRepositoryTests.LoginAttemptsAsyncStartsAtTheLatestSuccessInTheWindow`, `LoginAttemptsAsyncBreaksIdenticalTimestampsByInsertionOrder`, `LoginAttemptsAsyncKeepsTheNewestRowsChronologicallyWhenTheTailExceedsTheLimit` |
| D1 | Journal and publication race: apply wrote the filesystem before taking the document lock or checking the revision. A publication between prepare and apply left an applied row that every recovery attempt rejected, which also failed startup. | Prepare checks the observed revision and records a durable per-document reservation in `meta` in the same transaction. A second active mutation of that document is refused. Publication returns `Superseded` while the reservation exists, and the reservation is released only at a terminal state (finalized, failed, or conflict). Apply, prepared-row recovery, and finalization hold the document lock across every filesystem effect and re-check the revision. Legacy rows written before reservations existed resolve idempotently. A stale prepared row never writes the destination; if its rename already happened, the row is reconciled and then conflicted. A stale applied row becomes `conflict` (target changed): the bytes on disk are kept, and the newer metadata is not rolled back. When the bytes differ from the current hash, an `external_edit` revision is recorded on top of the newer revision without regressing the publication fence. | `Storage: JournalTests.PublicationCannotAdvanceARevisionReservedByAMutation`, `PrepareRejectsStaleRevisionsAndASecondMutationOfTheSameDocument`, `LegacyStaleAppliedRowRecoversAsConflictKeepingBytesAndNewerMetadata`, `LegacyStalePreparedRowNeverWritesTheDestination`, `LegacyStaleRenamedPreparedRowIsReconciledThenConflicted`; existing crash-point, recovery, and conflict tests still apply |
| D2 | Relocated source deny roots: FLD-1 protected fixed paths and the data root, but not a configuration file or secrets directory relocated with `SECONDBRAIN_CONFIG` or `SECONDBRAIN_SECRETS_DIRECTORY`. | A single `RuntimeLocations` resolution in LaneC now feeds both the configuration loader and `SourcePathValidator`. The validator denies the actual secrets directory, the configuration directory, and each directory a configuration symlink passes through. Matching is lexical and after symlink resolution, against both descendants and ancestors, even under `allowed_roots`. Legitimate sibling sources remain allowed. M0 only registers sources; the fix lands before ingestion. | `Server: SourcesTests.RelocatedConfigurationAndSecretsAreDeniedEvenUnderAnAllowedRoot`, `ConfigurationSymlinkTargetAndAliasedSecretsAreDenied`, `RelativeConfigurationLinkBeneathAliasedParentResolvesFromThePhysicalParent`, `ConfigurationLinkTraversingADirectoryLinkThenParentFollowsTheLinkTarget`, `DanglingConfigurationLinkProtectsItsTargetWithoutDenyingEverything`, `RegistrationUsesTheComposedRuntimeLocations`. A follow-up confirmed two configuration-symlink layouts that bypassed the deny roots and fixed both in `SourcePathValidator.cs`; `SourcesTests` passed 29/29. The independent review reported no concrete bypass or regression (static review only). |
| D3 | Migration disk preflight measured `/` instead of a separately mounted data root. | `MigrationRunner` takes Core's `IDiskCapacity`, so Storage gains no Infrastructure dependency. Infrastructure's `DiskCapacity` picks the longest mount root containing the symlink-resolved path, on segment boundaries, and serializes reads of the mount table. Unknown capacity fails closed before any migration marker is written; there is no fallback to the root filesystem. The daemon (LaneA) and the CLI's local initialization register the probe. | `Infrastructure: FileSystem.DiskCapacityTests.LongestContainingMountWinsOnSegmentBoundaries`, `SymlinkedDataRootSelectsItsTargetFilesystem`, `NoContainingMountIsUnknownRatherThanTheRootFilesystem`, `RealMountTableReportsCapacityForAnExistingDataRoot`, `ConcurrentProbesNeverReportUnknownCapacity`; `Storage: Migrations.DiskPreflightMeasuresTheDataRootFilesystemBeforeMigrationMarkers`, `UnknownDiskCapacityFailsClosedBeforeMigrationMarkers`; `Storage: CompositionTests.MigrationStartupUsesTheRegisteredDataRootDiskProbe` |

### Contract, composition and documentation changes

- **REST.** Both credential listings now take `limit` and `after` and may return `Link: rel="next"`. The `after` cursor is a protected SEC-32 token that expires after 15 minutes, so a client that pauses longer restarts from the first page. Without parameters they return the first 100 rows instead of up to 1,000. Step-up is now required (403 `step-up-required`) for logout-all and for revoking another session. Password endpoints add 408, 413, 415 and 429 problems. No operation or component schema was added or removed, so the OpenAPI golden, which pins only the operation list and component schemas (not query parameters or status codes), did not change. `OpenApiSnapshotTests.OpenApiMatchesGolden` passes against the current source.
- **Composition golden.** `RuntimeLocations` (LaneC) is a new singleton, and `IDiskCapacity` is now registered first by LaneA, ahead of storage, which makes the limits lane's existing `TryAddSingleton` a no-op. `PasswordRequestMiddleware` is a pipeline middleware, not a DI entry. The cursor correction resolves the existing `IKeyRing` and `TimeProvider` in the listing handlers and adds no registration. No other entry changed.
- **Configuration.** `models.<role>.capabilities` is new and appears in the commented provisioning examples. No package versions or project dependencies changed beyond the Storage→Core use of `IDiskCapacity`.
- **Documentation.** `spec.md` §7.3 (credential pagination with protected cursors, and the `/keys` row), FLD-1, SEC-7, SEC-10, SEC-15 (connected-address re-check, plus HTTP/1.1 described as a property of the current adapter design rather than of HTTP/2), SEC-30, and §13; `README.md`; the CLI README; `LaneD-Integration.md`; and the provider `INTEGRATION.md`.

### Compatibility notes

These are established behaviors that the fixes keep. They are recorded as limits, not as open decisions.

1. **API keys and step-up.** An API key holding `admin` creates and revokes credentials without step-up, as `LaneD-Integration.md` describes. Step-up applies to interactive sessions. These fixes did not change API-key behavior.
2. **Unauthenticated step-up requests.** Password admission and the body bound run before authentication. An unauthenticated `/auth/step-up` request therefore takes an admission slot and has its body read, at most 16 KiB within 10 seconds, before it receives 401. It is bounded by the same limits as login: 8 in flight globally and 4 per source.

### Verification status

Each implementation lane ran its own scoped checks before integration. Those runs overlapped, so they are not final gate counts and must not be added together:

- Provider: 57/57, including 23 new tests.
- Core: 104/104. Infrastructure configuration: 60/60. Architecture: 58/58.
- Auth: Server subset 137/138 (the only failure was the expected composition golden), AuthRepository 28/28, Deploy subset 54/54.
- Durability: Storage 113/113, Infrastructure 76/76, Deploy 109/109, Server 163/164 (the only failure was the composition golden).

Integration step: the composition golden was updated as described above. `dotnet build tests/SecondBrain.Server.Tests/SecondBrain.Server.Tests.csproj --configuration Release --no-restore -warnaserror --disable-build-servers` succeeded with 0 warnings and 0 errors. `dotnet test` on that project with `--filter 'FullyQualifiedName~SecondBrain.Server.Tests.CompositionSnapshotTests|FullyQualifiedName~SecondBrain.Server.Tests.OpenApiSnapshotTests'` ran 2 tests: 2 passed, 0 failed.

First integrated run, before the A3 cursor correction: locked restore succeeded, the Release `-warnaserror` solution build had 0 warnings, the full deterministic `Category!=Qualification` run passed 696 tests, and the hardened Compose G1 smoke passed. These results predate the correction and are not final evidence for the current tree.

A3 cursor correction, focused checks only. Against the unsigned cursor, before the change, a hand-made `(created_at, id)` cursor returned 200 starting at the forged position. A cursor was also accepted for another credential, another listing, another instance, after a generation or epoch bump, and at 15 minutes old. After the correction, each Release build of the Server, Storage, Deploy, Architecture, and provider test projects (`--no-restore -warnaserror --disable-build-servers`) had 0 warnings and 0 errors. Results: `CredentialCursorTests` 14/14. The focused Server auth, paging, sources, and snapshot set 104/104. The deterministic Server suite 180/180. `AuthRepositoryTests` 29/29. The deterministic Storage suite 114/114. `CliConvergenceTests` 23/23. Architecture 58/58. The Server fixture now seeds `meta.instance_id` alongside the account epoch, as `brain init` does.

Final integrated verification, on the corrected tree. It ran on 2026-10-08 against the uncommitted working tree on local `main` at `bf54691`, after a comment-only correction in `CredentialPaging.cs` (the token length is bounded before decryption and the decrypted payload before parsing). Each gate ran once, in order, and exited 0:

1. `dotnet restore SecondBrain.slnx --locked-mode --disable-build-servers`: all projects were up to date for restore, with no warnings or errors.
2. `dotnet build SecondBrain.slnx --configuration Release --no-restore -warnaserror -p:ContinuousIntegrationBuild=true --disable-build-servers`: build succeeded, 0 warnings and 0 errors. It was incremental; every project's output was newer than its sources, and the Server assembly was rebuilt after the comment edit.
3. `dotnet test SecondBrain.slnx --configuration Release --no-build --no-restore --disable-build-servers --filter 'Category!=Qualification' --logger trx --results-directory <fresh directory>`: seven TRX files, 711 tests, 711 passed, 0 failed, 0 not executed.

   | Project | Passed / total |
   | --- | --- |
   | Core | 104/104 |
   | Architecture | 58/58 |
   | Storage | 114/114 |
   | Infrastructure | 76/76 |
   | Providers.OpenAICompatible | 57/57 |
   | Deploy | 122/122 |
   | Server | 180/180 |
   | **Total** | **711/711** |

   The focused classes for these fixes all passed: `CredentialCursorTests` 14/14, `AuthHardeningTests` 24/24, `SourcesTests` 29/29, `AuthRepositoryTests` 29/29, `CliConvergenceTests` 23/23, `JournalTests` 25/25, `Migrations` 14/14, `DiskCapacityTests` 5/5, and the provider classes in `ProviderSafetyRegressionTests.cs` (`ConnectionPinRaces` 5/5, `ConfiguredModelCapabilities` 10/10, `BoundedDiscovery` 8/8). The composition and OpenAPI golden tests passed. `Qualification.VllmReady` was excluded by the filter and is not counted; nothing here is live qualification.
4. `SECONDBRAIN_PRIVATE_ADDRESS=127.0.0.1 SECONDBRAIN_SMOKE_PROJECT=<disposable project> deploy/tests/compose-smoke.sh`: G1 passed. It covered provisioning, `brain init`, the daemon as UID 1654 and the extractor as UID 1655, `/health` 200 and `/ready`, CLI login, key create/list/revoke, `providers list` and `providers test mock`, the Razor login form (303) and signed-in shell, browser session listing and revocation (401 afterwards), logout-all rejecting the browser session (401) and the stored API key (exit 4), re-login and `doctor` against the extractor socket, zero effective capabilities, a read-only root filesystem, and the blocked egress canary. The script removed the run's containers, volumes and network; the run's generated host root under `/tmp` was then removed. Docker containers, volumes and networks matched the pre-run state.

`git diff --check` reported nothing, the untracked source and test files have no trailing whitespace, and no `packages.lock.json`, `Directory.Packages.props`, `global.json` or `.csproj` file changed.

The 696-test run above predates the A3 cursor correction; the 711-test run is the evidence for the current tree. Live vLLM qualification, remote CI, the target-host spikes (systemd socket activation, egress enforcement, arm64 descriptor ABI, tailnet HTTPS) and the external integrations (Cloudflare Access and Tunnel, native credential stores) remain pending, so M0's definition of done is not met.

## Addendum: 2026-10-09 rereview corrections

Recorded against the uncommitted local `main` tree at `bf54691f61a95a5a529280b0b87c9f1ba600133b`. The October 8 addendum remains historical evidence: its ten-finding closure assessment and **711-test total are superseded** by this rereview and the **750-test current result** below. Five additional defects remained in those fixes. This addendum records their corrections without closing M0.

| Rereview finding | Current correction and regression evidence |
| --- | --- |
| **P1: check-to-write policy race** | Final policy validation and socket-write initiation now share an atomic boundary with policy publication; asynchronous completion is awaited outside it. Already-admitted writes may finish, but subsequent writes use the current policy and the actual writing request's context. The original `/tmp/astra-m0-p1` reproduction was rerun after the build: validation step 5 reported `BLOCKED`. Ordering tests retain legitimate admitted-write controls; `InferenceBodyAdmissionTests.ReplacementBetweenBodyChunksRefusesTheLaterWrite` and `QueuedWriteContextTests.ConnectionCreatedForCancelledDiscoveryCannotAuthorizeQueuedInference` cover body chunks and a cancelled discovery connection reused by queued inference. |
| **P2: unqualified catalog trust** | `ModelCatalog.Register` requires the concrete `(provider, model)` pair. Implicit vendor and mock model-name defaults were removed. Unknown bindings use explicit production YAML limits/capabilities, and fallback declarations remain independent. `ConcreteCatalogScopeTests.FamiliarNamesHaveNoImplicitDeclarations`, `SameModelOnDifferentProvidersHasIndependentReviewedDeclarations`, and `DiscoveryCannotBorrowAnotherProvidersReviewedModel`, together with the existing configured-capability acceptance/contradiction tests, cover rejection and legitimate declarations. |
| **A2: admitted actor invalidated before mutation** | Credential create/revoke/logout-all validate the actor's id, generation, current account epoch, kind, persisted scope, revocation, every expiry, and required session step-up inside the same writer transaction as the mutation. Password-verified issuance and local maintenance retain separate paths. Rotation also checks hard expiry and replacement epoch. Authority failures precede target lookup and retain the existing HTTP 409 mapping. `AuthRepositoryTests.QueuedMutationRechecksActorBeforeLookingUpTarget` covers queued revocation, rotation, epoch, expiry and step-up expiry; `AuthorityMutationTests.AdmittedAdminRevokedDuringAuthorizationCannotMutate` covers HTTP creation/revocation. Current actors, fresh step-up, admin API keys without session step-up, self-logout, protected cursors and CLI flows remain covered. |
| **D1: generation-only publication bypass** | Prepare validates the revision, captures the normalized current generation in the durable expected fence, and reserves the document in the same transaction. Apply/finalize recheck both fields; `Reprocess` respects the reservation. A requested finalization generation is distinct from the captured expected generation, and legacy recovery preserves a newer existing generation. `JournalTests.GenerationReservationCoversPreparedAndAppliedMutation`, `AcceptedMutationMayAdvanceItsGenerationAndExternalConflictPreservesNewerFence`, and `SameRevisionNewGenerationNeverRollsBackDuringRecovery` cover the original sequence, current/legacy rows, restart and external conflict controls. |
| **D1: nonregular displaced-entry recovery** | Typed non-following observations preserve directories, symlinks and other nonregular conflicts. Displaced entries are restored or quarantined without following/deleting them; no content revision is invented without a regular-file hash. `JournalTests.NonregularDestinationConflictsWithoutInventingARevision` and `RecoveryPreservesNonregularDisplacedEntryAndThenIsIdempotent` cover persisted directory displacement, additional replacement, unreadable mode-000 directories and repeat recovery on macOS and Linux. Existing regular-file exchange and symlink-admission tests remain positive controls. |

Primary implementation files changed in this rereview are grouped by boundary: `Core/Privacy/IProviderEgressPolicy.cs` and `PrivacyPolicy.cs`, plus `Providers.OpenAICompatible/PolicyHttpClientFactory.cs` and transport tests; `Providers.OpenAICompatible/ModelCatalog.cs` and concrete catalog/configuration tests; `Core/Auth/IAuthRepository.cs`, `Storage/Auth/AuthRepository.cs`, `Server/Auth/AuthEndpoints.cs` and repository/HTTP authority tests; `Storage/Durability/MutationJournal.cs`, `ManagedMutationFiles.cs`, `PublicationCoordinator.cs` and journal/publication tests. Mock-backed configuration fixtures and provisioning examples now supply explicit model metadata. `README.md`, `spec.md` and integration notes describe the current contracts; historical architecture evidence is preserved. No commits, pushes, package, project or SDK changes were made.

### Current verification and review limits

Fresh integrated evidence is indexed by `/tmp/secondbrain-m0-sol-final-metadata.json`, which links the restore/build/test logs and seven TRX files. The gates each exited 0:

1. `dotnet restore SecondBrain.slnx --locked-mode --disable-build-servers`.
2. `dotnet build SecondBrain.slnx --configuration Release --no-restore -warnaserror -p:ContinuousIntegrationBuild=true --disable-build-servers`: **0 warnings, 0 errors**.
3. `dotnet test SecondBrain.slnx --configuration Release --no-build --no-restore --disable-build-servers --filter 'Category!=Qualification' --logger trx --results-directory <fresh directory>`: **750/750 passed, 0 failed, 0 skipped**.

| Project | Passed / total |
| --- | ---: |
| Core | 109/109 |
| Architecture | 58/58 |
| Storage | 134/134 |
| Infrastructure | 76/76 |
| Providers.OpenAICompatible | 69/69 |
| Deploy | 122/122 |
| Server | 182/182 |
| **Total** | **750/750** |

The non-Markdown versioned and untracked nonignored source/configuration inputs had the same fingerprint before and after the gates: `90ca6951c6d5eec93937bfb4ab80a2241440fcd9863074c714bac127226c304f`.

Focused `JournalTests|PublicationTests` passed **54/54** as non-root UID 501 on macOS and **54/54** as non-root UID 1654 on Linux/amd64. `/tmp/secondbrain-m0-sol-linux-final-metadata.json` and `/tmp/secondbrain-m0-sol-linux-final.log` record the latter: `docker run --rm --platform linux/amd64 --network none --read-only --cap-drop=ALL --security-opt no-new-privileges --user 1654:1654 … dotnet vstest /tests/SecondBrain.Storage.Tests.dll '--TestCaseFilter:FullyQualifiedName~JournalTests|FullyQualifiedName~PublicationTests'`, with read-only test binaries and mounted result output. VSTest exited 0. The Linux SDK printed a workload-verification advisory; no workload changes were necessary.

**Current Docker G1: PASSED, exit 0.** `/tmp/secondbrain-m0-sol-compose-metadata.json` and `/tmp/secondbrain-m0-sol-compose-final.log` record the exact command: `SECONDBRAIN_PRIVATE_ADDRESS=127.0.0.1 SECONDBRAIN_SMOKE_PROJECT=secondbrain-m0-sol-final SECONDBRAIN_HOST_ROOT=/tmp/secondbrain-m0-sol-compose-i2xl_ax5 deploy/tests/compose-smoke.sh`. Provisioning/init, daemon UID 1654 and extractor UID 1655, health/readiness, CLI login/keys/providers/sessions/doctor, Razor login (303) and signed-in shell, session revocation (401), logout-all rejecting the cookie (401) and stored key (expected exit 4), re-login, extractor ping, read-only root, zero effective capabilities and blocked canary all passed. Four initial curl 52 empty-reply startup retries recovered normally. The owned host root was removed; Docker containers, volumes and networks matched their pre-run state (2/14/5).

A fresh independent GPT-6.1 Sol read-only candidate review found an EACCES (13) residual gap in nonregular recovery. The parent confirmed it; the durability correction and mode-000 regression variants are included in the final gates above. No other concrete issue was found. There was **no second independent review after that correction**; the parent inspected the changed source and regressions. The optional symlink-race harness was blocked and was not retried. Dynamic coverage therefore models persisted directory displacement, existing regular-file exchange and symlink admission rather than claiming that blocked race experiment ran.

Linux/arm64 native descriptor ABI qualification, full target-host checks, live vLLM, remote CI and external integrations remain pending. The deterministic suite excludes `Qualification.VllmReady`; its unset-URL path is not live qualification. **M0 remains open.**

## Addendum: 2026-10-09 live vLLM qualification

Recorded against the uncommitted local `main` tree based on `0e90c97`, then pushed as branch `vllm-provider-qualification` (pull request #1). This addendum closes item 1 of "Remaining qualifications and boundaries" (live vLLM) and records item 2's remote CI evidence below. Items 3–4 (target-host Linux spikes, external integrations) and the M0 stubs are unchanged, so **M0 remains open** on those obligations.

### Provider

The reference vLLM host is the RTX 5090 workstation itself (Fedora 44, driver 615.71.09, 32 GB VRAM). vLLM hosts one model per server, so [deploy/vllm/compose.yaml](../../deploy/vllm/compose.yaml) runs two `vllm/vllm-openai:v0.31.0` containers on the one GPU, offline against a read-only, user-owned Hugging Face cache, with every capability dropped:

| Role | Provider | Endpoint | Model | Served limits |
| --- | --- | --- | --- | --- |
| chat, enrich | `vllm` | `http://127.0.0.1:8000/v1` | `Qwen/Qwen3-8B` (bf16) | 32768-token context, 4096 declared output tokens, Hermes tool parser, Qwen3 reasoning parser, thinking off by default, 38,080-token KV cache at 70 % of GPU memory |
| embed | `vllm-embed` | `http://127.0.0.1:8001/v1` | `Qwen/Qwen3-Embedding-0.6B` | 1024 dimensions (Matryoshka 32–1024), 8192-token input, batch 32, 18,976-token KV cache at 12 % of GPU memory |

Steady state uses 27.4 GB of the 32.6 GB card (chat 21.9 GB, embed 4.1 GB, desktop compositor 0.5 GB). The chat engine initialised in 141 s (22 s torch.compile, 103 s CUDA graph capture); the embedding engine's first start took 87 s.

### Capability evidence for the reviewed declaration

`models.chat.capabilities: { tools: true, streaming: true }` was checked directly against the served model with `curl` before it was declared:

- A `get_weather` tool request returned `finish_reason: tool_calls` with the structured call `{"city": "Boston"}`, both in the non-thinking default (0 reasoning tokens, 20 completion tokens) and in thinking mode during the first bring-up (76 reasoning tokens).
- `stream: true` produced six `data:` chunks ending in `data: [DONE]` with the requested text.
- With thinking on by default, a 400-token budget was spent entirely on reasoning and returned no content, so the Compose file sets `--default-chat-template-kwargs '{"enable_thinking": false}'`. A request carrying `chat_template_kwargs: {"enable_thinking": true}` still works: 17×23 answered `391` with 934 reasoning tokens kept in `reasoning_content`, not `content`.
- `/v1/embeddings` returned 1024-dimensional vectors, and 256 with `dimensions: 256`.

### Qualification run

`Qualification.VllmReady` gained an optional `SECONDBRAIN_QUAL_VLLM_EMBED_URL` (an optional `embed_url` input in `qualification.yml`). A distinct value becomes a second trusted provider, `vllm-embed`, in the YAML the test writes and loads through the production configuration path; unset, the single-provider shape is unchanged. The deterministic suite passed **750/750** after that change (`dotnet test SecondBrain.slnx --no-build --no-restore --filter 'Category!=Qualification'`: Architecture 58, Core 109, Infrastructure 76, Storage 134, Providers.OpenAICompatible 69, Deploy 122, Server 182).

The live gate then ran twice from this checkout, the second time against the final Compose file above:

```sh
export SECONDBRAIN_QUAL_VLLM_URL=http://127.0.0.1:8000/v1
export SECONDBRAIN_QUAL_VLLM_EMBED_URL=http://127.0.0.1:8001/v1
export SECONDBRAIN_QUAL_VLLM_CHAT_MODEL=Qwen/Qwen3-8B
export SECONDBRAIN_QUAL_VLLM_EMBED_MODEL=Qwen/Qwen3-Embedding-0.6B
export SECONDBRAIN_QUAL_VLLM_DIMENSIONS=1024
export SECONDBRAIN_QUAL_VLLM_CONTEXT_WINDOW=32768
export SECONDBRAIN_QUAL_VLLM_MAX_OUTPUT_TOKENS=4096
export SECONDBRAIN_QUAL_VLLM_MAX_INPUT_TOKENS=8192
dotnet test tests/SecondBrain.Server.Tests --no-build --no-restore --filter 'Category=Qualification' --logger trx
```

**`SecondBrain.Server.Tests.Qualification.VllmReady`: Passed** (0.69 s). `/ready` answered 200 with every component ready, including `provider:chat`, `provider:enrich` and `provider:embed` probed through the policy-owned transport. The TRX is kept at `/tmp/secondbrain-vllm-qualification-2026-10-09.trx`. G2's live column is therefore satisfied on this host.

### Remote CI and the deferred remote qualification

GitHub Actions ran the CI workflow twice on 2026-10-09 with every job green: run `37934907738` on the push of `0e90c97` to `main` (3 m 49 s) and run `37940748698` on pull request #1 (3 m 48 s: build and deterministic tests, Docker image, G1 hardened Compose smoke, and the three linux-x64 publishes). That is the remote CI evidence item 2 asked for.

The live `qualification.yml` workflow still has no runner that can reach the vLLM host: the hosted `ubuntu-24.04` label cannot see this machine's loopback, and the public repository has no self-hosted runner. The operator decided on 2026-10-09 to keep the live qualification local for now; registering a self-hosted runner and recording a remote run is tracked as issue #2.

## Addendum: 2026-10-09 target-host systemd spikes on the RTX 5090 workstation

Recorded on branch `vllm-provider-qualification` after the live vLLM qualification above. The workstation that serves vLLM also served as the Linux target host: Fedora Linux 44 (KDE Plasma Desktop Edition), kernel `7.2.5-200.fc44.x86_64`, systemd 259 (`+BPF_FRAMEWORK`), cgroup v2, SELinux enforcing, `kernel.unprivileged_bpf_disabled=2`. The published self-contained linux-x64 Server, CLI and Extractor outputs were merged into `/usr/local/lib/secondbrain`; `brain init --provision --deployment systemd` created the 1654/1655/1656 identities and directories; the configuration bound chat and enrich to `http://127.0.0.1:8000/v1`, embed to `http://127.0.0.1:8001/v1`, enabled the canary against `1.1.1.1:443`, and declared an HTTP listener on `127.0.0.1:7171` plus an HTTPS listener on the tailnet address `100.67.195.36:7443` with the `tailscale cert` certificate for `fedora.tail9a7993.ts.net`. A drop-in added one tailnet peer (`100.127.250.19`) to `IPAddressAllow`. The operator ran the root steps; the bundle and its teardown live under the untracked `artifacts/systemd-spike/`.

### Findings fixed before the units ran

1. **SELinux domain.** Files under `/usr/local/lib` are labelled `lib_t`, so systemd kept `brain serve` in the confined `init_t` domain and the kernel denied the data-root lock: `avc: denied { read write } ... name=".lock" ... tcontext=unconfined_u:object_r:var_t:s0`. Labelling the three executables `bin_t` through `semanage fcontext` moves them to `unconfined_service_t`; after that no AVC denial mentions the daemon or extractor.
2. **Incomplete layout.** The CLI publish carries a layout copy of the daemon but not `SecondBrain.Server.staticwebassets.endpoints.json` or `wwwroot`, so the daemon failed at startup with "The static resources manifest file ... was not found" until the Server publish was merged in. `deploy/README.md` now states both requirements.
3. **Extractor cold start.** Socket activation worked on the first connection (`Extractor socket ready at /run/secondbrain/extractor.sock; activated: True`), but the service took 5.0 s from `Started` to "socket ready" while `ExtractorPing` gives up after 2 s, so the first two `/ready` calls answered 503 with "Extractor socket ping failed" and the extractor logged the two abandoned clients. The next call passed and the service stayed up (`NRestarts=0`).

### Results

| Check | Result |
| --- | --- |
| Socket activation | `secondbrain-extractor.socket` listening on `/run/secondbrain/extractor.sock`; the service started on the daemon's first ping and kept running; `/ready` reports "Extractor answered ping" |
| Egress enforcement | `IPAddressDeny=0.0.0.0/0 ::/0`, `IPAddressAllow=127.0.0.0/8 ::1/128 100.127.250.19/32` plus the template pins, `IPAccounting=yes`; `/ready` 200 with canary "Egress canary: Blocked. Egress canary state is current." while `provider:chat`, `provider:enrich` and `provider:embed` are ready through the loopback allowance; the unit's lifetime IP accounting stayed in the kilobytes |
| Access JWKS refresh | Not exercised: no Cloudflare Access team is configured and the `192.0.2.1` placeholder remains, so this half of the egress spike stays with the external-integration item |
| HTTPS on the tailnet address | Kestrel bound `100.67.195.36:7443` with the Let's Encrypt certificate (subject and SAN `fedora.tail9a7993.ts.net`, issuer Let's Encrypt `YE1`, valid until 2026-11-25). A request from the host's own tailnet address is dropped by the unit's ingress filter because that address is not allowed, which is the documented behaviour. With the drop-in allowing exactly `100.103.184.124/32`, the tailnet peer `iphone192.tail9a7993.ts.net` (iOS, `leahyjustin@icloud.com`, reached through the `mia` relay) loaded `https://fedora.tail9a7993.ts.net:7443/health` in Safari with normal certificate verification: the journal records three `HTTP/2 GET https://fedora.tail9a7993.ts.net:7443/health` responses with status 200 at 10:49:22, 10:49:53 and 10:49:56 local time, and the unit's ingress accounting rose to 27 packets. `/health` answers 200 with an empty body and no content type, which Safari presents as a download; the first attempt had failed only because the drop-in still named the operator's previous phone |
| `brain doctor` | Run as the `secondbrain` identity with a disposable file credential store, because only root and the daemon can read the 0600 configuration. Every local precondition passed: root modes and ownership, both listeners on assigned private or loopback interfaces with no public bind, the HTTPS certificate file, the key ring directory, Data Protection and HMAC material, the extractor ping, and the model bindings. The daemon half then passed through the policy-owned transport: the journal records `POST /auth/login` 200 at 10:42:37 and `GET /diagnostics` 200 at 10:42:38 local time, after one 401 caused by an empty password prompt in zsh |
| Native Linux Secret Service | Exercised earlier the same day against the retained Compose smoke daemon: `brain login` stored "SecondBrain API credential" (attributes `application=secondbrain`, `account=<origin>/<name>`), `keys list` and `providers test` authenticated through the stored entry, and the entry was removed afterwards. macOS Keychain remains |

Renewal reload and expired-certificate rejection were not exercised. Linux arm64 descriptor ABI, the Access and Tunnel integrations, the macOS Keychain store and the remote qualification run (issue #2) remain open. **M0 remains open** on those items.
