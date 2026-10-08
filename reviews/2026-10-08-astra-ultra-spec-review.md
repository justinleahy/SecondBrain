## Overall assessment

The architecture is coherent enough to prototype, but the spec is not buildable to its stated guarantees as written. The largest problems concern authoritative storage, privacy enforcement, mutation consistency, and identity across imports; these require design decisions before implementation. The typed-content addition is useful, but calendar recurrence, container membership, temporal semantics, and schema evolution substantially increase the work beyond adding a `type` column and JSON properties. Provider neutrality is achievable, but the current interfaces omit information needed for correct embeddings, capability validation, conversation portability, and safe retries. Resolve those contracts and reduce the first-release surface before treating the roadmap or performance targets as commitments.

## Findings

1. **Critical — The index contains authoritative state that cannot be rebuilt from the vault.**  
   **Sections/elements:** D3; §2.2, “The database is a cache of the files”; §6, imported Markdown is “cached and rebuildable”; §8 Enrich, “Stored in the DB”; §16, conversations live in `index.db`; Appendix A.  
   Deleting the index can lose conversations, user overrides on external documents, manually created relationships, entity decisions, runtime-created source definitions, and generated identities for imported records. Re-extraction cannot reconstruct user decisions or reproduce nondeterministic enrichment. Originals indexed in external folders are also absent from the vault, contradicting a literal vault-only rebuild.  
   **Recommended change:** Define the authoritative representation of every persisted field. Store stable identities, overrides, relationships, tombstones, conversations, and other authored state in durable files; distinguish them from disposable extraction and search caches. Provide canonical content or snapshots for external sources if vault-only reconstruction remains mandatory. Make a clean rebuild preserve identities and authored metadata, not merely produce searchable text.

2. **Critical — `no_cloud` does not cover all routes by which protected information leaves the machine.**  
   **Sections/elements:** §15, “Documents from such a source are never sent to a non-local provider”; “queries are embedded with each model in use”; §§8, 9, 13.5.  
   A local assistant can read private mail, formulate a search query containing its contents, and send that query to the hosted embedding provider. Hosted reranking can receive protected candidates before chat withholding. Switching a previously local conversation to hosted chat can disclose old tool results, answers, or compaction summaries. Classification, enrichment, attachments, and notes derived from protected documents are similarly unspecified.  
   **Recommended change:** Enforce privacy centrally before **every** provider request. Persist and propagate restrictions through derived content, queries, conversation blocks, and summaries. Filter before reranking and context assembly; skip prohibited embedding legs. Define the boundary explicitly: the daemon cannot guarantee what an independent REST/MCP client does with returned content.

3. **Critical — The definition of “local” invalidates the privacy guarantee.**  
   **Section/element:** §15, an endpoint is local when its host is “loopback or a private address.”  
   A private address can identify another machine or a cloud deployment. A loopback service can forward requests to a hosted provider. Thus `privacy.local_only: true` does not establish “nothing leaves my machine.”  
   **Recommended change:** Distinguish in-process execution, trusted local services, trusted LAN services, and remote services. Require explicit authorization for LAN/proxy egress; explain that an address alone cannot attest where inference runs. Apply policy checks to configuration reloads, fallbacks, redirects, and batch operations, not just startup.

4. **Critical — Imported active content can share the credential-bearing application origin.**  
   **Sections/elements:** §7.3, `/documents/{id}/file` returns original bytes “with the stored MIME type”; §11, the UI receives an injected API key.  
   HTML is an accepted format. Serving an imported HTML document inline at the daemon origin can execute its scripts with the same browser authority as the application. This converts an ingested document into executable instructions, independently of prompt injection. Highlighted snippets and rendered Markdown also lack a sanitization contract.  
   **Recommended change:** Serve active originals as downloads or through a genuinely isolated origin/sandbox without application credentials. Specify safe rendering, allowed URL schemes, `nosniff`, restrictive CSP, and handling of remote images and embedded resources. Never render arbitrary original bytes at the privileged UI origin.

5. **Major — Authentication bootstrap and Origin validation are incomplete.**  
   **Sections/elements:** SEC-2, “Every HTTP request … carries an API key”; §11, “no login screen on loopback”; SEC-3 only covers MCP; Appendix B, `cors_origins: []`.  
   The initial UI request cannot already possess the key that the daemon is supposed to inject. No bootstrap exception or trust check is defined. DNS rebinding can target that bootstrap route rather than `/mcp`; GET requests may lack an Origin header. An empty allowlist also has no documented same-origin behavior, and a reverse proxy’s loopback connection must not be mistaken for a local browser.  
   **Recommended change:** Specify credential bootstrap, canonical Host validation, exact Origin matching, absent versus `null` Origin behavior, and trusted proxy handling across the UI and API. Use a bounded UI session credential rather than distributing a general administrative key. Pin the supported MCP protocol versions.

