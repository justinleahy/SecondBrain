## Overall assessment

v0.5 substantially improves the design, but it is not buildable to all its stated guarantees and is not ready for M0 **as M0 is currently defined**. The three-store split, explicit provider trust, scoped bridge credentials, and safe rendering rules address important parts of the first review. However, the new security policy contradicts several public API contracts, while filesystem mutations, publication, purge, and backup still lack mechanisms sufficient for their guarantees. Some deployment requirements cannot coexist under the example permissions and mounts. The next revision should reconcile these contracts and define the missing state transitions, rather than add more security requirements.

## Round-1 disposition

“Resolved” means the original defect has a specified mechanism or has been explicitly removed from v1. It does not mean an implementation has been verified.

| Finding # | Status | Where in v0.5 | Reason |
|---|---|---|---|
| 1 | Partially resolved | §§5.1, 15.10, 16; A.1–A.2 | Authored state now survives index deletion; external-source dependencies and identical model-derived rebuild results remain inconsistent with the recovery promise. |
| 2 | Resolved | §§13.5, 15.1, 15.6; F | Within the agreed instance-wide scope, every provider operation passes through policy; per-source restrictions are explicitly deferred and external-client responsibility is stated. |
| 3 | Resolved | §§13.5, 15.6 | Explicit trusted endpoints replace address-class inference; redirects, proxies, DNS pinning, and secondary SDK requests are covered. |
| 4 | Resolved | §11; SEC-22 | Originals are download-only, PDFs use extracted text, and rendering requires sanitization and text-safe sinks. |
| 5 | Resolved | §§11, 15.2–15.3 | Password bootstrap, server-side sessions, Host/Origin validation, and trusted-proxy handling replace injected administrative keys. |
| 6 | Unresolved | §7.3; §8; §10.2 | Database revisions still cannot detect an external edit not yet observed by the watcher or make filesystem replacement conditional. |
| 7 | Partially resolved | §§5.3, 8; A.1–A.2 | Revision/generation fences exist, but deletion fencing, metadata publication, and filesystem/state recovery remain incomplete. |
| 8 | Partially resolved | §§5.3, 7.2, 15.9 | Durable suppression fixes ordinary rescan resurrection; in-flight publication, retained containers, and complete live-copy purge remain unresolved. |
| 9 | Partially resolved | §§5.3, 7.1; A.1 | Identity, occurrence, content reuse, and namespaced keys are separated; conflicting calendar records across imports remain ambiguous. |
| 10 | Partially resolved | §5.7; ING-9; A.1; F | Membership, checkpoints, and partial outcomes exist, but v1 calendars already require the shared-membership behavior deferred in F. |
| 11 | Partially resolved | §8; A.2 | Versioned extraction keys and exact embedding-input hashes exist; window-dependent calendar materialization is still mixed into cached extraction without the window in its key. |
| 12 | Partially resolved | §13.6; A.2 | Named spaces and coverage counters exist; concurrent catch-up, chunk-layout changes, restart, and activation conditions are not specified sufficiently. |
| 13 | Partially resolved | §5.4; ING-8; E | Normative schemas exist, but property-preserving reclassification conflicts with strict destination schemas. |
| 14 | Resolved | ING-8; FLD-8, FLD-11 | One precedence matrix distinguishes semantic type from physical extraction. |
| 15 | Partially resolved | §§5.5, 9.3; E | Nullable time, basis, precision, timezone, and intervals exist; all-day/floating values, missing event ends, and occurrence-level querying remain incomplete. |
| 16 | Partially resolved | §5.7; A.2 | Coverage advancement is specified; recurrence-instance query semantics and cross-import reconciliation remain incomplete. |
| 17 | Resolved | §5.4; §8 Chunk | Large records may split, retain context, and are checked against embedding input limits. |
| 18 | Resolved | §8 Normalize; A.2 | Separate lossless/search representations and offset maps provide the required mechanism. |
| 19 | Resolved | §5.8; RET-7; A.2 | Segments, optional speakers, relative timestamps, and chunk segment ranges are represented. |
| 20 | Resolved | §5.6; §§9.2–9.3 | Normalized identifiers and role-qualified filters distinguish participation from speaker attribution. |
| 21 | Resolved | §9.3; D | REST and MCP share structured predicates, cursors, sorting, truncation, and coverage. |
| 22 | Resolved | §9.1; A.2 | SQL-selected candidates followed by exact scoring constitute a viable filtering design. |
| 23 | Resolved | §9.1 | Diversity is applied within each retrieval leg before its top-50 cutoff. |
| 24 | Resolved | §9.1; A.2 | Unit-normalized vectors establish cosine scoring; external-content FTS has explicit transactional maintenance. |
| 25 | Partially resolved | §§13.2–13.3 | Binding-specific capabilities, limits, intent, and fallback behavior exist; batch/compaction operations and unknown-model capability resolution do not. |
| 26 | Partially resolved | §10.2; §15.9 | Canonical history and turn-boundary switching fix portability, but payload expiry can remove still-referenced tool history. |
| 27 | Partially resolved | §10.2; A.1 | Execution IDs and replay policy exist; filesystem effects and durable execution results still lack a recoverable commit protocol. |
| 28 | Partially resolved | ING-10; §10.2 | Revision/excerpt citations exist, but generation-only reprocessing can invalidate locators without changing the revision. |
| 29 | Partially resolved | §10.2; §20 | Marker validation and granularity are explicit; citation support and abstention on nonempty but irrelevant retrieval remain unspecified. |
| 30 | Partially resolved | ING-4; §8; RET-13; §10.2 | Small-capture deadlines, degradation, and reservations exist; asynchronous lexical fallback and spend authorization remain inconsistent. |
| 31 | Partially resolved | §§7.3–7.4, 10.2, 15.4–15.5 | Executor enforcement and immutable proposals exist; direct REST mutations, CLI approval, and auto-approval eligibility undermine the contract. |
| 32 | Partially resolved | §§7.4, 15.4 | Bridge credentials and authenticated provenance are fixed; read/spend dispatch gaps remain. |
| 33 | Partially resolved | §§6, 15.9–15.12 | Secrets moved outside the vault and logs default to redaction; purge lineage and key-storage/rotation contracts remain incomplete. |
| 34 | Resolved | §1; F | Resolved entities are explicitly outside v1, removing the requirement to implement their merge/undo behavior now. |
| 35 | Partially resolved | ING-7, ING-11; §15.7 | Processing bounds and process isolation are specified, but sandbox launch/access, pre-extraction parsing, and final unlink safety remain unresolved. |
| 36 | Partially resolved | §17; B | Hardware, dimensions, selectivity, and provider-time separation improve the targets; churn, peak memory, and representative extraction workloads remain undefined. |
| 37 | Partially resolved | §§19–20; F | Scope reduction and adversarial tests help, but milestone dependencies and some acceptance oracles remain inconsistent. |
| 38 | Partially resolved | §§2, 7.4, 9.2; B–G | Several original names are corrected; highlight format, capture types, aliases, pipeline order, and vector-file terminology still drift. |
| 39 | Resolved | §5.2; A.1–A.2 | Independent tag assertions and preserved textual link targets retain the necessary evidence. |
| 40 | Resolved | §5.3; FLD-2, FLD-4 | Deep rescans, generation invalidation, and bounded move reconciliation address missed changes. |
| 41 | Partially resolved | §7.3 | Most lifecycle outcomes are explicit; mutation responses still describe direct execution despite the new proposal policy. |

