# M3 Web UI implementation

Status: **complete and deployed for the user-agreed scope**, 2026-10-24. The user explicitly waived VPN and Cloudflare qualification. See the [final acceptance ledger](M3-acceptance.md) for the passing local checks, deployed-file verification and remaining network qualifications. The checkpoints below record implementation history.

The scope is [specification §11 and §19](../../spec.md), with the existing contracts and acceptance constraints in [the handoff](M3-handoff.md). The active goal remains completion of that full scope; the first implementation checkpoint does not narrow it.

## First implementation checkpoint

- `/library` and `/search`: browse current documents with cursor pagination; keyword, semantic and hybrid search; type, tag, source, people, occurred and updated filters; text-encoded range highlights; explicit degraded-search messages; links to a revision and passage in the viewer.
- `/library/{id}`: current document rendering through the existing safe Markdown renderer; properties, provenance, tags, lossless-text toggle, extracted PDF chunk/page labels, original downloads, and a notice when the linked revision differs from the current revision.
- `/capture` and `/library/{id}/edit`: direct note capture without a model; title/type/tags/properties editing; text editing for supported originals; revision-checked saves; retention of an unsaved draft after a 412 conflict, with the latest text shown separately. Loading the latest revision explicitly replaces the draft before a new save.
- `/manage/sources` and `/manage/jobs`: existing source scan/deep-scan and pause/resume actions; job status, stage, attempts, errors and retry controls.
- `/settings`: current sessions and API-key listings with continuation controls; sign-out, session/key revocation, password step-up and service diagnostics. Step-up reloads the circuit after the server rotates the session.
- Shared browser API calls use same-origin cookies, fresh antiforgery tokens for unsafe methods, explicit revision headers and no redirect following. Existing endpoint authority and admission rules remain in force.
- Shared navigation connects these screens to chat and approvals; layouts adapt to 390 px width.

These are partial implementations of the screens, not declarations that every specification detail for a screen is complete.

## Timeline and catalog checkpoint

The working tree also includes the following M3 features:

- `/activity`: timeline with exclusive date bounds in the configured instance time zone, day grouping, type/people filters, tags and cursor pagination. A day spanning a clock change uses its actual UTC boundaries. Undated documents remain in the library.
- `/manage/trash`: metadata-only listing and revision-checked restore through the existing journaled mutation endpoint. The document viewer has an explicit move-to-trash confirmation.
- `/manage/suppressions`: metadata-only listing and confirmed lifting. A lift changes only the suppression; it does not restore a trashed or purged document. Lifting requires an interactive session and CSRF token, and rechecks authority inside the write transaction.
- Search type facets count every keyword-matching document under the non-type filters, including matches beyond the ranked result page. Semantic-only search labels its counts as documents in the selected filters, rather than claiming they are relevance totals. Source options show names without exposing source configuration and support pagination. Applied search filters and result-page cursors are retained in the URL.
- Query results now include committed occurrence times and tags. The viewer can open related documents, while unavailable source documents return no related results.

New HTTP routes have bare and `/v1` forms: `GET /timeline`, `/trash`, `/suppressions`, `/tags`, `/types`, `/browse/sources`, `/documents/{id}/related`; `POST /search/facets`; and `DELETE /suppressions/{id}`. Catalog continuation tokens are protected and bound to the instance, operation, credential, generation, epoch and expiry. The trash contract contains no original path or retained document text.

`KnowledgeBrowserApiTests` and `TimelineWindowTests` cover calendar boundaries, real filtered timeline pages, metadata redaction, cursor isolation, suppression authorization and facet counts beyond the ranked-result limit. `tests/browser/m3-catalog-workflows.js` exercises search/history, timeline and people filtering, trash, suppression lifting and restoration at desktop and 390 px. The full Debug suite passed **1,923 tests across eight projects**. The subsequent Server-only circuit follow-up passed **330 tests**, including five new cases, for **1,928 unique passing cases** covered across these phases. [Verification and source hashes](evidence/m3-catalog-20261024/verification.json) distinguish the phases from a final Release acceptance run.