6. **Major — `updated_at` cannot protect files from concurrent external edits.**  
   **Sections/elements:** §7.3 `PATCH` supports `If-Match` on `updated_at`; §7.4 `brain_update`; §21, “the watcher wins … and the assistant’s write is retried.”  
   An editor can save a file during the watcher’s 1.5-second debounce. The database timestamp remains unchanged, so an assistant write passes its precondition and overwrites the edit. Automatically retrying a previously approved replacement against different content can repeat that loss. The endpoint says preconditions are supported, while the risk table says they are required.  
   **Recommended change:** Require a source-revision precondition, revalidate the actual file, and define conflict-preserving writes for uncoordinated editors. Do not claim atomic compare-and-swap against arbitrary editors without an explicit mechanism. A changed target must trigger conflict handling and renewed approval of a rebased operation. Apply external-write restrictions equally to REST, MCP, CLI, and UI.

7. **Major — The single-writer rule does not prevent stale jobs or make filesystem and database changes atomic.**  
   **Sections/elements:** §8 Admit updates the Document row; Index replaces chunks transactionally; four workers; §16 timed-out jobs are reaped; §17 crash durability.  
   Edit A can start processing, edit B can finish, and A can later overwrite B’s index. A job can republish a deleted document. A timed-out worker may complete after its replacement starts. Updating document metadata during Admit can also expose new metadata with old chunks. SQLite transactions do not cover vault writes or inbox moves.  
   **Recommended change:** Introduce document revisions and fenced job attempts. Publish only if the job’s revision is still current, including enrichment and link updates. Stage metadata and all search projections together. Specify a recoverable file-write/move protocol, durable request idempotency, startup reconciliation, and crash behavior at each publication boundary.

8. **Major — Deletion can resurrect content, and “purge” has no consistent meaning.**  
   **Sections/elements:** FLD-6; `DELETE /documents/{id}`; SEC-8; §22 question 8, “searchable tombstones”; Appendix A `parent_id`.  
   Deleting an indexed external document without deleting its original permits the next scan or rebuild to recreate it. Deleting one email leaves it inside its `.mbox`. Purging a parent with attachment children conflicts with the declared foreign key unless application behavior is invented. Searchable tombstones contradict immediate removal from search.  
   **Recommended change:** Define deletion separately for managed files, external references, container members, and attachments. Persist suppression records outside the disposable index. Define restore and re-import behavior, child handling, and shared-cache ownership. State whether purge means removal from active retrieval or removal from originals and live derivatives; explicitly address retained containers, conversations, traces, and backups.

9. **Major — Logical identity, physical location, and content deduplication are conflated.**  
   **Sections/elements:** §5 `(source_id, path)` identity; ING-2 same-source content-hash dedupe; Appendix A `UNIQUE(source_id, natural_key)`.  
   Two files with identical bytes are distinct under the path rule but identical under ING-2. Deleting or moving one becomes ambiguous. The natural-key constraint omits its namespace, so a contact UID and event UID can collide within the same inbox source. Stable document identity is also unspecified for duplicate frontmatter IDs, missing Message-IDs, and conflicting records bearing the same key.  
   **Recommended change:** Separate logical document identity, source occurrences, content-addressed computation reuse, and request idempotency. Define namespaced natural keys, their scope, collision handling, and missing-key behavior. Do not collapse unrelated documents solely because their bytes match. Qualify re-import no-ops as applying to unchanged records and define revision precedence for changed records.

10. **Major — Containers need membership, checkpointing, and reconciliation contracts.**  
    **Sections/elements:** ING-9, `container path#natural key`; FLD-9 move-after-success; §19 M4, 50k-message import.  
    The same message can occur in multiple exports, but one document has only one path. Removing one export can therefore delete shared content or discard provenance. The spec does not distinguish additive imports from authoritative snapshots, define what missing records mean, or explain partial parse failures and interrupted imports. A container upload also produces many documents while the REST contract returns one document.  
    **Recommended change:** Represent import occurrences and container membership explicitly. Checkpoint per record, reconcile removals only after a complete successful snapshot, and define changed-container handling, partial success, original ownership, and completion responses. Separate container limits from member limits: 50k messages fit the default 50 MB cap only at roughly 1 KB per message.

