# M2 — Ask and MCP: build plan and completion

| | |
| --- | --- |
| **Spec** | [Specification v0.6](../../spec.md), §§7.4, 10, 12–13, 15 and §19 row **M2 — Ask and MCP** |
| **Goal** | Answer from retrieved knowledge with verifiable citations and expose the same application services through a bounded assistant and MCP. Execute proposed writes only through authorized interactive approval. |
| **Status** | Implemented and verified in the checkout on 2026-10-24; see the [acceptance ledger](M2-acceptance.md). |
| **Date** | 2026-10-09 |

## Prerequisites

Use the [M1 acceptance gates](M1-ingest-and-search.md#2-exit-criteria-and-implementation-tests) as the handoff checklist. Verify actual implementation and test evidence before depending on retrieval snapshots, generation-bound locators, immutable payload lineage, durable proposals, publication recovery or journaled mutations. An unimplemented M1 guarantee remains prerequisite work; it cannot be replaced by assistant prompting.

M1 is **closed as of 2026-10-24** for the explicitly accepted single-host Fedora / RTX 5090 deployment. The corrected source passed locked restore, a clean Release build and all 1,750 deterministic tests; the privileged systemd workflow passed with verified deployed binaries and cleanup. The original 100k-document/1M-chunk local scale evidence remains dated October 9. Separate-LAN provider qualification is explicitly deferred, not passing evidence; see the [closure ledger](M1-convergence-report.md#remaining-qualification-and-handoff). M1 was subsequently installed in the main systemd service on October 24 with the existing account, credentials and configuration preserved. M2 implementation and acceptance are recorded in the separate [M2 ledger](M2-acceptance.md).

Keep the clean architecture dependency rules: application policies and ports in Core, SQL in Storage, platform adapters in Infrastructure, HTTP composition in Server and provider-specific behavior in its adapter. Fetch current SDK and protocol documentation before selecting dependencies; the protocol statements in the specification require implementation-time verification.

## Scope

- Read-only `/ask` and a persisted assistant loop with explicit allowed tools, bounded exposure and an egress manifest.
- Conversation ownership, portable transcripts, provider switches between turns and lineage-aware compaction.
- Immutable proposals, compare-and-set approval, journaled execution and deterministic auto-approval eligibility.
- Verified citations, persisted streaming events, replay, revocation and retry rules.
- Browser-paired CLI sessions and `brain ask`, `brain chat` and `brain mcp`.
- Stateless HTTP MCP, method/tool/resource authorization and a read-only-by-default stdio bridge.

The full browser search/editor/chat experience remains M3. Transcript/calendar parsing, enrichment, extended adapters, budgets, backup and retention remain M4. M2 must report unavailable capabilities explicitly and preserve M1 degraded retrieval reasons.

## Work items

### 1. Durable turn, execution and ownership contracts

Extend the existing state schema and payload/reference store for conversations, turns, ordered events, executions and egress manifests. Persist initiating credential, credential generation and account epoch. Define one application ownership policy for listing, continuing, replaying and deleting conversations. Keys access only their own conversations; interactive sessions use account ownership.

Store argument hashes and committed execution results before the assistant continues. Reject a second message while a turn is running. Define recovery states and repeat recovery without duplicate events or effects.

### 2. Bounded executor and read-only ask

Freeze the allowed-tool set at turn start from scopes and the calling surface. Validate every model-supplied argument with the same bounded contracts as the corresponding application service. `ask` receives only read tools. The executor enforces tool-call, document and byte caps, including initial retrieval, neighbor expansion and metadata exposure.

`read_document` accepts only documents surfaced in the current turn or named by the user. Count all tool-result components delivered to the provider. Record history and compaction exposure separately. Recheck authority and current privacy policy before each tool/provider boundary. Finish interrupted or capped work as `partial` with explicit reasons.

### 3. Citations and answer behavior

Assign stable per-turn passage identifiers and validate returned markers against that map. Resolve citations by document, revision, processing generation and locator; verify the excerpt hash. Preserve stored excerpts for changed/deleted revisions according to lineage and purge rules, without resolving a stale locator to new content.

Distinguish an empty successful retrieval from failed or degraded retrieval. Label general knowledge and reject unsupported citation markers. Implement recorded-provider claim-support and irrelevant-corpus abstention evaluations before claiming grounded-answer quality.

### 4. Proposals and exactly-once effects

Consume M1 proposal records through a single approval service. Bind immutable payload, every referenced revision, generated destination and policy generation. Recheck the interactive session's authority, then claim approval atomically. API keys and bridge credentials cannot approve. Editing a proposal supersedes it with a new immutable proposal.

Execute through the mutation journal and recover a crash after file effects without re-executing them. Reject stale target revisions and preserve competing editor changes. Auto-approval is restricted to browser-session creates/appends into `notes/` when the persisted execution record proves that no retrieved content entered the turn, including via compaction.

### 5. Streaming, replay and portable conversations

Persist each SSE event before delivery and support sequence-based replay. Reauthorize both replay and delivery. A disconnected client does not cancel the turn. Revocation stops only the initiating credential's work at the next boundary and does not invalidate unrelated sessions.

Retry/fallback is allowed only before any committed write. After a write, resume from recorded results, including redaction markers after purge. Store a canonical transcript, discard provider-specific continuation metadata on switching and apply switches between completed turns. Compaction retains document lineage and retrieved-content provenance.

### 6. Paired CLI and MCP

Implement browser-approved short-lived CLI sessions using the existing session authority and step-up rules. Keep normal `brain login` API keys distinct. Bind `ask` and `chat` to HTTP services and permit approval only through a paired interactive session.

Expose the shared application contracts through the current supported MCP SDK. Enforce scope checks after resolving every method/tool/resource/prompt, including compatibility paths. `resources/list` requires `read`; annotations and discovery filtering never substitute for authorization. The stdio bridge uses its own read-only key by default, fixed configured destinations and replaced authorization/destination headers. Public-origin calls include operator-configured Access service credentials.

## Acceptance evidence

The scenarios below are implemented. The [acceptance ledger](M2-acceptance.md) maps them to executed tests and retained evidence.

| Gate | Required evidence |
| --- | --- |
| M2-G1 | U4/U5/U8/U13/U19/U20 execute through the real application services with recorded providers. |
| M2-G2 | `ask` rejects a write call; injected document instructions cannot cause a filesystem write. |
| M2-G3 | A malicious provider cannot read an unsurfaced document or exceed document, byte, metadata or tool-call caps. |
| M2-G4 | Two concurrent approvals execute once; a crash after the file effect recovers without repeating it. |
| M2-G5 | API keys and bridge credentials cannot approve; paired CLI sessions can, with current authority and required step-up. |
| M2-G6 | Revocation ends only the affected turn; replay and delivery reject stale authority. |
| M2-G7 | A write-only key cannot list/read resources, including through the older-client compatibility path. |
| M2-G8 | Claim-support meets §20 thresholds and the irrelevant-corpus abstention evaluation passes its documented threshold. |
| M2-G9 | Provider switching consumes the portable transcript; compaction and purge preserve provenance/redaction behavior. |
| M2-G10 | Event replay preserves ordering without duplicate effects, and auto-approval rejects any turn containing retrieved content. |

## Definition of done

Run locked restore, warnings-as-errors build and the full deterministic suite, retaining M0 and M1 guarantees. Record discoverable test names, actual results and protocol/SDK versions. Exercise assistant and bridge behavior against the daemon, keep live-provider qualification separate, document operator setup and draft the M3 UI handoff. Do not mark missing prerequisite gates or unavailable external qualification as passing.
