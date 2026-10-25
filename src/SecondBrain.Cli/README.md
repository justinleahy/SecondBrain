# brain

`brain` uses System.CommandLine and emits a JSON result envelope with `--json` on every command. Exit statuses are `0` success, `1` error, `2` usage, `3` daemon unreachable, and `4` failed precondition. Errors in JSON mode use stdout; text errors use stderr.

Implemented local commands:

```
brain init --provision [--dry-run] [--deployment systemd|compose|none]
brain init [data-root] [--reset-password]
brain doctor [--extractor-socket /run/secondbrain/extractor.sock]
brain maintenance rotate-keys [--revoke-all]
brain maintenance backup-key --recovery-copy /private/recovery/location/backup.key
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

## Encrypted backups (M4 checkout)

An administrator can create a backup while the daemon is running, either in **Settings → Encrypted backups** or with:

```sh
brain maintenance backup --json
brain maintenance backup --to /path/to/existing/private-directory --json
brain maintenance backup --to /path/to/new-archive.sbk --json
```

The command prompts for the account password, confirms it through a temporary session, and closes that session when finished. Without `--to`, the encrypted archive stays in the daemon's `backups/` directory. With `--to`, it also downloads a private file and refuses to overwrite an existing destination. The destination's parent directory must already exist. A partial backup returns exit code 4 and is explicitly labelled incomplete in the result and filename.

Set `backup.key_file` to an absolute path outside the data root. With the daemon stopped, run `brain maintenance backup-key --recovery-copy /private/recovery/location/backup.key`. The key's existing parent must be mode `0700`; both parent directories must be owned by the invoking user, have no symbolic links and disallow group/other writes. The recovery location must be outside both the data root and the key directory. Run with an identity that can lock the data root and leave the generated key readable by the daemon.

This command creates a cryptographically random **raw 32-byte** key and matching recovery file, both mode `0600`, without printing the key. It never replaces an existing key or mismatched recovery file. Repeating it with the same key and recovery location is safe. Keep the recovery copy off the server; making a second file on the server alone does not satisfy recovery custody. The key is not hexadecimal text or a passphrase. An explicit `SECONDBRAIN_BACKUP_KEY` overrides the file and must contain those same 32 bytes encoded as base64; unset it before using the provisioning command.

```yaml
backup:
  key_file: /etc/secondbrain/secrets/backup.key
  keep: 14
  segment_mib: 1
  include_trash: true
  external_roots: []
