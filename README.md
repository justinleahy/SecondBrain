<img src="assets/icon.svg" width="64" height="64" alt="">

# SecondBrain

A personal knowledge daemon built on .NET 10 and Blazor Interactive Server. M0 now connects validated YAML configuration, two crash-safe SQLite stores, provider-neutral model roles, privacy transport, credentials, browser sessions, admission limits, source registration, deployment, and the `brain` CLI. Ingestion, extraction beyond ping/descriptor probes, search, and chat land in later milestones.

The local mock and hardened Docker gates are automated. M0 remains open until the live vLLM qualification passes once; the server has not been set up yet. Target-host systemd egress and tailnet HTTPS spike procedures are in [deploy/README.md](deploy/README.md).

## Build and test

Install .NET SDK **10.0.105** (`global.json` permits the latest patch in its feature band). Run from this checkout:

```sh
dotnet restore --locked-mode
dotnet build -warnaserror --no-restore
dotnet test --no-build --no-restore
```

The solution is `SecondBrain.slnx`. Package versions are central in `Directory.Packages.props`; every project checks in `packages.lock.json`. After changing project references, regenerate locks with `dotnet restore --force-evaluate`, then verify locked restore. NuGet access is required until the package cache is populated. No package versions changed during convergence.

Self-contained publishes use runtime **10.0.12**. `Directory.Build.targets` pins the implicit Blazor assets package to the same version. Ordinary development builds use the installed shared runtime. Tests use xUnit 2.

CI restores locked dependencies, builds with warnings as errors, runs every deterministic test project, publishes self-contained `linux-x64` Server/CLI/Extractor binaries, builds the image, and executes the Compose gate. Tests with `Category=Qualification` run only in the manually dispatched live-provider workflow; an unset vLLM URL makes the local qualification test a no-op, which does not count as live qualification.

```sh
dotnet test tests/SecondBrain.Server.Tests --no-restore
dotnet test --no-build --no-restore --filter 'Category!=Qualification'
```

## Layout and integration

| Project | Responsibility |
| --- | --- |
| `SecondBrain.Core` | Domain model, application use cases, policies and ports; no project references; no I/O |
| `SecondBrain.Infrastructure` | Platform adapters shared by the daemon and `brain`: YAML configuration loading and reload, secrets, Unix helpers, the data-root lock, the file key ring, Argon2, DNS/TCP defaults, the extractor ping client and host file-system checks |
| `SecondBrain.Storage` | State/index stores and their handles, migrations, initialization, publication, journal recovery, and all SQL (auth, sources, password policy, account recovery) |
| `SecondBrain.Providers.OpenAICompatible` | Reviewed model capabilities, role registry and policy-owned HTTP transport |
| `SecondBrain.Server` | Composition, HTTP/HTTPS, Access assertions, account/API keys/sessions, limits, sources, diagnostics and mounted Razor shell |
| `SecondBrain.Cli` | `brain` commands; local storage/account bootstrap and daemon HTTP adapters |
| `SecondBrain.Extractor` | Isolated Unix-socket ping and descriptor-probe stub |
| `tests/SecondBrain.MockProvider` | Explicit `mock-chat` and four-dimensional `mock-embed` test models |
| `tests/SecondBrain.Architecture.Tests` | Dependency and layering rules for the projects above (see [docs/build/clean-architecture-plan.md](docs/build/clean-architecture-plan.md)) |

CLI uses Core, Infrastructure and Storage for credential generation, calibrated password hashing and the local account SQL; its Server reference is layout-only, so the daemon is copied beside `brain`. HTTP diagnostic routes are `/providers`, `/providers/test` and `/diagnostics` (also `/v1` aliases), all requiring `admin`. `/ready` checks migrations, both stores, the lifetime lock, canary, every required provider role, and a real extractor ping. Healthy provider observations expire at five minutes and are invalidated when their accepted configuration changes.

