## Threat model

**Assets**

- Vault contents, originals, conversations, citations, provenance, revisions, suppressions, and pending-operation integrity.
- API keys, UI sessions, password/passkey credentials, provider keys, cursor/signing keys, and privacy configuration.
- Host filesystem, SQLite databases/WAL files, vector files, backups, provider budget, GPU/CPU/disk availability.
- The authenticated browser origin and the guarantee that model-bound data goes only where policy allows.

**Trust boundaries**

- WireGuard/Tailscale peer → plaintext HTTP daemon.
- Browser cookie/Blazor circuit → server-side UI actions.
- MCP client → laptop bridge → `/mcp`.
- Synced folder/untrusted file → watcher → extractor worker → authoritative stores.
- Assistant/model output → executor → read tools, pending operations, and writes.
- Daemon → model providers, telemetry, DNS, proxies, and redirects.
- Container/service account → host volumes, secrets, and backups.

**Attacker models**

- Malicious documents, PDFs, HTML, frontmatter, transcripts, and calendar files.
- A compromised sync tool capable of racing or replacing watched files.
- A malicious MCP client, a stolen bridge credential, or a stolen browser session.
- Another device on the VPN, with or without a valid scoped credential.
- A malicious or compromised model provider returning adversarial calls or content.
- Compromised dependencies or parser/native binaries.
- An attacker reading backups, volumes, or the Docker host.

## Overall assessment

v0.4 substantially fixes the previous authoritative-state problem and the imported-HTML same-origin problem. It also correctly extends privacy policy to derived provider calls and replaces address-class inference with an explicit trust decision, but the actual egress path is still not controlled strongly enough to support “nothing leaves my network.” The most serious new issue is that `sources` authority appears to grant arbitrary server-file access, including access to the application’s own secrets. Session lifecycle, MCP authority, parser isolation, aggregate denial-of-service controls, purge semantics, and deployment hardening also need normative contracts before M0 should be considered security-ready.

## Findings

1. **Critical — The `sources` scope is an arbitrary server-file reader and potential privilege-escalation path.**

   **Concerned:** §7.2 FLD-1, “Folder sources are paths on the server”; §7.3 `POST /sources`; §15.1 SEC-3, which separates `sources` from `admin`; SEC-13’s containment check.

   **Scenario:** A credential with `sources` registers `/etc/secondbrain`, `/srv/secondbrain/vault/.brain`, `/proc/self`, or another daemon-readable directory. With `read`, it retrieves the admitted files through `brain_get` or `/files/{id}`. Even without `read`, enabling enrichment can send secret contents to a hosted provider. Reading the session-signing key or provider credentials can turn `sources` access into full administrative compromise. Real-path containment does not help when the attacker selected the containment root.

   **Recommended change:** Make allowed source roots an admin-defined static allowlist. The `sources` scope may create sources only below those roots. Hard-deny secrets, configuration, internal databases, logs, pseudo-filesystems, devices, and managed output directories. Changing allowed roots should require `admin`, recent reauthentication, and an audit event.

2. **High — `local_only` validates a configured destination, not the actual egress path.**

   **Concerned:** U12, “nothing leaves my network”; §13.5’s exact scheme/host/port match; SEC-9’s check “before every provider request”; §16’s optional OpenTelemetry.

   **Scenario:** A trusted URL passes policy, but the HTTP client follows a `307/308` redirect, honors an ambient proxy, resolves a trusted hostname to a changed destination, or an SDK performs telemetry, model discovery, batch polling, or a secondary request through its own client. A hosted OTLP exporter can also send query, path, exception, or tool metadata while `local_only` remains green.

   **Recommended change:** Route all outbound operations through one policy-owned transport. In `local_only`, disable ambient proxies and automatic redirects or revalidate every hop; constrain DNS resolution or pin service identity; cover health checks, model discovery, retries, fallbacks, batches, and telemetry. Use host egress firewalling as the enforcement backstop. Otherwise weaken the promise to: “the daemon sends provider data only to configured trusted endpoints.” The v0.3 privacy fix is therefore improved but incomplete.

