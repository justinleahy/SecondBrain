# Chat list and citation numbering

The browser previously closed a numbered list on every blank line, rendering each item as a new list beginning at 1. The renderer now keeps same-kind list items together across blank lines and still closes the list before a paragraph, heading, code block, or different list kind.

The citation registry numbers all retrieved sources, while an answer can cite only some of them. Displaying those original numbers produced gaps such as [1] and [3]. A presentation-only conversion now numbers cited sources consecutively and rewrites the matching in-text markers together. Stored answers, source identities, excerpts, verification hashes, and API responses retain their original registry references. Existing conversations receive the display correction when reopened.

Validation: all 252 server tests passed. A browser fixture used four numbered items separated by blank lines and an answer citing registry sources 1 and 3 out of three retrieved sources. It rendered one list numbered 1–4 and two source cards numbered [1] and [2], with matching answer markers. The persisted answer still cited registry sources 1 and 3. The browser console had no errors. Evidence is under `docs/build/evidence/m2-numbering-20261024/`; the screenshot is in `artifacts/m2-numbering-20261024/corrected-numbering.png`.

Deployed and verified on 2026-10-24 at 18:30 UTC. The [deployment report](evidence/m2-numbering-20261024/deployment-result.json) confirms preserved account/document state and healthy services. All 430 installed files match the release manifest; loopback and LAN readiness passed. Refresh and reopen an existing conversation to apply the display corrections.

## Clickable citations

Verified citation markers now link to source cards scoped to their own turn. Clicking or pressing Enter on a focused citation opens the excerpt, scrolls it into view, and focuses its summary. Reduced-motion preferences are respected. Code examples and provisional or unknown citation numbers remain plain text; model-provided links are still not rendered as active HTML.

The update passed all 257 server tests and browser checks for mouse navigation, keyboard activation, repeated source numbers in different turns, source expansion/focus, and a 390 px viewport. Evidence is in `docs/build/evidence/m2-citation-links-20261024/`. The update was deployed and verified on 2026-10-24 at 18:34 UTC, including the installed bundle, readiness, and the navigation script served over the LAN.