11. **Major — Cache keys do not identify the computation being cached.**  
    **Sections/elements:** §8 `.brain/cache/<hash>.md`; embedding cache `(content_hash, model)`; Appendix A `sha256(heading_path + text)`.  
    Unchanged bytes can require different extraction after a type, extractor, schema, or normalization change. A model alias can refer to different models at different endpoints. Changing dimensions under the same model name can reuse incompatible vectors. Title, speaker, timestamp, or other prefixes can change the actual embedding input without changing the specified chunk hash. Containers additionally need distinct per-record hashes.  
    **Recommended change:** Fingerprint extraction by record content and extractor/schema/options versions. Fingerprint embedding spaces by provider/model revision, dimensions, task mode, preprocessing, and normalization. Hash the exact serialized embedding input. Keep canonical identities and authored metadata out of disposable cache entries.

12. **Major — The schema cannot represent simultaneous embedding spaces or migration generations.**  
    **Sections/elements:** §13.5 keeps old vectors during re-embedding; §15 separate `embed_local`; Appendix A one fixed-dimensional `chunk_vectors` and one model/dimension pair in `meta`; Appendix B uses 1024 and 768 dimensions.  
    The schema cannot distinguish hosted, local, old, and replacement vector sets. Search against retained old vectors still requires queries embedded by the old model; retaining vectors does not preserve semantic availability when that model disappears. Concurrent edits and deletes can also leave the replacement generation stale at cutover.  
    **Recommended change:** Define named embedding spaces, versioned generations, query bindings, coverage tracking, catch-up processing, atomic activation, restart behavior, and garbage collection. Specify keyword degradation when a required query model is unavailable.

13. **Major — The typed registry lacks normative schemas and an evolution contract.**  
    **Sections/elements:** §5.1 “Key properties”; §6 flat frontmatter; §14 hot-reloaded types; `PATCH` reclassification.  
    Field names do not establish interoperable shapes for `from`, `attendees`, `participants`, `recurrence`, task `status`, units, required values, or nulls. Contact `UID` and calendar recurrence identity are used for dedupe without corresponding defined properties. Flat frontmatter introduces collisions between task status and ingestion status, base fields and custom properties, and duplicated fields such as `thread_id`.  
    **Recommended change:** Supply versioned schemas, reserved names, canonical frontmatter mappings, and precedence rules. Define schema migration and unknown-type preservation. Reclassification must preserve original properties and provenance while atomically updating derived fields, people, natural keys, chunks, and indexes. Metadata-only changes must invalidate every affected projection, not just changes to `content`.

14. **Major — Classification precedence contradicts itself and cannot reliably round-trip typed Markdown.**  
    **Sections/elements:** ING-8 and §8 put door declarations before signatures; FLD-11 says signatures “still win”; FLD-8 omits `type` and typed properties from frontmatter parsing.  
    A transcript-default folder containing `.ics` has competing classification rules. An imported email represented as Markdown can lose its type during rebuild if the frontmatter requirements are followed literally. Defaults, glob mappings, explicit declarations, manual overrides, and persisted classification are not distinguished.  
    **Recommended change:** Publish one precedence matrix. Separate physical format/container detection from semantic type assignment. Parse persisted type/properties before classification, preserve explicit overrides, and define behavior when declared type and physical format disagree.

15. **Major — Temporal semantics are incomplete and internally inconsistent.**  
    **Sections/elements:** D11 and §5.1 require `occurred_at` everywhere; Appendix A allows null; §7.3 uses UTC timestamps; Appendix D alternates “ISO date” and “ISO date-time”; RET-11 versus CHAT-10.  
    `file`, `contact`, and `task` have no occurrence mapping. Articles may lack publication dates; transcripts may contain only relative timestamps. Fabricating ingestion time would misrepresent when something happened. A single timestamp cannot represent task creation, deadline, and completion, or an event’s full interval. “Last week” and daily grouping have no instance time zone or boundary rules.  
    **Recommended change:** Define nullable occurrence time with basis, precision, and provenance. Preserve date-only, floating, and zoned values where necessary. Specify instance time zone, week boundaries, half-open query intervals, null ordering, and event overlap. Distinguish activity-by-update queries from occurrence queries; do not insist every temporal question uses the same field.

