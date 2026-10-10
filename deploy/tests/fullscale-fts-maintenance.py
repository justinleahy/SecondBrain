"""Run the isolated copied-database bounded FTS experiment and retain provenance."""
import hashlib
import json
import os
import pathlib
import subprocess

root = pathlib.Path('artifacts/m1-qualification/fullscale-100k').resolve()
copy = root/'lexical-copy-bounded-maintenance'
runtime = root/'fts-maintenance-production-runtime'
recordings = pathlib.Path('eval').resolve()
assert copy.is_dir() and runtime.is_dir()
copy.chmod(0o700); runtime.chmod(0o700)
result = subprocess.run([
    'docker', 'run', '--rm', '--user', f'{os.getuid()}:{os.getgid()}', '--read-only', '--network', 'none',
    '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges:true', '--memory', '512m', '--pids-limit', '128',
    '--tmpfs', '/tmp:rw,noexec,nosuid,size=128m,mode=1777',
    '--mount', f'type=bind,src={copy},dst=/data/lexical-copy-bounded-maintenance',
    '--mount', f'type=bind,src={runtime},dst=/qualification,readonly',
    '--mount', f'type=bind,src={recordings},dst=/recordings,readonly',
    '--entrypoint', 'dotnet', 'secondbrain:local', '/qualification/FullScaleSandboxHelper.dll',
    '--fts-maintenance', '/data/lexical-copy-bounded-maintenance/index.db', '/recordings/questions.jsonl',
], capture_output=True, text=True)
(root/'fts-bounded-maintenance-output.log').write_text(result.stdout+result.stderr)
assert result.returncode == 0, result.stderr
report = json.loads(result.stdout)
report['assembly_provenance'] = {
    'production_dependency_source': 'Packaged production runtime used by dirty registry check; unchanged bounded SQLite facade',
    'image_id': json.loads((root/'fullscale-dirty-registry-results.json').read_text())['image_id'],
    'helper_configuration': 'Release',
    'assembly_sha256': {name: hashlib.sha256((runtime/name).read_bytes()).hexdigest()
                       for name in ['FullScaleSandboxHelper.dll', 'SecondBrain.Storage.dll', 'SecondBrain.Core.dll']},
}
(root/'fts-bounded-maintenance-results.json').write_text(json.dumps(report, indent=2)+'\n')
print(json.dumps({key: value for key, value in report.items()
                  if key not in ['statements', 'fingerprints_before', 'fingerprints_after']}, indent=2))
