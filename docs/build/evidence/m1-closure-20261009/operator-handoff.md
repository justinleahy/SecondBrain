# Isolated systemd qualification handoff

Current self-contained `linux-x64` Release server, CLI and extractor binaries are merged in the directory below. `operator-publish-sha256.json` records all 416 files; the bundle has all three apphosts and web assets. The script creates unique disposable units and roots, uses numeric identities 1654/1655/1656 with shared group 1654, provisions the sticky inbox/private claims, and removes only its own fixtures on exit. It does not replace installed M0 units.

The fresh bundle was published after the final 1749 deterministic passes from
the same frozen runtime source recorded in `runtime-manifest.json`; all recorded
`src/` hashes matched at publication; subsequent blank-line-only M1Schema normalization is recorded in the runtime manifest. The host selected SDK 10.0.111 under the allowed
`global.json` patch roll-forward. The Docker build pins SDK 10.0.105, so this
establishes source correspondence rather than byte-identical image assemblies.
Locked RID restore, all three self-contained publishes, and ordinary locked
restore completed with all 19 `packages.lock.json` files unchanged. Exact
commands and validation are in `operator-publish.log`.

The previous bundle and its logs, manifest, handoff, and runtime source manifest
are preserved in
`artifacts/m1-qualification/operator-publish-preserved-20261010T032009Z`.
The current bundle's `publish-provenance.json` records the preserved location,
SDK, and package lock hashes. Neither publishing nor static bundle validation
executes the privileged systemd gate.

```sh
sudo deploy/tests/systemd-m1-qualification.sh /home/justinleahy/Developer/SecondBrain/artifacts/m1-qualification/operator-publish/merged /home/justinleahy/Developer/SecondBrain/deploy/tests/m1-workflow.py 17171 | tee artifacts/m1-qualification/systemd-operator.log
```

Run from the repository root. Root/operator authentication is required: the actual host rejects unattended `sudo -n true` because a password is required. This gate has not been run; a static shell check is not qualification evidence. Provider endpoints must remain reachable at 127.0.0.1:8000 and :8001. The fixture uses the canary-enabled shipped systemd profile, finite 8GiB daemon memory budget, and real rsync source writes. A passing run must include workflow output, service hardening fields, and clean fixture teardown. Never copy private `init.json`, authentication cookies, or temporary passwords into evidence.

The CLI informational version contains ba81f952877ccf5e3abd34a88ecff9e45100eeb5 because this bundle was published before the scoped M1 commit. Its 240 recorded runtime-source hashes, rather than that version stamp, identify the reviewed M1 content.
