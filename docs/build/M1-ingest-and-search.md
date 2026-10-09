# M1 — Ingest and search: build plan

| | |
| --- | --- |
| **Spec** | [Specification v0.6](../../spec.md), §19 row **M1 — Ingest and search**; §§5–9, 12–13, 15–17, 20 and Appendix A |
| **Duration** | Weeks 3–4 |
| **Goal** | Capture a file or text, find it through keyword, semantic and structured retrieval, and keep authored state and search projections correct through edits, deletions, crashes and rebuilds. |
| **Status** | Draft for M0 handoff. Every M1 test named below is **planned**, not an existing or passing test. |
| **Date** | 2026-10-08 |

This plan is for the engineer implementing M1. It identifies the behavior to ship, the M0 contracts to extend, and the evidence required to close the milestone. The [M0 build plan](M0-foundations.md) remains the prerequisite. M0 closed on 2026-10-09 for the reference Fedora x86_64 / Tailscale deployment under the operator's [accepted closure scope](M0-convergence-report.md#m0-closure-2026-10-09); the remaining integration qualifications have explicit follow-up issues and are not passing evidence.

## 1. Scope

### In scope

- Registered folder sources under the incoming root: filesystem events, startup and periodic shallow rescans, deep hash rescans, per-source polling, debounce, include/exclude rules and `.brainignore`.
- The built-in inbox import source: a 20-second poll, claim by atomic rename on the source filesystem, private staging, resumable import and visible failed claims.
- Descriptor-based staging and real extraction in the dedicated sandbox for `.md`/`.markdown`, `.txt`, `.pdf`, `.html`/`.htm` and `.docx`. Format detection and frontmatter parsing happen there, before the daemon interprets the result.
- Lossless extracted text, normalized search text and reversible offsets; structure-aware chunking with versioned input hashes and embedding-model token bounds.
- The existing `note`, `article` and `file` types, the type-precedence rules applicable to them, original and effective properties, parsed tags and links, and indexed metadata needed for their structured filters.
- Named embedding spaces, embedding/extraction caches, batched embedding, unit-normalized float32 vectors in SQLite, a segmented in-memory vector array and a dense slot map, plus restart-safe dual-write space activation.
- Lexical-first publication, external-content FTS5 with range highlights, filtered exact cosine scoring, per-leg document diversity, RRF, supported rerank modes and explicit degraded results.
- `POST /v1/documents`, `POST /v1/search`, `POST /v1/query`, and the read/job/source/maintenance and session-authorized mutation routes necessary to exercise this milestone's lifecycle. Request validation and OpenAPI use the same contracts.
- Suppression, source absence, trash, restore and explicit logical purge with payload lineage; the full publication coordinator, mutation journal, startup recovery and conflict-preserving writes.
- `brain add`, `brain search` and `brain query`, including machine-readable output and the existing exit statuses.
- Rebuild equivalence and a checked-in retrieval seed corpus and evaluation harness.

### Out of scope

The assistant, `/ask`, `/chat`, MCP, browser-paired CLI sessions and proposal approval execution land in M2. The full search/viewer/editor/sources UI lands in M3. Transcript and calendar types and parsers, recurrence, calendar coverage, optional enrichment/write-back, additional vendor adapters, budgets, encrypted backup/restore/export, retention scheduling, SBOMs and release signing land in M4. OCR, audio transcription, approximate vector indexes and forensic erasure remain later work.

M1 still honors the mutation-policy matrix: a key may create or add metadata directly, but a non-additive update or delete produces a durable proposal and has no filesystem effect. The M2 approval executor consumes those records. Direct browser-session mutation and purge can be exercised through REST without adding the M3 editor. Automatic folder reconciliation operates only within its registered source.

U1 mentions asking questions about an inbox PDF. Its M1 acceptance proves the PDF is available to retrieval within 30 seconds; answering through `/ask` is M2. U17 distinguishes deleting **in the app**, which creates a suppression, from disappearance on disk, which creates an absence that may clear.

## 2. Exit criteria and planned tests

