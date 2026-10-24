# M4 — Meetings, enrichment, operations

Status: planned; calendar subscription requirements added 2026-10-09. This document defines future work, not implemented functionality. The current product contract is [spec.md](../../spec.md), especially §5.7, CAL-1–CAL-7, §7.1, §15.6 and §19.

## 1. Outcome and scope

An administrator can paste a shared iCloud `webcal://` URL or HTTPS iCalendar feed into Sources, register it once, and query its events as the feed changes. SecondBrain periodically downloads snapshots, imports them through the same sandboxed calendar pipeline used for `.ics` files, and reports freshness separately from recurrence coverage. Subscriptions are read-only and need no Apple account credentials.

M4 retains its existing scope: transcript parsing and segments; calendar containers, recurrence and occurrence queries; people and role-qualified filters; reclassification and enrichment; remaining provider adapters; optional frontmatter write-back; budgets and usage; encrypted backup/restore/export; retention; supply-chain scanning and release signing. This plan details the calendar subscription addition and its dependencies. The remaining M4 work below is still required for milestone closure.

Authenticated CalDAV account discovery/sync, private sharing invitations, OAuth, app-specific passwords, remote event editing and a general URL clipper remain out of scope. M1 and M2 continue to reject unsupported calendar operations until the M4 contracts are implemented.

## 2. User flow and contracts

1. An operator opts in to exact HTTPS origins through `sources.calendar_allowed_origins` and provisions matching host firewall allowances. The default is no remote calendar access. This permission applies only to source downloads and does not grant model-provider trust, including under `local_only`.
2. An administrator chooses **Add calendar subscription**, enters a name and link, and optionally changes the hourly refresh interval (minimum five minutes). REST and CLI call the same source use case. Enabled registration persists a source and queues its initial refresh; the UI shows that no snapshot has succeeded yet.
3. The daemon normalizes `webcal` to HTTPS, validates the destination, downloads into private staging and passes only the staged descriptor to the sandbox. A URL without an `.ics` suffix is valid input. The sandbox validates the calendar and returns bounded event/recurrence data.
4. The publisher reconciles the complete accepted snapshot into the subscription's stable container. Event identity survives refreshes; missing members become absent; user suppression continues to win. Search and occurrence queries use the normal projections.
5. Sources shows last attempt, last successful check, last accepted content change, next attempt and sanitized failure state. **Refresh now**, **Pause** and **Resume** are available; removing a source stops polling and follows the existing source-removal contract. Pause preserves existing events and blocks both scheduled and manual fetches until resume.
6. Queries and the assistant distinguish stale subscription data from an incomplete recurrence coverage window. Failed refreshes retain the last accepted events and coverage, even if sharing is revoked remotely.

Planned REST shape, using a deliberately nonfunctional example:

```http
POST /v1/sources
Authorization: Bearer <admin credential>
Content-Type: application/json

{
  "kind": "calendar_subscription",
  "name": "Shared calendar",
  "url": "webcal://calendar.example.invalid/published/2/EXAMPLE",
  "poll_interval_s": 3600,
  "enabled": true
}
```

The URL is write-only in this API. Source responses expose name, origin, state and timestamps, never the full URL or protected envelope. `PATCH /v1/sources/{id}` changes name, interval or enabled state; changing the URL requires a new source. `POST /v1/sources/{id}/scan` schedules a refresh; `?deep=1` bypasses validators. Source administration stays `admin`-only, including browser antiforgery and the existing session requirements.

The planned CLI command is `brain sources add-calendar --name "Shared calendar"`, which prompts for a hidden URL, or accepts `--url-stdin`. It also supports `--poll-interval`, `sources scan`, `sources pause`, `sources resume` and existing removal. Do not encourage token-bearing URLs in command-line arguments or shell history. The UI and CLI must label registration as queued until the first import succeeds.

## 3. Work items in dependency order

### 1. Finish the shared calendar importer

Add the `calendar_event` type and `.ics` container extraction in the sandbox, including masters, exceptions, cancellations, time zones, floating and all-day times, bounded recurrence expansion and occurrence query records. Use the spec's per-container natural keys, absence/suppression distinction, coverage generation and daily advancement. File uploads and subscriptions must use the same parser and reconciliation path.

