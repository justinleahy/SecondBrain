# Approval preview Markdown — 2026-10-24

Status: **deployed and verified** on the reference service at 20:14 UTC.

Suggested notes and suggested updates previously displayed their complete Markdown source inside a plain-text block. `ApprovalCard` now uses the existing safe `ChatText` renderer, with headings, lists, bold text, inline code and fenced code blocks. The shared renderer also supports pipe tables with alignment, escaped pipes, code within cells, and bounded column counts. The preview scrolls vertically; wide tables scroll within the preview on phones. Edit continues to show the original Markdown, and rendering does not change the immutable proposal payload or approve it. Raw HTML remains encoded and remote images are not loaded.

All **302 deterministic server tests** passed in Release. This includes component rendering of create/update proposals, unchanged payloads, table formatting and HTML/citation boundary cases. Results: `artifacts/approval-markdown-tests/approval-markdown.trx`.

An authenticated disposable browser instance exercised the real approvals page at 1440×1000 and 390×844. Checks confirmed rendered headings, table rows and code, original Markdown in Edit, no page overflow, horizontal table scrolling on mobile, inactive embedded script text and zero console errors/warnings. The [desktop](evidence/approval-markdown-20261024/desktop.png) and [mobile](evidence/approval-markdown-20261024/mobile.png) captures use synthetic notes and a temporary account. No real proposal was approved during verification.

The release overlays the freshly published self-contained server on the verified installed bundle, retaining the CLI and extractor. Only six files changed: the server DLL/PDB, static-asset endpoint manifest, scoped CSS and its two compressed forms. The managed deployment service preserved the account, credentials, configuration, secrets and key ring. Independent verification matched all **433 installed files** and passed **six live HTTP checks** across loopback and LAN, including readiness and the exact served stylesheet.

See the [verification record](evidence/approval-markdown-20261024/verification.json), [deployment result](evidence/approval-markdown-20261024/deployment-result.json), [publish provenance](evidence/approval-markdown-20261024/provenance.json) and [file manifest](evidence/approval-markdown-20261024/sha256-manifest.json). The bundle is retained at `artifacts/approval-markdown-release-20261024/merged`. Refresh an existing browser tab to load the updated assets.