## New findings

The numbering below identifies this review’s findings. Some expose new contradictions; others identify the remaining failure in a replacement mechanism.

1. **Critical — The proposal boundary is bypassable through REST and implicit upserts.**  
   **Concerned:** D10; §15.4, `write` grants “propose updates and deletions”; §7.3, `PATCH` “Returns the new revision” and `DELETE` immediately suppresses/trashes; ING-2, an existing calendar natural key updates its document.

   A stolen write credential can perform through REST the same replacement that `brain_update` must submit for human approval. Fixing `PATCH` alone is insufficient: uploading another `.ics` through the direct-create endpoint can update existing members through natural-key reconciliation.

   **Change:** Define one mutation-policy matrix used by every door. REST updates and deletions must return proposals. Explicitly classify natural-key upserts, folder reconciliation, restore, suppression removal, and purge. A registered folder may legitimately authorize automatic reconciliation, but that exception must not silently extend to arbitrary API uploads.

2. **Major — Proposal CAS and execution records do not establish exactly-once filesystem effects.**  
   **Concerned:** §8, “temp-then-rename with `fsync`”; §10.2, “atomic compare-and-set on the proposal’s status”; A.1 `pending_operations` and `tool_executions`.

   An append can replace the file and crash before recording success. Retrying can append twice. Marking the proposal executed before replacing the file instead risks permanently acknowledging an operation that never happened. Startup comparison of `revision` and `indexed_revision` does not identify a filesystem effect completed before its state transaction. A redacted `result_summary` also cannot necessarily replay the committed tool result.

   **Change:** Specify a durable prepare/apply/finalize journal with operation ID, expected and resulting hashes, reserved destination, payload, revision, and replayable result. Recovery must recognize an already-applied filesystem effect. Commit the revision, proposal resolution, execution result, and request-idempotency result together in state.

