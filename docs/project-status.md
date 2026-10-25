# SecondBrain project status

Updated **2026-10-25**. **M4 is complete and deployed on the reference Fedora / RTX 5090 host for the user-agreed scope**, using signed candidate **v0.4.0-rc.6**. [Acceptance and deployment evidence](build/M4-acceptance.md). The researched next milestone is [M5 — private email import and source privacy](build/M5-private-email-and-source-privacy.md). This page describes current capability; [the specification](../spec.md) also includes future requirements.

## Milestones

| Milestone | Current status | Evidence / next scope |
| --- | --- | --- |
| M0 — Foundations | Closed for the reference deployment | [Closure and deferred qualifications](build/M0-convergence-report.md#m0-closure-2026-10-09) |
| M1 — Ingest and search | Closed for the accepted single-host scope | [Closure, live ingestion and scale evidence](build/M1-convergence-report.md#m1-closure-2026-10-24) |
| M2 — Ask and MCP | Complete and deployed | [Acceptance ledger](build/M2-acceptance.md), [deployment record](build/M2-complete-deployment.md) |
| M3 — Web UI | Complete and deployed; VPN/Cloudflare qualification waived by the user | [Acceptance, scope and deployment evidence](build/M3-acceptance.md) |
| M4 — Meetings, enrichment, operations | Complete; deployed 2026-10-25 at 05:39:53 UTC | [Acceptance](build/M4-acceptance.md), [plan](build/M4-meetings-enrichment-operations.md), [historical progress](build/M4-progress.md) |
| M5 — Private email import and source privacy | Researched; ready for its implementation goal | EML/MBOX, threads/attachments, recoverable containers, inherited privacy and separate local embedding space; [plan and 16 gates](build/M5-private-email-and-source-privacy.md) |

## Deployed and usable

- **Browser:** [reference LAN UI](http://10.66.10.20:7171/) with sign-in, streaming chat, source excerpts, conversation controls, approval proposals, retry and provider exposure manifests.
- **Knowledge UI:** library/search with facets and people/date filters, document/PDF passages, speaker/timestamp transcript viewing, calendar occurrences and coverage, Monaco note editing, timeline, sources/jobs, suppressions and trash.
- **Ingestion:** sandboxed Markdown/text/PDF/HTML/DOCX, VTT/SRT and ICS extraction; durable file/text capture and watched-folder reconciliation. Shared webcal/HTTPS calendar subscriptions support conditional refresh, freshness and pause/resume. Remote calendar origins require explicit configuration and firewall opt-in; the private qualification feeds were not registered in production.
- **Classification and enrichment:** optional local/provider classification and recoverable enrichment, original-property preservation, summaries, suggested tags, mentions and link suggestions, controlled batches and optional journaled Markdown frontmatter write-back.
- **Settings and operations:** providers/roles, privacy, credentials, chunking/reprocessing, shared inference budgets and usage attribution, retention, encrypted backup/download, portable exports and verified offline restore. Sensitive operations require the documented administrator session and step-up. Existing initialized accounts require a complete encrypted archive before the M4 schema upgrade.
- **Assistant and CLI:** read-only cited `brain ask`, resumable `brain chat`, saved history and approval proposals. Calendar date questions start with ordered structured occurrences and bounded verified passages. Browser or paired CLI approval is required for proposed writes.
- **MCP:** stateless `POST /mcp` and the `brain mcp` stdio bridge retain eight tools, resources and prompts. The configured Codex bridge uses a read-only credential. [Contracts and setup](build/M2-mcp.md).

The installed CLI is `/usr/local/lib/secondbrain/brain`; its bundle is `/usr/local/lib/secondbrain`. The production upgrade verified **465 installed files** and preserved the account, credentials, configuration, secrets, key ring and four document identities. Existing authenticated retrieval returned the same three readable texts and revisions. [Production evidence](build/evidence/m4-production-20261025/verification.json), [authenticated readback](build/evidence/m4-production-20261025/authenticated-readback.json).

## Codex connection

The reference user's `~/.codex/config.toml` contains an enabled `secondbrain` stdio server using the installed CLI, `http://127.0.0.1:7171`, and the dedicated `codex` read-only credential in Linux Secret Service. Its enabled tools are `brain_search`, `brain_query`, `brain_get`, `brain_list_recent` and `brain_tags`. It can retrieve context for Codex; server-side `brain_ask` and write tools are excluded from this client configuration.

The configured bridge successfully completed discovery, tool listing and an authenticated `brain_list_recent` call with both supported protocol generations, 2026-07-28 and 2025-11-25. Loopback and LAN health/readiness checks also returned 200. [Sanitized connection evidence](build/evidence/m2-codex-20261024/connection-verification.json) contains no secrets or document contents. Restart an already-running Codex session to load the new connection; the bridge check does not establish that the current session reloaded its tool catalog. [Exact configuration](build/M2-mcp.md#codex-on-the-reference-host).


M4 post-deployment checks reused that existing credential successfully and exercised the new calendar query contract. No registered production calendar returns partial coverage, rather than an unsupported claim of an empty complete calendar.

## Verification and limits

- The final [remote release run](https://github.com/justinleahy/SecondBrain/actions/runs/40165478709) passed **2,555 deterministic Release tests** across eight projects, locked audits, warning-free build, parser mutations, sustained-write recovery, distinct-user staging, hardened Compose workflows, all package/container scans and keyless signing. All known-vulnerability scans passed at the unchanged low threshold with zero matches and no ignores. Local deployment safeguards total 41; release-inventory safeguards total nine. [Signed release proof](build/evidence/m4-release-20261025/verification.json).
- Production verification passed **22 loopback/LAN HTTP checks**, installed/served asset comparison, provider/canary readiness, maintenance authentication and actual systemd identity/isolation checks. The signed-out LAN browser connected with zero console errors/warnings and no overflow at desktop and phone widths. This browser check did not exercise an authenticated production assistant turn. [Browser scope](build/evidence/m4-production-20261025/browser.json).
- Real-export transcript and calendar answers passed bounded agent review. The user explicitly waived the pricing-specific transcript example. Calendar review validates chronology, times, coverage and current record citations, with its title/status wording limitation retained. These examples do not establish universal model answer quality. [M4 acceptance and limits](build/M4-acceptance.md).
- A 100,006-file encrypted archive restored in 2,818.49 seconds after 137 concurrent backup edits; authored-state comparison passed. The offline restore rebuilds text and semantic vectors catch up asynchronously. A separate current-image startup loaded one million vectors within the memory allowance with zero restarts. [Scale evidence](build/evidence/m4-backup-scale-20261024/large-corpus-result.json).
- Production pre-upgrade encrypted backup/schema completion is inferred from the verified M4 binary reaching ready under its mandatory fail-closed migration gate; root-private archive bytes were not independently read. Recovery was tested in disposable stores. Off-server recovery-key custody is operator-attested.
- Earlier M2/M3 acceptance remains historical evidence. The recorded local-provider evaluation achieved 30/30 supported facts cited, 0/10 unsupported claims presented as sourced and 9/10 irrelevant-corpus abstentions; its retained failure and bounded scope remain documented. [Evaluation protocol](../eval/assistant/README.md).
- VPN and Cloudflare origin checks remain unperformed under the explicit M3 waiver. External vendor adapters use controlled contracts; production model and reasoning settings were preserved. Earlier platform/network follow-ups remain separate from this reference-host acceptance.

## Next work

Implement [M5 — private email import and source privacy](build/M5-private-email-and-source-privacy.md) under the new user-requested goal. Qualify local EML/MBOX exports, attachment/thread identity, inherited restrictions across all inference paths, a separate local embedding space, recovery and the 500,000-message corpus. Live mailbox protocols, contacts/tasks, entity resolution and approximate vector indexing remain later work. M5 production promotion follows a separate operator decision after its acceptance gates.