16. **Major — Calendar recurrence can silently make structured answers incomplete or wrong.**  
    **Sections/elements:** §8, “recurrences expanded for a bounded window”; §5 natural key; U15; RET-11.  
    There is no horizon, advancement job, or incomplete-coverage signal. A weekly meeting imported once can disappear from later queries even though its source is unchanged. Cancellation, exceptions, moved occurrences, stale exports, and master updates lack reconciliation rules. A moved occurrence must retain its original recurrence identity rather than adopt its new start time.  
    **Recommended change:** Preserve masters, exceptions, cancellations, revision metadata, and original recurrence identities. Track materialized coverage and extend it on schedule or demand. Define handling of all-day events, DST, floating time, exclusions, and removed occurrences. Never return an apparently complete calendar answer outside known coverage.

17. **Major — Mandatory single-chunk records conflict with bounded processing and provider neutrality.**  
    **Sections/elements:** §5.1 emails are one chunk, events “never split,” contacts/tasks single chunk; §8 and Appendix B maximum 1024 tokens.  
    Long emails, event descriptions, attendee lists, or contact notes can exceed both the configured chunk maximum and a selected embedding model’s input limit. Approximate token counts do not prevent provider rejection or truncation. A giant record is also a poor retrieval passage.  
    **Recommended change:** Preserve one logical document per record while permitting multiple bounded retrieval chunks. Prefer one chunk when it fits. Repeat necessary record context and retain source spans. Define explicit oversize handling and validate against the actual model’s input constraints.

18. **Major — Extraction and normalization can remove knowledge and break source fidelity.**  
    **Sections/elements:** §8 strips email quotes/signatures and collapses whitespace/removes zero-width characters; §11 promises collapsed quotes and exact passage highlighting.  
    Inline email replies can occur inside quoted regions; signatures can contain relevant contact information. Blanket whitespace normalization can alter Markdown code, tables, or meaning-bearing text. Retaining original bytes is insufficient when assistant read tools expose only the stripped extraction. Offsets into normalized text also do not automatically locate original content.  
    **Recommended change:** Preserve a lossless readable representation separately from retrieval-oriented cleanup. Treat quote/signature detection conservatively and retain accessible segments. Make normalization format-aware and maintain mappings from searchable text to source locations.

19. **Major — Transcript attribution is promised but absent from the data and citation contracts.**  
    **Sections/elements:** U16; §5.1 speaker/timestamp on every chunk; Appendix A `chunks`; RET-7; Appendix C citations.  
    A 400-token window can contain several speakers or overlapping turns. A single `speaker @ timestamp` prefix cannot represent that faithfully. Neither the chunk schema nor the result contract includes speaker spans or media time ranges. A document-level `person` filter cannot establish that Sarah spoke the returned passage.  
    **Recommended change:** Define transcript segments with optional speaker identity, relative start/end times, and source offsets. Map each chunk and citation to its constituent segments. Expose attribution and media ranges in results, and preserve unknown speakers or missing absolute meeting dates explicitly.

20. **Major — Person filters cannot express advertised questions precisely.**  
    **Sections/elements:** U14; RET-12, “emails from Sarah”; Appendix D `person` matches any role; Appendix A identifiers are stored “as given.”  
    An email **to** Sarah qualifies for a query asking for emails **from** Sarah. It is also undefined whether a display name, formatted mailbox, bare address, and differently cased identifier match. Phase-2 entities cannot supply missing v1 query semantics.  
    **Recommended change:** Preserve structured person references with normalized identifiers and display names. Add role-qualified filters and define matching behavior consistently across SQL, hybrid search, and MCP. Keep document participation separate from passage-level speaker attribution.

21. **Major — REST/MCP structured-query parity and pagination are already broken.**  
    **Sections/elements:** `brain_query.properties`; `GET /documents` lacks properties filtering; Appendix D omits `cursor` while forbidding additional properties; §7.4 promises `next_cursor`.  
    The stdio bridge has no specified REST mapping for `properties: {"status":"open"}`; REST `status` means ingestion state. After 200 records, an MCP client cannot submit the returned cursor. Whole-thread and “all meetings” queries can therefore remain permanently truncated.  
    **Recommended change:** Define one structured-query contract with typed predicates, role filters, cursor, sort direction, deterministic ID tie-breaking, and coverage/truncation indicators. Expose it through both REST and MCP, using a POST query endpoint if necessary. Specify filter semantics for `/ask` and whether default single-pass answers use structured querying for non-topical questions.

