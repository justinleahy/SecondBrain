# M3 acceptance — 2026-10-24

Status: **complete and deployed for the user-agreed scope**. The user explicitly instructed: **“skip vpn and cloudflare”**. Those two origin qualifications were waived, not passed. Phone-width sign-in and workflows were exercised on isolated local origins. No authenticated production-account assistant turn is claimed.

M3 implements the browser scope in specification §11 and the [handoff](M3-handoff.md): library/search, document viewing, Monaco editing, conflict recovery, timeline, source/job management, suppressions/trash, settings and responsive navigation. Transcript/calendar rendering, meeting enrichment, retention and backup operations remain M4 work.

## Acceptance checks

| Requirement | Result and evidence |
| --- | --- |
| U6: summary with exact-passage footnotes | Passed at 1280 px and 390 px using the real local Qwen chat/embedding services and synthetic documents. Each displayed footnote opened its source; the excerpt matched the document and its recorded SHA-256. [Answers and checks](evidence/m3-local-acceptance-20261024/assistant-and-restart.json). |
| U7: this week's work | Passed at both widths. The answers selected the two current Project Orion work documents and excluded the archived Project Vega item. The fixture also contained older reference material; this is a bounded recency-aware example, not a general answer-quality claim. [Answers and checks](evidence/m3-local-acceptance-20261024/assistant-and-restart.json). |
| U11: failed files and retry | Passed in Release at both widths: named files and failure reasons, three transient failures followed by successful retry through the real extractor, deterministic invalid-PDF retry refusal, counts, pause/resume and removal. Original source bytes were preserved. [Browser checks](evidence/m3-local-acceptance-20261024/catalog-sources-csp.json), [original hashes](evidence/m3-local-acceptance-20261024/originals-after-source-removal.json). |
| U16: phone UI | Local phone-width password sign-in and full workflows passed without horizontal overflow. VPN and Cloudflare portions were explicitly waived by the user. [Sign-in](evidence/m3-local-acceptance-20261024/assistant-and-restart.json), [Release workflows](evidence/m3-release-candidate-20261024/browser-checks.json). |
| Document rendering / CSP | Passed: PDF page/passage fidelity, escaped hostile titles/aliases/tags, no executable document markup, inline-script blocking and remote-image refusal. [Release rendering checks](evidence/m3-release-candidate-20261024/browser-checks.json), [hostile metadata](evidence/m3-local-acceptance-20261024/catalog-sources-csp.json). |
| Editor / conflicts | Rich/plain editor round trips, completion, frontmatter, dirty-navigation protection, revision conflicts and preserved filesystem-conflict drafts passed locally. Both external-revision reconciliation and saving a separate note passed. [Editor/conflict checkpoint](evidence/m3-chunking-20261024/browser-checks.json). |
| Timeline / lifecycle | Date/people filters, search history, facets, timeline, metadata-only trash, suppression lifting and restoration passed in Release at both widths. [Browser checks](evidence/m3-local-acceptance-20261024/catalog-sources-csp.json). |
| Settings / processing generations | Step-up, validated changes, conflict retention, providers/roles, budgets, key creation and chunking passed. Rechunking preserved text/revisions, refreshed semantic search, and rejected invalid bounds. Regression cases cover stale vectors, reservations, capacity, crash recovery and embedding-space activation. [Release checks](evidence/m3-release-candidate-20261024/browser-checks.json), [chunking checks](evidence/m3-chunking-20261024/verification.json). |
| Session / restart | A fresh server instance rejected the old circuit with the recovery dialog and retained its unsaved draft. Explicit reload kept the same authenticated session, six conversations and unchanged stored note. Transport interruption/rejoin also passed at both widths. The Release idle-revocation check closed the circuit in 1,912 ms while retaining an independent session. [Restart evidence](evidence/m3-local-acceptance-20261024/assistant-and-restart.json), [Release lifecycle checks](evidence/m3-release-candidate-20261024/browser-checks.json). |

## Release and deployment

Locked restore and **1,956 Release tests across eight projects** passed with warnings treated as errors. All **33 Python deployment tests** and **six JavaScript transport checks** passed. [Release verification](evidence/m3-release-candidate-20261024/verification.json). The later acceptance harness is test-only; it does not change the deployed application binaries.

The self-contained `linux-x64` server, CLI and extractor were published with SDK 10.0.111/runtime 10.0.12. The user completed the [private settings-directory migration](../../deploy/systemd/writable-settings.md). The reference unit now permits writes to `/etc/secondbrain/settings`, retaining the protected parent directory and secret mounts.

The existing fixed-purpose deployment service installed M3 and restarted the daemon. Independent verification matched **all 484 installed files** and passed **26 loopback/LAN HTTP checks**, including the new routes' authentication boundaries and exact static-asset bodies. The deployment report confirms preservation of the account, credentials, configuration/secrets, key ring and four existing documents. [Deployment result](evidence/m3-deployment-20261024/deployment-result.json), [independent verification](evidence/m3-deployment-20261024/independent-verification.json), [published-file manifest](evidence/m3-deployment-20261024/sha256-manifest.json), [provenance](evidence/m3-deployment-20261024/publish-provenance.json).

The fixed deployment service retains its legacy `chat` release label. The independently verified file manifest identifies the installed M3 candidate.

The UI is available at **http://10.66.10.20:7171/**. The candidate is retained at `artifacts/m3-candidate-20261024/`; the previous staging bundle and the deployment service's private rollback backup are preserved. All isolated browser fixtures were stopped after verification.

## Qualification limits

The required remote-origin checks were waived explicitly; neither VPN nor Cloudflare acceptance is claimed. The configured public hostname did not resolve during the read-only probe, and the reference VPN policy does not admit this test host's own tailnet address. These observations remain deployment follow-ups rather than completed checks.

The local model cases use a small synthetic corpus. Their binding reported the requested reasoning preference as ignored. The server-restart browser test recreated an application instance over retained synthetic state and signing keys; the production process restart is recorded separately by the deployment service. Earlier accidental unfiltered million-chunk diagnostics failed and remain recorded in the [conflict/reconnect checkpoint](evidence/m3-conflicts-reconnect-20261024/unfiltered-qualification.log); this release does not claim a new scale qualification.

**Next milestone: [M4 — meetings, enrichment and operations](M4-meetings-enrichment-operations.md).**
