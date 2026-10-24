# Browser chat deployment — 2026-10-24

This historical browser-only release was superseded by the [complete M2 deployment](M2-complete-deployment.md) at 19:57 UTC.

The original deployment's status-only asset checks missed empty JavaScript responses, leaving the rendered page inactive. This was [corrected and redeployed at 18:14 UTC](M2-static-assets-fix.md), with byte-for-byte asset verification and a confirmed browser WebSocket connection. The original deployment evidence below is historical.

Status: **deployed and verified** at 2026-10-24 18:07 UTC. The [upgrade report](evidence/m2-deployment-20261024/deployment-result.json) confirms all 427 files, migrations, provider/extractor readiness, blocked egress canary, authenticated chat routes, and served page/assets. Existing account credentials, configuration, key material, and the M1 smoke-test document were preserved.

[Independent verification](evidence/m2-deployment-20261024/independent-verification.json) checked the installed files against the release manifest and passed 14 HTTP checks over loopback and `10.66.10.20:7171`. The [production browser page](evidence/m2-deployment-20261024/browser-page.json) loaded the chat sign-in screen, both stylesheets, Blazor script, and icon, with [zero console errors](evidence/m2-deployment-20261024/browser-errors.txt). These LAN requests originated on the host; a separate LAN client was not retested during this upgrade. Authenticated chat, retrieval, and approvals were exercised in the live-provider browser qualification described below.

The release was published from runtime source commit `6080b87d7c5ba4ee9f6dc06bacc3b573a6c95cc2`, using SDK 10.0.111, Release configuration, warnings as errors, and self-contained `linux-x64` output. The Server, CLI, and Extractor publishes are merged into `artifacts/m2-release-20261024/merged`. The [manifest](evidence/m2-release-20261024/sha256-manifest.json) covers all 427 files; the [provenance](evidence/m2-release-20261024/publish-provenance.json) records source hashes and merge precedence.

The implementation passed all 1,800 deterministic .NET tests and [live browser/provider validation](M2-browser-chat-validation.md). The deployment helper's 11 Python tests cover backup creation, bundle validation, authentication probes, account preservation, rollback, partial service-stop recovery, and the new chat schema/assets/HTTP gates.

The operator ran this command on the reference host:

```sh
cd /home/justinleahy/Developer/SecondBrain
sudo python3 deploy/systemd/upgrade-m1.py \
  artifacts/m2-release-20261024/merged \
  artifacts/m2-release-20261024/sha256-manifest.json \
  --release chat --evidence-dir artifacts/m2-deployment-20261024
```

The upgrade takes a private backup, preserves configuration, credentials, key material, existing documents, and systemd drop-ins, and attempts rollback if verification fails. It verifies installed binary hashes, M1 and chat schema, provider/extractor readiness, the blocked egress canary, authenticated API boundaries, and the served chat page and static assets. Sanitized results go to `artifacts/m2-deployment-20261024/deployment-result.json`; private diagnostic logs stay under `/var/backups/secondbrain/chat-*`.

Reload **http://10.66.10.20:7171/** and sign in with the existing account. The same LAN policy continues to allow `10.66.10.0/24` and `10.66.20.0/24`. Ask “What is the project codename?” to check retrieval of the existing M1 smoke-test note, then expand its citation. Requests to save or change notes produce an approval card; approve only a desired change. Read-only mode answers without creating a proposal.

## Browser goal completion evidence

| Requirement | Evidence |
| --- | --- |
| Usable browser chat and assistant backend | Conversation list, streaming, replay, read-only mode, and mobile layout passed the live browser qualification; the installed production chat page renders and all release files match. |
| Retrieval-grounded answers and verified citations | Live answers matched the seeded source facts; source cards showed exact excerpts/revisions and the exposure manifest. Citation hash/registry checks and purge lineage have deterministic coverage. |
| Authorization and approval controls | Scope, ownership, revocation, antiforgery, and API-key approval rejection tests passed. Browser approval/edit/discard checks verified no file before approval and exactly one saved file afterward. Production chat reads reject unauthenticated requests. |
| End-to-end validation and installation | Live local Qwen chat/embedding qualification, 1,800 deterministic tests, 11 upgrade safeguard tests, successful systemd upgrade, and independent installed-file/HTTP/browser verification. |

This deployment covers browser chat. It does not claim completion of the remaining paired CLI/MCP milestone.