22. **Major — SQL filter pushdown lacks a viable physical retrieval design.**  
    **Sections/elements:** RET-3; RET-10; Appendix A bare vector table and `properties_json`; Appendix B custom indexed fields.  
    Joining global vector top-50 results to filtered documents is post-filtering: it can miss every qualifying record. Many-to-many people/tags and arbitrary typed properties need a deliberate candidate restriction strategy. The spec also promises indexed custom properties without defining their indexes or reload lifecycle; all-types chronological queries have no corresponding leading `occurred_at` index in the sketch.  
    **Recommended change:** Specify and prototype filtered KNN plans and indexed property storage. Include a correct exact-distance fallback where appropriate. Test filters whose matches all fall outside unfiltered top-k. Do not assume arbitrary joins become prefilters inside the vector search; sqlite-vec documents specific filtering facilities and limitations. [sqlite-vec filtering documentation](https://alexgarcia.xyz/sqlite-vec/features/vec0.html)

23. **Major — The fixed candidate budget does not achieve document diversity.**  
    **Section/element:** §9, top 50 per leg → fusion → three-chunk per-document cap.  
    If one long document occupies both top-50 lists, applying the cap leaves three results. Other relevant documents at rank 51 never enter consideration. The cap prevents redundant output but does not prevent one document from crowding others out during candidate selection.  
    **Recommended change:** Diversify within retrieval legs, retrieve document candidates first, or expand candidates adaptively until sufficient distinct documents remain. Bound expansion and test this adversarial case. Specify how neighbor expansion interacts with caps, filters, privacy, deduplication, and context limits.

24. **Major — The SQL sketch does not implement the specified metric and leaves FTS maintenance unresolved.**  
    **Sections/elements:** RET-1 cosine similarity; Appendix A `embedding FLOAT[1024]` and FTS `content=''`; §8 replacement transaction; RET-7 highlighted snippets.  
    `vec0` defaults to L2, which need not rank arbitrary provider vectors like cosine. Contentless FTS does not support ordinary update/delete behavior or provide text to native snippet/highlight functions. Literal implementation of the apparent replacement strategy will therefore produce different ranking or require undocumented maintenance logic.  
    **Recommended change:** Declare cosine explicitly or enforce validated unit normalization. Choose an external-content FTS projection, or specify contentless deletion and separate highlighting precisely. Keep all projections transactionally consistent. [sqlite-vec distance configuration](https://alexgarcia.xyz/sqlite-vec/features/knn.html), [SQLite FTS5 documentation](https://www.sqlite.org/fts5.html#external_content_and_contentless_tables)

25. **Major — Provider capabilities need to describe model bindings and limits, not just adapters.**  
    **Sections/elements:** §13.2 `capabilities()` and `embed(texts[])`; §13.3 booleans; Appendix B, “any model the provider serves works.”  
    Models behind one adapter can differ in tool support, context limits, output limits, schema support, and reasoning controls. The embedding interface cannot express query-versus-document intent, required by supported providers such as Cohere. Fixed 64-item batches ignore token and request limits. Batch backfills and native compaction are promised without corresponding operations in the interfaces.  
    **Recommended change:** Resolve capabilities per concrete binding, including limits and supported feature combinations. Add embedding intent and explicit truncation policy; provide batch lifecycle/compaction interfaces or defer those features. Treat reasoning levels as qualitative preferences and expose their effective mapping or ignored status. Reconcile “streaming is always on” with non-streaming fallback. [Cohere embedding input types](https://docs.cohere.com/v2/docs/semantic-search-embed)

26. **Major — “Resume exactly on any provider” is an unsupportable conversation contract.**  
    **Sections/elements:** §5 Conversation/Message; CHAT-8; §13.5 dropping provider-specific blocks; native compaction in §10.1.  
    Visible messages are portable; provider-native continuation state, compaction artifacts, hidden reasoning, and unfinished tool exchanges are not necessarily portable. A smaller replacement context window can also require additional summarization. Dropping provider-specific blocks is not an exact restoration of the previous state.  
    **Recommended change:** Promise preservation of user-visible history and explain any lossy compaction. Maintain a portable canonical transcript and portable summaries, with provider-specific continuation metadata kept separately. Define safe switch boundaries and handling of incompatible or incomplete histories.

27. **Major — Retrying a failed assistant turn can repeat successful mutations.**  
    **Sections/elements:** §10.2 fallback after a failed turn; §13.5 shared retry client; chat SSE events; Appendix A messages.  
    A model can successfully append to a note and then lose its stream. Retrying the turn on the same or fallback model can append again. Browser reconnects and repeated `/chat` submissions have a similar ambiguity. Collapsing errors and refusals into one type also conflicts with the later retryable/non-retryable distinction.  
    **Recommended change:** Persist turn IDs, ordered events, tool execution IDs, and committed results. Resume from execution state rather than replaying side effects. Define disconnect/cancellation behavior, partial-answer status, same-conversation concurrency, and refusal versus transient failure handling. State when fallback is permitted after output or mutation has already occurred.

28. **Major — Citations have no revision-stability contract.**  
    **Sections/elements:** §8 replaces chunks; §10.2 promises stable IDs; Appendix A chunk ULIDs and stored citations.  
    Reindexing can remove cited chunk IDs. Reusing IDs by ordinal can instead redirect an old citation to a different passage. A source may also change between retrieval and completion of the answer.  
    **Recommended change:** Cite a document revision and immutable passage/source locator, retaining an excerpt or hash sufficient to show what was actually used. Define historical retention and explicit changed/deleted-source states. Never silently resolve an old citation against the current chunk occupying the same position.

29. **Major — Marker parsing does not establish exact citations or grounded answers.**  
    **Sections/elements:** CHAT-1; §10.2 `[n]` fallback produces the same citation objects as native citations; Appendix C `start`/`end`.  
    A marker identifies a passage, not an exact quote span or proof that the claim follows from it. Multi-search numbering, literal bracketed numbers, invalid markers, and uncited assertions are unspecified. Semantic retrieval also returns nearest neighbors from an irrelevant corpus, so “results exist” cannot mean “evidence exists.” Appendix C’s answer is 60 characters long; offsets 64–101 cannot be answer offsets if that is their intended meaning.  
    **Recommended change:** Distinguish passage-level references, exact source spans, and answer spans, with explicit offset units. Validate markers against an immutable per-answer passage map and expose actual citation granularity. Define unsupported-claim and insufficient-evidence behavior, including nonempty irrelevant corpora, partial results, and withheld sources. Evaluate citation support, not just citation presence.

30. **Major — Provider outages and budget stops lack a unified degradation contract.**  
    **Sections/elements:** ING-4 keyword fallback; §8 embed-before-index; §9 hybrid search; §10.2 token hard stop; §16 health.  
    Capture degrades when embeddings are unavailable, but ordinary indexing and hybrid querying do not specify equivalent behavior. A slow healthy provider can violate capture deadlines without triggering the outage case. Rerank or enrichment failure may unnecessarily block useful results. Failed or withheld retrieval must not be reported as “not in your brain.” Concurrent calls and batch work can overshoot a daily token budget before usage is known.  
    **Recommended change:** Define lexical, vector, and enrichment readiness separately; set bounded deadlines and return explicit degraded/partial states. Separate liveness from provider readiness. Specify reservation/settlement or bounded overshoot for budgets, accounting for concurrent, failed, cached, and batch requests.

31. **Major — Assistant authorization is described as UI behavior rather than an execution contract.**  
    **Sections/elements:** CHAT-4 “never followed”; CHAT-5 write tools; CHAT-6 UI previews; `/ask` and `brain_ask` read-only; REST/CLI chat.  
    Prompts and untrusted wrappers cannot guarantee that a model never follows injected instructions. Brain-only tools can still retrieve unrelated private data or corrupt knowledge. The spec defines no approval protocol for CLI/REST chat and no endpoint for accepting pending writes. A shared assistant loop could also expose writes through a supposedly read-only answer tool.  
    **Recommended change:** Enforce caller scopes and allowed tools outside the model. Give `/ask` and `brain_ask` a read-only executor. Define pending operations and approve/edit/discard semantics across interactive surfaces, binding approval to exact arguments and target revisions. Treat prompt injection resistance as a tested mitigation, with deterministic authority boundaries supplying the guarantee.

32. **Major — The stdio root credential defeats client scoping and reliable provenance.**  
    **Sections/elements:** D1 client-specific keys; §7.4 stdio “uses the root key”; `mcp:<client name>` from initialization; SEC-2 scopes.  
    Every stdio client shares administrative authority and one revocation identity. The handshake name is self-asserted, so it cannot establish authenticated provenance. Scope names alone do not say who may add filesystem sources, incur model costs, change configuration, approve writes, or read protected material.  
    **Recommended change:** Support dedicated scoped credentials per bridge client. Derive authenticated identity from the credential and retain the handshake name only as claimed metadata. Define an endpoint/tool permission matrix and propagate authorization into background jobs and assistant executions. Keep root credentials for administration.

33. **Major — Secret placement and logging contradict the privacy and deletion claims.**  
    **Sections/elements:** `.brain/secrets` inside the vault; SEC-6, “Secrets are never written into the vault”; §6 “never synced”; SEC-5 logs every tool call’s inputs.  
    File mode `0600` does not prevent same-user backup or sync software from copying secrets. Export exclusions do not control those tools. Logging complete create/update inputs creates additional copies of sensitive document bodies that outlive deletion and can make logs a second knowledge store.  
    **Recommended change:** Put recoverable secrets outside the vault or in an OS credential store. Define backup/restore treatment of credential verifiers and bootstrap secrets. Default logs to IDs and redacted metadata, with explicit retention for sensitive debugging. Align the resulting retention matrix with purge semantics.

34. **Major — Entity resolution contradicts itself and cannot support exact undo with the shown state.**  
    **Sections/elements:** §5.2 and §8 fuzzy matches above a threshold; §21 names are proposed, never applied; §20 merge/split exact inverses; Appendix A `merged_into`.  
    Two implementations can legitimately disagree about automatic name matching. Bare handles, shared email addresses, and provider attendee IDs are not universally unique person identifiers. A redirect alone does not explain how to undo chained merges, transferred keys, or links created after a merge.  
    **Recommended change:** Require name-only matches to remain proposals. Namespace strong keys and define conflict/shared-identity handling. Preserve resolution evidence and merge history, prohibit cycles, and specify assignment of post-merge links during a split. Test merge → new ingestion → split, not just immediate reversal.

35. **Major — Input-byte limits do not bound extraction work or filesystem effects.**  
    **Sections/elements:** ING-7 50 MB; §7.3 100 MB request limit; §8 worker threads; SEC-4 path handling.  
    DOCX decompression, nested MIME parts, pathological PDFs, record expansion, and recurrence generation can consume far more resources than input size suggests. A worker thread shares the daemon’s memory limit. Attachment filenames and generated note paths also need the same containment rules as folder reads; resolving a symlink once does not address later replacement.  
    **Recommended change:** Bound decoded bytes, nesting, members, records, chunks, extraction output, and processing time. Use cancellable/isolateable workers and quarantine deterministic resource failures. Define safe path generation, attachment-name handling, exclusion of internal state, and containment checks at actual read/write time.

36. **Major — The performance targets are not sufficiently qualified or demonstrated.**  
    **Sections/elements:** §17; RET-9; ING-4; §20 synthetic performance tests.  
    One million 1024-dimensional float32 vectors occupy **4.096 GB**; the duplicate embedding cache makes that approximately **8.192 GB**, before text, FTS, WAL, and metadata. Keeping both old and new generations can double that vector payload again. Scanning one vector set in 300 ms requires roughly 13.7 GB/s of reads plus distance calculations. This is hardware/cache-dependent, not automatically impossible, but cannot be promised for an unspecified laptop. Arbitrary in-process models are also incompatible with an unconditional 300 MB idle-RSS limit.  
    **Recommended change:** Fix reference hardware, dimensions, model footprint, warm/cold state, concurrency, filter selectivity, and representative file sizes. Separate local processing latency from embedding/rerank/model latency. Define readiness during migrations/model loading. Benchmark in M1 before committing to scale; make small captures lexically searchable under a bounded deadline.

37. **Major — The roadmap defers foundational guarantees and uses insufficient exit criteria.**  
    **Sections/elements:** §19 M1–M5; §20; FLD-7 unspecified JSON exports; §5.1 Markdown-checklist tasks.  
    U4/U5 smoke tests do not establish citation validity, provider switching, safe approval, or non-egress. A no-op mailbox re-import does not test changed records, overlapping containers, interrupted imports, or deletion. “JSON exports from meeting and chat tools” is not a format contract. Checklist-derived tasks need identities and edit/reorder semantics that are absent. M4 combines these with privacy, enrichment, adapters, backups, deployment, and DOCX in three weeks. “Phase 2” entities also appear as a scheduled milestone without an explicit v1 boundary.  
    **Recommended change:** Define the release boundary and cut unspecified JSON families, checklist harvesting, and entities from v1 unless backed by concrete fixtures and contracts. Limit initial adapters and typed imports to actual required exports. Move rebuild, recovery, privacy enforcement, and authorization tests into their earliest implementing milestones. Add fault injection, cross-door round trips, held-out retrieval/no-answer cases, changed-container imports, and privacy tests under provider failure and switching.

38. **Minor — Several field, role, and type names have already drifted.**  
    **Sections/elements:** D5/Appendix E versus §13.1; §7.4 URL ingestion; Appendix E natural keys; Appendix A entity roles; RET-7/Appendix C.  
    Four model roles become five with `embed_local`; URL ingestion creates undefined type `web` rather than `article`; the glossary drops recurrence identity from calendar keys; `organizer` exists in `document_people` but not resolved entity roles; MCP promises result types that the retrieval result definition omits. The SSE example also omits the conversation ID promised in `message.end` and uses a different cached-token field name from the SQL schema.  
    **Recommended change:** Establish normative schemas and terminology, then generate or validate examples and tool definitions against them. Resolve each mismatch explicitly rather than relying on “shared schemas” to prevent drift later.

39. **Minor — Tag and link schemas lose evidence their semantics need.**  
    **Sections/elements:** Appendix A `document_tags` primary key excludes origin; incoming links use `ON DELETE CASCADE`; §8 dangling-link behavior.  
    A tag cannot record simultaneous user and LLM assertions. Replacing enrichment can therefore lose user attribution or require an unstated precedence rule. Purging a link target deletes the incoming relationship instead of leaving its textual target dangling for future resolution. Nullable `target_text` also makes a weak identity for explicit ID-based links.  
    **Recommended change:** Store tag assertions independently and derive effective tags. Preserve unresolved textual relationships when targets disappear. Define separate identities for parsed links and explicit document-to-document relationships.

40. **Minor — The rescan rule cannot detect every missed change or required reprocessing.**  
    **Sections/elements:** FLD-2 rescans repair missed events; FLD-4 processes only when both stat information and content hash change; §5 moves detected within the same scan.  
    A restore or synchronization tool can change bytes while preserving size and modification time. Configuration or extractor changes can require processing unchanged bytes. Moves observed in separate event batches do not satisfy the stated same-scan condition, and identical copies make hash-based matching ambiguous.  
    **Recommended change:** Provide deep integrity rescans, invalidate processing on relevant configuration generations, and define a bounded move-reconciliation window with conservative ambiguity handling.

41. **Minor — Several public API lifecycle outcomes remain undefined.**  
    **Sections/elements:** §7.3 `201 {document, job}`, 30-second `wait`, 24-hour `Idempotency-Key`, optimistic concurrency; §7.4 truncated results.  
    Clients cannot tell what status/body to expect after wait timeout, duplicate submission, partial container success, degraded indexing, or reuse of an idempotency key with a different payload. The document also does not define whether property PATCH merges or replaces, whether stale preconditions return conflict or precondition failure, or how source removal handles existing documents.  
    **Recommended change:** Add explicit request/response examples and status rules for these cases, including durable idempotency scope, payload matching, and expiration. Define mutation semantics independently of transport and apply them consistently to the bridge.

## Questions the author must answer

- Is **all of `index.db` disposable**, or only its search projections? What must survive deletion of the database and extraction cache?
- Does vault-only recovery include content indexed outside the vault, and must it preserve original bytes or only canonical searchable content?
- Is document identity per source occurrence, per source account/natural key, or global across doors? How should conflicting versions and overlapping exports behave?
- Does deletion suppress indexing, alter original files, erase container members, or some combination? What happens to historical citations and derived notes?
- Does “local” mean this machine or a trusted network? Does `no_cloud` cover derived queries/history and independent REST/MCP clients?
- Which missing occurrence times remain unknown, what time zone defines temporal language, and how much calendar history/future coverage is guaranteed?
- Are model switches permitted only between completed turns? What degradation is acceptable when the old embedding model disappears?
- Which clients may manage sources, spend provider budget, access protected material, and approve assistant writes?
- Which exact export formats, provider adapters, hardware, and corpus fixtures define v1? Are entities unequivocally outside that release?
- Must an answer represent a complete enumeration, or may it summarize a bounded sample? How will the API and UI distinguish those outcomes?

## What is strong

- Keep the shared ingestion pipeline and separation of structured querying from semantic retrieval.
- Keep explicit provider choice, role bindings, and a provider-neutral visible conversation format.
- Keep original-source preservation, user overrides, and the prohibition on automatic name-only entity merges.
- Keep the hard constraints; make storage, authorization, privacy, and recovery enforce them rather than weakening them to match the current sketch.
