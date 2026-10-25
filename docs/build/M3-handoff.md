# M3 UI handoff

M3 is now [accepted and deployed for the user-agreed scope](M3-acceptance.md), with VPN/Cloudflare qualification explicitly waived. This handoff is retained as the implementation starting point; [M4](M4-meetings-enrichment-operations.md) is next.

M2 implementation was verified on 2026-10-24; see the [acceptance ledger](M2-acceptance.md). This document describes the contracts available to M3. The complete M2 release is [deployed to the reference service](M2-complete-deployment.md), and the reference user's read-only Codex bridge is verified. **M3 is the next implementation milestone**; see [current project status](../project-status.md).

## Existing surfaces

- `/chat` and `/approvals` use the same durable services as the CLI. Answers stream provisionally, then the persisted final answer replaces them. Source links resolve verified excerpts and their document revision; changed/deleted sources retain their stored excerpt, while purge removes it.
- The browser supports conversation selection, deletion, approval cards, source inspection, the egress manifest and retry. Broader browsing, search, source/job management and editing remain M3 work.
- `POST /chat`, `/ask`, `/turns/{id}/retry`, conversation listing/detail/deletion, `/conversations/{id}/transcript`, `/turns/{id}/events`, `/turns/{id}/manifest` and pending-operation routes also have `/v1` aliases. See the generated OpenAPI snapshot for exact contracts.
- Chat commands are `/save [text]`, `/new`, `/search <query>`, `/query <type> [JSON filters]`, `/recent [count]`, `/sources`, and `/pending`. `/sources` means citations for the previous substantive answer. `/save` without text carries the previous answer's lineage into its proposal. CLI additionally handles `/approve`, `/discard`, `/retry`, `/help` and `/exit` locally.

## UI invariants

1. Keep provisional text distinct from the verified final answer. Render source markers with the matching turn's citation map; numbers are local to a turn. Escape text and sanitize Markdown. Raw model HTML is not trusted.
2. Resume event observation with the last committed sequence. Disconnecting an observer does not cancel accepted work. A second message cannot start while the conversation has a running turn.
3. Retrying uses the existing turn ID and a fresh idempotency request ID. Start replay after the returned `replayAfter`. A fallback is allowed only before a committed write; later retries replay recorded effects on the original provider/model. Do not implement retry by resubmitting the original prompt as a new turn.
4. Approval, edit and discard use immutable server proposals. Render the exact proposal and handle expired, superseded, stale-target, revoked and purged outcomes. An edit creates a replacement proposal. API keys and MCP clients cannot approve.
5. Never infer auto-approval eligibility from UI state. The server decides from the persisted context and originating browser credential. A paired CLI session requires explicit approval.
6. Display partial/failed reasons, degraded retrieval and coverage separately from an empty successful search. Treat a purged transcript or result as unavailable, not as an empty answer to reconstruct.
7. The manifest retains individual provider requests across retry/fallback. Summaries and earlier conversation context retain document lineage; purging a source can remove derived text elsewhere in the conversation.
8. Ownership and authority are server-side. Use same-origin cookie sessions with antiforgery on writes. Handle revocation during streaming and require a fresh login or step-up when the server requests it.

## M3 scope

Implement the specification's full browser workflows for searching, browsing, opening and editing knowledge, managing sources and jobs, and handling conflicts. Reuse the existing ingestion, retrieval, publication and approval services. Keep transcript/calendar and meeting-specific enrichment controls unavailable until M4 implements their underlying types and workflows.

Start with search and document viewing, then editing/conflict handling and source/job management. Complete timeline, suppressions/trash, settings and responsive layouts. Closure still requires the specification's U6, U7, U11 and U16 browser gates at desktop and 390 px through both VPN and Cloudflare origins, document-rendering CSP checks, and prompt circuit disconnection after session revocation. Existing chat and signed-out deployment checks do not close those gates.

The complete M2 server and CLI are installed in the reference systemd service. The [deployment record](M2-complete-deployment.md) includes preserved-state, binary, endpoint and browser checks.
