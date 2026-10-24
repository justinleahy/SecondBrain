# Browser chat implementation progress

This work implements the browser chat goal on top of the deployed M1 service. It does not close the full M2 milestone or change the installed service yet.

The working implementation has:

- A browser chat page at `/` and `/chat`, using the existing authenticated Blazor circuit. It displays conversations, streamed text, activity, source excerpts, and interrupted-turn status. `/approvals` also exposes pending changes from account conversations and API-key clients to the authenticated browser.
- Persistent conversation ownership, idempotent turn admission, one active turn per conversation, ordered replay events, and restart recovery. Accepted work belongs to the host rather than the browser connection.
- A bounded `Microsoft.Extensions.AI.FunctionInvokingChatClient` pipeline using the configured provider. Each provider round trip revalidates authority, enforces context/output bounds, and reserves persistent hourly/global daily tokens.
- Retrieval through the existing search, structured-query, and document repositories. Sources carry their exact revision, processing generation, excerpt, and hash; citation numbers are verified against that registry.
- Cumulative tool, metadata, document, and byte limits. Reading an arbitrary model-invented document ID is rejected.
- Payload lineage shared with the M1 purge journal. Purging a source redacts derived answers, source excerpts, and replay events, including protection against output arriving after the purge. Changed/deleted sources retain their historical excerpt and display their current status.
- Immutable proposals for creating, updating, appending to, tagging, and linking managed notes. Browser cards support approval, replacement by an edited proposal, and discard. API keys cannot approve. Automatic approval is restricted to browser-created/appended notes with no retrieved context.
- Journaled approval execution with fixed document, destination, mutation, and execution IDs. Concurrent approvals claim once; crash recovery resumes the original operation, including a preparation interrupted before its first file effect. Tag changes preserve original file bytes, and links target stable document IDs.

The following remain before the browser chat goal can be accepted:

- Remaining tool coverage and per-call execution records, bounded conversation compaction, REST/SSE surfaces, and complete degraded-state/egress reporting.
- The live-provider/browser workflow, final regression checks, and an operator-ready deployment artifact.

Full M2 also includes the paired CLI/MCP work in specification §§7.4, 10, and 12, which this progress record does not claim as complete.

Validation on 2026-10-24: the Release build and all **1,788 deterministic tests** passed with warnings treated as errors. New tests cover conversation ownership, idempotent/concurrent admission, per-call budget enforcement, revocation, restart replay, source hash validation, current/deleted citation state, purge races, and a hosted read-only turn surviving caller cancellation. Approval tests cover simultaneous approval, three create-crash boundaries, update recovery, immutable edits, discard, key rejection, stale targets, changed policy, expired/purged proposals, automatic-approval restrictions, byte-preserving tag changes, and stable-ID links. Real-transport scripted-provider tests verify that a requested write remains pending until browser approval, that read-only asks reject invented writes, and that an invented document ID cannot expose its content. Live-provider/browser qualification remains outstanding.

The deployed M1 installation remains the authoritative live service until a tested chat bundle is installed.