3. **High — The plaintext deployment is not fail-closed.**

   **Concerned:** SEC-1 permits `server.allow_public: true` with only a warning; §11’s cookie is not `Secure`; §16 leaves Docker publication unspecified; passkeys are offered despite a remote HTTP origin.

   **Scenario:** A conventional Docker mapping such as `7171:7171` publishes the port on every host interface even though Kestrel sees only a private container address. An accidental public bind, untrusted reverse-proxy leg, or DNS/routing compromise exposes password login, bearer keys, and cookies in plaintext. `Host`, `Origin`, and `SameSite` do not authenticate the server to the browser. WebAuthn also will not operate normally at non-loopback `http://brain.lan`.

   **Recommended change:** Remove public binding in v1, or refuse startup without a verified TLS terminator. Ship Compose with the host port bound explicitly to the WireGuard address or a Tailscale sidecar/network namespace, plus host firewall rules. Add host-side exposure checks to `brain doctor`. Defer passkeys until HTTPS exists, then use `Secure`, host-only cookies.

4. **High — UI sessions have administrative blast radius without a complete lifecycle.**

   **Concerned:** §11’s signed `HttpOnly`, `SameSite=Strict` cookie; SEC-2’s “revocation is immediate”; SEC-3 gives the UI all four scopes; SEC-5 only mentions signing-key rotation. No logout/session-management endpoints are specified.

   **Scenario:** Malware or theft exposes a browser profile. The attacker obtains all-scope authority, including credential creation, provider/privacy changes, source registration, purge, and maintenance. No absolute or idle expiry, post-login identifier rotation, logout-all, session inventory, or fixation defense is specified. An established Blazor/SignalR circuit may retain its principal after the backing cookie or session is revoked.

   **Recommended change:** Use opaque CSPRNG session IDs with keyed server-side verifiers, idle and absolute expiry, rotation after authentication and privilege changes, and per-session/global authorization epochs. Add logout, logout-all, session listing, and revocation. Abort live circuits on expiry or revocation and recheck authorization before sensitive UI actions. Require recent password/passkey step-up for credentials, privacy/trusted-service changes, public exposure, purge, and key rotation.

5. **High — MCP authorization must occur after method/tool dispatch, and the bridge default is overprivileged.**

   **Concerned:** All MCP operations share `POST /mcp`; SEC-3 says scopes are enforced “per endpoint”; §7.4 defaults the bridge credential to `read` and `write`; Appendix D’s annotations are client hints.

   **Scenario:** A literal endpoint-level check cannot distinguish `brain_search` from `brain_update`. A malicious read-only client submits the write tool to the same route. Separately, a compromised desktop MCP client can use the default bridge to enumerate full documents and mutate them without extracting the credential itself. `readOnlyHint` and `destructiveHint` are advisory and can be ignored.

   **Recommended change:** Define and enforce a normative MCP method/tool/resource/prompt-to-scope matrix inside the dispatcher, including batch and compatibility paths. Never use discovery filtering or annotations as authorization. Default bridge profiles to `read` only; require an explicitly named per-client credential for writes. The bridge must replace authorization and destination data rather than forwarding client-controlled HTTP metadata verbatim.

6. **High — Authorization propagation into resumable work is asserted but not represented.**

   **Concerned:** SEC-3 says scopes propagate into background jobs and assistant executions; §7.3 says turns continue after disconnect; Appendix A’s `jobs`, `turns`, and `tool_executions` lack an initiating credential or authorization generation.

   **Scenario:** A compromised key starts a scan or many assistant turns and is then revoked. After restart or lease recovery, work resumes as ambient daemon authority because the durable job does not identify which principal authorized it. The system cannot both resume the work and prove that current authority still permits each tool call, provider expense, result replay, or filesystem effect.

   **Recommended change:** Persist `initiating_credential_id`, authorization generation, and the fixed allowed-operation set on turns, jobs, and executions. Recheck revocation/current scopes before interactive tool calls, provider spending, replay, and delivery. Explicitly define which already-admitted ingestion jobs may continue as an internal service identity after revocation.