All names in this table are proposed acceptance tests to add during M1. Deterministic CI uses fixture/recorded providers; live-provider qualification is separate. A constant-vector mock is useful for transport tests but cannot establish semantic retrieval quality.

| Gate | Required outcome | Planned tests | Required evidence |
| --- | --- | --- | --- |
| M1-G1 | U1: inbox PDF becomes retrievable within 30 seconds | `M1StoriesTests.U1_InboxPdfSearchableWithin30Seconds`; `InboxTests.ClaimedFileSurvivesImportFailure` | Run the daemon, real descriptor transport and sandbox against a text-layer PDF; include the inbox poll and staging time. A rewrite of the original pathname becomes a separate entry. |
| M1-G2 | U2: watched Markdown edits reach search | `M1StoriesTests.U2_MarkdownEditReplacesSearchProjection`; `FolderTests.DeepScanDetectsUnchangedMtimeEdit`; `FolderTests.PollUsesSourceInterval` | Events meet the <10-second edit target; polling meets the configured interval; shallow/deep scans cover missed events and unchanged stat tuples. |
| M1-G3 | U9: an unambiguous move preserves identity without re-embedding | `M1StoriesTests.U9_MovePreservesIdentityAndEmbeddingCache`; `FolderTests.AmbiguousMoveCreatesSeparateIdentity` | Compare document id, revision history and embedding-call count. Identical bytes at two simultaneous paths remain two documents. |
| M1-G4 | U17: suppression prevents resurrection; absence remains reversible | `M1StoriesTests.U17_SuppressedDocumentDoesNotReappearOnRescan`; `FolderTests.DisappearanceCreatesAbsenceAndReappearanceReattaches`; `DeletionTests.RestoreClearsSuppression` | Rescan and restart do not clear suppression. Source disappearance never deletes an external file and does not create user suppression. |
| M1-G5 | U18: interrupted imports resume without duplicates or stale chunks | `M1StoriesTests.U18_InterruptedImportResumes`; `JobsTests.ExpiredLeaseRequeuesFencedJob`; `JobsTests.StaleRevisionAndGenerationCannotPublish` | Restart after admission, lease acquisition and projection publication; repeat recovery; inspect identities, queued jobs and projections. |
| M1-G6 | U21: a crafted file fails without stopping the daemon | `M1StoriesTests.U21_CraftedPdfFailsWithoutStoppingDaemon`; `ExtractionTests.FailedDocumentRemainsFindableByTitleAndPath` | The real sandbox exits or reports a bounded error, the stage/reason is durable, and `/health` plus an unrelated search still succeed. |
| M1-G7 | Rebuild preserves authored truth, deterministic projections and citation resolution | `RebuildTests.AuthoredStateAndDeterministicProjectionsAreEquivalent`; `RebuildTests.LocatorAndExcerptResolveAfterRebuild`; `RebuildTests.ModelDerivedOutputsAreMarkedRegenerated` | Delete the index, rebuild from vault plus state, compare identities/overrides/suppressions, lossless text/chunks/FTS/metadata and fixture locators/excerpts. Full assistant citation generation is M2. |
| M1-G8 | Every publication boundary keeps the preceding committed projections searchable | `PublicationIntegrationTests.CrashAtEveryBoundaryPreservesPreviousSearchProjection`; `PublicationIntegrationTests.IndexFailureRollsBackVectorArray`; `PublicationIntegrationTests.ReadersObserveOneSnapshotAndVectorGeneration` | Inject faults after state publishing, before and after index commit, and before finalization. Recover across two restarts; query metadata, lexical and vector projections together. |
| M1-G9 | Symlink swaps and hard links cannot expose bytes outside the source boundary | `StagingTests.SymlinkSwapCannotEscapeRetainedRoot`; `StagingTests.HardLinkCannotExposeProtectedBytes`; `StagingTests.OnlyRegularFilesAreAccepted`; `StagingTests.DownstreamReadsOnlyImmutableStage` | Use descriptor-relative opens and adversarial races, plus distinct daemon/sync UIDs on Linux. A registration-time realpath check alone is insufficient. |
| M1-G10 | The sandbox has no network, store or secret access and receives only a descriptor | `SandboxIsolationTests.NetworkStoresAndSecretsAreInaccessible`; `SandboxIsolationTests.EnvironmentIsScrubbed`; `SandboxIsolationTests.TimeoutKillsProcessGroup` | Exercise the actual Linux service/container boundaries and a hung parser with a child process. An in-process parser substitute cannot close this gate. |
| M1-G11 | Work bounds reject bombs and deterministic failures are not retried | `ExtractionBoundsTests.DecompressionBombIsBoundedAndNotRetried`; `ExtractionBoundsTests.FrontmatterAndXmlBoundsAreEnforced`; `ExtractionBoundsTests.PdfPagesAndDecodedBytesAreBounded`; `ExtractionBoundsTests.DaemonRejectsOversizedSandboxOutput` | Assert named bound, bounded CPU/memory/output, metadata-only or skipped status as appropriate, and zero further attempts until bytes or extractor generation change. Disable external entity/resource/action fetching. |
| M1-G12 | Delete/replace churn uses vector memory proportional to live chunks | `VectorSlotsTests.DeleteReplaceChurnReusesSlots`; `VectorSlotsTests.ReclamationWaitsForReaders`; `VectorSlotsTests.RestartRebuildsDenseMap` | Run sustained replacements and deletions across segment boundaries; check live slots, retired slots and measured array allocation after old readers finish. SQLite row-id growth must not drive allocation. |
| M1-G13 | A competing editor's change is preserved as a conflict | `MutationIntegrationTests.EditDuringDebounceProducesConflictFile`; `MutationIntegrationTests.ExternalChangeBeforeRenameIsPreserved`; `MutationIntegrationTests.AppliedMutationFinalizesExactlyOnce` | Race the watcher/editor against an import or managed-file write. Preserve the external bytes, write the conflict file, record the target change and admit the new revision; repeat recovery. |
| M1-G14 | Filtered retrieval finds candidates outside the unfiltered top 50 | `RetrievalTests.FiltersApplyBeforeBothLegs`; `RetrievalTests.PerLegDiversityBeforeFusion`; `RetrievalTests.AdversarialFilteredMatchOutsideTop50` | Exercise keyword and vector legs separately and together, with distractors dominating the global ranking. |
| M1-G15 | Retrieval recall@8 is at least 0.85 on the seed set | `RetrievalEvaluationTests.SeedRecallAt8Meets085` | Commit the seed documents, labeled questions, recorded embeddings and provider/model fingerprint; report recall@5, recall@8 and MRR for each mode. Do not fit the labels to results. |
| M1-G16 | Embedding space changes activate only after complete current-generation coverage | `EmbeddingSpacesTests.DualWriteCatchesConcurrentEdits`; `EmbeddingSpacesTests.RestartResumesCatchUp`; `EmbeddingSpacesTests.AtomicActivationRequiresLiveCoverage`; `EmbeddingSpacesTests.OldProviderFailureFallsBackToKeyword` | During catch-up, create/edit/delete documents and restart. Queries continue using the old space until activation and never mix dimensions or spaces. |
| M1-G17 | Capture, authorization and bounded query contracts work end to end | `DocumentsApiTests.LexicalCaptureAndEmbeddingDeadline`; `DocumentsApiTests.IdempotencyAndCredentialProvenance`; `DocumentsApiTests.KeyMutationCreatesProposalOnly`; `QueryTests.IndexedFieldsHalfOpenWindowsAndStableCursors`; `QueryTests.CursorRejectsStaleAuthority`; `CliIngestTests.AddSearchQueryAgainstDaemon` | Include provider outage, stale revision, input limits, query with no provider calls, revocation between pages, every CLI exit status, and `--json`. |
| M1-G18 | Purge removes every live copy by reference and preserves the named retained classes | `PurgeTests.LineageRemovesPayloadsCachesTracesAndExcerptCopies`; `PurgeTests.RetainedClassesAndScrubbedTombstone`; `PurgeTests.LateJobCannotRestorePurgedContent` | Seed payloads and references even though assistant turns arrive in M2. Verify redaction markers and deletion of derived copies; retain user-typed messages, unowned originals and other classes listed in §15.9. |

