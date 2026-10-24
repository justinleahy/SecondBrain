# Browser chat deployment — 2026-10-24

Status: the tested release is ready; installation in the main systemd service is awaiting the operator's interactive sudo command.

The release was published from runtime source commit `6080b87d7c5ba4ee9f6dc06bacc3b573a6c95cc2`, using SDK 10.0.111, Release configuration, warnings as errors, and self-contained `linux-x64` output. The Server, CLI, and Extractor publishes are merged into `artifacts/m2-release-20261024/merged`. The [manifest](evidence/m2-release-20261024/sha256-manifest.json) covers all 427 files; the [provenance](evidence/m2-release-20261024/publish-provenance.json) records source hashes and merge precedence.

The implementation passed all 1,800 deterministic .NET tests and [live browser/provider validation](M2-browser-chat-validation.md). The deployment helper's 11 Python tests cover backup creation, bundle validation, authentication probes, account preservation, rollback, partial service-stop recovery, and the new chat schema/assets/HTTP gates.

Run on the reference host:

```sh
cd /home/justinleahy/Developer/SecondBrain
sudo python3 deploy/systemd/upgrade-m1.py \
  artifacts/m2-release-20261024/merged \
  artifacts/m2-release-20261024/sha256-manifest.json \
  --release chat --evidence-dir artifacts/m2-deployment-20261024
```

The upgrade takes a private backup, preserves configuration, credentials, key material, existing documents, and systemd drop-ins, and attempts rollback if verification fails. It verifies installed binary hashes, M1 and chat schema, provider/extractor readiness, the blocked egress canary, authenticated API boundaries, and the served chat page and static assets. Sanitized results go to `artifacts/m2-deployment-20261024/deployment-result.json`; private diagnostic logs stay under `/var/backups/secondbrain/chat-*`.

After a successful installation, reload **http://10.66.10.20:7171/** and sign in with the existing account. The same LAN policy continues to allow `10.66.10.0/24` and `10.66.20.0/24`. Ask “What is the project codename?” to check retrieval of the existing M1 smoke-test note, then expand its citation. Requests to save or change notes produce an approval card; approve only a desired change. Read-only mode answers without creating a proposal.

This deployment covers browser chat. It does not claim completion of the remaining paired CLI/MCP milestone.