7. **High — A malicious model provider can use read tools as a vault-exfiltration oracle.**

   **Concerned:** CHAT-5 exposes `search`, `query_records`, `read_document`, and `list_recent`; §10.2 sends tool results back into provider context; SEC-6 constrains tools only by caller scope and surface.

   **Scenario:** During an innocuous UI question, a compromised hosted provider requests broad searches, recent-document enumeration, and arbitrary document reads unrelated to the question. Those calls are valid under the UI’s `read` scope, and every result is returned to the provider. Eight calls per turn only slows enumeration. Prompt-injected documents can induce the same behavior.

   **Recommended change:** Mint a narrower per-turn read capability. Perform initial retrieval outside the model, restrict `read_document` to document IDs selected for that turn, and cap searches, documents, bytes, and cumulative tool-result content. Require confirmation before broadening retrieval to a hosted provider and expose an egress manifest. If unrestricted reads remain, state explicitly that the chat provider is trusted for confidentiality of the entire vault.

8. **High — Pending approval is not yet a safe commit protocol, and auto-approve removes the human boundary.**

   **Concerned:** SEC-7 binds an argument hash, one target revision, and expiry; `/pending` permits approval with `write`; Appendix A records a creator but no approver rule; CHAT-6 permits UI auto-approval.

   **Scenario:** A bridge credential lists and approves a proposal created in a UI turn. The spec does not define whether creator, approver, or intersected authority executes it. Two concurrent approvals may execute once each unless state transition and side effect are atomic. One `target_revision` cannot fence both sides of `link_documents`; creates have no target revision and can resolve to a different generated path after a collision. With auto-approve enabled, malicious content or a provider can directly cause writes.

   **Recommended change:** Store and execute one immutable canonical payload. Bind tool/schema version, creator, allowed approver class, all referenced resources and revisions, generated destination, policy/config generation, expiry, and execution ID. Recheck current authority and use an atomic one-time compare-and-set before executing. Editing or rebasing must create a new proposal. Limit auto-approve to short-lived, explicitly enabled, low-risk creates or appends into quarantine; never allow replacement, external-file writes, deletion, or operations derived from retrieved untrusted content.

9. **High — Argon2id API-key verification creates an unauthenticated CPU/memory denial-of-service surface.**

   **Concerned:** SEC-2 stores API keys as Argon2id hashes; Appendix A has only `secret_hash`; §7.3 reserves `429` for login and provider back-pressure. API-key entropy and format are unspecified.

   **Scenario:** Another VPN device floods endpoints with random bearer values. Without a public lookup identifier, the daemon may perform memory-hard verification against many stored hashes per request. Parallel attempts starve Kestrel, SignalR, SQLite, and workers. The same device can maintain the single UI account in lockout.

   **Recommended change:** Generate keys from at least 256 bits of CSPRNG output in `id.secret` form. Look up the public ID cheaply and store a constant-time HMAC-SHA-256 or SHA-256 verifier; reserve Argon2id for human passwords. Specify salt length, algorithm version, calibrated memory/time/parallelism, password-length limits, rehash migration, concurrent-verification limits, and console recovery. Use source-aware exponential delays rather than an indefinitely renewable global lockout.

10. **High — Per-file limits leave the service open to aggregate resource and budget exhaustion.**

    **Concerned:** ING-7; the 100 MB request limit; “`429` is used only for provider back-pressure and login rate limiting”; disconnected chat turns continue; Blazor Interactive Server; §17 assumes one concurrent user.

    **Scenario:** A compromised sync tool creates a million small files or continually mutates them. A stolen write key sends parallel uploads and starts turns in many conversations before disconnecting. A read key runs parallel unfiltered vector scans and deep `/ask` calls, exhausting the instance-wide model budget. A stolen session opens many SignalR circuits. None violates a per-file or per-conversation limit.

    **Recommended change:** Define global and per-credential/source limits for request rate, concurrent uploads/searches/turns, SSE streams, circuits, queued jobs, files per source, total admitted bytes, disk use, provider tokens/cost, and execution time. Add a separate `infer`/model-spend capability. Reserve capacity before accepting work, enforce disk low-water shutdown, coalesce persisted deltas, expire idle circuits/streams, and return `429` or `503` before resource commitment.