Linux isolation and resource-bound tests belong in deterministic CI with distinct identities and the shipped deployment profiles. Live vLLM runs qualify capability, embedding intent and throughput; they supplement the deterministic gates. Measure the §17 latency, ingestion and memory targets on the reference host with provider latency reported separately.

## 3. Dependencies inherited from M0

| M0 hook | M1 use | Constraint to preserve |
| --- | --- | --- |
| `IStateStore`, `IIndexStore`, queued writers/read leases (now `SecondBrain.Storage` infrastructure, not Core contracts), migrations and `IStorageStatus` | Add the text/chunk/FTS/vector/cache/filter projections and persistent processing jobs through migrations. M1 code in Core declares repository and projection ports in `SecondBrain.Core.*`, and Storage implements them over these handles. | State remains authoritative; the index is disposable. Keep the exclusive root lock, pool/transaction bounds and crash-safe migration state machine. |
| `IPublicationCoordinator`, `(revision, generation)` fences and `ICrashPoints` | Extend snapshot replacement to every real projection and the vector array. | Serialize per document; check the fence inside the index transaction; never rely on `ATTACH` for cross-store atomicity. |
| `IMutationJournal`, managed-file atomic writes and immutable replay/finalization records | Inbox import, managed capture, trash, restore, purge and conflicts. | Prepare/apply/finalize and recovery must remain idempotent. External source files are not mutable daemon-owned files. |
| `Document`, occurrences, revisions, `TypeRegistry`, `props_original` | Admit and classify the first real content doors. | Content hashes skip recomputation, never merge identities. Frontmatter cannot assert new identity or provenance. |
| Jobs/payloads/payload_refs/idempotency/embedding_spaces schema hooks | Durable queue, replay, named spaces and purge lineage. | Carry initiating credential, credential generation and account epoch. Bodies live once and references retain lineage. |
| Extractor v1 framing, `SCM_RIGHTS`, `ping` and descriptor probe | Introduce a versioned real-extraction operation and bounded result protocol. | No source path, provider secret, store handle or inherited environment enters the sandbox. The daemon enforces deadline and validates every response. |
| `IProviderRegistry`, embedding abstractions, model limits and policy-owned transport | Document/query embeddings and supported rerank. | Every request, retry and discovery call crosses current privacy checks. Adapters do not create independent HTTP clients. |
| Validated reloads, root validation, incoming/data separation and the key ring (Core code uses `IHmacKeyRing` in `SecondBrain.Core.Security`; `IKeyRing`, `FileKeyRing` and `DataRootLock` are in `SecondBrain.Infrastructure.Security`) | Bind source schedules, processing generations and signed cursors. | Invalid config leaves the accepted snapshot in use. Writable cryptographic material stays in the data-root key ring; external secrets stay read-only. |
| `IScopePolicy`, endpoint policies, credential authority and opaque sessions | Add document/query/search routes and enforce the common mutation matrix. | `read` covers retrieval; `write` covers capture; `admin` and recent step-up cover purge. No key can approve a proposal. Reauthorize each cursor page. |
| `IAdmissionController` (in `SecondBrain.Core.Limits`, so background dispatchers can use it directly without the HTTP middleware) and reservation metadata | Upload/search concurrency, queue/corpus reservations and disk low-water admission. | Restore persistent counters at startup and transfer reservations into queued work; releasing them at the HTTP response is insufficient. |
| Readiness/doctor contributors and hardened Compose/systemd deployment | Report extraction and persistent processing health with the real sandbox. | A reachable or unverified enabled canary blocks local-only provider traffic; capture and keyword retrieval still work. `/health` stays liveness. |
| CLI HTTP adapters, credential store and result envelope | Add capture/search/query without a second local engine. | Preserve secret handling, `--json` and exit statuses 0–4. |

