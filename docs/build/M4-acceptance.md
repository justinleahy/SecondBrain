# M4 acceptance and production deployment

Accepted and deployed **2026-10-25** for the user-agreed scope. M4 delivers transcript and calendar ingestion, shared calendar subscriptions, enrichment and provider operations, budgets, retention, encrypted recovery/export, and signed release gates. [Implementation plan and closure matrix](M4-meetings-enrichment-operations.md), [historical progress](M4-progress.md).

The production release is **v0.4.0-rc.6**, commit `deb2f9fbafed27b2a001d4028c78c3bc07c695d7`. It remains a draft GitHub release, as authorized. Production promotion uses the verified signed candidate; this acceptance does not describe a published final release.

## Acceptance evidence

| Requirement | Result and evidence |
|---|---|
| Real transcript, speakers and timestamps | Approved VTT import, repeated identity, speaker-filtered search and fresh local-model answer passed. Agent review compared the private answer and captured timing citation. [Review and limits](evidence/m4-real-transcript-20261024/semantic-review.json). |
| Calendar files and shared feeds | Real iCloud and Outlook imports, complete-container identity, conditional refresh, recurrence/exception/cancellation, coverage and desktop/phone controls passed for the recorded scope. [Live feeds](evidence/m4-subscription-live-20261024/verification.json), [certificate-verified hardened HTTPS and recovery](evidence/m4-clean-base-20261024/final-calendar-https.json). |
| U14 real meeting-date answer | Fresh local Qwen answer lists the first three meetings chronologically with correct start times, three current citations, coverage and an explicit partial-list notice. The underlying week contained 35 meetings. [Semantic review and retained attempts](evidence/m4-real-calendar-chat-20261025/semantic-review.json). |
| Enrichment, people, adapters, write-back and usage | Deterministic and recorded local-model/browser checks passed. Hosted vendor behavior is qualified with controlled adapter contracts. [Progress and evidence links](M4-progress.md). |
| Encrypted backup during concurrent writes | Manifest-verified offline restore, captured revision, trash, remapped external roots, rebuilt keyword search, epoch advancement and rejection of old sessions passed on disposable Compose and systemd deployments. [Mounted restore](evidence/m4-clean-base-20261024/final-recovery-mounted.json), [systemd](evidence/m4-systemd-20261024/verification.json). |
| Large-corpus recovery and memory | The 100,006-file encrypted archive was restored in 2,818.49 seconds after 137 concurrent edits during backup. Authored-state comparison passed. A separate current-image startup loaded one million vectors within the 8-GiB limit and non-vector heap allowance, with zero restarts. [Recovery](evidence/m4-backup-scale-20261024/large-corpus-result.json), [million-vector startup](evidence/m4-backup-scale-20261024/final-million-vector-startup.json). |
| Isolation and egress | Numeric-UID systemd and hardened Compose workflows passed. The actual kernel fixture passed 18 IPv4/IPv6 boundary checks; the extractor remained offline. [Systemd](evidence/m4-systemd-20261024/verification.json), [Compose audit](evidence/m4-clean-base-20261024/final-hardened-audit.json), [kernel boundaries](evidence/m4-clean-base-20261024/final-ipv4-ipv6-kernel.json). |
| Recovery-key custody | The operator prepared the production key and confirmed storing the recovery copy off-server. Only local file metadata was inspected; remote custody is operator-attested. [Custody evidence](evidence/m4-key-custody-20261024/verification.json). |
| Final release and production | All remote jobs, strict scans, keyless signing, local signature/checksum verification and production verification passed. [Release evidence](evidence/m4-release-20261025/verification.json), [production evidence](evidence/m4-production-20261025/verification.json). |

## Release and production verification

The final [GitHub Actions run](https://github.com/justinleahy/SecondBrain/actions/runs/40165478709) passed the complete 2,555-test deterministic solution, locked dependency audits, warning-free Release build, inventory safeguards, editor dependency audit, parser mutations, sustained-write recovery, distinct-user staging, three application package scans, container scans, extractor/firewall isolation and hardened Compose meeting/recovery workflows. No vulnerability exceptions or lowered thresholds were used. The 41 local deployment safeguards and nine release-inventory safeguards are recorded in the progress ledger.

The downloaded checksum manifest was verified with Cosign against the exact repository workflow, `refs/tags/v0.4.0-rc.6`, GitHub OIDC issuer and source commit, with transparency-log verification enabled. Outer and package checksums and every package inventory matched. The 465-file deployment merge retains the CLI/server platform runtime assemblies and the CLI static-assets manifest; the latter differs from the server package only in Last-Modified timestamps. No shipped binary was rebuilt after signature verification.

The existing frozen deployment service installed this bundle with a root-private rollback snapshot. Its report verifies all existing account/credential records, configuration, secrets, key ring and four document identities were preserved. Read-only post-deployment checks matched every installed file, checked loopback/LAN health and readiness, compared served assets, verified new maintenance/usage authentication boundaries and retained systemd isolation. The existing Codex credential retrieved the same three readable document texts and revisions. The signed-out LAN browser was checked separately.

The complete encrypted pre-upgrade backup and schema-8 transition are inferred from the verified M4 binary becoming ready on the initialized M3 account: the mandatory transition fails closed without that backup. The root-private archive name, bytes and recovery key were not independently read. Production recovery itself was not destructively exercised; restore qualification used disposable stores.

## Agreed exceptions and practical limits

- The user explicitly waived U15's pricing-specific real-transcript example because no suitable export was available. The broader real speaker/topic and timestamped-citation check passed; the pricing example is waived, not passed.
- M3's user-waived VPN and Cloudflare qualification remains a separate follow-up. M4 deployment verification covers the reference host's loopback and LAN routes.
- Real-answer review was performed by the agent on bounded local-model examples, not by a human and not as a universal quality guarantee. The transcript review does not prove an exclusivity claim. The calendar answer repeats canceled wording from a title beside its confirmed structured status; this does not prove cancellation or attendance. Dates are validated against structured occurrences while passages identify the corresponding event record.
- The large offline restore rebuilds text first. Derived semantic vectors repopulate asynchronously; the separate million-vector startup is not a claim that the restored archive contained its disposable vector index. Historical superseded failures remain in the progress ledger and evidence.
- Real calendar URLs, transcript bodies, event details, model answers and recovery-key bytes are absent from committed evidence. The approved feeds were exercised in disposable stores; deployment did not add them to production.

## Next milestone

[M5 — Private email import and source privacy](M5-private-email-and-source-privacy.md) is the researched next scope: local EML/MBOX exports, message/thread/attachment identity, recoverable containers, inherited `no_cloud` policy, a separate local embedding space, and 500,000-message qualification. Its implementation goal is separate from M4 closure. M5 production promotion requires a later operator decision after its acceptance gates.