11. **High — Real-path containment is still vulnerable to filesystem races, and inbox deletion can remove replacement files.**

    **Concerned:** SEC-13, “resolved real path” checks; FLD-9’s “copy, verify hash, index, then delete”; §8’s temp/move protocol.

    **Scenario:** A compromised sync writer presents an ordinary file while the daemon resolves it, then swaps the file or a parent for a symlink before `open`. The daemon reads a secret outside the source root. Hard links can bypass real-path containment entirely when the writer shares access. During inbox import, the sync tool replaces the source after verification; the daemon then deletes the newer unrelated file. A file changing during extraction can also produce a content hash, text, and citation revision that refer to different bytes.

    **Recommended change:** Operate relative to a retained root directory descriptor. On Linux use `openat2` with `RESOLVE_BENEATH`, `RESOLVE_NO_SYMLINKS`, and `RESOLVE_NO_MAGICLINKS`; accept regular files only and `fstat` the opened descriptor. Stage immutable bytes privately and parse only the stage. Before inbox deletion, compare device/inode/ctime/size/hash against the captured source. Use separate Unix identities so a sync process cannot hard-link daemon-readable secrets.

12. **High — The extraction worker is not defined as a security boundary.**

    **Concerned:** ING-7’s “cancellable worker”; §8’s “isolated worker with a timeout and memory ceiling”; §18’s PDF, DOCX, HTML, YAML, calendar, and subtitle parsers.

    **Scenario:** A crafted file exploits a parser or triggers native code execution. An ordinary thread or unsandboxed child inherits the daemon’s environment, vault/state access, secrets, and network, enabling credential theft, authoritative-state alteration, SSRF, or exfiltration. Cooperative cancellation cannot stop a native hang or crash.

    **Recommended change:** Define an out-of-process sandbox: dedicated unprivileged UID, scrubbed environment, no secrets, no network, no databases, read-only access to one staged input, private scratch space, bounded IPC output, cgroup/rlimit CPU/RSS/PID/file-size limits, seccomp/AppArmor, and kill-the-process-group timeouts. Disable external XML entities, DOCX relationships, HTML resource fetching, PDF actions, and calendar attachments. Validate all worker output again in the daemon.

13. **High — Parser and recurrence bounds still permit attacker-controlled superlinear work.**

    **Concerned:** ING-7’s decoded/member/nesting limits; §5.7 recurrence materialization; ING-6’s automatic retries; Appendix D schemas lack most string/array bounds.

    **Scenario:** A small `.ics` contains a decades-old `FREQ=SECONDLY` rule, huge `RDATE` sets, or pathological timezone definitions. A naïve library performs billions of iterations before reaching the 24-month window. DOCX ZIP entries, YAML aliases, XML trees, PDF filter chains, or enormous model-generated tool arrays may allocate heavily before the post-decode limits fire. The same deterministic bomb is retried three times.

    **Recommended change:** Enforce streaming limits on compression ratio, per-entry and aggregate output, XML/YAML nodes/aliases/depth/scalars, PDF objects/pages/filter output/pixels, calendar components/timezones/property lengths, recurrence steps and occurrences, child jobs, and all tool-call strings/arrays. Require recurrence seeking or reject pathological rules. Mark deterministic structure/resource failures non-retryable until bytes or parser generation change.

14. **High — The claimed SQLite transaction cannot atomically publish external vector bytes.**

    **Concerned:** §8 Index, “One transaction replaces … vectors” and “A crash leaves the previous projections intact”; Appendix A stores vectors in memory-mapped `.f32` files referenced by SQLite slots.

    **Scenario:** A stale worker writes a reused vector slot and then loses its SQLite fence, corrupting current data despite being unable to commit metadata. A crash or disk-full condition can commit a slot mapping before bytes are durably flushed. Truncating or replacing a mapped file can crash readers. SQLite cannot roll back sidecar-file writes.

    **Recommended change:** Use immutable append-only vector segments or per-job staging files. Write checksummed data, `fsync`, atomically publish the segment, and then commit a SQLite manifest; never overwrite active slots. Stale workers must write only private staging. Startup reconciliation should verify lengths/checksums and rebuild mappings without durable bytes.

