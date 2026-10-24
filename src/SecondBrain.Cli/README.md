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

`login <url> [--name --scopes]`, `keys create --scopes|list|revoke <id>`, `providers list|test [name]`, and `sessions list|revoke <id>|revoke-all` use the daemon's HTTP endpoints. Administrative commands select the daemon through `--url`/`SECONDBRAIN_URL`, or the first private configured origin, and read the credential stored under `--credential-name`/`SECONDBRAIN_CREDENTIAL_NAME` (default: the machine name). API keys can also be supplied through `SECONDBRAIN_API_KEY`. Key, provider and daemon diagnostic commands require an `admin` scope; login defaults to `read`, so use `brain login <url> --name local-admin --scopes admin,read` before administration. Text results show public metadata, and `--json` returns the same data in the result envelope.

`init` validates roots and creates the key ring under the exclusive lock, migrates both stores, and atomically seeds the account, calibrated Argon2id policy and initial admin verifier. Its newly generated admin key is printed once in text mode, or once as `data.adminKey` in JSON mode. Repeating init preserves the account and never redisplays or replaces the key. `--reset-password` atomically replaces the password and policy and advances the account epoch, invalidating existing sessions and API keys. `maintenance rotate-keys --revoke-all` uses the same durable epoch transaction. These local commands require the daemon to be stopped so they can acquire its data-root lock.

Passwords are read without console echo. Noninteractive init/reset can use a short-lived `SECONDBRAIN_BOOTSTRAP_PASSWORD`; login and session commands use `SECONDBRAIN_PASSWORD`, or one password line on stdin. Login creates a temporary browser session through `/auth/login`, obtains antiforgery tokens, completes `/auth/step-up`, issues the requested API key through `/keys`, stores it, and closes the browser session. Login output includes only public credential metadata. `sessions` commands authenticate with the account password in an ephemeral browser session and use the existing `/auth/sessions` and `/auth/logout-all` routes. `sessions revoke` and `revoke-all` also complete `/auth/step-up` with the same password, then refresh the antiforgery token before revoking. `keys list` and `sessions list` follow every `Link: rel="next"` page. They follow a link only if it stays on the daemon origin and listing path, so credentials are never sent elsewhere. A rejected, repeated or over-1,000-page link chain fails without a partial result. Session administration does not grant browser-session authority to API keys. The current checkout adds persisted browser-paired CLI sessions; see [M2 progress](../../docs/build/M2-cli-mcp-progress.md). Pairing and assistant commands are deployed to the reference service; see the [M2 deployment record](../../docs/build/M2-complete-deployment.md). The public origin still requires Cloudflare Access; `SECONDBRAIN_ACCESS_ASSERTION` supplies the `Cf-Access-Jwt-Assertion` header when an assertion has been obtained externally.

The injectable interfaces in `Commands/CliContracts.cs` are `IInitializationCommands`, `ICredentialCommands`, `ISessionCommands`, `ILoginCommands`, `IProviderCommands`, `IDoctorExtension`, `IDaemonCommands`, and `IKnowledgeCommands`. `CliServices` supplies production bindings by default; tests can select `new CliServices(bindDefaults: false)` when explicitly exercising unavailable integration. The local store/account adapter uses Core, Infrastructure and Storage; brain references Server only for layout, so the daemon is copied beside it, and compiles against none of its types. A failed epoch transaction after rotation is reported as a failure. Local doctor checks root ownership/modes, configured versus actual host interfaces and exposure, certificate-file presence, key-ring modes and HMAC integrity, required model bindings, Access settings, and a framed extractor ping. `/diagnostics` adds store/migration state, the held root lock, provider capabilities and current reachability, trusted-service policy, canary state, extractor readiness, and Access signing-key refresh. The socket defaults to `extractor.socket_path` in YAML, with `--extractor-socket` and `SECONDBRAIN_EXTRACTOR_SOCKET` overrides.

Login credentials are saved to macOS Keychain or Linux Secret Service (`secret-tool`). When unavailable, Unix uses an atomic `0600` file under a `0700` directory at `~/.config/secondbrain/credentials`; Windows requires an OS store adapter. Login output contains only origin, name, scopes and public credential id. `LoginCredential.ApiKey` is excluded from serialization and its string representation. Credential-creation adapters may reveal a newly issued key once; listing and revocation adapters return public metadata only.

For an isolated local check, set `SECONDBRAIN_CREDENTIAL_STORE=file` and `SECONDBRAIN_CREDENTIAL_DIRECTORY=<temporary-directory>` to avoid writing the host's Keychain/Secret Service. The repository README and `deploy/tests/compose-smoke.sh` contain the complete mock-provider init/serve/login/keys/doctor sequence with temporary deployment roots and the required distinct Unix identities.

Capture and retrieval use the configured daemon origin and a stored key. Log in with `--scopes read,write` to capture and retrieve; these operations do not require `admin` or `infer`.

```sh
printf '%s' 'A note to remember' | brain add - --title 'My note' --tags personal --json
brain add ./article.pdf --title 'Article' --wait 5 --idempotency-key article-001 --json
brain search 'what I remember' --mode keyword --type note --tag personal --json
brain query --type note --where 'title=My note' --sort updated_at --limit 50 --json
```

