"""Qualify M4 migration and encrypted backup/restore on a copy of the 100k synthetic corpus."""
import argparse
import hashlib
import http.cookiejar
import json
import os
import pathlib
import subprocess
import threading
import time
import urllib.error
import urllib.request


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', required=True, type=pathlib.Path)
    parser.add_argument('--root', required=True, type=pathlib.Path)
    parser.add_argument('--project', default='secondbrain-m4-scale-qualification')
    parser.add_argument('--port', type=int, default=18342)
    parser.add_argument('--resume', action='store_true')
    parser.add_argument('--startup-only', action='store_true', help='After the restore run has stopped, qualify the current image against its retained corpus.')
    parser.add_argument('--restore-only', action='store_true', help='Restore the retained completed archive without creating another one.')
    parser.add_argument('--verify-restored-only', action='store_true', help='Verify an already installed archive against its retained previous state, without restoring again.')
    args = parser.parse_args()
    repo = pathlib.Path(__file__).resolve().parents[2]
    source = args.source.resolve(strict=True)
    root = args.root.resolve()
    if 'm1-qualification' not in str(source) or 'm4-qualification' not in str(root) or source == root:
        raise ValueError('Use the retained M1 synthetic fixture and a separate M4 qualification directory.')
    if not args.project.startswith('secondbrain-m4-') or not 18000 <= args.port < 19000:
        raise ValueError('Use an isolated M4 project and loopback port.')
    if root.exists() and not args.resume:
        raise ValueError('The destination already exists; review it before explicitly resuming.')
    if (args.startup_only or args.restore_only or args.verify_restored_only) and not args.resume:
        raise ValueError('Partial qualification requires an explicitly retained corpus.')
    if sum([args.startup_only, args.restore_only, args.verify_restored_only]) > 1:
        raise ValueError('Select only one partial qualification.')
    previous_restore = None
    if args.verify_restored_only:
        previous_restore = json.loads((root / 'restore-qualification.json').read_text())
        if 'restoreInstallation' not in previous_restore['phases']:
            raise ValueError('The previous offline CLI must finish installing before verification.')
    root.mkdir(parents=True, exist_ok=True, mode=0o700)
    origin = 'http://127.0.0.1:' + str(args.port)
    env = dict(os.environ, SECONDBRAIN_HOST_ROOT=str(root), SECONDBRAIN_PRIVATE_ADDRESS='127.0.0.1',
        SECONDBRAIN_PORT=str(args.port), SECONDBRAIN_DOCKER_SUBNET='172.31.243.0/24',
        SECONDBRAIN_DOCKER_ADDRESS='172.31.243.2', SECONDBRAIN_PROVIDER_ALLOWLIST='172.29.0.1:11881',
        SECONDBRAIN_ACCESS_ALLOWLIST='', SECONDBRAIN_FULLSCALE_HOOK_DIRECTORY=str(repo / 'deploy/tests/fullscale-heap-hook/bin/Release/net10.0'))
    compose = ['docker', 'compose', '--project-name', args.project, '-f', 'deploy/compose.yaml',
        '-f', 'deploy/tests/compose-smoke.yaml', '-f', 'deploy/tests/m4-compose.yaml', '-f', str(root / 'overlay.json')]
    provider = None
    sampling = threading.Event()
    samples = []
    report = {'status': 'running', 'syntheticData': True, 'productionChanged': False,
              'sourceMountedReadOnly': True, 'project': args.project, 'phases': {}}
    output_prefix = 'verify-' if args.verify_restored_only else 'startup-' if args.startup_only else 'restore-' if args.restore_only else ''
    report_path = root / (output_prefix + 'qualification.json')

    def checkpoint(phase, value):
        report['phases'][phase] = value
        report_path.write_text(json.dumps(report, indent=2) + '\n')
        print(json.dumps({'phase': phase, 'result': value}), flush=True)

    def run(arguments, data=None, timeout=180, check=True):
        result = subprocess.run(arguments, cwd=repo, env=env, input=data, capture_output=True, timeout=timeout)
        if check and result.returncode:
            raise RuntimeError('Fixture command failed: ' + result.stderr.decode(errors='replace')[-1500:])
        return result

    def provision(script, data=None):
        return run(compose + ['--profile', 'provision', 'run', '--rm', '--no-deps', '-T',
            '--entrypoint', 'sh', 'provision', '-eu', '-c', script], data)

    def cli(*arguments, timeout=3600):
        result = run(compose + ['--profile', 'provision', 'run', '--rm', '--no-deps', '-T', '--user', '1654:1654',
            '--entrypoint', 'dotnet', 'provision', '/app/cli/brain.dll', *arguments, '--json'], timeout=timeout, check=False)
        value = json.loads(result.stdout)
        if result.returncode or value.get('exitCode') != 0:
            raise RuntimeError('Fixture CLI failed: ' + str(value.get('code')) + ': ' + str(value.get('message')))
        return value.get('data')

    client = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
    csrf = None

    def request(method, path, body=None, headers=None, timeout=1800):
        supplied = {'Origin': origin, **(headers or {})}
        if csrf and method != 'GET': supplied['X-CSRF-TOKEN'] = csrf
        raw = None
        if body is not None: raw = json.dumps(body).encode(); supplied['Content-Type'] = 'application/json'
        with client.open(urllib.request.Request(origin + path, raw, supplied, method=method), timeout=timeout) as response:
            return json.load(response)

    def login():
        nonlocal csrf
        password = (root / '.qualification-password').read_text()
        for path in ['/auth/login', '/auth/step-up']:
            csrf = request('GET', '/auth/antiforgery')['token']
            request('POST', path, {'password': password})
        csrf = request('GET', '/auth/antiforgery')['token']

    def ready():
        try: return request('GET', '/ready', timeout=3)['ready']
        except (urllib.error.URLError, ConnectionError, TimeoutError): return False

    def wait_ready():
        start = time.monotonic()
        while time.monotonic() - start < 1800:
            if ready(): return time.monotonic() - start
            time.sleep(1)
        raise TimeoutError('Scale fixture did not become ready within 30 minutes.')

    def read_stats(exclude=None, previous_state=None):
        program = '''import sqlite3,json,hashlib,os
r="/fixture/data/"
s=sqlite3.connect("file:"+r+"state/state.db?mode=ro",uri=True)
i=sqlite3.connect("file:"+r+"index/index.db?mode=ro",uri=True)
h=hashlib.sha256()
for row in s.execute("SELECT id,revision,content_hash,status FROM documents WHERE id != ? ORDER BY id",(os.environ.get("EXCLUDE_ID",""),)):
 h.update(json.dumps(row,separators=(",",":")).encode());h.update(b"\\n")
def authored_hash(db):
 digest=hashlib.sha256()
 fields=[row[1] for row in db.execute("PRAGMA table_info(documents)") if row[1] not in {"status","publish_state","indexed_revision","error"}]
 columns=",".join('"'+field.replace('"','""')+'"' for field in fields)
 for row in db.execute("SELECT "+columns+" FROM documents WHERE id != ? ORDER BY id",(os.environ.get("EXCLUDE_ID",""),)):
  digest.update(json.dumps(row,separators=(",",":")).encode());digest.update(b"\\n")
 return digest.hexdigest()
result={"documents":s.execute("SELECT COUNT(*) FROM documents").fetchone()[0],"chunks":i.execute("SELECT COUNT(*) FROM chunks").fetchone()[0],"vectors":i.execute("SELECT COUNT(*) FROM chunk_vectors").fetchone()[0],"documentStateHash":h.hexdigest(),"authoredDocumentHash":authored_hash(s),"statusCounts":dict(s.execute("SELECT status,COUNT(*) FROM documents GROUP BY status")),"migrations":s.execute("SELECT version,phase,backup_name FROM app_migrations ORDER BY version").fetchall(),"scaleEdit":s.execute("SELECT id,revision FROM documents WHERE title='M4 scale editable' ORDER BY id DESC LIMIT 1").fetchone(),"scaleTrash":s.execute("SELECT id,revision FROM documents WHERE title='M4 scale trash' ORDER BY id DESC LIMIT 1").fetchone()}
if os.environ.get("PREVIOUS_STATE"):
 old=sqlite3.connect("file:/fixture/data/"+os.environ["PREVIOUS_STATE"]+"?mode=ro",uri=True)
 result["previousAuthoredDocumentHash"]=authored_hash(old)
 result["accountEpochAdvanced"]=int(s.execute("SELECT value FROM meta WHERE key='account_epoch'").fetchone()[0])>int(old.execute("SELECT value FROM meta WHERE key='account_epoch'").fetchone()[0])
 prior={row[0] for row in old.execute("SELECT id FROM credentials")}
 result["priorCredentialsRevoked"]=all(row[1] is not None for row in s.execute("SELECT id,revoked_at FROM credentials") if row[0] in prior)
print(json.dumps(result))
'''
        result = run(['docker', 'run', '--rm', '--network', 'none', '--read-only', '-e', 'EXCLUDE_ID=' + (exclude or ''),
            '-e', 'PREVIOUS_STATE=' + (previous_state or ''),
            '--mount', 'type=bind,source=' + str(root) + ',target=/fixture,readonly',
            'python@sha256:a3799099b64fd333f262768c7a64733f6e4edb7bc569813710a100214bdcf278', 'python', '-c', program])
        return json.loads(result.stdout)

    def memory_sample():
        while not sampling.wait(2):
            row = {'at': time.time()}
            for service in ['daemon', 'extractor']:
                result = run(compose + ['exec', '-T', service, 'sh', '-c',
                    'cat /sys/fs/cgroup/memory.current /sys/fs/cgroup/memory.peak /sys/fs/cgroup/memory.max'], check=False)
                if result.returncode == 0:
                    current, peak, limit = result.stdout.split()
                    row[service] = {'current': int(current), 'peak': int(peak), 'limit': int(limit)}
            samples.append(row)

    try:
        # No running daemon may write the source while its state/index/vault are copied.
        running = run(['docker', 'ps', '-q']).stdout.decode().split()
        inspected = json.loads(run(['docker', 'inspect', *running]).stdout) if running else []
        assert not any(m.get('RW') and m.get('Source') == str(source / 'data') for c in inspected for m in c['Mounts'])
        if args.verify_restored_only:
            assert not any(m.get('RW') and m.get('Source') == str(root / 'data') for c in inspected for m in c['Mounts']), 'Wait for the original restore runner to stop.'
        if not (root / 'data').exists():
            clone = '''cp -a --reflink=auto /source/data /target/data
if test -d /target/data/qualification-heap; then mv /target/data/qualification-heap /target/previous-qualification-heap; fi
mkdir -p /target/incoming /target/config/settings /target/config/secrets /target/project/recovery
cp /source/config/config.yaml /target/config/settings/config.yaml
cp /source/.qualification-password /target/.qualification-password
chown 1654:1654 /target/project /target/project/recovery /target/config/settings /target/config/secrets /target/config/settings/config.yaml
chmod 700 /target/project /target/project/recovery /target/config/settings /target/config/secrets
chmod 600 /target/config/settings/config.yaml /target/.qualification-password
chown ''' + str(os.getuid()) + ':' + str(os.getgid()) + ''' /target/.qualification-password
'''
            start = time.monotonic()
            run(['docker', 'run', '--rm', '--network', 'none', '--read-only', '--user', '0:0',
                '--cap-drop', 'ALL', '--cap-add', 'CHOWN', '--cap-add', 'DAC_OVERRIDE', '--cap-add', 'FOWNER',
                '--security-opt', 'no-new-privileges:true', '--mount',
                'type=bind,source=' + str(source) + ',target=/source,readonly', '--mount',
                'type=bind,source=' + str(root) + ',target=/target', '--entrypoint', 'sh', 'secondbrain:m4', '-eu', '-c', clone], timeout=900)
            checkpoint('clone', {'seconds': time.monotonic() - start, 'sourcePreserved': True})
        (root / 'resolv.conf').write_text('nameserver 10.8.0.1\noptions timeout:1 attempts:1\n')
        (root / 'overlay.json').write_text(json.dumps({'services': {'daemon': {
            'environment': {'DOTNET_STARTUP_HOOKS': '/qualification/FullScaleHeapHook.dll',
                'SECONDBRAIN_FULLSCALE_HEAP_DIRECTORY': '/srv/secondbrain/qualification-heap'},
            'volumes': [{'type': 'bind', 'source': env['SECONDBRAIN_FULLSCALE_HOOK_DIRECTORY'], 'target': '/qualification', 'read_only': True}]}}}))
        run(compose + ['--profile', 'provision', 'run', '--rm', 'provision'], timeout=300)
        # The historical bulk-data generator used mkdir's 0755 default for the vault.
        # Normalize only this copy before exercising M4's strict backup preflight.
        provision('mkdir -p /srv/secondbrain/trash; chown 1654:1654 /srv/secondbrain/trash; '
                  'chmod 700 /srv/secondbrain/vault /srv/secondbrain/trash /srv/secondbrain/keyring')
        checkpoint('fixturePreparation', {'copiedBackupRootsPrivate': True, 'sourcePermissionsUnchanged': True})
        provision('if test -d /srv/secondbrain/qualification-heap; then '
                  'mv /srv/secondbrain/qualification-heap /provision/project/heap-before-' + str(time.time_ns()) + '; fi')
        config = provision('cat /etc/secondbrain/settings/config.yaml').stdout.decode()
        config = config.replace('bind: 172.29.0.2', 'bind: 172.31.243.2').replace('http://127.0.0.1:17173', origin)
        if '\nbackup:' not in config: config += '\nbackup:\n  key_file: /etc/secondbrain/secrets/backup.key\n  keep: 5\n'
        provision('cat > /etc/secondbrain/settings/config.yaml; chown 1654:1654 /etc/secondbrain/settings/config.yaml; chmod 600 /etc/secondbrain/settings/config.yaml', config.encode())
        cli('maintenance', 'backup-key', '--recovery-copy', '/provision/project/recovery/backup.key')
        provider_log = (root / (output_prefix + 'provider.log')).open('w')
        provider = subprocess.Popen(['python3', 'deploy/tests/fullscale-recorded-provider.py', '--metrics',
            str(root / (output_prefix + 'provider-metrics.jsonl'))], cwd=repo, env=env, stdout=provider_log, stderr=subprocess.STDOUT)
        time.sleep(.5)
        if provider.poll() is not None: raise RuntimeError('The isolated recorded-vector provider could not start.')
        sampler = threading.Thread(target=memory_sample, daemon=True); sampler.start()
        start = time.monotonic()
        run(compose + ['up', '-d', 'egress', 'extractor', 'mock-provider', 'daemon'], timeout=180)
        wait_ready()
        initial = read_stats()
        assert initial['documents'] >= 100000 and initial['chunks'] >= 1000000
        if not args.verify_restored_only: assert initial['vectors'] >= 1000000
        assert any(version == 8 and phase == 'applied' and archive for version, phase, archive in initial['migrations'])
        checkpoint('startup' if args.startup_only else 'migration', {'secondsThroughReadinessAndCountVerification': time.monotonic() - start, **initial})
        if args.startup_only:
            run(compose + ['exec', '-T', 'daemon', 'touch', '/srv/secondbrain/qualification-heap/request-full-gc'])
            time.sleep(3)
            heap = [json.loads(line) for line in provision('cat /srv/secondbrain/qualification-heap/heap.jsonl').stdout.splitlines()]
            forced = [row for row in heap if row['forced_full_gc']][-1]
            assert forced['vector_statistics']['LiveSlots'] >= 1000000
            assert forced['gc_heap_size_bytes'] < 1_000_000_000 + forced['vector_statistics']['VectorBytes']
            runtime = json.loads(run(['docker', 'inspect', args.project + '-daemon-1']).stdout)[0]
            assert runtime['RestartCount'] == 0 and not runtime['State']['OOMKilled']
            checkpoint('memory', {'postStartupForcedGc': forced, 'daemonImageId': runtime['Image'],
                'daemonMemoryLimit': runtime['HostConfig']['Memory'], 'daemonRestarts': runtime['RestartCount']})
            report['status'] = 'passed'
            return
        login()
        if args.verify_restored_only:
            installed = previous_restore['phases']['restoreInstallation']['cli']
            record_path = pathlib.PurePosixPath(installed['recoveryRecord'])
            record_id = record_path.name.removeprefix('.restore-complete-').removesuffix('.json')
            assert record_path.parent == pathlib.PurePosixPath('/srv/secondbrain')
            assert len(record_id) == 32 and all(c in '0123456789abcdef' for c in record_id)
            recovery_record = json.loads(provision('cat ' + str(record_path)).stdout)
            assert recovery_record['Phase'] == 'complete'
            old_state = next(item['Previous'] for item in recovery_record['Roots'] if item['Name'] == 'state')
            assert old_state.startswith('/srv/secondbrain/.sb-restore-') and old_state.endswith('-0.old')
            relative_state = old_state.removeprefix('/srv/secondbrain/') + '/state.db'
            assert '/' not in relative_state.removesuffix('/state.db')
            baseline = previous_restore['phases']['restoreBaseline']
            note_id = baseline['editableDocumentId']
            after = read_stats(note_id, relative_state)
            assert after['authoredDocumentHash'] == after['previousAuthoredDocumentHash']
            assert after['accountEpochAdvanced'] and after['priorCredentialsRevoked']
            recovered = request('GET', '/v1/documents/' + note_id + '?include=text')
            expected = 'm4 scale checkpoint0000' if recovered['revision'] == 1 else 'm4 scale checkpoint%06d' % (recovered['revision'] - 1)
            assert recovered['revision'] < baseline['liveRevision'] and recovered['text'] == expected
            trash_id = baseline['stats']['scaleTrash'][0]
            assert any(item['id'] == trash_id for item in request('GET', '/v1/trash')['items'])
            hits = request('POST', '/v1/search', {'query': recovered['text'], 'mode': 'keyword'})['results']
            assert any(hit['document_id'] == note_id for hit in hits)
            run(compose + ['exec', '-T', 'daemon', 'touch', '/srv/secondbrain/qualification-heap/request-full-gc'])
            time.sleep(3)
            heap = [json.loads(line) for line in provision('cat /srv/secondbrain/qualification-heap/heap.jsonl').stdout.splitlines()]
            forced = [row for row in heap if row['forced_full_gc']][-1]
            assert forced['gc_heap_size_bytes'] < 1_000_000_000 + forced['vector_statistics']['VectorBytes']
            runtime = json.loads(run(['docker', 'inspect', args.project + '-daemon-1']).stdout)[0]
            assert runtime['RestartCount'] == 0 and not runtime['State']['OOMKilled']
            checkpoint('restoreVerification', {'stats': after, 'capturedRevision': recovered['revision'],
                'laterLiveRevision': baseline['liveRevision'], 'allOtherAuthoredStatesEqual': True,
                'trashPreserved': True, 'rebuiltKeywordSearchVerified': True, 'daemonImageId': runtime['Image'],
                'daemonRestarts': runtime['RestartCount'], 'postRestoreForcedGc': forced,
                'semanticCoverage': 'Derived vectors are excluded from the archive; coverage rebuilds after restart through the configured embedding provider.'})
            report['status'] = 'passed'
            return
        if args.restore_only:
            assert initial['scaleEdit'] and initial['scaleTrash']
            note_id, live_revision = initial['scaleEdit']
            trash_id = initial['scaleTrash'][0]
            stable = read_stats(note_id)
            checkpoint('restoreBaseline', {'editableDocumentId': note_id, 'liveRevision': live_revision, 'stats': stable})
            run(compose + ['stop', 'daemon'])
            started = time.monotonic()
            restored = cli('maintenance', 'restore', '/provision/project/restore.sbk', timeout=7200)
            restore_seconds = time.monotonic() - started
            checkpoint('restoreInstallation', {'seconds': restore_seconds, 'cli': restored})
            run(compose + ['start', 'daemon']); wait_ready()
            try: request('GET', '/auth/me'); raise AssertionError('Old session survived restore.')
            except urllib.error.HTTPError as error: assert error.code == 401
            login()
            recovered = request('GET', '/v1/documents/' + note_id + '?include=text')
            expected = 'm4 scale checkpoint0000' if recovered['revision'] == 1 else 'm4 scale checkpoint%06d' % (recovered['revision'] - 1)
            assert recovered['revision'] < live_revision and recovered['text'] == expected
            after = read_stats(note_id)
            assert after['documents'] == stable['documents'] and after['authoredDocumentHash'] == stable['authoredDocumentHash']
            # The archive omits the disposable index. Offline restore rebuilds text;
            # embeddings and their derived indexed/partial status catch up after restart.
            assert after['chunks'] >= 1000000
            assert any(item['id'] == trash_id for item in request('GET', '/v1/trash')['items'])
            hits = request('POST', '/v1/search', {'query': recovered['text'], 'mode': 'keyword'})['results']
            assert any(hit['document_id'] == note_id for hit in hits)
            run(compose + ['exec', '-T', 'daemon', 'touch', '/srv/secondbrain/qualification-heap/request-full-gc'])
            time.sleep(3)
            heap = [json.loads(line) for line in provision('cat /srv/secondbrain/qualification-heap/heap.jsonl').stdout.splitlines()]
            forced = [row for row in heap if row['forced_full_gc']][-1]
            assert forced['gc_heap_size_bytes'] < 1_000_000_000 + forced['vector_statistics']['VectorBytes']
            runtime = json.loads(run(['docker', 'inspect', args.project + '-daemon-1']).stdout)[0]
            assert runtime['RestartCount'] == 0 and not runtime['State']['OOMKilled']
            checkpoint('restore', {'stats': after, 'capturedRevision': recovered['revision'], 'laterLiveRevision': live_revision,
                'allOtherAuthoredStatesEqual': True, 'oldSessionRejected': True, 'trashPreserved': True,
                'rebuiltSearchVerified': True, 'daemonImageId': runtime['Image'], 'daemonRestarts': runtime['RestartCount'], 'postRestoreForcedGc': forced})
            report['status'] = 'passed'
            return
        note = request('POST', '/v1/documents', {'content': 'm4 scale checkpoint0000', 'title': 'M4 scale editable'})['document']
        trash = request('POST', '/v1/documents', {'content': 'Retained M4 scale trash', 'title': 'M4 scale trash'})['document']
        request('DELETE', '/v1/documents/' + trash['id'], headers={'If-Match': '"' + str(trash['revision']) + '"'})
        stable = read_stats(note['id'])
        revisions = {note['revision']: 'm4 scale checkpoint0000'}
        state = {'revision': note['revision'], 'edits': 0}
        durations = []
        failures = []
        stop = threading.Event()

        def writer():
            try:
                while not stop.is_set():
                    text = 'm4 scale checkpoint%06d' % (state['edits'] + 1)
                    started = time.monotonic()
                    value = request('PATCH', '/v1/documents/' + note['id'], {'content': text},
                        headers={'If-Match': '"' + str(state['revision']) + '"'})['document']
                    durations.append(time.monotonic() - started)
                    state['revision'] = value['revision']; revisions[value['revision']] = text; state['edits'] += 1
                    stop.wait(.2)
            except Exception as error: failures.append(error)

        thread = threading.Thread(target=writer); thread.start()
        try:
            while state['edits'] < 3 and not failures: time.sleep(.1)
            if failures: raise failures[0]
            before = state['edits']; started = time.monotonic(); backup_started_at = time.time()
            backup = request('POST', '/v1/maintenance/backup')
            seconds = time.monotonic() - started
            during = state['edits'] - before
            assert during > 0 and backup['partialFiles'] == 0 and not backup['archive']['partial']
        finally:
            stop.set(); thread.join(timeout=1800)
        if failures: raise failures[0]
        assert not thread.is_alive()
        name = backup['archive']['name']
        digest = hashlib.sha256(); downloaded = 0
        with client.open(origin + '/v1/maintenance/backups/' + name, timeout=1800) as response:
            while block := response.read(1024 * 1024): digest.update(block); downloaded += len(block)
        assert downloaded == backup['archive']['bytes'] and downloaded > 0
        checkpoint('backup', {'seconds': seconds, 'concurrentEdits': during, 'totalEdits': state['edits'],
            'startedAt': backup_started_at, 'completedAt': backup_started_at + seconds,
            'editP95Seconds': sorted(durations)[int(.95 * (len(durations)-1))], 'maximumEditSeconds': max(durations),
            'archiveBytes': downloaded, 'archiveSha256': digest.hexdigest(), 'result': backup})
        provision('cp --reflink=auto /srv/secondbrain/backups/' + name + ' /provision/project/restore.sbk; chown 1654:1654 /provision/project/restore.sbk; chmod 600 /provision/project/restore.sbk')
        run(compose + ['stop', 'daemon'])
        started = time.monotonic()
        restored = cli('maintenance', 'restore', '/provision/project/restore.sbk', timeout=7200)
        restore_seconds = time.monotonic() - started
        run(compose + ['start', 'daemon']); wait_ready()
        try: request('GET', '/auth/me'); raise AssertionError('Old session survived restore.')
        except urllib.error.HTTPError as error: assert error.code == 401
        login()
        recovered = request('GET', '/v1/documents/' + note['id'] + '?include=text')
        assert recovered['revision'] < state['revision'] and recovered['text'] == revisions[recovered['revision']]
        after = read_stats(note['id'])
        assert after['documents'] == stable['documents'] and after['authoredDocumentHash'] == stable['authoredDocumentHash']
        assert after['chunks'] >= 1000000
        assert any(item['id'] == trash['id'] for item in request('GET', '/v1/trash')['items'])
        hits = request('POST', '/v1/search', {'query': recovered['text'], 'mode': 'keyword'})['results']
        assert any(hit['document_id'] == note['id'] for hit in hits)
        checkpoint('restore', {'seconds': restore_seconds, 'cli': restored, 'stats': after,
            'capturedRevision': recovered['revision'], 'laterLiveRevision': state['revision'],
            'allOtherAuthoredStatesEqual': True, 'oldSessionRejected': True, 'trashPreserved': True, 'rebuiltSearchVerified': True,
            'semanticCoverage': 'Derived vectors catch up through the configured embedding provider after restart.'})
        run(compose + ['exec', '-T', 'daemon', 'touch', '/srv/secondbrain/qualification-heap/request-full-gc'])
        time.sleep(3)
        heap = [json.loads(line) for line in provision('cat /srv/secondbrain/qualification-heap/heap.jsonl').stdout.splitlines()]
        forced = [row for row in heap if row['forced_full_gc']][-1]
        assert forced['gc_heap_size_bytes'] < 1_000_000_000 + forced['vector_statistics']['VectorBytes']
        runtime = json.loads(run(['docker', 'inspect', args.project + '-daemon-1']).stdout)[0]
        assert runtime['RestartCount'] == 0 and not runtime['State']['OOMKilled']
        checkpoint('memory', {'postRestoreForcedGc': forced,
            'maximumSampledManagedBytes': max(row['managed_live_bytes'] for row in heap),
            'maximumSampledHeapMinusAllocatedVectors': max(row['gc_heap_size_bytes'] - (row.get('vector_statistics') or {}).get('VectorBytes', 0) for row in heap),
            'daemonImageId': runtime['Image'], 'daemonMemoryLimit': runtime['HostConfig']['Memory'],
            'daemonRestarts': runtime['RestartCount'], 'daemonOomKilled': runtime['State']['OOMKilled']})
        report['status'] = 'passed'
    except Exception as error:
        report['status'] = 'failed'
        report['error'] = {'type': type(error).__name__, 'message': str(error)}
        raise
    finally:
        sampling.set()
        if 'sampler' in locals(): sampler.join(timeout=60)
        if (root / 'overlay.json').exists():
            (root / (output_prefix + 'container.log')).write_bytes(run(compose + ['logs', '--no-color'], check=False).stdout)
            heap = run(compose + ['--profile', 'provision', 'run', '--rm', '--no-deps', '-T',
                '--entrypoint', 'cat', 'provision', '/srv/secondbrain/qualification-heap/heap.jsonl'], check=False).stdout
            (root / (output_prefix + 'heap.jsonl')).write_bytes(heap)
            report['memorySamples'] = len(samples)
            (root / (output_prefix + 'memory.json')).write_text(json.dumps(samples, indent=2) + '\n')
            run(compose + ['down', '--volumes', '--remove-orphans'], timeout=180, check=False)
        if provider:
            provider.terminate()
            try: provider.wait(timeout=10)
            except subprocess.TimeoutExpired: provider.kill(); provider.wait()
        report_path.write_text(json.dumps(report, indent=2) + '\n')
        print(json.dumps({'status': report['status'], 'output': str(report_path)}), flush=True)


if __name__ == '__main__':
    main()
