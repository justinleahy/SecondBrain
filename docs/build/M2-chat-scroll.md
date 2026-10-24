# Chat scroll following — 2026-10-24

Status: **deployed and verified** on the reference service at 20:17 UTC.

Sending a new message now scrolls the conversation to its latest turn. Opening an existing conversation starts at its latest message. While the reader stays at the bottom, streamed text, reply completion and viewport resizing keep the latest content in view. Scrolling up pauses following; returning to the bottom or sending another message resumes it. The approvals view is excluded.

The browser helper observes rendered turn identity, text and size changes locally, with updates batched into animation frames. It scrolls only the conversation pane. CSS scroll anchoring is disabled on that pane so browser anchoring does not compete with following. Existing keyboard submission and citation navigation remain in the same helper.

All **302 deterministic server tests** passed in Release, the build had zero warnings/errors, and `node --check` passed. The disposable browser fixture used a twelve-turn history. Browser checks verified initial positioning, sending from earlier history, preserving the reading position during content growth, following simulated streamed DOM updates, sending with Enter on mobile, viewport resizing, and excluding approvals. Tests used 1440×1000 and 390×844 viewports. The messages sent through the actual UI were deterministic `/recent` commands; streamed growth was simulated in the browser without another model call.

The managed deployment retained the installed CLI/extractor and overlaid the freshly published self-contained server. Nine server/asset files changed. Independent verification matched all **433 installed files** and passed **eight HTTP checks**, including loopback/LAN readiness and exact JavaScript/CSS response bytes. Account, credentials, configuration, secrets and key ring were preserved.

See the [verification record](evidence/chat-scroll-20261024/verification.json), [publish provenance](evidence/chat-scroll-20261024/provenance.json), [deployment result](evidence/chat-scroll-20261024/deployment-result.json), and [browser checks](evidence/chat-scroll-20261024/send-message.json). Test logs remain in `artifacts/chat-scroll-tests/` and `artifacts/chat-scroll-browser/`. The release bundle is `artifacts/chat-scroll-release-20261024/merged`. Refresh existing browser tabs to load the new helper.
