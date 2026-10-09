# Lane D convergence contract

All implementation is wired by the three existing composition calls through one startup filter. Program.cs, shared contracts, package pins and lockfiles are unchanged. Production storage/key-ring/config implementations arrive from lanes A/C; liveness does not resolve those dependencies.

## Storage and init

`SecondBrain.Core.Auth.IAdminCredentialFactory.CreateInitializationRecords(password, accountEpoch = 1)` returns the admin key once (`AdminCredential.Plaintext`) plus `CredentialRecord` and `AccountRecord` for Appendix A insertion. `CredentialFactory` implements this contract and also exposes separate account/key creation. The secret must never be logged or printed again. Register the deployment host's calibrated `PasswordParameters` before resolving `CredentialFactory`. See ../Auth/Calibration.md.

`IAuthRepository` (in `SecondBrain.Core.Auth`, implemented by `AuthRepository` in `SecondBrain.Storage.Auth`) and `ISourceRepository` (in `SecondBrain.Core.Sources`, implemented by `SqliteSourceRepository` in `SecondBrain.Storage.Sources`) use only `IStateStore` (in `SecondBrain.Storage`) leases/queued writers, Dapper, and existing Appendix A columns. Session security flags/device summaries are serialized in credentials.device; no schema additions are needed. Initialize meta.account_epoch to a positive integer. Account reset, restore, and revoke-all key rotation must atomically increment it.

`ICredentialAuthority.IsCurrentAsync(id, generation, accountEpoch)` is the fence for future jobs, cursors, turns, and transports. REST checks and session idle updates use writer compare-and-set; rehash/rotation cannot overwrite a concurrent reset/revocation. Interactive circuits hold a circuit permit, revalidate every 30 seconds, and reject stale authority before inbound activity dispatch. Existing circuits lose authentication on invalidation; stale inbound activity terminates the circuit through an exception. No session secret is included in public response DTOs.

## Readiness

Register `SecondBrain.Server.Http.IReadinessContributor` with a stable `Name` and `ValueTask<ReadinessStatus> CheckAsync(CancellationToken)`. Required names: `stores`, `migrations`, `lock`, `canary`, `extractor`, `provider:chat`, `provider:enrich`, `provider:embed`, and `provider:rerank` when configured. HTTP ships real store-open/canary adapters; other lanes register their contributors. Later registrations with the same name replace the default. A missing or failing contributor returns 503; provider results require `CheckedAt` from a successful observation within five minutes. Components are included in the response. Tests supply fake contributors and a local JWKS service.

## CLI HTTP calls

Both the paths below and their /v1-prefixed aliases are mapped.

| Methods and path | Scope and additional authority |
| --- | --- |
| GET /keys | admin |
| POST /keys | admin; browser session also requires step-up |
| DELETE /keys/{id} | admin; browser session also requires step-up |
| GET /sources | admin |
| POST /sources | admin; browser session also requires step-up; stores only, schedules nothing |
| POST /auth/login | anonymous password login; exact Origin always required |
| GET /auth/antiforgery | anonymous/cookie session; returns token and header name |
| GET /auth/me | browser session |
| POST /auth/logout, /auth/logout-all | browser session |
| GET /auth/sessions | browser session |
| DELETE /auth/sessions/{id} | browser session |
| POST /auth/step-up | browser session; rotates the session id |
| GET /health, /ready | anonymous on private host; public host still requires Access |
| GET /v1/openapi.json | anonymous on private host; public host still requires Access |

JSON key creation: `{ "name": "client", "scopes": ["read"], "expiresAt": null }`; returns `{ "id", "key", "scopes", "expiresAt" }` once, with no-store caching. Source creation schemas are in OpenAPI. Browser JSON clients obtain a fresh token from /auth/antiforgery after login or step-up and send it in X-CSRF-TOKEN for state changes. API keys can manage credentials/sources without interactive step-up; an admin grant does not imply read/write/infer. Cookie-only sensitive operations require step-up; paired CLI sessions/pairing and passkey enrollment remain outside M0.

## Admission integration

`IAdmissionController.TryReserve(credentialId, AdmissionPolicy)` (both in `SecondBrain.Core.Limits`; the HTTP `AdmissionMiddleware` stays in `SecondBrain.Server.Limits`) provides an idempotently disposable reservation or typed rejection. `AdmissionPolicy` endpoint metadata supplies upload/search/turn/stream/circuit resource, queued jobs, admitted bytes, ingestion and optional request content-length bytes. Root middleware reserves before endpoint execution and releases in finally; a future asynchronous job must retain/transfer its reservation for the entire admitted work lifetime. Counters currently cover active reservations; persistent queued jobs and corpus bytes must be restored/seeded at later ingestion startup. No ingestion/search/chat endpoints are introduced in M0.

## Additional stable problem constants requested

The frozen `ProblemTypes` has the ten M0 named policy conditions. Add constants for the adjacent URIs already emitted: authentication-required, step-up-required, antiforgery-rejected, invalid-request, source-path-rejected, not-found, method-not-allowed, conflict, request-too-large, unsupported-media-type, and internal-error. No existing contract change is needed otherwise.

## Deployment convergence

The configuration loader must reject missing production listeners/hosts/origins and validate reloads; standalone frozen scaffold liveness still accepts loopback/local host defaults. Cloudflare Access transport additionally requires the exact HTTPS team certificate URL in privacy.control_plane_egress. Kestrel supports PFX/P12 files and PEM certificates with an embedded key or sibling .key. Public and wildcard bind addresses are rejected. Production readiness becomes green only after other lanes register migrations/lock/provider/extractor contributors.

Browser verification used a temporary Kestrel host with the same composition and test store/key ring: form login ->303 ->signed-in page, framework JS ->200, live Blazor WebSocket, logout-all ->204, /auth/me ->401, and the signed-in view cleared on circuit revalidation. The temporary harness is outside the repository.

## Final lane verification

`dotnet build -warnaserror --no-restore --disable-build-servers`: zero warnings/errors. Server xUnit 2 suite: 125/125 passed, zero skipped (36 HTTP, 10 Access, 23 Sources, 15 Limits, 17 Auth, 14 Circuit, 4 race, 4 infrastructure, 2 scaffold smoke). All changes are lane-owned; no shared files or package locks changed.