Configuration comes from `--config`/`SECONDBRAIN_CONFIG`, normally `/etc/secondbrain/config.yaml`; `SECONDBRAIN_DATA_ROOT` overrides the YAML root. Provider endpoints, model IDs, listeners, Hosts and Origins must be explicit. The model catalog contains only reviewed registrations for concrete `(provider, model)` pairs, with no unqualified model-name entries or built-in defaults. A binding without a reviewed registration needs explicit `limits`, and a chat model also needs a reviewed `capabilities: { tools: true }` declaration (optionally `streaming` and `structured_output`); capabilities are never inferred from a provider alias, familiar model name, endpoint, or `openai_compatible` adapter kind. `extractor.socket_path` defaults to `/run/secondbrain/extractor.sock` and can be overridden with `SECONDBRAIN_EXTRACTOR_SOCKET`. Secret fields contain `${NAME}` references resolved from the environment or `SECONDBRAIN_SECRETS_DIRECTORY`, never literal secrets. Invalid reloads keep the previous validated snapshot.

Credential issuance, revocation and logout-all revalidate the actor's generation, current account epoch, revocation, expiry, kind, scopes and required session step-up in the same writer transaction as the mutation. Admin API keys retain credential-management permission without session step-up; self-logout requires a current session but no step-up. Provider policy publication shares an atomic boundary with final validation and socket-write initiation; writes already admitted may finish, and their asynchronous completion is awaited outside that boundary.

Mutation preparation validates the revision, captures the current generation in a durable expected fence, and reserves the document in the same transaction. Apply and finalize recheck revision and generation. Publication and reprocessing respect that reservation, and legacy recovery never regresses a newer fence. Nonregular entries displaced by an atomic exchange are restored or quarantined as conflicts, never followed or deleted; no content revision is invented without a regular-file hash.

## Disposable local Docker run

Docker with Compose and Linux firewall support is the easiest complete local run, including on macOS. This command creates temporary roots, a temporary mock-backed configuration and separate non-root daemon/extractor/sync identities, runs the entire flow, and tears down its containers:

```sh
SECONDBRAIN_PRIVATE_ADDRESS=127.0.0.1 bash deploy/tests/compose-smoke.sh
```

The executable script records the exact provisioning/startup commands and assertions. Its fixture is [config.compose-smoke.yaml](deploy/tests/config.compose-smoke.yaml), with `local_only: true`, trusted loopback mock roles, and an enabled canary that the namespace firewall blocks. It runs:

```sh
# Inside the isolated daemon container, with its real YAML and UID1654:
dotnet /app/cli/brain.dll init --json
dotnet /app/cli/brain.dll serve
curl --fail http://127.0.0.1:7171/ready

# Each following command uses --url http://127.0.0.1:7171
# and --credential-name compose-smoke before the subcommand:
dotnet /app/cli/brain.dll --url http://127.0.0.1:7171 --credential-name compose-smoke login http://127.0.0.1:7171 --name compose-smoke --scopes admin --json
dotnet /app/cli/brain.dll --url http://127.0.0.1:7171 --credential-name compose-smoke keys create --scopes read --json
dotnet /app/cli/brain.dll --url http://127.0.0.1:7171 --credential-name compose-smoke keys list --json
dotnet /app/cli/brain.dll --url http://127.0.0.1:7171 --credential-name compose-smoke keys revoke "$created_key_id" --json
dotnet /app/cli/brain.dll --url http://127.0.0.1:7171 --credential-name compose-smoke providers list --json
dotnet /app/cli/brain.dll --url http://127.0.0.1:7171 --credential-name compose-smoke providers test mock --json
dotnet /app/cli/brain.dll --url http://127.0.0.1:7171 --credential-name compose-smoke doctor --json
```

The script also tests the Razor login form, signed-in shell, session list/revoke/logout-all, revoked-key rejection, read-only root filesystem, zero serving capabilities and blocked public egress. Passwords and new API keys are consumed privately. CLI login/administration uses a disposable `SECONDBRAIN_CREDENTIAL_STORE=file` and `SECONDBRAIN_CREDENTIAL_DIRECTORY=/tmp/credentials`; no developer Keychain or Secret Service account is changed. The extractor socket uses a Linux named volume because Docker Desktop host-file sharing cannot reliably preserve Unix socket modes.

For browser inspection, `SECONDBRAIN_SMOKE_KEEP=1` retains a successful stack; its output includes the Compose project and temporary root. Stop it with the same `--project-name` and both Compose files after review. See [deployment instructions](deploy/README.md) for production provisioning, operator-supplied allowlists and certificates.

## Running directly on Linux or macOS