**Port rule.** New M1 persistence and file-system needs are declared as ports in `SecondBrain.Core` and implemented by adapters in `SecondBrain.Storage` (SQL) or `SecondBrain.Infrastructure` (file system, sockets, platform). Core does no I/O, and the rules in `tests/SecondBrain.Architecture.Tests` enforce this (see [clean-architecture-plan.md](clean-architecture-plan.md)).

**Deferred seams.** Two ports are added together with their first consumer, not before: an `IPayloadProtection` port for cursor signing (work item 8) and an `IExtractorClient` port for the real extraction protocol (work item 3).

Core continues to have no project references. Keep domain/pipeline contracts provider-neutral; place vendor SDK references only in adapters. Coordinate changes to shared contracts and regenerate project lock files when references change. Package-version changes need a concrete build or parser requirement and a recorded reason.

## 4. Work items, in dependency order

### 1. Durable processing contracts and schema

Define staged-input, extraction-result, processing-generation, job and projection contracts. Add M1 index tables from Appendix A and any necessary state indexes through a new migration. Persist job leases, attempts, failing stage/reason, authority and retry timing. Restore queued-job and admitted-byte accounting before accepting ingestion; folder jobs use the daemon's internal identity, and already-admitted ingestion follows §15.4.

**Accept:** migration/restart tests, lease recovery, stale fences and admission tests extend the M0 suites; no new ingest route bypasses reservations.

