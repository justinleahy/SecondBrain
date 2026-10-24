# Browser chat validation — 2026-10-24

This record covers the browser chat implementation on top of M1. It does not close the paired CLI/MCP milestone or claim that the new build is installed in the production systemd service.

## Live browser checks

`ChatBrowserQualification.LocalBrowserAndProvider` started the real Kestrel application on `127.0.0.1:17172`, using temporary stores, a temporary test account, and the existing local vLLM chat/embedding services. The harness uses the existing test-host overrides for its account, key ring, and extractor, so it does not replace systemd identity/firewall qualification. No production account, configuration, or vault was changed.

The browser used the served Blazor application and its normal cookie, antiforgery, and circuit paths. The models were `Qwen/Qwen3-8B` and `Qwen/Qwen3-Embedding-0.6B`.

| Check | Observed result |
|---|---|
| Grounded answer | The model correctly answered the synthetic Lighthouse codename, owner, and review date, with `[1]` pointing to the exact stored passage. |
| Citation display | The source card expanded to the original text, document ID, revision, and passage ordinal. |
| Exposure manifest | The manifest named the provider/model and both retrieved documents, including the uncited garden note. |
| Approval boundary | The proposed note's file did not exist before approval. Clicking Approve created exactly the requested note; the document count increased from two to three. |
| Editing/discarding | Editing created a new proposal and superseded the original. Discarding the replacement created no file. |
| Read-only ask | A request to save a note produced a refusal, no proposal, and no document. |
| Reload | The completed read-only turn was still available after reloading the page. |
| Reopened mode and activity | A final run preserved the read-only checkbox when reopening its conversation and loaded the persisted tool activity. |
| Approval receipt | After a browser approval, the next model response correctly identified the note as saved, using the server's recorded outcome. |
| Abstention | A question about an absent access code received an explicit statement that it was not present in the retrieved material. |
| Purge | After step-up and purge of the synthetic source, reopening the conversation displayed redaction notices in place of its derived answers. |
| Phone width | At a 390 px viewport, the document width remained 390 px. |
| Browser errors | The corrected run had no console errors. |

The initial browser run found a CSP violation from inline styles and an incorrectly bound string parameter in the manifest component. Both were corrected and retested. Chat text is rendered through a small formatting-only encoder: model HTML, links, and images cannot become active content.

Local screenshots, browser snapshots, API responses, and harness results are under `artifacts/m2-browser-20261024-2/` and `artifacts/m2-browser-20261024-3/`. Selected synthetic state checks are retained alongside this document under `evidence/m2-browser-20261024/`.

## Automated coverage

Tests cover persisted turn/event ordering, credentials and ownership, per-call token and exposure limits, permitted tool names, exactly-once approval claims, file/publication crash recovery, immutable edits, stale/expired/purged proposals, automatic approval restrictions, sanitized formatting, extractive history compaction and purge lineage, approval outcomes in resumed history, and HTTP/SSE authorization and antiforgery.

All **1,800 deterministic tests passed** on the final implementation, with warnings treated as errors. Production installation is tracked separately in the progress document.
