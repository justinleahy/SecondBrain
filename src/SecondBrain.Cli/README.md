# brain

`brain` uses System.CommandLine and emits a JSON result envelope with `--json` on every command. Exit statuses are `0` success, `1` error, `2` usage, `3` daemon unreachable, and `4` failed precondition. Errors in JSON mode use stdout; text errors use stderr.

Implemented local commands:

```
brain init --provision [--dry-run] [--deployment systemd|compose|none]
brain init [data-root] [--reset-password]
brain doctor [--extractor-socket /run/secondbrain/extractor.sock]
brain maintenance rotate-keys [--revoke-all]
brain serve
```

The provisioning command runs as root. It supports `--data-root`, `--incoming-root`, `--config-dir`, `--runtime-dir`, `--units-dir`, `--tmpfiles-dir`, `--compose-dir`, `--templates`, `--daemon-uid`, `--daemon-gid`, `--extractor-uid`, and `--sync-uid`. `--skip-users` uses existing numeric container identities. Templates are read from the installed `deploy` tree; repeat runs preserve existing configuration and deployment files while repairing directory ownership and modes. Directories need traversal permissions: secrets and keyring directories are `0700`, config and secret files `0600`, incoming is `0750` owned by the sync UID with the daemon GID, and the shared runtime directory is `02770`.

Installed Compose projects omit repository-relative `build` sections and use prebuilt images. Build the images from the repository's `deploy/compose.yaml` before installing, or replace the image tags with published images. Provisioning also installs `.env.example` and the optional Cloudflare Tunnel configuration template; fill in the host paths and private address before `docker compose up`.

Configuration uses `--config` or `SECONDBRAIN_CONFIG`, then `/etc/secondbrain/config.yaml`. `--data-root` or `SECONDBRAIN_DATA_ROOT` overrides the configured data root. `--secrets-dir` or `SECONDBRAIN_SECRETS_DIRECTORY` selects the external secrets directory (`SECONDBRAIN_SECRETS_DIR` remains an alias). Numeric identity environment variables are `SECONDBRAIN_DAEMON_UID`, `SECONDBRAIN_DAEMON_GID`, and `SECONDBRAIN_SYNC_UID`. `serve` validates root security, checks lock availability, then launches the installed daemon, which retains the lock. `SECONDBRAIN_SERVER_PATH` may select a daemon executable or `.dll`; the latter is launched with `dotnet`. Child diagnostic logs go to stderr so JSON stdout remains parseable.

`login <url> [--name --scopes]`, `keys create --scopes|list|revoke <id>`, `providers list|test [name]`, and `sessions list|revoke <id>|revoke-all` are wired to explicit convergence adapters in `CliServices`. Unbound commands report exit `4` and `integration-required`, without simulating success. `init` validates roots and creates the key ring under the exclusive lock, then invokes the store/account adapter. Initial configuration leaves provider/model bindings unset until the administrator supplies them.

The convergence interfaces in `Commands/CliContracts.cs` are `IInitializationCommands` (A/D), `ICredentialCommands`, `ISessionCommands`, `ILoginCommands` (D), `IProviderCommands` (B), `IDoctorExtension` (A/B/D), and `IDaemonCommands` (daemon launch replacement). `IAccountEpochRevoker` from Core must be bound before `rotate-keys --revoke-all` runs. A failed revocation is reported as a failure. Local doctor checks root ownership/modes, configured versus actual host interfaces and exposure, certificate-file presence, key-ring modes and HMAC integrity, required model bindings, Access settings, and a framed extractor ping; daemon-side diagnostics come from `IDoctorExtension`.

Login credentials are saved to macOS Keychain or Linux Secret Service (`secret-tool`). When unavailable, Unix uses an atomic `0600` file under a `0700` directory at `~/.config/secondbrain/credentials`; Windows requires an OS store adapter. Login output contains only origin, name, scopes and public credential id. `LoginCredential.ApiKey` is excluded from serialization and its string representation. Credential-creation adapters may reveal a newly issued key once; listing and revocation adapters return public metadata only.