The daemon requires a **different sync UID** owning the incoming root, with the daemon group allowed to read it. Creating that isolated ownership needs administrator privileges. The Docker flow above exercises this automatically; a direct host run must prepare the same boundary. The following Bash commands use only temporary roots and the explicit mock. Run the ownership step with sudo, and choose an unused non-root sync UID or an existing dedicated sync account.

```bash
sb_local_root=$(mktemp -d /tmp/secondbrain-local.XXXXXX)
sb_local_root=$(cd "$sb_local_root" && pwd -P)  # Canonical /private/tmp on macOS.
chmod 711 "$sb_local_root"
export SECONDBRAIN_DAEMON_UID=$(id -u)
export SECONDBRAIN_DAEMON_GID=$(id -g)
export SECONDBRAIN_SYNC_UID=1656
mkdir -m 700 "$sb_local_root/data" "$sb_local_root/secrets" "$sb_local_root/run"
mkdir -m 750 "$sb_local_root/incoming"
sudo chown "$SECONDBRAIN_SYNC_UID:$SECONDBRAIN_DAEMON_GID" "$sb_local_root/incoming"
export SECONDBRAIN_CONFIG="$sb_local_root/config.yaml"
export SECONDBRAIN_SECRETS_DIRECTORY="$sb_local_root/secrets"
export SECONDBRAIN_SERVER_PATH="$PWD/src/SecondBrain.Server/bin/Debug/net10.0/SecondBrain.Server.dll"
export SECONDBRAIN_CREDENTIAL_STORE=file
export SECONDBRAIN_CREDENTIAL_DIRECTORY="$sb_local_root/credentials"
cat > "$SECONDBRAIN_CONFIG" <<YAML
data_root: $sb_local_root/data
server:
  listeners: [{scheme: http, bind: 127.0.0.1, port: 7171}]
  hosts: [127.0.0.1, localhost]
  origins: [http://127.0.0.1:7171]
providers:
  mock: {adapter: openai_compatible, base_url: http://127.0.0.1:8181/v1, trusted: true}
models:
  chat: {provider: mock, model: mock-chat, capabilities: {tools: true, streaming: true}, limits: {context_tokens: 8192, max_output_tokens: 1024}}
  enrich: {provider: mock, model: mock-chat, capabilities: {tools: true, streaming: true}, limits: {context_tokens: 8192, max_output_tokens: 1024}}
  embed: {provider: mock, model: mock-embed, limits: {embed_dimensions: 4, embed_max_input_tokens: 8192, embed_batch_max: 32}}
privacy:
  local_only: true
  trusted_services: [http://127.0.0.1:8181]
  egress_canary: false
sources:
  incoming_root: $sb_local_root/incoming
  allowed_roots: [$sb_local_root/incoming]
extractor:
  socket_path: $sb_local_root/run/extractor.sock
limits:
  global: {disk_low_water_gb: 0}
YAML
chmod 600 "$SECONDBRAIN_CONFIG"
dotnet tests/SecondBrain.MockProvider/bin/Debug/net10.0/SecondBrain.MockProvider.dll --urls http://127.0.0.1:8181 > "$sb_local_root/mock.log" 2>&1 &
sb_mock_pid=$!
dotnet src/SecondBrain.Extractor/bin/Debug/net10.0/SecondBrain.Extractor.dll --socket "$sb_local_root/run/extractor.sock" > "$sb_local_root/extractor.log" 2>&1 &
sb_extractor_pid=$!
read -r -s -p 'Temporary account password: ' SECONDBRAIN_BOOTSTRAP_PASSWORD
printf '\n'
export SECONDBRAIN_BOOTSTRAP_PASSWORD
export SECONDBRAIN_PASSWORD="$SECONDBRAIN_BOOTSTRAP_PASSWORD"
brain() { dotnet src/SecondBrain.Cli/bin/Debug/net10.0/brain.dll "$@"; }
brain init --json   # Save the admin key now; repeated init never prints it again.
unset SECONDBRAIN_BOOTSTRAP_PASSWORD
dotnet src/SecondBrain.Cli/bin/Debug/net10.0/brain.dll serve > "$sb_local_root/daemon.log" 2>&1 &
sb_daemon_pid=$!
# Retry while the configured stores/provider/canary/socket start.
curl --fail --retry 20 --retry-all-errors --retry-delay 1 http://127.0.0.1:7171/ready
brain login http://127.0.0.1:7171 --name local-admin --scopes admin --json
created_key_id=$(brain --credential-name local-admin keys create --scopes read --json | python3 -c 'import json,sys; print(json.load(sys.stdin)["data"]["id"])')
brain --credential-name local-admin keys list --json
brain --credential-name local-admin keys revoke "$created_key_id" --json
brain --credential-name local-admin providers list --json
brain --credential-name local-admin providers test mock --json
brain --credential-name local-admin doctor --json
brain sessions list --json
# Open http://127.0.0.1:7171/login in a browser and sign in with the account password.
# Stop every process before reset, rotation or removing the roots.
kill "$sb_daemon_pid" "$sb_extractor_pid" "$sb_mock_pid"
wait "$sb_daemon_pid" || true
unset SECONDBRAIN_PASSWORD
```

