# M2 completion work

The objective to implement **all remaining M2 features** is complete as of 2026-10-24, and the full server/CLI release is [deployed](M2-complete-deployment.md). See the [acceptance ledger](M2-acceptance.md) for the requirement audit and final evidence, and [current project status](../project-status.md) for Codex connectivity and the M3 next steps. Intermediate test counts below are historical checkpoints.

## Implemented and deployed

- `brain ask <question> [--deep]`: submits a read-only turn, observes persisted events, and prints the final verified answer, citations, warnings, and terminal status. JSON output retains the full turn. Partial and failed one-shot answers return a nonzero exit status.
- Ask search limits are enforced by the executor: one search normally, up to five with `deep`, within the configured tool/document/byte budgets. The policy is persisted; changing `deep` is an idempotency conflict.
- `brain chat [conversation-id]`: resumes history and interrupted observation, streams provisional text, then reports the verified answer and sources. `/new`, `/pending`, `/approve ID`, `/discard ID`, `/help`, `/exit`, and `/quit` are recognized locally. Reconnection uses the last event sequence and never resubmits an accepted turn. Terminal control sequences are stripped. JSON chat emits newline-delimited event, turn, pending, and final result objects.
- `brain login <url> --interactive --name <name>`: displays a five-minute pairing link/code. A signed-in browser confirms the matching device/code with a password and antiforgery protection. The CLI receives a one-time credential for a session with a one-hour idle timeout and an eight-hour absolute lifetime, stored under `<name>.session` separately from the existing API key. Assistant commands prefer this session. Revocation and account epoch changes invalidate it.
- `brain pending list|approve|discard`: approval and discard require the paired session and reuse the existing journaled approval path. A paired CLI session cannot qualify for browser-only automatic approval or issue another paired session.
- Pairing challenges retain only a verifier for the polling secret, expire after five minutes, have per-source/global bounds, and revalidate the approving browser inside the claim transaction. Secure challenges cannot be claimed over an insecure request.
- The assistant now supports deterministic `/save [text]`, `/search <query>`, `/query <type> [JSON filters]`, `/recent [count]`, `/sources`, `/pending`, and `/new`. Retrieval commands use the same bounded executor and storage services. `/sources` shows the previous substantive answer's verified passages; `/save` without text retains that answer's source lineage. Browser and CLI share the server implementation; CLI approval commands remain local adapters.
- `POST /turns/{id}/retry` (also `/v1`) resumes only the latest interrupted turn, with an idempotent request ID. CLI `/retry [--fallback]` and the browser retry button use it. A pre-write retry can use the configured fallback. After a committed write it keeps the original provider, model and endpoint and replays recorded execution outcomes. Spent tool/document/byte/token budgets remain spent; purged results become redaction markers and citation numbers are never reused.
- `GET /conversations/{id}/transcript` (also `/v1`) returns portable messages and exact tool calls/results under conversation ownership. Provider switches between turns consume this history without provider-specific continuation IDs.
- Journal finalization binds generated-note lineage to the proposal and its originating call atomically with the effect, so purge cannot miss those payloads between the file write and assistant bookkeeping.
- For creates, publication binds that lineage atomically when the generated document first exists. Recovery preserves a redacted committed outcome when purge precedes assistant bookkeeping.
- Provider-call exposure snapshots retain the provider/model/endpoint and referenced document IDs across a pre-write fallback; the current turn binding no longer erases the earlier provider's manifest entry.
- The configured `reasoning` preference reaches the standard `reasoning_effort` control only when the model declares `reasoning_control`. Unsupported preferences are reported as `reasoning_ignored`. The configured output limit is honored, reasoning content is included in context accounting, and private reasoning is excluded from the user answer and portable transcript.

## Validation

The real-daemon CLI integration test passed: browser pairing stored a separate session, preserved the original API key, approved a pending note through the CLI, and completed a read-only ask.

Targeted tests live in `CliAssistantTests`, `CliPairingTests`, `CliConvergenceTests.Cli_PairThroughBrowserThenAskAndApproveWithoutReplacingApiKey`, `ChatWorkflowTests.AskSearchCapIsEnforcedOutsideTheModel`, and `ChatApiTests.AskDeepOptionIsFrozenAndCannotChangeOnIdempotentReplay`. Validation on 2026-10-24 covered all **1,845 deterministic tests**: the full run passed 1,843 and identified two expected golden differences; after reviewing and updating those snapshots, both snapshot tests passed. Locked restore and warnings-as-errors builds passed. Local TRX logs and `verification.json` are in `artifacts/m2-completion-tests/`. This was an intermediate checkpoint; final M2 verification appears below.

The build uses the installed .NET 10.0.111 SDK under the repository's feature-band roll-forward policy. In this environment MSBuild runs with `-m:1 -nr:false`; the test runner needs local sockets unavailable in the filesystem sandbox. Build projects serially because their output directories are shared.


The [MCP server and bridge](M2-mcp.md) now pass their integration tests, including a real-daemon bridge exchange, both protocol versions, write-only proposals, resources, prompts and signed cursors. The full regression run plus the corrected authorization/snapshot rerun covers **1,862 unique deterministic test cases**, with all latest outcomes passing. Logs and the merged outcome ledger are in `artifacts/m2-mcp-tests/`.

The retry, slash-command and create/purge changes subsequently passed all **1,886 deterministic tests** with warnings treated as errors. Logs are in `artifacts/m2-retry-command-tests/` (`verified*.trx`). Later reasoning-control and per-provider manifest changes are covered by the final Release verification below.

## Final verification and next work

All **1,895 deterministic tests passed in Release** with warnings treated as errors. Locked restore passed. The recorded-provider evaluation passed with 30/30 supported facts cited correctly, 0/10 unsupported assertions presented as sourced, and 9/10 irrelevant-corpus abstentions; the failed password-policy response remains in the fixture and review. Every irrelevant case retrieved four passages. See [the evaluation](../../eval/assistant/README.md), [the acceptance ledger](M2-acceptance.md) and [the M3 handoff](M3-handoff.md).

M3 is the next implementation milestone. The matching M2 server/CLI are now [deployed and verified](M2-complete-deployment.md). Live external-provider or Cloudflare qualification remains separate operator work and is not represented as a completed test.

The MCP SDK documentation and release notes were checked through Context7 and upstream. `ModelContextProtocol.AspNetCore` **2.2.0** was restored and pinned; it supports protocol 2026-07-28 and `HttpServerSessionMode.Stateless`. The MCP endpoint and stdio bridge are implemented; see [MCP contracts and evidence](M2-mcp.md). Sources: [SDK release](https://github.com/modelcontextprotocol/csharp-sdk/releases/tag/v2.2.0), [stateless transport](https://csharp.sdk.modelcontextprotocol.io/v2/concepts/stateless/stateless.html).

The complete M2 release was deployed to the main systemd service on 2026-10-24 at 19:57 UTC; see the [deployment record](M2-complete-deployment.md).