### 2. Stage and claim inputs safely

Retain source-root descriptors; open children relative to them without following symlinks, validate regular files and stage/hash the bytes once. For inbox entries, claim into `inbox/.claimed/` on the same filesystem before staging; ignore claimed paths in the watcher. Journal import finalization so a failed claim is visible and recoverable. Enforce 50 MB input and global admission bounds while copying. Sanitize managed destination names and reserve collisions atomically.

**Accept:** M1-G1 and M1-G9, including Linux cross-identity tests and changes to the original pathname after claim/staging.

### 3. Complete the sandbox protocol and parsers

Extend the M0 descriptor protocol with real extraction, physical format detection, bounded frontmatter and bounded text/page/metadata output. Select reviewed parser dependencies for the five M1 formats and add fixtures for each. Preserve PDF page markers and flag scanned PDFs `needs_ocr`; do not silently claim OCR. Disable network/resource fetching, entity expansion and active document actions. Apply streamed decoded-byte, ratio, depth, node, page and chunk bounds. Add a supervisor that kills the process group at the 120-second extraction deadline, isolates scratch work and restarts the service as needed.

The M0 frame is capped at 65,536 bytes; full extracted text can exceed it. Introduce bounded streaming/chunked responses or another versioned descriptor-based result transfer, rather than unbounded JSON or a filesystem path the daemon trusts.

**Accept:** M1-G6, M1-G10 and M1-G11 with real Linux isolation, malformed protocol frames and a dishonest oversized result.

### 4. Normalize, classify and chunk deterministically

Implement lossless text/search text with offset mapping, versioned normalization and the applicable ING-8 precedence rules. Honor overrides and preserve `props_original`; derive effective properties and source times without substituting ingestion time. Parse tags/links in the sandbox and validate them in the daemon. Chunk by heading/paragraph with defaults 512 target tokens, 1,024 maximum and 64 overlap; preserve fenced blocks/tables where possible, retain page/context/offset information and hash the exact embedding input.

**Accept:** golden files and offset round trips; type precedence/frontmatter authority tests; chunking obeys the resolved embedding input limit. Normalization/chunker generation changes reprocess without changing authored revision.

### 5. Publish lexical projections before vectors

Extend the coordinator to atomically replace the indexed metadata snapshot, text, chunks, external-content FTS rows, tags/links/people and indexed properties. Publish these immediately after extraction. Small JSON captures write/admit/publish keyword projections synchronously with a <1-second target; attempt embeddings within the 3-second deadline, then report `indexed_partial` and queue them. File and bulk work return durable document/job ids, with bounded `wait` support. Failed extraction remains findable by title/path.