15. **High — Purge omits durable copies of the content it claims to remove.**

    **Concerned:** SEC-15; §10.2’s portable transcript; Appendix A `messages.content_json`, `turn_events.payload_json`, `tool_executions.args_json/result_json`, `pending_operations.args_json`, `jobs.payload_json`, and `idempotency.response_json`; retrieval traces and SQLite WAL/free pages.

    **Scenario:** A secret appears in a proposed `create_document`. It is persisted in the event log, pending operation, tool execution, transcript, and possibly idempotency response before the document is later purged. SEC-15 removes vault/index representations but not those copies. A later state-database or volume reader recovers the full body. Retaining bounded citation excerpts is explicit; retaining complete mutation arguments is not.

    **Recommended change:** Define a field-by-field retention and lineage matrix. Store bodies once and use IDs, hashes, and bounded redacted summaries in audit/event records. Give resolved pending operations, idempotency records, execution payloads, traces, and migration backups explicit TTLs. On purge, remove linked live copies where policy allows. Rename the operation “logical purge” unless forensic erasure is intended; forensic erasure needs per-object encryption/crypto-erasure plus WAL/cache/vector and backup treatment. Also reconcile `logging.debug_retention_days` with Appendix B’s different `retention.debug_log_days`.

16. **High — Backups expose essentially the entire knowledge base in plaintext.**

    **Concerned:** §6 claims location means backup and sync tools “never” copy secrets; SEC-12 says secrets are excluded by location; §16’s backup and automatic pre-migration backup.

    **Scenario:** A vault plus `state.db` backup exposes originals, notes, conversations, tool records, pending operations, paths, usage, and password/API-key verifiers. A generic host backup can also include `/etc/secondbrain`, Docker environment values, core dumps, and migration snapshots. Because `.brain` lives below the vault, an ordinary vault sync may copy index text, vectors, logs, traces, trash, WAL files, and state unless separately excluded.

    **Recommended change:** Explicitly trust backup readers or provide authenticated backup encryption with a separately managed key. Specify output ownership/mode, `umask 0077`, atomic creation, retention, exclusion rules, and restore verification. Prefer secret mounts over environment variables, scrub child environments, and disable core dumps. Restore from an old backup should advance the session authorization epoch and prompt for API/provider-key rotation.

17. **High — Vault and state backups do not form one recovery-consistent snapshot.**

    **Concerned:** §5 makes vault plus state jointly authoritative; §16 takes a consistent snapshot only of `state.db` and treats the vault as an independent file backup; restore promises “restore vault and `state.db` … rebuild.”

    **Scenario:** A note is moved or edited while the database snapshot and filesystem backup occur. Restore pairs revision N metadata with revision N+1 bytes, or no file. A raw file backup may copy `state.db` without its WAL. Index-mode external originals are not present in the vault at all, so vault-plus-state restore cannot reconstruct them unless those roots are separately restored.

    **Recommended change:** Add an application snapshot barrier or filesystem snapshot producing a manifest of document IDs, revisions, hashes, schema versions, external-source dependencies, and processing generations. Restore must verify the manifest before serving. Document that external source roots are part of the backup set. Use the SQLite online-backup API rather than copying live DB/WAL files.

18. **High — Deployment lacks the least-privilege controls needed to contain parser or dependency compromise.**

    **Concerned:** §16 only promises Docker and systemd artifacts; §6 annotates the `secrets/` directory as `0600`; WAL, logs, vectors, backups, and databases have no required modes.

    **Scenario:** A normal `022` umask makes content stores readable to other host accounts. A root-running container turns a parser compromise into root-owned writes on mounted volumes. A directory mode of `0600` is itself unusable because traversal needs execute permission.

    **Recommended change:** Require a dedicated non-root UID, `0700` directories, `0600` sensitive files, `umask 0077`, and startup ownership/mode validation. Docker should have a read-only root filesystem, dropped capabilities, `no-new-privileges`, no Docker socket, read-only secret mounts, resource limits, and restricted egress. The systemd unit should use `NoNewPrivileges`, `ProtectSystem=strict`, `ProtectHome`, `PrivateTmp`, an empty capability bounding set, controlled `ReadWritePaths`, and restricted address families.