### Circuit lifecycle

Repeated browser navigation exposed a permit leak in the original lifecycle: disconnected circuits remained counted until the framework's reconnect cache expired. Connected-circuit permits are now released on disconnection and reacquired only after fresh authority validation on reconnection. Simultaneous connected circuits still respect the configured limit; Blazor retains its bounded reconnection cache. Unit cases cover permit reuse, reconnection at capacity, disposal, revoked credentials, changed generations/epochs and idle expiry.

`SessionWatch` also checks the captured session authority every two seconds without refreshing idle expiry. It sends invalid authority through Blazor's fatal lifecycle exception handling, outside any error boundary, so an idle circuit closes without needing a browser action. The final browser run passed nine full-page loads and closed an idle revoked session's WebSocket **1,901 ms after revocation**, while an independently authenticated session remained valid. The original editor/conflict/capture/settings browser sequence and the catalog sequence both passed again on this build. [Browser evidence](evidence/m3-catalog-20261024/browser-checks.json) and mobile screenshots are retained with the checkpoint.

The framework lifecycle and background exception handling were checked against the official [circuit handler documentation](https://learn.microsoft.com/aspnet/core/blazor/fundamentals/signalr?view=aspnetcore-10.0) and [component synchronization documentation](https://learn.microsoft.com/aspnet/core/blazor/components/synchronization-context?view=aspnetcore-10.0). Bootstrap scripts are loaded before the framework starts interactive rendering.

## Editor and document fidelity checkpoint

- Notes use a locally bundled, pinned Monaco editor with a plain-text toggle. Both modes preserve the current draft, and leaving a dirty editor requires confirmation. Saves still use the current document revision and preserve a conflicting draft.
- The frontmatter form edits title, type, tags, aliases, source URL and structured properties, with read-only document identity, source and stored timestamps shown separately. Authored Markdown links are retained in the projection. The viewer shows aliases, tag origins, resolved links, source metadata and paginated revision history; history lists metadata rather than historical document bodies.
- Wikilink and tag completion in Monaco uses bounded, authenticated catalog requests. Tag and people fields also complete the current comma-separated entry without discarding earlier entries. Filter controls remain disabled until their initial values load.
- PDF viewing uses the extractor's real page boundaries and highlights the selected passage across page boundaries. A cache lookup fix preserves access to extraction metadata after format-based classification and metadata-only revisions. Page ranges are used only when the cached extraction text matches the current committed text.
- Monaco and its worker are served from this application. Style elements carry a response nonce; generated layout style attributes are allowed. Inline scripts, evaluation, remote images and remote workers remain blocked. See [asset build instructions](../../tools/editor/README.md).

The local editor browser workflow passes at 1280 px and 390 px: rich editing, completion, plain-text round trips, navigation confirmation, aliases, links, history and revision saves. A real two-page PDF verifies page separation and highlighted text. The rendered-document probe confirms that inline scripts and remote images are blocked. The earlier conflict/capture/settings and catalog/lifecycle workflows also pass on this build. The circuit check passed nine direct page loads and closed the revoked idle session in **1,905 ms**, while preserving an independent session. [Browser evidence and screenshots](evidence/m3-editor-20261024/browser-checks.json) record the scope. This is development evidence, not the required VPN/Cloudflare acceptance run.

The final Debug suite passed **1,932 tests across eight projects**, with warnings treated as errors. Metadata cases cover aliases, literal-prefix completion and deletion visibility, protected revision history, per-response style nonces, and PDF page fidelity after classification and metadata-only edits. The transport assertions and reproducible local asset build also passed. [Verification and source hashes](evidence/m3-editor-20261024/verification.json) identify this checkpoint. The browser harness was stopped afterward; no deployment was performed.

## Settings checkpoint

The working-tree Settings page now shows provider locality and credential presence, resolved role capabilities and limits, privacy and egress status, resource limits, the enforced daily token budget, and time/ingestion/assistant preferences. A fresh password confirmation enables configuration editing. Provider definitions and role bindings use the existing provider and privacy validators. New API keys default to read-only, display their secret once, and remain subject to the existing credential authorization and scope rules.

`GET /configuration` and `PUT /configuration`, with `/v1` aliases, expose a redacted configuration view and section-scoped changes. Reads require admin scope; writes additionally require an interactive session, current step-up and antiforgery. A raw-file revision prevents stale edits from overwriting a newer configuration. The editor retains a conflicting draft until the user explicitly reloads. The write path validates the candidate before atomic replacement, preserves secret references and unedited/deferred YAML sections, retains file permissions and symlinks, and rechecks authority immediately before committing. YAML formatting may be normalized.

Editable sections are privacy, providers, model bindings, limits, daily budget, time, ingestion, chunking and assistant safeguards. Network listeners, allowed roots, Access configuration and extractor isolation remain restart-only. The chunking follow-up below adds controlled processing generations. Backup and retention controls remain M4 work. The installed systemd/Compose configuration is currently read-only; deployment must provide a narrowly writable configuration location before this workflow can pass on the reference service.

The final Debug suite passed **1,940 tests across eight projects**, plus six JavaScript transport checks. The local settings browser sequence passes at desktop and 390 px: step-up, privacy reload, budget persistence, conflict retention, resolved role-limit changes, provider creation/edit/removal and one-time read-only key creation. The prior editing/capture/settings sequence passed again. The circuit check completed nine full page loads and closed a revoked idle session in **1,909 ms** while preserving an independent session. [Browser evidence](evidence/m3-settings-20261024/browser-checks.json), [screenshots and source/test hashes](evidence/m3-settings-20261024/verification.json) identify this checkpoint. The fixture was stopped afterward.

Configuration tests also cover permission/symlink preservation, stale external edits, invalid candidates, secret-reference and deferred-section preservation, and revocation during candidate validation. The latter leaves both the file and running options unchanged. These are local development checks; the deployed-origin gates remain open.

## File conflicts and reconnection checkpoint

An edit that encounters changed filesystem content now returns a distinct conflict response. The editor keeps the draft, offers a download of its journal-preserved copy, and lets the user either load the indexed external revision or save the draft as a separate note. A missing or nonregular destination is kept intact and still preserves the draft. Conflict downloads require current read authority, belong to the requested document, and disappear after purge.

The conflict journal now atomically queues extraction of an observed external revision and clears its stale text projection while retaining metadata. A further filesystem change before extraction is rejected rather than published under the earlier hash. Existing journal recovery and purge behavior remain covered by the regression suite.

The reconnection dialog reports interrupted, failed and rejected connections. It offers explicit retry/reload controls and lets the user keep the page open to copy an unsaved draft. It does not automatically reload a rejected circuit. Real WebSocket interruption and rejoin preserved an unsaved draft, which was subsequently saved, at both 1280 px and 390 px. Rejected-state controls were checked separately using a simulated state event; this does not qualify an actual daemon restart.

The final Debug suite passed **1,942 tests across eight projects**, with warnings as errors, plus six JavaScript transport checks. Desktop/mobile file-conflict and reconnect workflows passed, as did the existing search/edit/capture/settings sequence. Nine direct loads passed and the revoked idle circuit closed in **1,905 ms**, preserving the independent session. [Browser checks](evidence/m3-conflicts-reconnect-20261024/browser-checks.json), mobile screenshots and [verification hashes](evidence/m3-conflicts-reconnect-20261024/verification.json) identify this checkpoint. The fixture was stopped afterward; no deployment was performed.

An initial test invocation omitted the standard `Category!=Qualification` filter. Besides fixtures requiring explicit launch settings, it ran two million-chunk retrieval qualifications, both of which failed with a disposed bounded connection during search. [The retained log](evidence/m3-conflicts-reconnect-20261024/unfiltered-qualification.log) records those unresolved scale results; the passing count above is the subsequent standard regression run, not a scale qualification.

## Configurable chunking checkpoint

Settings now provides labeled target, maximum and overlap controls. Bounds are validated against each other and the resolved embedding model's input limit. New captures and edits snapshot those settings, and admitted file work retains its original profile across later configuration changes. Existing indexed documents are rechunked in bounded batches while retaining their text, content hash, revision, metadata and occurrence time. Unrelated processing-generation fields are retained. A chunk-count failure leaves the readable projection intact and records a failed job; a changed profile can retry it.

Each changed chunk profile has a distinct embedding-space identity. The provider identity remains independently checked, stale embedding work cannot publish over the new generation, and a new space cannot activate until its live documents have the matching profile and complete vectors. The worker resumes incomplete publication after a crash, observes mutation reservations and queue limits, wakes on settings changes, and periodically reconciles late completions. Its catalog scan advances by document ID rather than restarting from the first row for every batch.

The follow-up also makes admission capacities and document time interpretation use current configuration after a settings save. API checks verify that lowering the file limit takes effect without restarting the daemon.

The final Debug suite passed **1,953 tests across eight projects**, with warnings as errors, plus six JavaScript transport checks. At 1280 px and 390 px, the browser changed chunk sizes for existing text without changing its revision, verified semantic search recovery, rejected invalid bounds and restored the default profile. The editor/PDF/CSP, file-conflict, reconnect, settings and basic browser workflows passed again. Nine direct loads passed and the revoked idle circuit closed in **1,910 ms**, preserving the independent session. [Browser evidence](evidence/m3-chunking-20261024/browser-checks.json), [verification and source hashes](evidence/m3-chunking-20261024/verification.json), and the mobile settings screenshot record this checkpoint. The fixture was stopped afterward. This remains local development evidence; no M3 deployment or origin acceptance is claimed.

## Verification

### Sources and jobs checkpoint

The sources screen now creates configured folder sources, shows document counts and persisted scan errors, runs shallow/deep scans, pauses/resumes sources, and confirms removal. It keeps the existing admin and step-up requirements. Removed sources remain listed with their state; their original files remain on disk. The built-in inbox cannot be removed from this screen.

The processing queue uses a protected, filter-bound cursor rather than stopping at the first 200 records. Job cards show document titles, source names and original filenames, plus stage, attempts and failure reason. Retry eligibility comes from the same policy that checks the write. A deterministic failure explains that the bytes or extractor generation must change. Retried jobs refresh until their outcome is visible. Catalog pages omit jobs for unavailable documents and never return document bodies or raw job payloads.

New bare and `/v1` routes are `GET /browse/jobs` and `GET /browse/source-counts`. Source counts require admin scope. Scan failures are stored as bounded source status metadata, cleared after a successful scan, and returned by the existing admin source list. The manual retry policy now includes transient extractor protocol failures, matching the automatic retry path.

The final Debug suite passed **1,934 tests across eight projects**. The source browser workflow passes at desktop and 390 px: create, scan, inspect two failed files, retry a three-attempt transient extractor outage through the real extractor, refuse retry for a malformed PDF, show counts, pause/resume and remove. Removal hides retained text and preserves both original source files byte-for-byte. [Browser evidence](evidence/m3-sources-20261024/browser-checks.json), [file preservation](evidence/m3-sources-20261024/originals-after-removal.json) and [test/source hashes](evidence/m3-sources-20261024/verification.json) are retained. This qualifies the U11 workflow locally; it does not close its required VPN/Cloudflare gate. The isolated browser fixture was stopped afterward.

The initial browser checkpoint passed **307 server tests** in Debug with warnings treated as errors, plus **six JavaScript transport tests** and the complete development browser sequence. [The initial verification record](evidence/m3-browser-checkpoint-20261024/verification.json) includes test-result hashes and the changed source manifest; [the browser result](evidence/m3-browser-checkpoint-20261024/browser-checks.json) states its scope and limitations. This is not the Release or full-milestone acceptance run.

The browser checks caught two defects after the initial implementation: a session-identity collection that could not be deserialized, and step-up navigation attempting to resume an already invalidated circuit. The final build uses a concrete browser identity contract and initiates navigation in the browser after successful session rotation or logout. Both fixes have regression tests. The final browser sequence verified successful step-up/reload and sign-out in addition to the search, editing, conflict and mobile scenarios.

The isolated Kestrel fixture is `KnowledgeBrowserQualification.BrowserWorkflows`. It creates temporary stores, an account with the synthetic password `test-password`, and synthetic note/article data. It does not use the installed daemon's account or documents. Set `SECONDBRAIN_M3_BROWSER_HARNESS=1`, `SECONDBRAIN_M3_BROWSER_OUTPUT` to a fresh directory, and `SECONDBRAIN_M3_BROWSER_PORT` to a free loopback port; run that test with the qualification filter, then create `stop` in the output directory to shut it down. Complete the build before starting a fixture, and stop the fixture before rebuilding. The framework's [static assets manifest](https://learn.microsoft.com/aspnet/core/fundamentals/static-files?view=aspnetcore-10.0) supplies routes and compressed representations; an overlapping build produced an empty compressed bootstrap response during development. The final host served the complete script with both identity and gzip encoding.

`tests/browser/m3-workflows.js` runs the search/view/edit/conflict/mobile-capture/settings sequence through Playwright's page API. Its current page must be on the fixture origin. It authenticates through the public API using only the fixture credentials. The native sign-in form was also exercised separately. The fixture's loopback origin is not evidence for the required VPN or Cloudflare acceptance.

`node tests/browser/browser-api.test.cjs` verifies fresh CSRF tokens, revision headers, same-origin credentials, redirect refusal, failure-before-mutation, path rejection, credential continuation links and browser navigation after session rotation. All six tests passed at the checkpoint.

## Final scope and evidence

| Requirement | Remaining implementation or acceptance |
| --- | --- |
| Search | Local Release checks pass for facets, filters, history, completion and viewer links. Remote-origin qualification was waived. |
| Document viewer | Links, history, tag origins, structured metadata and PDF page/passage fidelity pass locally. M4 transcript/event renderers remain unavailable. |
| Editor | Conflict-file outcomes, Monaco/frontmatter, completion, mode switching and draft-loss protection pass locally. |
| Timeline | Time-zone grouping, date/type/people filters and paging pass. M4 calendar coverage remains unavailable. |
| Sources/jobs | U11 passed in Release at both widths, including real extraction retry and original-file preservation. |
| Lifecycle | Trash/restore, suppression lifting and regression lineage checks pass. |
| Settings | Controlled chunking, providers/roles, limits/budgets, privacy and credentials pass. The user completed the production writable-configuration migration. |
| Session lifecycle | Transport rejoin, fresh-server recovery, retained sessions/conversations and prompt idle revocation pass locally; the deployed daemon also restarted successfully. |
| U6/U7 | Real local-model summaries pass at both widths, with verified exact-passage footnotes and current-work selection. |
| U16 / origins | Phone-width sign-in/workflows pass locally. VPN and Cloudflare checks were explicitly waived, not passed. |
| CSP | Local Release checks pass for inline scripts, remote images, hostile PDF text and hostile metadata. |
| Closure | 1,956 Release tests, 33 Python tests, six transport checks, all 484 installed-file hashes and 26 deployed HTTP checks pass. See [final acceptance](M3-acceptance.md). |

The early checkpoints remain development evidence. Final local Release and deployment checks are recorded in the acceptance ledger; the remote-origin portions were waived by the user.
