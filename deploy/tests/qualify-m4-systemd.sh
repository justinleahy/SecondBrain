#!/usr/bin/env bash
# Reviewable operator entry point; touches only the existing qualifier's disposable units and roots.
set -euo pipefail
qualification_m4_repo=$(cd "$(dirname "$0")/../.." && pwd)
qualification_m4_artifacts="$qualification_m4_repo/artifacts/m4-release-recovery-final-20261024"
python3 -I - "$qualification_m4_artifacts" <<'PY'
import hashlib, json, pathlib, stat, sys
base = pathlib.Path(sys.argv[1]); bundle = base / 'merged'
assert bundle.is_dir() and bundle.resolve(strict=True) == bundle
expected = json.loads((base / 'sha256-manifest.json').read_text())
actual = {}
for p in bundle.rglob('*'):
    assert not p.is_symlink()
    assert p.is_dir() or stat.S_ISREG(p.stat().st_mode)
    if p.is_file():
        with p.open('rb') as stream: actual[str(p.relative_to(bundle))] = hashlib.file_digest(stream, 'sha256').hexdigest()
assert actual == expected and len(actual) == 465
assert all((bundle / n).is_file() for n in ['brain', 'SecondBrain.Server', 'SecondBrain.Extractor'])
print('M4 qualification bundle verified: 465 files.')
PY
if [ "${1:-}" = --verify-only ] && [ "$#" -eq 1 ]; then exit 0; fi
if [ "$#" -ne 0 ] || [ "$(id -u)" -ne 0 ]; then
    printf '%s\n' 'Run with sudo after review, or use --verify-only without sudo.' >&2
    exit 2
fi
export SECONDBRAIN_QUALIFICATION_CONFIG_TEMPLATE="$qualification_m4_repo/deploy/tests/config.m4-systemd.yaml"
export SECONDBRAIN_M4_SYSTEMD_EVIDENCE="$qualification_m4_repo/docs/build/evidence/m4-systemd-20261024/verification.json"
bash "$qualification_m4_repo/deploy/tests/systemd-m1-qualification.sh" \
    "$qualification_m4_artifacts/merged" "$qualification_m4_repo/deploy/tests/m4-systemd-workflow.py" 18346