This host-only example disables the canary explicitly; doctor reports it as disabled, which does **not** prove a firewall is installed. The hardened Compose test keeps the canary enabled and verifies a blocked result. Direct macOS root preparation could not be executed here because passwordless sudo is unavailable; the complete Docker flow is the verified local end-to-end path.

`brain init --reset-password` resets the account and atomically invalidates all old keys/sessions. `brain maintenance rotate-keys` retains old HMAC versions; `--revoke-all` also advances the account epoch. Local maintenance requires the daemon to be stopped because both retain the same exclusive root lock. Calibrated Argon2 parameters are persisted with the account and reused by the daemon.

Login defaults to `read`; request `--scopes admin` for administrative CLI commands. Match login `--name` with later `--credential-name` (both otherwise default to the machine name). Session commands prompt for the account password, use a temporary browser cookie plus fresh CSRF tokens, and close that temporary session. `sessions revoke` and `revoke-all` reuse that password for the step-up these actions require. `keys list` and `sessions list` follow every result page, or fail without a partial result; persistent browser pairing is M2. For an Access-protected public origin, provide a valid `SECONDBRAIN_ACCESS_ASSERTION` in addition to the SecondBrain credential. Token acquisition and live Access configuration require the operator's setup.

Every command supports `--json`. Exit codes are `0` success, `1` error, `2` usage, `3` daemon unreachable and `4` failed precondition. Native credential storage uses macOS Keychain/Linux Secret Service; a private 0600 file under a 0700 directory is the Unix fallback. Keys appear only at initial/new creation, never in login output or list/revoke responses.

## Qualification and milestone evidence

[G1–G12 evidence and known gaps](docs/build/M0-convergence-report.md) records actual test names, command results and remaining operational qualifications. To run the live readiness gate once a vLLM binding exists:

```sh
export SECONDBRAIN_QUAL_VLLM_URL=http://YOUR_VLLM_HOST:8000/v1
export SECONDBRAIN_QUAL_VLLM_CHAT_MODEL=YOUR_REVIEWED_NATIVE_TOOLS_MODEL
export SECONDBRAIN_QUAL_VLLM_EMBED_MODEL=YOUR_EMBEDDING_MODEL
export SECONDBRAIN_QUAL_VLLM_DIMENSIONS=YOUR_EMBEDDING_DIMENSIONS
# Optional enrich model and reviewed context/output/input token limits are documented in qualification.yml.
dotnet test tests/SecondBrain.Server.Tests --no-restore --filter 'Category=Qualification'
```

The qualification test writes the supplied models, limits and `models.<role>.capabilities` declarations to YAML, loads them through the production configuration path, and probes all configured roles through production transport and `/ready`. A passing mock test or unset-URL no-op is not a substitute for this live run.

[The M0 plan](docs/build/M0-foundations.md) defines the guarantees. [The M1 ingest and search draft](docs/build/M1-ingest-and-search.md) maps upcoming work and planned acceptance tests to the hooks delivered by M0.

## Contributing

Keep Core independent of implementation projects. Make shared contract/composition changes together with their consumers, maintain the common scope matrix, route provider traffic through the privacy-owned transport, and regenerate dependency locks after reference changes. Run the full suite and relevant gates before committing. The temporary parallel-lane freeze has ended.

Created in [T3 Code](https://t3.codes).