19. **Medium — Untrusted frontmatter has no identity or provenance trust rule.**

    **Concerned:** §5.3 says IDs are assigned at admit while managed notes carry them; §5.4 reserves `id` and `source`; FLD-8 parses reserved fields; ING-3 says provenance is credential-derived.

    **Scenario:** A compromised sync tool writes Markdown containing the ULID of an existing managed note and a forged `source`. A permissive implementation may attach the hostile occurrence to the existing identity, corrupt its revision history or links, or display forged provenance. Two files can claim the same ID during rebuild.

    **Recommended change:** Honor a frontmatter ID only for an already-known system-managed occurrence. Assign fresh IDs to new external files and keep claimed IDs as inert metadata, if at all. Reject duplicates and never accept provenance from content. Bound frontmatter size, aliases, nodes, depth, tags, and scalar lengths before state mutation.

20. **Medium — The PDF viewer and other raw-HTML UI sinks reopen browser-origin risk.**

    **Concerned:** SEC-11 says originals are download-only and never inline; §11 renders PDFs through pdf.js; RET-1/Appendix C use HTML `<b>` highlights in snippets.

    **Scenario:** The authenticated UI fetches a malicious PDF and passes it to pdf.js at the credential-bearing origin, bypassing the isolation benefit of `Content-Disposition`. A pdf.js or wrapper flaw can issue authenticated same-origin requests. Separately, rendering FTS snippets through `MarkupString` to preserve `<b>` can turn attacker-controlled surrounding text into stored HTML.

    **Recommended change:** Prefer an extracted-text PDF viewer for v1, or place pdf.js on a cookie-less origin/in a sandbox without `allow-same-origin`, using a short-lived file-specific capability. Disable PDF actions, JavaScript, attachments, forms, and automatic external navigation; cap pages and pixels. Return highlight ranges rather than HTML. Treat titles, paths, snippets, speakers, tool output, and provider output as text, and use an explicit sanitizer/CSP allowlist for every Markdown-to-HTML sink. The previous imported-HTML critical is otherwise substantially resolved.

21. **Medium — Self-contained cursor and signing tokens lack a normative cryptographic format.**

    **Concerned:** §7.3’s “opaque, signed, self-contained” cursors; stateless MCP pagination; §11’s session-signing key; SEC-7’s argument hash.

    **Scenario:** An implementation may emit a readable MAC-only cursor containing a sensitive query, omit expiry or endpoint/query binding, reuse the session key, or accept the cursor after credential revocation and index-policy changes. Replaying a broad-query cursor with different filters can confuse pagination if the request is not bound. Different JSON serializations can also hash differently from the bytes ultimately executed.

    **Recommended change:** Specify a versioned envelope containing `kid`, purpose/audience, instance, operation, normalized query/filter hash, sort and tie-break tuple, relevant generation, credential/auth epoch, `iat`, and short `exp`. Reauthorize every page; a cursor must never convey authority. Use separate Data Protection purposes or separate HMAC/AEAD keys for sessions, cursors, antiforgery, and capabilities. Encrypt cursor contents if “opaque” means confidential. Execute the immutable stored approval bytes or define canonical JSON. Require CSPRNG randomness for API keys, sessions, capabilities, and ULID randomness; IDs themselves must never be treated as authorization secrets.

22. **Medium — Supply-chain and update policy is absent for a high-risk parser surface.**

    **Concerned:** §18 names numerous NuGet packages, native `e_sqlite3`, pdf.js/Monaco, an unspecified subtitle parser and component library, and self-contained .NET binaries, but no pinning or update controls.

    **Scenario:** A compromised transitive package executes during build or runtime with vault and secret access. A vulnerable bundled .NET runtime, SQLite binary, or pdf.js remains deployed even after the host OS is patched. Malicious synced files directly exercise the riskiest parser dependencies.

    **Recommended change:** Require central exact package versions, locked restore, approved NuGet sources/source mapping, a pinned SDK and digest-pinned container image, an SBOM covering native/browser assets, vulnerability scanning, signed provenance/releases, and a security-update SLA. Self-contained deployments must be rebuilt for runtime/native advisories; OS patching is insufficient. Fuzz parser boundaries and retain risky libraries inside the extraction sandbox.