3. **Major — Live-file mutation still has two distinct race windows.**  
   **Concerned:** §7.3 revision preconditions; §10.2 approvals; ING-11’s comparison “Before an inbox import deletes its original.”

   When external writes are enabled, an editor can change a file during watcher debounce without changing the database revision. An approved replacement then overwrites the unseen edit. Separately, the inbox comparison can succeed and the sync process can replace the pathname before the subsequent unlink; the replacement is deleted. Descriptor-safe opening protects the read, not a later pathname mutation.

   **Change:** Define conflict-preserving external writes rather than claiming filesystem CAS from a database revision. For inbox removal, specify an ownership/handoff or quarantine protocol that claims and verifies the entry safely; otherwise retain the original. Rechecking immediately before an ordinary unlink does not close the race.

4. **Major — There is no coherent publication protocol across state metadata, index projections, deletion, and RAM vectors.**  
   **Concerned:** §§5.1, 5.3, 8; A.1–A.2. Index claims “One transaction replaces every projection … metadata,” then updates RAM “After commit.”

   Four failures remain:

   - Current title/type/properties/time live in `state.documents`; the index has no matching indexed metadata snapshot. Reads can join new metadata to old chunks.
   - Deletion changes status and suppression but does not explicitly advance the revision or another lifecycle fence. Already-admitted jobs can pass the stated revision/generation check after deletion or purge.
   - One writer **per store** does not specify how a state change is serialized against an index transaction’s fence check.
   - SQL readers can see committed new chunks before the corresponding vector array update, or hold old SQL snapshots while vector slots change.

   **Change:** Define a publication coordinator, indexed metadata snapshots, lifecycle fencing, and a query snapshot that pins matching SQL and vector generations. Require deletion/suppression checks at publication and read time. Do not rely on `ATTACH` for cross-store crash atomicity: SQLite explicitly excludes that guarantee when attached databases use WAL. [SQLite transaction guarantees](https://sqlite.org/lang_attach.html)

5. **Major — “Bodies stored once” does not supply the lineage required for logical purge.**  
   **Concerned:** SEC-25–26; A.1 `payloads`, `messages.content_json`, `turn_provider_state`; §6 imported originals.

   A user pastes a secret into chat, the assistant saves it, and the resulting document is purged. Removing the proposal payload does not identify the original message, quoted tool results, compaction summary, provider continuation state, or enabled debug-log copy. There is no normative document-to-payload/referrer model. A purged calendar member also remains inside an owned `.ics` original; imported originals are not covered by the managed-note trash rule.

   **Change:** Specify field-level ownership and lineage, including tombstone scrubbing, messages, summaries, provider state, logs, caches, owned originals, and containers. Explicitly exempt unowned external originals. For every other retained class, either implement redaction/removal or narrow “every live copy” to a precisely stated guarantee.

6. **Major — Payload expiry contradicts conversation retention and replay.**  
   **Concerned:** §15.9: payloads are deleted with their last referrer but also “30 days at most after resolution”; events/executions live for the conversation’s lifetime; §10.2 preserves tool calls/results.

   A live conversation can reference a payload that retention must delete. Purge creates the same condition immediately. Neither the canonical transcript nor event replay defines an expired/redacted block, and `tool_executions` lacks an explicit committed-result reference.

   **Change:** Choose retention precedence. Define redacted/expired transcript blocks and their effect on replay and resumed turns. Retain replayable results for the promised replay lifetime, or explicitly terminate that guarantee after expiry. Do not reconstruct a missing result by executing the tool again.

7. **Major — Revoking one credential invalidates every credential.**  
   **Concerned:** SEC-9; A.1 `meta.auth_epoch` and `credentials.auth_epoch`; §16 key rotation.

   The single global epoch increments “on revocation,” while every credential stores its issuance epoch and stale credentials are rejected. Revoking one browser session therefore invalidates all sessions, bridge keys, cursors, and jobs. This contradicts the advertised per-session/per-client behavior and complicates admitted ingestion continuing under internal authority. Rotation is also always epoch-invalidating in SEC-9 but optional in §16.

   **Change:** Separate an account-wide epoch from per-credential generations. Define which events invalidate each, how admitted jobs transfer authority, and whether key rotation is routine rotation or explicit global revocation.

8. **Major — The `infer` scope conflicts with normal search and capture.**  
   **Concerned:** §§7.3–7.4, 10.2, 15.4–15.8: “Model spend requires the `infer` scope,” but hybrid search requires only `read`, and capture only `write`.

   The default read-only bridge either cannot perform its default hybrid search or can spend embedding/rerank budget without authority. Capture has the same problem for embedding, classification, and enrichment. Continuing admitted ingestion as the daemon does not establish its initial spend grant or preserve the initiating credential’s hourly charge. The usage schema lacks a general credential/job attribution path.

   **Change:** Decide whether query/index embeddings are bounded daemon-funded services implicit in `read`/`write`, or require `infer`. Specify lexical fallback, enrichment eligibility, reservation ownership, and charging after revocation. Apply that decision to REST, MCP, jobs, and usage records.

9. **Major — Read authorization has data-bearing exceptions and undefined ownership.**  
   **Concerned:** §7.4 permits `resources/list` for “any valid credential” but defines it as paging recently updated documents; §7.3 `/chat` requires `write + infer`; §15.4 restricts conversations to “one’s own”; A.1 `conversations`.

   A write-only or inference-only credential can enumerate document resources without `read`. Chat mandates initial retrieval without requiring `read`, leaving an authorization bypass or an undefined capture-only mode. Conversation ownership is absent from the schema: turn initiators do not define who may list, resume, replay, or delete a conversation, particularly across browser sessions.

   **Change:** Require `read` for data-bearing resource discovery. Define chat behavior without it. Add an explicit ownership/access rule distinguishing the single human account, browser sessions, and client credentials.

10. **Major — CLI approval and sensitive admin-key operations have no valid authentication path.**  
    **Concerned:** §12 `brain login` stores an API key; CLI chat approves “inside its interactive session”; §7.3 approval is UI-session-only; SEC-10 requires step-up for source changes and other administration.

    A documented CLI login cannot approve because API keys are forbidden from approving. An admin-key script cannot satisfy session-only step-up despite source management explicitly accepting admin keys. A terminal prompt does not itself turn an API key into an interactive authenticated session.

    **Change:** Define a separately authenticated, short-lived CLI session, including its Origin/antiforgery treatment, or make approvals browser-only. Classify sensitive admin-key operations explicitly as prohibited, session-required, or supported through a defined additional authentication flow.

11. **Major — Read-only secrets conflict with initialization and key rotation.**  
    **Concerned:** §§6, 16; SEC-23, SEC-28, SEC-32; M0’s non-root `brain init`; A.1 credential verifiers.

    The deployment mounts the secrets directory read-only while the daemon must persist scheduled Data Protection rotation and service `rotate-keys`. Non-root initialization also cannot create configuration/secrets in the described read-only runtime filesystem without a provisioning step. HMAC rotation cannot simply regenerate stored verifiers for inactive clients when their original secrets are unavailable.

    **Change:** Separate host provisioning, externally supplied read-only secrets, and a protected writable key-ring directory. Define HMAC key IDs, old-key retention, re-verification, and credential reissue/expiry. Data Protection supports read-only consumers, but automatic key generation then needs a separate writer. [Data Protection configuration](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/configuration/overview)

12. **Major — The source layout cannot support the required separate sync user.**  
    **Concerned:** FLD-1 denies “The data root”; SEC-27 requires daemon-owned `0700`; Appendix B puts both allowed source roots beneath `/srv/secondbrain`.

    A separate sync UID cannot traverse the specified private parent to populate either `vault/inbox` or `sync/papers`. Interpreting the data-root prohibition recursively also rejects every example source; interpreting it narrowly needs an explicit exception for authored subtrees.

    **Change:** Put shared incoming roots outside the private daemon root, or specify an exact mount/ownership arrangement. Define allowed authored roots and denied internal subtrees separately, and make startup permission validation match that layout.

13. **Major — The sandbox lacks a launch/access mechanism, and parsing can precede it.**  
    **Concerned:** SEC-20, SEC-27–28; FLD-8; §8 Classify/Extract; §18 Core dependencies.

    A non-root daemon with no capabilities and `NoNewPrivileges` cannot ordinarily launch a child under another UID. That UID also cannot traverse private staging. The sidecar alternative does not specify per-input access or IPC. Meanwhile, frontmatter is parsed before classification and `.ics` members are expanded in Classify, before the explicitly sandboxed Extract stage.

    **Change:** Specify the actual pre-provisioned worker/service or narrow launcher, descriptor/IPC handoff, and filesystem/network restrictions for both deployment modes. Put untrusted frontmatter parsing, container enumeration, and recurrence materialization behind that boundary. Correct the pipeline to stage before admission consumes the staged hash.

14. **Major — Egress rules conflict with Access verification and hosted-provider operation.**  
    **Concerned:** SEC-3, SEC-17, SEC-28; §13.5; Appendix B.

    SEC-17 permits only trusted services and the tunnel connector, but Access verification needs signing-key acquisition and refresh. The connector is not an outbound key-discovery proxy. SEC-28’s provider egress restriction is also unconditional, potentially blocking hosted providers when `local_only` is false. Finally, the firewall is alternately required and merely a missing condition that downgrades the guarantee.

    **Change:** Separate provider-data egress from narrowly defined control-plane traffic such as DNS and Access keys, with explicit failure and rotation behavior. Define hosted-mode rules and whether missing firewall enforcement blocks operation or visibly selects a weaker mode. Qualify “nothing leaves my network”: public browser traffic deliberately traverses Cloudflare. Access signing keys require ongoing refresh, not just initial provisioning. [Cloudflare JWT validation](https://developers.cloudflare.com/cloudflare-one/access-controls/applications/http-apps/authorization-cookie/validating-json/)

15. **Major — The backup procedure neither covers all authoritative files nor specifies a consistent bounded-memory snapshot.**  
    **Concerned:** §15.10’s “short publication barrier” and “copies vault files”; §6 places trash outside the vault; §18 selects `.NET AesGcm`.

    Still-restorable trash is omitted. Releasing the barrier before copying permits mismatched state/files; holding it throughout copying makes the pause proportional to the corpus. External editors are unaffected by a daemon publication barrier. Manifest verification at restore detects inconsistency after the backup has already failed its purpose.

    **Change:** Define immutable version pinning, filesystem snapshots, or a complete-copy barrier with an explicit outage bound. Verify hashes during backup creation; include trash and define external-root capture. Specify a streaming authenticated archive format: one whole-archive call to the named `AesGcm` API requires complete plaintext/ciphertext buffers and does not scale to the admitted corpus under the heap target. [AesGcm API](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.aesgcm.encrypt?view=net-10.0)

16. **Major — Rebuild equivalence remains stronger than the retained inputs permit.**  
    **Concerned:** U10; §17 “Identical search results”; §8 enrichment “Stored in the index”; §16 deletes the index; A.2 stores embedding spaces and caches there.

    Rebuilding can produce different LLM tags, changing filter membership, even with unchanged source files. Deleted embedding caches cannot preserve vectors from an unavailable model. A state pointer to an active embedding-space ID does not preserve the full binding/generation descriptor stored in the deleted index. External index-mode files are additional recovery inputs.

    **Change:** Define equivalence precisely. Either retain the model-derived outcomes needed for exact reproduction, or promise preservation of identities, authored state, deterministic projections, and citation excerpts. State external-source prerequisites. Pin model outputs, generations, clock, and fixtures for automated equivalence tests.

17. **Major — Citation locators are unstable across generation-only changes, and marker validity still does not establish support.**  
    **Concerned:** ING-10 reprocesses “without changing their revisions”; §10.2 permits a chunk-ordinal locator and validates `[n]` markers; §20 assistant evaluation.

    Rechunking revision 3 can move the cited passage from ordinal 5 to ordinal 8. The revision still matches, so the stated changed-revision fallback does not protect “open passage.” Storing an excerpt hash without requiring its validation is insufficient. Separately, a valid marker can point to an irrelevant passage; current tests do not define supported-claim or nonempty-but-irrelevant abstention behavior.

    **Change:** Bind locators to an extraction/chunk generation or text fingerprint and require excerpt verification before opening a current location. Fall back to the retained historical excerpt on mismatch. Add claim-support and irrelevant-corpus evaluations with explicit acceptance criteria.

18. **Major — Vector memory is bounded by historical row-ID growth rather than live corpus size.**  
    **Concerned:** §9.1 and A.2: “one contiguous float array indexed by chunk row id.”

    SQLite row IDs are not dense live ordinals. Replacing older documents can increase the highest row ID while the live chunk count stays constant. Literal addressing by `rowid × dimensions` can therefore require about 41 GB of slots after reaching ten million historical IDs, despite only one million live vectors. A sufficiently large contiguous managed array also encounters runtime element-count limits. [Array limits](https://learn.microsoft.com/en-us/dotnet/api/system.array.maxlength)

    **Change:** Use a dense slot map independent of row IDs, with safe reclamation and bounded peak allocation during growth and activation. Test sustained update/delete churn. The approximately 4 GB baseline for one million live 1024-dimensional vectors is not itself the problem.

19. **Major — Embedding-space activation has counters but no complete cutover protocol.**  
    **Concerned:** §13.6, “activates atomically when coverage is complete”; A.2 `embedding_spaces`, `chunks`, and `chunk_vectors`.

    The corpus can change while a replacement space is building. The specification does not define how edits/deletes affect its denominator, how late jobs are fenced, or which exact revision/generation set must be covered at activation. A new binding with a smaller input limit may also require a new chunk layout, while the schema supports only one layout per document.

    **Change:** Define the active/building-space state machine, concurrent dual processing, catch-up barrier, restart recovery, and retirement. Specify whether migrations retain compatible chunk layouts, maintain separate generations, or reject incompatible changes until a controlled rebuild.

20. **Major — Materialized calendar instances are not represented in the public query contract.**  
    **Concerned:** §§5.5–5.7, 9.3; A.2 `event_occurrences`; C and E.

    A weekly master beginning in 2025 has one document ID and one `occurred_at`, but “meetings last week” needs several 2026 instance records. The contract still sorts and paginates by document `(sort, id)` without an occurrence identity or effective instance fields. `ends_at` is optional in E but mandatory in the index and overlap rule. Date-time-only schemas also do not directly represent the promised date-only/floating semantics.

    **Change:** Define occurrence result identity, instance-aware filtering/sorting/cursors, exception precedence, cancellation treatment, and normalization of missing ends and all-day/floating values. Publish coverage with the corresponding materialization generation; retained state coverage must not claim completeness after index deletion. Cache pure calendar parsing separately from window-dependent expansion.

21. **Major — Calendar files already depend on the shared-membership design deferred to Appendix F.**  
    **Concerned:** §5.3 source-scoped natural keys; §5.7; ING-9; A.1 `container_members`; F.

    Two `.ics` files in one source containing the same UID become one logical document. Removing the event from one successful snapshot then suppresses that document even if the other file still contains it. The later-`DTSTAMP` rule handles duplicates within an import, not an older export imported after a newer one. Automatically removed members also lack a clear reappearance rule distinct from user suppression.

    **Change:** Either scope identities per container and accept duplicates, or implement shared membership in v1. Remove individual memberships, derive effective presence across them, define cross-import version precedence, and separate automatic absence from explicit “never index again.”

22. **Major — Reclassification cannot both preserve properties and validate the destination schema.**  
    **Concerned:** §5.4; ING-8 “never discards `props`”; E’s `additionalProperties: false`, especially empty `note` and `file` schemas.

    An article containing `author` and `url` cannot become a note while preserving those properties and satisfying the note schema. Removed/custom types have the same problem. Schema migrations also alter properties while ING-10 describes generation changes as not changing document revisions.

    **Change:** Separate preserved original properties from validated effective properties. Define archive/migration semantics and which changes advance the document revision. Reclassification must update time, people, natural keys, indexed properties, and chunks under the publication contract.

23. **Major — Read caps have incompatible accounting interpretations and an undefined completeness contract.**  
    **Concerned:** CHAT-5 says “the provider sees at most” 12 documents; §10.2 limits tool results per turn but also sends history and summaries; §9.3 permits 200 records.

    Historical context can already contain more than twelve documents before retrieval starts. Counting every event row makes an ordinary twenty-meeting enumeration partial; excluding metadata permits substantial disclosure outside the apparent document limit. Initial retrieval, neighbors, summaries, profile content, and provider switches have no explicit accounting rule. The provider can also expand the surfaced set through its own searches, so that set is not a relevance boundary.

    **Change:** Define whether the cap limits newly retrieved data or total outbound exposure, and count every context component consistently. State bounded disclosure as the guarantee unless expansion is separately authorized. Provide explicit partial/continuation or structured aggregation behavior for larger enumerations, and test it.

24. **Major — The auto-approval exclusion depends on provenance the executor does not track.**  
    **Concerned:** CHAT-6: “any write derived from retrieved untrusted content always wait”; §10.2 context assembly; SEC-12.

    The executor records arguments, targets, and policy generation, but no mechanism determines whether generated prose was derived from retrieved content. A model can blend the current message with retrieved passages or a historical summary. Asking it to declare the origin would make the model responsible for the authorization boundary.

    **Change:** Use a conservative, deterministic eligibility rule—for example, mark a turn ineligible for auto-approval once untrusted retrieved content or its derivatives enter context—or remove auto-approval for those flows. Define enrichment write-back’s authorization separately so it does not become another implicit mutation exception.

25. **Major — Ordinary asynchronous ingestion still lacks the small-capture degradation path.**  
    **Concerned:** ING-4 applies to small text captures; §8 still sequences `embed → index`; ING-6 leaves permanent failures searchable only by title/path.

    With embeddings unavailable, a dropped PDF or ordinary watched Markdown file can remain unsearchable by its extracted text even though extraction succeeded. This differs materially from JSON/MCP capture and can defeat U1 while the lexical index is healthy. Optional classification failure can similarly become a prerequisite failure unless its fallback is specified.

    **Change:** Define projection readiness independently for every door. Publish lexical/text projections after successful extraction, queue missing vectors, and report `indexed_partial`; retain revision/generation fencing when vector completion arrives. Specify classification timeout/failure fallback independently of extraction failure.

26. **Major — Capability declarations still promise operations the provider contract cannot express.**  
    **Concerned:** §§13.2–13.3; §8 batch backfills; §10.2 native compaction; B’s arbitrary model bindings.

    A `batch` capability boolean does not provide submission, polling, cancellation, durable remote IDs, or result collection. Native compaction similarly lacks a portable operation contract. Required model limits have no stated resolution/override mechanism when an adapter encounters an unfamiliar model ID.

    **Change:** Add optional batch and portable-compaction interfaces with persisted lifecycle state, or defer those features. Define capability-resolution precedence and explicit binding overrides. This must remain adapter-owned rather than requiring vendor checks in the engine.

27. **Major — The roadmap’s automated gates do not match its implementing milestones.**  
    **Concerned:** §19 “all automated unless noted”; §§15.10, 16, 20, 22.

    M0 requires MCP-tool authorization and circuit revocation, while MCP and UI are scheduled for M2/M3. `/ready` includes a sandbox that arrives in M1. Encrypted pre-migration snapshots are mandatory before backup implementation in M4. M1 tests recurrence bombs before calendar parsing arrives in M4. Several promised formats, including DOCX, lack an explicit implementation milestone. “None” of the open questions gate M0 conflicts with requiring `/ready` green against still-unselected vLLM bindings.

    **Change:** Bring thin real test surfaces forward or explicitly distinguish middleware contract gates from later integrations. Align format tests with implementation, provide migration recovery from the first migration, and assign every v1 format. Replace broad “scenario passes” language with fixtures and assertions, including citation support and completeness. Pin external test infrastructure or separate deterministic CI from live-provider qualification.

28. **Minor — Ingestion targets cannot be evaluated against the default configuration.**  
    **Concerned:** U1’s thirty-second capture; FLD-2’s ten-minute rescan; B’s inbox `watch: poll`; §17’s “≥ 20 PDFs/min” and “bounded by provider rate limits, not the pipeline.”

    The only specified periodic polling interval can delay inbox discovery by nearly ten minutes. PDF throughput has no page/size distribution or CPU baseline; four valid extractions taking 120 seconds each yield two files per minute before provider work.

    **Change:** Define a poll interval compatible with the capture story, or qualify that story by watch mode. Pin representative throughput fixtures, CPU, concurrency, and inclusion of sandbox startup. Measure aggregate process/container peak memory as well as daemon heap; do not assert that parsing can never be the bottleneck.

29. **Minor — Public-origin MCP lacks a complete outer-credential configuration.**  
    **Concerned:** §7.4 requires an Access service token through the tunnel; §12 and D describe storing only a SecondBrain key.

    The documented bridge configuration cannot supply both authentication layers. This is an integration omission, not an inherent incompatibility between Access and MCP.

    **Change:** Specify bridge-owned Access credential storage, selection, header injection, and rotation, and provide a complete tunnel example. Test the bridge path and identify direct HTTP clients that can supply the required credentials.

30. **Minor — The supposedly shared schemas and examples still disagree.**  
    **Concerned:** RET-1 and C use `snippet()`/`<b>` highlights while SEC-22 requires ranges; §7.4 says `brain_remember` accepts v1 types while D allows three; §5.4 lists note `aliases` as properties while E reserves them as base fields; §§2, 4 and G retain vector-file terminology.

    Implementers cannot treat all these elements as normative. In particular, the HTML snippet example encourages precisely the rendering path the security revision intended to remove.

    **Change:** Define plain snippet text plus highlight ranges and offset units; choose the capture type subset explicitly; reconcile field locations and storage terminology. Validate examples against the normative schemas in CI.

## Readiness for M0

**Not ready for M0 as written.** It is ready for focused implementation spikes that resolve the foundation contracts.

Before treating M0 as an accepted milestone, change:

- **Authorization:** reconcile direct creation versus proposed mutation, implicit upserts, inference spending, conversation ownership, interactive CLI authentication, step-up, and per-credential revocation.
- **Durable state:** specify the mutation journal, publication/lifecycle fences, replay-result representation, and payload ownership/retention sufficiently to implement the initial schemas.
- **Deployment:** provide a workable source/staging/key-ring ownership layout, sandbox launch mechanism, provisioning sequence, and egress policy compatible with Access and hosted mode.
- **Acceptance:** align M0’s real components with its tests, define migration recovery, and select the binding or test substitute needed for `/ready`.

The precise vector layout and performance tuning can be settled during early M1, before its publication and scale gates close. Read-cap UX, claim-support evaluation, and remote MCP compatibility belong in M2 acceptance. Calendar occurrence, membership, and temporal normalization can be completed before M4 implementation, but they must be acknowledged as v1 work rather than deferred infrastructure. Backup archive details can mature during the build; authoritative-file inventory and snapshot semantics must be decided before claiming recovery guarantees.

## Questions the author must answer

1. Which automatic updates are authorized by registering a folder source, and how do API container re-imports differ?
2. Are query/index embeddings implicit services of `read`/`write`, or do they require `infer`? Who pays for admitted background work after revocation?
3. Can the CLI obtain an interactive session, and which sensitive actions remain available to admin keys?
4. Does revoking one credential affect only that credential? Who owns conversations across sessions and client keys?
5. Does logical purge redact conversation history, summaries, debug logs, and owned container originals, or are some explicit retained classes?
6. Must rebuild reproduce model-derived tags and rankings exactly, or preserve authored truth and deterministic projections while reporting regenerated results?
7. Is calendar identity per source or per container, and which export is authoritative when several contain conflicting versions?
8. Do read caps bound incremental retrieval or the entire outbound context? Must broad structured questions produce complete enumerations?
9. What deterministic condition makes a write eligible for auto-approval after retrieval or conversation compaction?
10. Does missing firewall enforcement block operation, and what non-provider egress is explicitly permitted?

## What is strong

- The three-store separation preserves a viable files-first architecture.
- SQL prefiltering and exact vector scoring give retrieval a concrete implementation path.
- Explicit provider bindings and trusted-service declarations preserve the vendor-neutral design.
- Download-only originals, scoped bridge credentials, immutable proposals, and bounded extraction are sound mechanisms once their surrounding contracts are reconciled.
