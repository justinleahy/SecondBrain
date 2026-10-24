# Browser chat implementation progress

This work implements the browser chat goal on top of the deployed M1 service. It does not close the full M2 milestone or change the installed service yet.

The working implementation has:

- A browser chat page at `/` and `/chat`, using the existing authenticated Blazor circuit. It displays conversations, streamed text, activity, source excerpts, and interrupted-turn status.
- Persistent conversation ownership, idempotent turn admission, one active turn per conversation, ordered replay events, and restart recovery. Accepted work belongs to the host rather than the browser connection.
- A bounded `Microsoft.Extensions.AI.FunctionInvokingChatClient` pipeline using the configured provider. Each provider round trip revalidates authority, enforces context/output bounds, and reserves persistent hourly/global daily tokens.
- Retrieval through the existing search, structured-query, and document repositories. Sources carry their exact revision, processing generation, excerpt, and hash; citation numbers are verified against that registry.
- Cumulative tool, metadata, document, and byte limits. Reading an arbitrary model-invented document ID is rejected.
- Payload lineage shared with the M1 purge journal. Purging a source redacts derived answers, source excerpts, and replay events, including protection against output arriving after the purge. Changed/deleted sources retain their historical excerpt and display their current status.

The following remain before the browser chat goal can be accepted:

- Immutable write proposals, browser approval/rejection, and exactly-once execution/recovery. No write tool is exposed by the current executor.
- Additional tool coverage, bounded conversation compaction, REST/SSE surfaces, and appropriate provider-failure handling.
- Adversarial executor tests, the live-provider/browser workflow, final regression checks, and an operator-ready deployment artifact.

Full M2 also includes the paired CLI/MCP work in specification §§7.4, 10, and 12, which this progress record does not claim as complete.

Validation on 2026-10-24: Release build with warnings as errors passed, and all **1,770 deterministic tests** passed. New tests cover conversation ownership, idempotent/concurrent admission, per-call budget enforcement, revocation, restart replay, source hash validation, current/deleted citation state, purge races, and the full hosted read-only turn surviving cancellation of its caller. Live-provider/browser qualification and approval execution are still outstanding.

The deployed M1 installation remains the authoritative live service until a tested chat bundle is installed.