23. **Medium — SQLite concurrency, WAL growth, migrations, and the one-process assumption need enforceable limits.**

    **Concerned:** D4; §5.1’s “one connection”; four workers; persisted streaming events; online backups; §16’s separate forward-only migrations.

    **Scenario:** If one connection is literal, asynchronous work can serialize or misuse it. With multiple connections, long reads or backups can prevent checkpoints while turns and jobs append, filling the disk. Two daemon instances sharing a volume can both manipulate jobs and vector files. A crash between state and index migrations leaves mismatched schemas.

    **Recommended change:** Take an exclusive data-root instance lock. Specify a bounded read pool and one queued writer, busy/statement timeouts, cancellation handlers, bounded transaction lifetimes, WAL checkpoint/journal-size policy, and disk-low-water behavior. Disable extension loading and enable defensive/trusted-schema settings. Migrations need exclusive execution, disk preflight, a durable idempotent state machine, and crash tests at each boundary.

## Residual risks accepted by design

- **No TLS inside the VPN:** Reasonable only if the tunnel terminates on the actual server, ACLs restrict intended devices, DNS/routing are controlled, and public/container exposure fails closed. Another WireGuard peer normally cannot passively decrypt peer traffic, but it can directly attack the service. Plain HTTP also precludes ordinary passkeys and `Secure` cookies.
- **Compromised laptop or MCP client:** A stolen `read` credential can exfiltrate the entire vault, and a `write` credential can mutate it. The daemon cannot control what an authorized external client does with returned data. Read-only-by-default bridge credentials materially reduce this risk.
- **Trusted model service:** Trust-listing a provider is a confidentiality decision, not proof of locality. A compromised trusted service sees and can retransmit everything sent to it.
- **Compromised Docker host/root:** A host administrator can read process memory, secrets, volumes, and plaintext databases. Application controls cannot prevent this without a stronger external key and isolation model.
- **Prompt injection:** Even with correct write authorization, injected content can bias answers, retrieval choices, and provider-visible reads. The executor limits authority; it does not make model reasoning trustworthy.
- **Compromised sync tool:** It can poison, remove, or churn source files. Snapshotting and sandboxing can contain races and parser attacks, but cannot make the source truthful.
- **No per-source privacy:** When `local_only` is false, every document is eligible for configured hosted embedding, enrichment, rerank, and chat flows. That is acceptable only if made explicit at configuration time.
- **Backups and retained excerpts:** Historical backups and deliberately retained citation excerpts survive logical purge unless separate retention or encryption-key deletion is applied.

## Questions the author must answer

1. Which preapproved server roots may a `sources` credential register, and are changes admin-only?
2. Is the UI cookie an opaque server-side session or a stateless ticket? What are its idle/absolute lifetimes and circuit-revocation semantics?
3. Will v1 add a secure HTTPS origin, or are passkeys explicitly deferred?
4. Does `local_only` govern every process egress path, including redirects, proxies, DNS changes, telemetry, provider discovery, and SDK secondary calls?
5. Are MCP mutations direct writes, pending user-approved operations, or selectable per credential? Who may approve whose proposal?
6. What happens to running turns, scans, and jobs when the initiating credential is revoked or narrowed?
7. What exact OS isolation does the extraction worker have?
8. What global and per-credential limits apply to uploads, searches, turns, circuits, source cardinality, queue depth, disk usage, recurrence expansion, and model spend?
9. Does purge mean removal from live retrieval, removal from all live application copies, or forensic erasure?
10. Are backups encrypted, and how are vault, state, external source roots, and migration state captured at one recovery-consistent point?
11. What publication protocol makes SQLite vector mappings and external `.f32` bytes crash-atomic?
12. What exact cursor format, key separation, expiry, replay policy, and rotation behavior are normative?
13. What package pinning, release provenance, vulnerability-response SLA, and self-contained-runtime rebuild policy are required?
14. Is a root-equivalent host or backup reader explicitly outside the protection boundary?

## What is strong

- Moving identities, suppressions, overrides, conversations, and approvals into `state.db` fixes the prior disposable-index authority defect, assuming external source bytes remain available.
- The explicit inventory of query embedding, rerank, enrichment, classification, chat, and fallback calls is the right privacy foundation.
- Attachment-only original HTML, `nosniff`, sanitized derived Markdown, Host/Origin checks, antiforgery, and read-only `/ask` materially improve the web boundary.
- Revision/generation fences, exact-argument proposals, scoped bridge credentials, explicit degraded states, and decoded-work limits are sound building blocks.

