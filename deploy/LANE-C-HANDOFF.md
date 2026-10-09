# Lane C convergence handoff

## Delivered

- Item 2: `YamlConfigurationLoader` loads the Appendix B M0 sections, validates
  provider aliases/bindings/fallback locality, roots, private listeners/certificates,
  dimensions, null structures and secret references. Known later-milestone sections
  are accepted without binding to the frozen M0 options. `SECONDBRAIN_DATA_ROOT`
  overrides the file. Diagnostics contain field names and reasons, never source values.
- `ReloadingConfiguration` uses atomic validated snapshots, file replacement/write
  watching and SIGHUP. Invalid or restart-only changes preserve the current snapshot.
  Notifications are serialized; reentrant reloads cannot deliver stale snapshots last.
  `AddConfiguration` exposes `IOptionsMonitor<SecondBrainOptions>` and `ISecretResolver`.
  `YamlConfigurationLoader`, `ReloadingConfiguration` and `SecretResolver` live in
  `SecondBrain.Infrastructure.Configuration`; `ISecretResolver` stays in `SecondBrain.Core.Configuration`.
- Item 3a: root provisioning, idempotent templates/mode repair, dry-run, overridable
  directories and numeric identities; non-blocking lifetime flock; startup root
  ownership/mode/separation checks and distinct non-root daemon/sync identities.
  `FileKeyRing` persists ULID HMAC versions atomically with fsync, retains all versions
  for constant-time verification, initializes ASP.NET Data Protection and uses the
  four purposes from the plan. Routine rotation does not revoke credentials.
  `FileKeyRing` and `IKeyRing` live in `SecondBrain.Infrastructure.Security`; `IHmacKeyRing`,
  `IAccountEpochRevoker` and `KeyRingPurposes` stay in `SecondBrain.Core.Security`.
  The lifetime lock (`DataRootLock`), root checks (`RootSecurityValidator`) and Unix helpers
  (`UnixPath`, `UnixSecurity`, `UnixAccounts`) also live in `SecondBrain.Infrastructure.Security`.
- Item 14: pinned serving images, separate extractor/tunnel identities, restricted
  UID-specific egress namespace, read-only serving filesystems/secrets, capability
  removal, resource limits, systemd units/tmpfiles, deployment templates and G1 CI.
  The egress namespace helper alone needs NET_ADMIN; serving processes have none.
- Extractor: bounded versioned JSON framing, ping, SCM_RIGHTS, a bounded descriptor
  read probe, descriptor cleanup/CLOEXEC, scrubbed environment and systemd fd3 adoption.
- Item 15: the `brain` command tree, JSON envelopes, exit codes 0–4, provision/init
  lane-C work, key rotation, local doctor, credential storage and installed daemon
  launcher. Other lanes' commands return explicit integration-required failures.

## Integration bindings

The frozen `ProviderOptions.ApiKeyReference` remains an unresolved `${NAME}` string.
Lane B must resolve it with `ISecretResolver.Resolve` when preparing credentials;
do not log/serialize the result. Environment precedes the named file under
`SECONDBRAIN_SECRETS_DIRECTORY` (default `/etc/secondbrain/secrets`). The CLI also
accepts the older `SECONDBRAIN_SECRETS_DIR` alias. Lane B owns DNS pinning and the
policy-owned provider transport; lane D owns listener/certificate and Access handling.
Both can subscribe to the standard options monitor registered in composition.

Bind the following `CliServices` adapters:

| Interface | Owner and responsibility |
| --- | --- |
| `IInitializationCommands` | A/D: both stores, initial admin credential/password, password reset |
| `ICredentialCommands` | D: API key creation/listing/revocation |
| `ISessionCommands` | D: session listing/revocation/logout-all |
| `ILoginCommands` | D: acquire the login API key, which the CLI stores without printing |
| `IProviderCommands` | B: provider listing and tests through policy transport |
| `IDoctorExtension` | A/B/D: stores/migrations, provider capabilities, canary and Access refresh |
| `IAccountEpochRevoker` | A/D: durable epoch bump for explicit `--revoke-all` |
| `IDaemonCommands` | Optional replacement for installed child daemon launch |

`init` creates the key ring under the lock before invoking A/D's initialization
adapter. Unbound store/account work fails with exit 4; no initial admin secret or
password is fabricated. Rotation with `--revoke-all` refuses to mutate without the
epoch adapter. If the epoch transaction fails after rotation, the command reports
failure and the added HMAC version remains valid. Bind retries/idempotency to the
durable epoch transaction at convergence.

No frozen contract, Program.cs, package pin or lock-file change is requested.
The CLI adds an ASP.NET framework reference for the existing Data Protection
implementation and disables package pruning to retain the locked dependency graph.

## Verification and remaining qualification

Run the warnings-as-errors solution build and both complete Core/Deploy test suites.
The complete local suites pass 63 Core and 98 Deploy tests, with no skips; the
solution build reports zero warnings and errors. The Compose G1 run provisions
the roots and serves as UID1654 with the offline extractor as UID1655, checks
HTTP200, read-only rootfs, zero capabilities and a blocked public canary.
`deploy/tests/compose-smoke.sh` is the G1 executable check: provision, serve, UID,
health, rootfs, capabilities and public canary. Structural unit tests run everywhere;
`systemd-analyze verify` was also exercised in a disposable Linux container with
installed executables substituted for syntax-only verification.

Descriptor passing and independent kernel CLOEXEC readback were exercised on
macOS arm64 and Linux amd64, including reading an unlinked passed file. Live
systemd socket activation, Linux arm64 native ABI, target-host cgroup-BPF enforcement
with successful DNS/Access/vLLM calls, and HTTPS from a real tailnet peer remain
qualification work. See `deploy/README.md` for the two spike procedures.

Native Keychain/Secret Service and Linux service-account creation were not exercised
against the developer's accounts; the 0600 fallback and provisioning behavior use
isolated tests. Real extraction is the explicit M1 stub. Operational allowlist and
Cloudflare/Tailscale values are administrator-supplied; shipped placeholders fail closed.
