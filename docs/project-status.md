# SecondBrain project status

Updated **2026-10-24**. **M2 is complete, deployed, and verified on the reference Fedora / RTX 5090 host. M3 is the next implementation milestone.** This page describes current capability; [the specification](../spec.md) also includes future requirements.

## Milestones

| Milestone | Current status | Evidence / next scope |
| --- | --- | --- |
| M0 — Foundations | Closed for the reference deployment | [Closure and deferred qualifications](build/M0-convergence-report.md#m0-closure-2026-10-09) |
| M1 — Ingest and search | Closed for the accepted single-host scope | [Closure, live ingestion and scale evidence](build/M1-convergence-report.md#m1-closure-2026-10-24) |
| M2 — Ask and MCP | Complete; full release deployed 2026-10-24 at 19:57 UTC | [Acceptance ledger](build/M2-acceptance.md), [deployment record](build/M2-complete-deployment.md) |
| M3 — Web UI | Next; chat, approvals and authentication already exist | Search/browse, document viewing/editing, timeline, sources/jobs, suppressions/trash, settings and mobile qualification; [handoff](build/M3-handoff.md) |
| M4 — Meetings, enrichment, operations | Planned | Transcript/calendar types, shared calendar feeds, enrichment, backup/export and release operations; [plan](build/M4-meetings-enrichment-operations.md) |

## Deployed and usable

- **Browser:** [reference LAN UI](http://10.66.10.20:7171/) with sign-in, streaming chat, verified source excerpts, conversation controls, approval proposals, retry and the provider exposure manifest.
- **Knowledge services:** file/text capture, sandboxed Markdown/text/PDF/HTML/DOCX extraction, lexical/semantic retrieval, structured queries, durable jobs and watched-folder reconciliation.
- **`brain ask`:** a read-only question with citations and terminal status; `--deep` allows up to five searches instead of one, subject to the remaining budgets.
- **`brain chat`:** a resumable conversation with saved history, slash commands, approval proposals and same-turn retry. Browser pairing enables explicit CLI approval; ordinary API keys and MCP cannot approve changes.
- **MCP:** stateless `POST /mcp` plus the `brain mcp` stdio bridge; eight tools, document/note resources and daily-review/topic-brief prompts. The default bridge requires a read-only key. [Contracts and setup](build/M2-mcp.md).

The installed CLI is `/usr/local/lib/secondbrain/brain`; the server bundle is `/usr/local/lib/secondbrain`. The deployment preserved the existing account, credentials, configuration, secrets, key ring and four documents present at deployment. The release was published from the verified working tree; its retained file manifest and source fingerprint identify the deployed snapshot.

## Codex connection

The reference user's `~/.codex/config.toml` contains an enabled `secondbrain` stdio server using the installed CLI, `http://127.0.0.1:7171`, and the dedicated `codex` read-only credential in Linux Secret Service. Its enabled tools are `brain_search`, `brain_query`, `brain_get`, `brain_list_recent` and `brain_tags`. It can retrieve context for Codex; server-side `brain_ask` and write tools are excluded from this client configuration.

The configured bridge successfully completed discovery, tool listing and an authenticated `brain_list_recent` call with both supported protocol generations, 2026-07-28 and 2025-11-25. Loopback and LAN health/readiness checks also returned 200. [Sanitized connection evidence](build/evidence/m2-codex-20261024/connection-verification.json) contains no secrets or document contents. Restart an already-running Codex session to load the new connection; the bridge check does not establish that the current session reloaded its tool catalog. [Exact configuration](build/M2-mcp.md#codex-on-the-reference-host).

## Verification and limits

- **1,895 deterministic tests passed** across eight projects with locked restore and Release warnings-as-errors builds. [Acceptance evidence](build/evidence/m2-closure-20261024/verification.json).
- The recorded local-provider evaluation passed with **30/30 supported facts cited, 0/10 unsupported claims presented as sourced, and 9/10 irrelevant-corpus abstentions**. It uses actual recorded chat/embedding responses and manually reviewed claim annotations; the failed abstention is retained. This small synthetic regression gate does not establish universal answer quality. [Evaluation protocol and limitations](../eval/assistant/README.md).
- Deployment verification matched **433 installed files**, completed **24 HTTP checks**, and loaded the LAN browser with its runtime connected and zero console errors/warnings. The browser deployment check was signed out; it did not exercise a live authenticated assistant turn. The later Codex check exercised authenticated retrieval. [Deployment evidence](build/M2-complete-deployment.md).
- Live Cloudflare/Access routing and external model vendors are not qualified by M2's local tests. Earlier closure records retain separate platform/network follow-ups. The deployment preserved production model/reasoning settings; the evaluation's explicit settings were not promoted automatically.
- Transcript/calendar parsing and shared calendar subscriptions remain unimplemented. M3's full UI acceptance, including desktop/mobile workflows through both specified origins, remains outstanding.

## Next work

Start M3 with browser search and document viewing, then editing/conflict handling and source/job management. Reuse the existing ingestion, retrieval, publication and approval services; retain the replay, ownership, citation and purge contracts in the [handoff](build/M3-handoff.md). Complete the remaining browsing, timeline, trash/suppression, settings and mobile workflows and the specification's browser acceptance gates before closing M3. Keep M4 calendar/meeting controls unavailable until their underlying services exist.