**Accept:** M1-G5, M1-G8 and M1-G17; no reader joins live state metadata to older index chunks.

### 6. Embeddings, spaces and vector slots

Resolve provider/model/dimensions/normalization/input-template/chunk-generation fingerprints; cache by `(space_id, input_hash)` and batch within model token/batch limits. Validate dimensions and finite vector values and store unit-normalized float32 BLOBs in the chunks' transaction. Build segmented arrays of 64k vectors with a dense slot map independent of SQLite row ids. Retain previous slot contents until commit, roll back on failed commit and reclaim freed slots only after old readers drain.

For a new binding, create `building`, dual-write new/changed chunks, checkpoint catch-up and atomically activate only at complete live current-generation coverage. Search uses the old active space until then; a dead old model yields `embed-unavailable` with keyword fallback. Reject a model whose input limit is smaller than the configured chunk maximum until controlled rechunking is scheduled.

**Accept:** M1-G3, M1-G12 and M1-G16; restart loads the correct active space and resumes catch-up.

### 7. Folder reconciliation and bounded scans

Add the events/shallow/deep tiers, per-source polling, per-path 1.5-second debounce and ignore rules. A startup shallow scan and nightly deep scan cover lost events. Check both stat tuples and hashes; checkpoint large fan-out scans with bounded concurrency. Match moves only within one reconciliation window and preserve identities conservatively. Record missing/removed occurrences without user suppression; respect suppression before admission. Source removal and manual scan APIs use the existing admin authorization policy.

**Accept:** M1-G1–M1-G5; source schedules are independently configurable and rescan/restart never duplicate import work.

### 8. Retrieval and structured queries

Build SQL-prefiltered candidate sets, FTS5 BM25 with sanitized phrase/prefix queries and plain-text highlight ranges, exact vector scoring over the same candidates, three-chunk-per-document diversity per leg, top-50 leg budgets and RRF with `k=60`. Add bounded neighbor expansion, optional recency boost and supported rerank with explicit failure degradation. Beyond the exact candidate cap, use the declared recency-ordered sample and return `exact-cap`.

Structured queries use only indexed fields, supported operators, half-open time windows, deterministic tie-breaking and a maximum page size of 200; they make no provider calls. Sign cursors with the cursor Data Protection purpose and include query hash, instance/operation, sort tuple, processing generation, credential id/generation, account epoch, issue time and 15-minute expiry. Reject a cursor reused for another query or stale authority. Keep the calendar-specific contract extensible for M4 rather than accepting unsupported types silently.

**Accept:** M1-G14, M1-G15 and M1-G17; result metadata and degraded reasons follow §9, highlights round-trip to lossless offsets, and query sanitization cannot inject SQL or HTML.

### 9. Mutations, deletion and lineage

Apply ING-12 at the service layer shared by every door. Implement direct managed creates/additive metadata, browser-session edits, durable proposals for key non-additive writes and deletes, source absence, app suppression, trash and restore. Route filesystem effects through the journal, advance revisions for delete/restore/purge and reject late jobs. Carry document lineage on each payload/ref, cached result, trace and excerpt. Explicit admin/step-up purge removes live copies, inserts redaction markers and scrubs the tombstone while reporting retained classes precisely. A competing file hash produces a conflict file and a durable target-changed result, never an overwrite.

**Accept:** M1-G4, M1-G13 and M1-G18. M2 extends the same lineage to actual turns, citations and compaction summaries; it must not introduce a second body store.

### 10. Startup recovery and rebuild

Under the root lock, recover journal effects, expire leases, requeue stale/unpublished projections, rebuild vector slots and clean stale staging safely. Rebuild from vault plus durable state preserves authored identities, revisions, overrides and suppressions; retain caches unless explicitly purged. Compare deterministic projections and fixture citation resolution. Recompute model-derived outputs only where implemented and mark them regenerated.