Keep fetching out of the parser. Ical.Net's [deserialization documentation](https://github.com/ical-org/ical.net/wiki/Deserialize-an-ics-file) supports loading calendar text independently of transport. Review and pin the selected package version during implementation, and use that version's bounded recurrence API; [migration guidance](https://github.com/ical-org/ical.net/wiki/Migrating-Guides) shows that recurrence APIs differ across versions. Both time and occurrence-count bounds must apply while enumerating, before collecting results.

**Accept:** existing §19 calendar gates pass for files: UID scoping, membership changes/reappearance, recurrence exceptions, day boundaries, coverage, cancellations and recurrence bombs. Malformed or incomplete snapshots cannot reconcile membership.

### 2. Persist subscription identity and authority

Add the source kind, validated settings, protected URL, one stable container identity, source generation, HTTP validators and durable polling state through a migration. Keep SQL in Storage, policies and ports in Core, and network/platform operations in Infrastructure. Add ports with their first consumers, following the [architecture plan](clean-architecture-plan.md).

Persist last attempt, last successful check, last accepted content change, next attempt, failure code and the last rejected content hash/processing generation. Use a dedicated Data Protection purpose for URL storage. Configured subscriptions accept secret references only; a resolved URL change must be rejected as an immutable source change rather than silently replacing the calendar. Restart, backup/restore and index rebuild retain the source/container identity. A rebuild can use the retained snapshot while remote fetching is unavailable.

Subscription jobs use the daemon's internal registered-source authority. A source generation fences refreshes and publication when the source is paused, removed or reconfigured. Downloaded content has no authority to register URLs, fetch attachments or change another source.

**Accept:** migration/restart tests preserve identity and protected settings; pause/remove racing publication cannot publish stale work; listings, export and job persistence contain no raw URL.

### 3. Implement the source-download transport

Normalize and validate links according to CAL-1. Require exact HTTPS origins in the configuration-only allowlist; do not reuse `privacy.trusted_services` or control-plane permissions. Apply host firewall provisioning for these origins in both systemd and Compose while retaining the egress canary boundary. This explicit permission can operate with local model processing under `local_only`.

Disable cookies, ambient proxies and automatic redirects. Check public unicast destinations, including IPv6 and mapped IPv4, and connect to the address actually validated. Revalidate current source/policy generations for each request and invalidate connections when permissions change. Redirects fail visibly; remote resource links and attachments inside calendar content are never fetched.

Stream the response to private staging with encoded/decoded byte and decompression limits and a 60-second total deadline. Issue conditional GETs using only validators from accepted snapshots. A `304` without an accepted snapshot gets one unconditional retry. Neither an unchanged `200` nor a valid `304` creates document revisions; coverage advancement remains independent. Bound headers and redact transport exception details that may include the request URI.

**Accept:** deterministic transport tests cover request construction, URL preservation, no-extension feeds, conditional responses, redirects, timeouts, oversized/chunked/compressed bodies, policy changes and prohibited destinations. Tests verify no request reaches a forbidden target.

### 4. Schedule and reconcile complete snapshots

Persist due times, coalesce manual and scheduled refreshes, use one active refresh per source, and honor queue/disk/work admission limits. Default polling is hourly with a five-minute minimum. Recover due work after restart without multiplying jobs. Back off transient failures with jitter, respect bounded Retry-After and avoid a tight loop after remote revocation.

Adopt new bytes and validators only after full sandbox parsing and daemon validation. Coordinate snapshot/container/member publication through the existing journal/coordinator; only a successful complete snapshot can mark members absent. Recovery must finish or retry the same accepted import without creating another container or duplicating members. Failed or interrupted imports leave the previous searchable state available.

Treat a valid empty VCALENDAR as an authoritative empty snapshot; treat empty HTTP bodies, `204`, partial/invalid responses and error statuses as failures. Remember deterministic parse failures by bytes and processing generation so repeated downloads of the same bad payload do not repeatedly consume parser resources. Preserve user overrides and suppressions during reconciliation. Staged files and superseded snapshots follow the existing retention and container-original lineage rules.

**Accept:** successive fixtures add, update, remove and restore members predictably; failure and crash injection preserve prior projections and validators; a later valid refresh recovers without duplicates.

### 5. Expose source controls and freshness

Extend the admin source endpoints, CLI and Sources UI with the flow in §2. Support paste entry and clear validation for `webcal` and HTTPS links, initial pending state, sanitized failures and refresh/pause/resume controls. Return source id and queued job state using the existing source/job conventions.

Implement never synced, current, stale, paused and failed states from persisted facts. A failed refresh or a successful check older than twice the poll interval makes the data stale. Query coverage adds freshness for the subscription sources relevant to the query, including sources with no matching records; it must not infer freshness only from returned events. Paused and never-synced sources cannot be represented as current. Propagate these facts to assistant/tool output without including URLs.

**Accept:** API and CLI tests enforce scopes, lifecycle and redaction; browser tests cover desktop and phone-width registration and stale-state recovery; occurrence queries distinguish stale input from incomplete materialization.

### 6. Qualify deployment, backup and the iCloud flow

Exercise approved source-download egress alongside local model processing in the hardened deployments. Verify forbidden origins remain blocked, the extractor has no network access and subscription origins cannot become provider destinations implicitly. Include protected subscription settings, polling state and retained snapshots in encrypted backup/restore; ordinary exports omit bearer URLs.

Run deterministic CI against synthetic ICS and a controlled transport. Use a private live iCloud subscription only for explicit qualification, keeping its URL and event bodies out of committed fixtures, reports and logs. Record transport/import success and sanitized counts, not the token. The example supplied with the requirement is a shape reference; documenting the feature does not subscribe to it.

**Accept:** one public iCloud calendar and one generic HTTPS feed pass live qualification before release, with sanitized evidence. Live service availability does not gate ordinary CI. Apple's [public-calendar sharing documentation](https://support.apple.com/guide/icloud/share-a-calendar-mm6b1a9479/icloud) is the reference for the intended iCloud flow.

## 4. Calendar subscription acceptance gates

These are planned tests, not evidence of tests already executed.

| Gate | Contract | Required evidence |
|---|---|---|
| M4-CAL-01 | CAL-1, U14a | A synthetic iCloud-shaped `webcal://host/published/2/token` becomes HTTPS with path/query intact; a generic HTTPS feed without `.ics` imports. Invalid schemes, userinfo and fragments fail before a request. |
| M4-CAL-02 | CAL-2, CAL-6 | Only admins can register/control a subscription; source responses and CLI JSON never disclose the URL. Hidden-prompt and stdin input reach the same use case. |
| M4-CAL-03 | CAL-3 | Initial `200`, unchanged `200`, changed `200` and conditional `304` yield correct revisions and timestamps; failed parses never replace accepted validators; `304` without a snapshot cannot claim success. |
| M4-CAL-04 | CAL-4 | Multiple refreshes keep one container and stable member ids; changed/absent/reappearing members reconcile correctly; user suppression survives; separate sources sharing a UID stay separate. |
| M4-CAL-05 | CAL-4 | Timeout, truncated ICS, HTML/login response, `204`, revoked/unauthorized link, `429` and `5xx` preserve the prior snapshot and events; only a valid complete empty calendar marks every member absent. |
| M4-CAL-06 | CAL-3, CAL-5 | Restart and simultaneous manual/scheduled triggers do not duplicate work; backoff/Retry-After are bounded; identical rejected bytes are not reparsed until bytes or generation change. |
| M4-CAL-07 | CAL-4 | Pause/remove during download or publication fences pending changes; injected crashes recover the same container/membership exactly once; removing never writes to the remote server. |
| M4-CAL-08 | §5.7 | Recurrence, moved/cancelled exceptions, all-day/floating times and DST boundaries match file-import behavior; daily materialization advances despite unchanged feed bytes. |
| M4-CAL-09 | CAL-5 | Sources and query/assistant output distinguish never synced, stale/failed, paused and current input from partial coverage; a successful retry clears stale failure state. |
| M4-CAL-10 | CAL-6 | Raw path/query tokens are absent from logs, errors, telemetry, source listings, job payloads, exported metadata and model context; encrypted restore preserves a working subscription. |
| M4-CAL-11 | CAL-7 | Unlisted origins, redirects, non-public IPs, mapped IPv6 bypasses and DNS rebinding cannot reach a forbidden target; changed permissions apply before later requests. |
| M4-CAL-12 | CAL-3, CAL-7 | Oversized/compressed responses and recurrence bombs stop within bounds; local-only models continue to use provider policy while source downloads use only their explicit allowances; the sandbox remains offline. |
| M4-CAL-13 | U14a | Desktop and mobile source workflows pass automated tests; private iCloud and generic HTTPS live qualification records contain sanitized results only. |

## 5. Remaining M4 work and closure

The calendar gates supplement, rather than replace, the existing milestone requirements:

- **Transcripts and people:** `.vtt`/`.srt` extraction, speaker/time segments, citation locators and role-qualified filters satisfy U3 and the existing transcript contracts.
- **Enrichment and providers:** preserve `props_original` on reclassification; implement optional enrichment/batch and remaining adapters with their contract tests; frontmatter write-back follows approved settings and the journal. Preserve U15 and all existing §19 gates.
- **Operations:** implement budgets/usage attribution, streaming encrypted backup and manifest-verified restore under sustained writes, exports, retention, dependency/SBOM scanning and signed releases.

Before M4 closes, run the repository's locked restore, warnings-as-errors build, deterministic test suite and relevant hardened deployment/browser gates. Record separate real-export and live-feed qualification evidence.