`add -` captures text from standard input (bounded to 1,000,000 characters). File uploads stream multipart content and reject input above 50 MiB. M1 extraction supports Markdown, text, PDF with a text layer, HTML and DOCX; OCR and transcript/calendar formats remain unavailable. `--props` accepts a JSON object; `--tags` accepts comma-separated tags. `--wait` is bounded to 30 seconds. Save the response's document and job ids and inspect its keyword/vector status; a successful capture can have pending vectors or failed extraction. Search reports explicit degraded reasons. Query reports `next_cursor`, `truncated` and `coverage`; pass a returned cursor with `--cursor` and the same query to continue. `--from` and `--to` apply a half-open occurred-time window. Repeat `--where field=value` for equality predicates over supported indexed fields.

Every command retains the JSON envelope and exit statuses: 0 success, 1 command/daemon error, 2 usage, 3 daemon unreachable, 4 failed precondition or daemon rejection. Standard text output also includes the complete result payload.

## M2 assistant commands

These commands require the matching server build. The complete M2 server and CLI were [deployed to the reference service](../../docs/build/M2-complete-deployment.md) on 2026-10-24.

```sh
brain login http://YOUR-DAEMON:7171 --name terminal --scopes read,infer
brain --url http://YOUR-DAEMON:7171 --credential-name terminal ask 'What did I decide about storage?'
brain --url http://YOUR-DAEMON:7171 --credential-name terminal ask 'Compare my storage notes' --deep --json
brain login http://YOUR-DAEMON:7171 --name terminal --interactive
brain --url http://YOUR-DAEMON:7171 --credential-name terminal chat
brain --url http://YOUR-DAEMON:7171 --credential-name terminal chat CONVERSATION-ID
brain --url http://YOUR-DAEMON:7171 --credential-name terminal pending list --json
brain --url http://YOUR-DAEMON:7171 --credential-name terminal pending approve PROPOSAL-ID --json
```

`ask` returns one read-only answer with its verified citations. `--deep` allows up to five searches, subject to the configured turn limits. Partial and failed answers retain their available content and return exit 1. `chat` streams provisional text, then shows verified citations and any corrections or warnings; its JSON mode emits one JSON object per line. Disconnecting an observer does not cancel the server's accepted turn. Resume using the printed conversation ID.

Interactive login prints a browser link and a matching code. Sign in, compare the code with your terminal, and confirm with your password. This grants an interactive session with a one-hour idle timeout and an eight-hour absolute lifetime. It is stored separately from the named API key. Re-run interactive login after expiration or when a privileged operation needs a fresh password confirmation. Session listing and revocation use the existing session commands.

At the chat prompt:

- `/search <query>` returns cited passages; `/recent [count]` lists recent document metadata.
- `/query <type> [JSON filters]` runs a structured query, for example `/query note {"tags":["project"],"limit":10}`. Filters include `people`, `predicates`, `occurred`, `updated`, `source`, `path_prefix`, `mime`, sorting and a pagination `cursor`.
- `/sources` shows the previous substantive answer's verified passages. Changed or purged passages must be retrieved again.
- `/save` proposes saving the previous substantive answer; `/save <text>` proposes the supplied text. Source references carry into derived saves, so they still require approval when automatic approval is enabled.
- `/new` starts another conversation; `/pending` shows proposals; `/approve ID` and `/discard ID` resolve a proposal through the paired session; `/exit` quits.
- `/retry` resumes the last interrupted turn. `/retry --fallback` selects the configured fallback before any write has committed. After a committed write, the same provider/model resumes from recorded results. Replayed operations retain spent budgets and do not repeat the write.

Search, query, recent, sources and save commands also work in browser chat and through `/chat`. They use the application services without a model call. Review the exact proposal before approving it. API keys cannot approve, and a paired session does not qualify for automatic approval. See the [M2 acceptance evidence](../../docs/build/M2-acceptance.md).

The chat binding's `reasoning: low|medium|high` is sent as `reasoning_effort` when that concrete model declares `capabilities.reasoning_control: true`. Otherwise answers report `reasoning_ignored`. Declare this capability only after testing the endpoint. For vLLM, [the standard reasoning control enables thinking](https://docs.vllm.ai/en/stable/features/reasoning_outputs/). The configured `limits.max_output_tokens` covers the provider's output, including reasoning tokens; allow enough room for the final answer. Private reasoning is not displayed or included in the portable transcript. The [recorded quality evaluation](../../eval/assistant/README.md) used a 4096-token limit.

## MCP bridge

`brain mcp` connects an MCP client over stdio to the configured daemon's `/mcp` endpoint. Its default API key must have only `read`; use `mcp --credential <name>` to deliberately select a separate write/infer key. Administrator keys and paired sessions are rejected. Protocol messages alone go to stdout; diagnostics go to stderr.

Cloudflare Access service tokens can be stored with `brain login <origin> --name <name> --access-client-id <id>` and `SECONDBRAIN_ACCESS_CLIENT_SECRET` (or `--access-client-secret`). This stores the Access token separately; run normal API-key login afterwards. See [MCP client setup and contracts](../../docs/build/M2-mcp.md), including the [verified Codex configuration](../../docs/build/M2-mcp.md#codex-on-the-reference-host).