**Accept:** M1-G5, M1-G7 and M1-G8; repeat recovery and rebuild without extra revisions or duplicate effects.

### 11. CLI, evaluation and operational proof

Bind `brain add <path|->`, `search` and `query` to daemon HTTP routes with scoped credentials, lifecycle/degraded output and `--json`. Commit a reviewed seed corpus and labeled question set with recorded embedding provenance; automate recall@5, recall@8 and MRR. Exercise the real sandbox through hardened Compose and systemd, then measure the §17 targets on the reference hardware. Update the local run instructions and list unsupported formats without promising future parsers.

**Accept:** M1-G15 and M1-G17, all deterministic gates in CI, recorded Linux isolation results and separate live-provider qualification results.

## 5. Verification and definition of done

- Restore with locked dependencies, build with warnings as errors and run every project; all M0 gates remain passing.
- Every planned M1 gate has a committed test with recorded passing evidence. Replace the table's proposed names with the actual discoverable names when implemented.
- Demonstrate a real daemon flow: register a temporary source, import a PDF and capture text, search/query, edit/move/suppress/restore, restart during work and rebuild. Verify returned projection states, job reasons and CLI exit statuses.
- Exercise staging races, sandbox isolation and resource limits with the shipped Linux deployment. Record target-host results separately from macOS unit/transport checks.
- Record seed-set recall@8 ≥0.85 and per-mode metrics. Record performance against the stated reference corpus and hardware; keep provider time separate.
- Every deferred security guarantee has a named milestone/work item. Real parsing and timeout orchestration are item 3; durable admission is item 1; cursors are item 8; lineage/purge is item 9. Assistant approval/exposure/revocation is M2; parser/type extensions, budgets, retention and encrypted backup/supply-chain work are M4 as specified in §19.
- No temporary test fixtures, raw documents, credentials or environment-secret values enter diagnostic output. A disabled canary is reported as disabled, never as proven host egress enforcement.
- Update the README and draft the next milestone plan before closing M1.

## 6. Decisions to resolve before implementation

1. **Parser selection and output transport.** Choose supported libraries and a bounded result protocol before item 3. The M0 ping/probe framing cannot carry arbitrary full-text output. Record dependency provenance, failure behavior and the stream bounds.
2. **Hard-link admission.** Descriptor-relative opens prevent symlink races but do not prove that an inode has no link outside the root. Preserve the dedicated sync/daemon ownership boundary; decide whether multiply linked files are rejected and test the rule with real Linux identities. Do not replace the hard-link gate with a path-only unit test.
3. **Seed corpus and model.** Select reviewed questions and recorded embeddings with their dimensions/fingerprint. The initial live model is administrator configuration, not a built-in vendor default. Preserve evaluation labels independently of retrieval implementation.
4. **Folder sync behavior.** Choose events or polling for the deployment's sync tool; the inbox defaults to 20-second polling. Benchmark against partial writes and replacement patterns from that tool.
5. **Later-feature boundaries.** Retain explicit unsupported outcomes for transcripts/calendar, OCR, paired CLI sessions and approval execution until their milestones land. M1's non-additive-key mutation route stores a proposal; it cannot fabricate an interactive approval.

The spec has two wording conflicts to treat consistently: §15.3 SEC-6 and §15.12 SEC-32 still mention cryptographic keys in the secrets directory, while §6 and §15.9 SEC-23 explicitly place the writable ring in the data root. Follow the latter layout and the M0 key-ring contract. The general model-spend sentence in §15.8 is qualified by the explicit scope rules in §§7.3 and 15.4: query/index embeddings, classification and search-time rerank are implicit `read`/`write` services; assistant calls and explicit enrichment require `infer`.

M0's live vLLM qualification, target-host canary/vLLM egress checks and HTTPS from a tailnet peer have recorded evidence. The unexercised Access egress path, additional platforms, certificate lifecycle and remote workflow remain the [tracked M0 follow-ups](M0-convergence-report.md#tracked-follow-ups). M1 must preserve the qualified Fedora/Tailscale behavior and must not treat those deferred checks as complete.