```

Backups contain a consistent state snapshot, vault, trash, local key ring, and registered folder sources in index mode, including disabled sources. `external_roots` adds required folders beneath `sources.allowed_roots`. Roots must exist, must not traverse symbolic links, and cannot overlap the data root or protected configuration and secret locations. Every initialized schema upgrade requires a complete encrypted backup, including upgrades from an earlier M4 checkout. Provision the key before starting that upgrade. A missing key or partial source backup stops it before the schema changes. Fresh initialization and later M4 startups do not require a backup key. Changing key or root settings requires a restart.

`keep` is reloadable and editable under **Settings → Encrypted backups → Edit backup retention**. Rotation runs after creation, hourly, and with `brain maintenance retention`. It keeps the newest configured number of archives and preserves the newest complete archive when newer attempts are partial. At most 64 old archives are removed per pass; remaining work continues on later passes. A new archive is preserved during its creation request even if the clock moved backwards. Unknown files, links and files without the backup format header are left alone. Interrupted removals recover before the next rotation.

The administrator API exposes `POST /v1/maintenance/backup`, `GET /v1/maintenance/backups`, `GET /v1/maintenance/backup/key`, and `GET /v1/maintenance/backups/{name}`. Creation and download require recent password confirmation for browser sessions. A concurrent backup is rejected; failures preserve existing documents. The list returns up to 128 newest archives. Archives authenticate every encrypted segment and include a manifest for file and state verification. These controls are available in the M4 checkout and are not deployed with M3.

### Restoring a backup

Stop the destination daemon, provision its data and incoming roots, and configure the original backup key. The destination configuration and provider secrets stay under the operator's control. Run the local command as the daemon user:

```sh
brain maintenance restore /path/to/archive.sbk --json
# To restore an external source to a different location:
brain maintenance restore /path/to/archive.sbk --external-root /old/source=/new/allowed/source --json
```

The command authenticates the whole archive and verifies its manifest in private staging before replacing any destination files. It rejects partial archives, unknown mappings, linked destinations, inadequate disk space and a running daemon. External sources default to their original paths; every destination must be beneath a configured allowed source root. The command will replace existing destination folders, preserving their prior contents in adjacent recovery folders with their original permissions. Map an external source beneath the incoming root instead of replacing the incoming root itself. Configuration and secret locations cannot be external restore destinations.

Restore installs the state, vault, trash, key ring and external roots, remaps their internal paths, invalidates old credentials from both the archive and the destination, and rebuilds the disposable search index. Document text and original file bytes remain unchanged. Sign in again with the restored account password and issue new API keys. Review and rotate provider credentials separately. Pending approvals expire and interrupted jobs are cancelled with a retry reason; review processing jobs after starting the daemon.

Restore records its recovery plan before creating prepared folders. If preparation or installation is interrupted, the destination refuses normal startup and local initialization until recovery. Run:

```sh
brain maintenance restore --rollback --json
```

Rollback restores every prior folder. If copying or metadata preparation was interrupted before installation began, it verifies that the original folders remain in place and records the partial prepared folders for review. The completion result names a private recovery record listing retained folders; inspect and remove them only after accepting the restored instance. They are not included in normal backups and are not automatically cleaned up. Verified pre-M4 archives are upgraded in private staging before installation. Full hardened-deployment acceptance remains unfinished.

### Portable export

```sh
brain maintenance export --to /path/to/existing/private-directory --json
brain maintenance export --to /path/to/new-export.tar --json
```

With no `--to`, the command saves a new archive in the current directory. Existing files are never overwritten. The command uses a temporary password-authenticated session and streams the response into a private file. The same operation is available under **Settings → Export your data** and at `POST /v1/maintenance/export` with administrator permission and recent password confirmation.

Exports are plaintext TAR archives containing `export.json`, `data/*.jsonl`, `files/` and `manifest.jsonl`. The metadata describes the table columns and counts. JSON Lines preserves SQLite text and numeric values; binary values use `{ "$base64": "..." }`. The manifest records file sizes and SHA-256 hashes. Files use per-export numeric directories and escaped names to avoid extraction collisions and unsafe paths across platforms; each manifest entry retains its original `source_root` and `source_path`. Original files, trash, revisions, conversations, proposals, usage and other knowledge metadata come from one pinned snapshot. The index is disposable and is omitted.

Authentication records, password hashes, signing keys, protected calendar links, HTTP validators, provider endpoint details and server configuration are excluded. Original document content remains unchanged. An explicit table/column policy rejects unreviewed schema additions. Exports do not replace encrypted backups and cannot be installed by the restore command. They need no backup key. An unavailable source file produces a labelled `.partial.tar`, a partial manifest row and CLI exit code 4. Temporary server export files are removed when the download closes.

## Shared calendars (M4 checkout)

These commands are implemented in the working checkout and are not deployed with M3. An administrator must first allow the exact HTTPS origin in `sources.calendar_allowed_origins`; deployment firewall provisioning is still pending.

```sh
brain sources add-calendar --name "Shared calendar"
brain sources list --json
brain sources scan SOURCE_ID
brain sources scan SOURCE_ID --deep
brain sources pause SOURCE_ID
brain sources resume SOURCE_ID
brain sources remove SOURCE_ID
```

`add-calendar` reads the HTTPS or `webcal` link through a dedicated hidden prompt. For automation, use `--url-stdin` with a protected input source; do not put a bearer link in command arguments or shell history. This prompt does not read the account-password environment variables. `--poll-interval` sets seconds (default 3600, minimum 300), and `--paused` registers without fetching. Registration returns source metadata and means that the first refresh is queued, not that importing has succeeded. Listings contain the origin, status and refresh timestamps, never the private URL.

Pause, resume and removal use an ephemeral password-authenticated session with recent-password confirmation, matching the browser's source controls. Removal stops polling and hides imported documents; it does not modify the remote calendar. Moving a calendar container to Trash hides its events and pauses its subscription. Restore the container before its events; then explicitly resume the subscription. A container purge erases its members and owned snapshots, while purging one event retains the container's original snapshots. Detached, never-accepted snapshots are cleaned up automatically. Accepted historical snapshots are retained until their container is purged.

`brain sources remove SOURCE_ID --purge` permanently purges a calendar subscription, its events, its saved snapshots and its protected link. It also works after ordinary source removal or before any import succeeded. The browser exposes the same operation as **Purge calendar…**, and REST uses `DELETE /v1/sources/SOURCE_ID?purge=1`. Purge persists its request before file cleanup and removes files in bounded batches; restart or background recovery finishes an interrupted request. A failed request can also be retried with the same source id. Unreadable or conflicting owned files can keep a purge pending until repaired. The same command now purges a folder source and its retained documents, including trash and imported calendar members. Files in the original source folder remain untouched. The built-in inbox cannot be purged as a source. Folder purge also works after ordinary source removal; failed requests retain a sanitized pending state and retry in the background. Automatic retention remains separate from explicit purge.

### Calendars declared in configuration

M4 also supports the following `sources` entries (merge the snippet into the existing configuration):

```yaml
sources:
  calendar_allowed_origins: [https://calendar.example.invalid]
  calendar_subscriptions:
    - id: team-calendar
      name: Team calendar
      url: '${TEAM_CALENDAR_URL}'
      poll_interval_s: 3600
      enabled: true
```

Supply `TEAM_CALENDAR_URL` through the existing private secret-file or environment mechanism. YAML must contain only the reference. The configured id and encrypted link are retained across restarts; changing the resolved link requires a new id. The daemon validates all entries before applying the registry update. Configuration source settings require a restart, alongside any necessary firewall changes.

At startup, declared name, interval and enabled state supersede temporary UI/CLI edits. Omitting a configured entry pauses its polling and preserves its events. Explicitly removing it through the source controls retains a tombstone, so the next restart does not silently recreate it; use a new id to register it again. API-created subscriptions are not affected by reconciliation of the configured entries.

A pause caused by trashing a container persists across restart and restore, even when configuration says `enabled: true`. Restore the container and explicitly resume it in Sources or with `brain sources resume` to clear that pause.

### Preparing calendar network rules

`brain maintenance calendar-egress --output-dir DIRECTORY` resolves the configured calendar origins and prepares systemd and Compose policy files without changing a running service. Only public unicast addresses are permitted, and any overlap with the configured canary is rejected. The command reads no calendar feed and never promotes a calendar host to provider trust. See the [deployment instructions](../../deploy/README.md#calendar-download-allowances-m4-checkout) for installation, DNS updates and optional IPv6 networking.

### Retention (M4 checkout)

`brain maintenance retention` asks the daemon to apply the current retention policy. It uses a recent-password session, matching **Settings → Retention → Run cleanup now…**. REST clients use `POST /v1/maintenance/retention`; the narrower temporary-record operation remains available at `/v1/maintenance/retention/metadata`.

The default document periods are 30 days for trash and 90 days for records removed from a source. Cleanup rechecks the document's status, revision and expiry at admission. A restored or reimported document is kept. Expiry uses the managed-file journal, preserves unowned external files and keeps owned calendar originals when only one calendar member expires. Once a source-removed identity expires, a later import may create a new identity; user-deleted identities retain their suppression.

The response separates document expiry, temporary-record counts and backup rotation. A deferred item can need an unreadable or conflicting owned path repaired before cleanup can finish. Newer records remain protected. Saved conversations outlive temporary-record retention, but excerpts and derived content referencing a purged document are redacted. Debug-log cleanup and full operational qualification are still under M4 development; these commands are not deployed with M3.

### Temporary-file cleanup and upgrades

Startup and scheduled/manual retention remove abandoned backup, export and restore workspaces bearing the application's ownership marker. Active operations retain a filesystem lock, including throughout downloads, and are left alone. Unmarked folders and retained restore rollback directories remain for operator review. Debug-body log files with the owned timestamp/GUID naming format expire according to `retention.debug_log_days`; other application logs are retained.

The M4 migration records its applying/applied state and the encrypted pre-upgrade archive name. Interrupted upgrades retry safely; a retry before the state transition creates a fresh backup. The index can finish rebuilding after the state transition. Obsolete local M0 rollback snapshots are removed only after both stores finish migration; current pre-upgrade archives use ordinary encrypted-backup retention.

### Model usage and budgets

`brain usage --json` returns the administrator usage report for the current UTC day. Use `--from 2026-10-20T00:00:00Z --to 2026-10-21T00:00:00Z` for an explicit interval of up to 31 days. The report groups calls by provider, model, purpose, credential and job; its daily total includes retained legacy chat charges. The API is `GET /v1/usage` (also `/usage`) and requires `admin`. At most 500 groups are returned, with an explicit truncation flag. Portable exports include the full usage tables.

Chat, search embeddings, document embeddings and reranking now share atomic daily and per-credential hourly token reservations. Actual input/output/cache counts settle the reservation when supplied by the provider; missing or malformed totals keep the reserved amount charged. Cached input is part of the input count. Interrupted calls retain their charge, and deleting a conversation does not reset the budget. Admitted document processing runs under the daemon's authority while retaining the initiating credential and current processing job for accounting; calendar members inherit their import's attribution.

**Settings → Model usage** shows today's total and purpose breakdown. **Edit daily budget** uses a numeric field; zero pauses model calls while keyword search remains available. The daily budget and per-credential hourly limits reload without restart. Classification and document enrichment use the same ledger. Token reports do not estimate dollar costs.

### Automatic document classification

Ambiguous uploaded or folder files can use the configured `enrich` model to choose `note`, `article`, `transcript`, or `file`. The shared type-precedence rule first respects a user override, a type declared by the importing client, frontmatter, an unambiguous calendar/transcript format, and source type mappings/defaults. Model inference is used only after those rules. Low-confidence, invalid, unavailable or timed-out results use the format default and let ingestion finish.

```yaml
classification:
  llm: true
  min_confidence: 0.8
```

Manage these values under **Settings → Document classification**; editing requires recent password confirmation. The settings apply to subsequent processing. Existing documents retain their current type until explicitly changed or processed again. Original properties remain preserved across type changes. The document viewer shows model confidence when that rule selected the type.

Classification uses a bounded excerpt and a 30-second deadline, honors the model's structured-output capability, and uses the shared token budget and privacy transport. A fenced result is cached in its extraction job before publication, so interruption between inference and publication can reuse it. Changed document, source, or model settings cannot apply a stale decision. This classification stage is separate from the document enrichment described below.


### Document enrichment (M4 checkout)

`brain enrich DOCUMENT_ID --json` requests a durable enrichment job and returns its status. It requires both `read` and `infer`. The equivalent API is `POST /v1/documents/{id}/enrich`. The document viewer also has an **Enrich document** action; refresh the document after a queued job finishes.

Enrichment adds a short summary, up to five tags, mentioned people/organizations/projects/places, and suggested internal note titles. Suggestions appear alongside the original document. Existing tag overrides remain authoritative; suggested links open an internal search and do not create links automatically. With write-back off, original file bytes and the document revision stay unchanged.

```yaml
enrichment:
  enabled: true
  write_back_frontmatter: false
  batch_backfill: true
```

**Settings → Document enrichment** turns automatic enrichment on or off. Eligible documents are scanned incrementally and processed through the configured `enrich` role, privacy controls and shared token budgets. Folder sources must opt in with `enrich: true`. Captures from an API or session require an active initiating credential with inference access; explicit requests are checked again before publication. Disabling enrichment also disables manual triggers. A model configuration change makes eligible documents candidates for fresh enrichment.

Requests use bounded document excerpts and a 60-second deadline. Invalid responses fail the optional job without hiding the document; transient errors and exhausted budgets back off. Saved results survive interrupted publication and can be published without another model call. Edits, deletion, source changes and expired authority fence stale results.

Native OpenAI batch processing is available for automatic enrichment backfills when explicitly enabled for the model. Other bindings use individual jobs. These M4 features are not yet deployed.

Enable **Save summaries and suggested tags in managed Markdown files** under **Settings → Document enrichment**, or set `enrichment.write_back_frontmatter: true`, to save the current result into an owned `secondbrain_enrichment` frontmatter section. This administrator setting requires recent-password confirmation in the browser/API. It applies to existing and newly enriched managed `.md` and `.markdown` files. External source files and other formats are left unchanged. Authored frontmatter, user tag overrides and body text are preserved; generated tags remain labelled as model suggestions.

Each write creates one `enrichment` revision through the mutation journal. The sandbox checks both the original frontmatter and the proposed file before it is written. File changes detected after inspection produce a conflict instead of overwriting the newer file. Interrupted writes recover through the same journal as edits. Enabling write-back can use an already saved result without calling the model again; that file operation runs under the administrator's setting, independently of the original capture credential. Turning the setting off stops new writes; an already prepared journal operation completes during recovery. The document viewer reports when suggestions have been saved in the file.

Folder scans track the source-file hash independently from the managed file hash, so saving enrichment does not trigger a reimport loop. Actual source changes still import; unchanged file moves preserve the enriched copy. Application schema version 7 adds this tracking and requires the existing encrypted pre-upgrade backup for an initialized database.

### Native Ollama (M4 checkout)

Set a provider's `kind` to `ollama` and its explicit `endpoint` to the native server root, such as `http://127.0.0.1:11434/`. The compatible `/v1` endpoint remains available through `kind: openai_compatible`. Use existing provider discovery to list installed models, then explicitly configure the chosen models, limits and capabilities. No model is selected or downloaded automatically.

Allow the exact native origin through the existing provider privacy settings. Requests retain DNS, redirect, proxy and live-policy checks. Chat supplies configured context and output limits; embeddings disable silent truncation and verify dimensions. Switching between native and compatible embedding protocols creates a new embedding identity. Keep a separate configured alias for an old endpoint if it must remain available across a restart during reindexing.

Native Ollama supports chat and embeddings, including streaming and declared structured output or reasoning. It does not implement a native reranker or batch API. This adapter is verified in the working tree and is not deployed.

### Native Anthropic (M4 checkout)

Use `kind: anthropic` with an explicit native API root and a configured secret reference for `api_key`. Assign this provider to chat or enrichment; embedding and reranking roles require another provider. Configure exact model identifiers, limits and capabilities. Declaring `prompt_caching: true` on a supported model enables a cache breakpoint after the stable system prefix. Reported input usage includes ordinary input, cache writes and cache reads; cache hits remain separately visible in usage reports.

The native adapter supports streaming, registered function tools, structured JSON and declared adaptive reasoning through the official SDK. Undeclared reasoning and caching are omitted. Incomplete responses fail rather than settling as successful calls. All traffic, including discovery, uses the shared provider privacy policy; SDK telemetry and automatic retries are disabled. No live Anthropic service qualification or production deployment is claimed by the controlled adapter tests.

Declare `native_citations: true` on a supported native Anthropic chat model to enable exact quotes. Each captured passage is sent as an inline citable document. Only provider ranges that match the captured text exactly can create evidence links; model-written numeric markers are removed. The browser and CLI show the verified quotes, and JSON adds `spans` with UTF-16 offsets relative to `source.excerpt`, quote hashes and `granularity: span`. The original passage, revision and hash remain available. Models without this declaration retain passage citations. Native citation documents cannot be combined with structured-output response formats.

Declare `batch: true` for an approved enrich model to use native Anthropic batches for automatic backfills. They share OpenAI's durable job, budget, cancellation and publication controls and the same Settings preference. Requests are submitted inline; completed native batches are deleted after collection. Results use bounded parsing and opaque request ids, and the provider's returned results URL is never followed.

The native Anthropic API does not expose a batch-level key lookup. A known batch id resumes normally after restart; a submission whose response was lost is not resubmitted or matched by reading unrelated batches. It remains unconfirmed and fails conservatively after 48 hours. Audit metadata is retained, and its reservation is not refunded automatically.

### Native OpenAI batches (M4 checkout)

Use `kind: openai` with an explicit API endpoint, normally ending in `/v1`, and a secret reference for its key. Declare `batch: true` under the enrich model's capabilities only after confirming that model supports the Batch API. **Settings → Document enrichment → Use batch processing for supported backfill models** controls automatic backfills; an explicit **Enrich document** request stays on the interactive path.

The worker reserves the shared token budget before uploading a bounded group of up to sixteen documents. It persists upload and submission intent, remote ids, results and individual publication progress. Restart recovery retains these reservations and never blindly resubmits an uncertain request. Completed results are matched by their request ids and checked against current document, source, credential and configuration state before publication. Failed or missing results remain charged conservatively when usage is unknown.

Input/output files are deleted after collection or cancellation where the provider still permits access. If an endpoint changes or cleanup is refused, the batch record retains the remote ids and a sanitized cleanup-pending reason for operator follow-up; it does not send those ids to a different endpoint. Unconfirmed uploads and submissions expire locally after one and forty-eight hours respectively without an automatic resubmission. Completed local batch payloads become eligible for normal retention cleanup.

Application schema version 8 records the batch metadata for both native providers. Initialized accounts require the existing complete encrypted pre-upgrade backup. Controlled HTTP, storage, restart and browser tests do not establish live cloud account access or model availability.
