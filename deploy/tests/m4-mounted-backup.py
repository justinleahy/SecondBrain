#!/usr/bin/env python3
"""Qualify mounted-source backup/restore in an explicitly isolated M4 Compose stack."""
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
    parser.add_argument('--origin', required=True)
    parser.add_argument('--project', required=True)
    parser.add_argument('--host-root', required=True)
    parser.add_argument('--output', type=pathlib.Path, required=True)
    args = parser.parse_args()
    if not args.project.startswith('secondbrain-m4-') or not args.host_root.startswith('/tmp/secondbrain-m4-'):
        raise ValueError('Only an isolated M4 qualification stack may be changed.')
    repository = pathlib.Path(__file__).resolve().parents[2]
    env = dict(os.environ, SECONDBRAIN_HOST_ROOT=args.host_root)
    compose = ['docker', 'compose', '--project-name', args.project, '-f', 'deploy/compose.yaml',
               '-f', 'deploy/tests/compose-smoke.yaml', '-f', 'deploy/tests/m4-compose.yaml']
    password = 'compose-smoke-password'  # Synthetic account created by compose-smoke.sh.

    def command(arguments, data=None, timeout=90, check=True):
        result = subprocess.run(arguments, cwd=repository, env=env, input=data, capture_output=True, timeout=timeout)
        if check and result.returncode:
            raise RuntimeError('Isolated fixture operation failed: ' + result.stderr.decode(errors='replace')[-1000:])
        return result

    def root(script, data=None):
        return command(compose + ['--profile', 'provision', 'run', '--rm', '--no-deps', '-T',
                                  '--entrypoint', 'sh', 'provision', '-eu', '-c', script], data)

    def cli(*arguments):
        result = command(compose + ['--profile', 'provision', 'run', '--rm', '--no-deps', '-T',
                                   '--user', '1654:1654', '--entrypoint', 'dotnet', 'provision',
                                   '/app/cli/brain.dll', *arguments, '--json'], timeout=180, check=False)
        value = json.loads(result.stdout)
        if result.returncode or value.get('exitCode') != 0:
            raise RuntimeError('Isolated CLI operation failed: ' + str(value.get('code')) + ': ' + str(value.get('message')))
        return value.get('data')

    client = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
    csrf = None

    def request(method, path, body=None, headers=None):
        supplied = {'Origin': args.origin, **(headers or {})}
        if csrf and method != 'GET': supplied['X-CSRF-TOKEN'] = csrf
        raw = None
        if body is not None:
            raw = json.dumps(body).encode(); supplied['Content-Type'] = 'application/json'
        with client.open(urllib.request.Request(args.origin + path, raw, supplied, method=method), timeout=120) as response:
            data = response.read(64 * 1024 * 1024 + 1)
            if len(data) > 64 * 1024 * 1024: raise ValueError('Fixture response exceeded its bound.')
            return json.loads(data) if 'json' in response.headers.get('Content-Type', '') else data

    def wait(predicate, seconds=45):
        until = time.monotonic() + seconds
        while time.monotonic() < until:
            value = predicate()
            if value: return value
            time.sleep(.1)
        raise TimeoutError('Isolated M4 fixture did not converge.')

    def healthy():
        try: request('GET', '/health'); return True
        except (urllib.error.URLError, ConnectionError): return False

    def login():
        nonlocal csrf
        csrf = request('GET', '/auth/antiforgery')['token']
        request('POST', '/auth/login', {'password': password})
        csrf = request('GET', '/auth/antiforgery')['token']
        request('POST', '/auth/step-up', {'password': password})
        csrf = request('GET', '/auth/antiforgery')['token']

    def capture(text, title):
        return request('POST', '/v1/documents', {'content': text, 'title': title, 'type': 'note'})['document']

    # Confirm the named container belongs to this disposable project before mutation.
    container = command(compose + ['ps', '-a', '-q', 'daemon']).stdout.decode().strip()
    inspected = json.loads(command(['docker', 'inspect', container]).stdout)[0]
    assert inspected['Config']['Labels']['com.docker.compose.project'] == args.project
    assert inspected['Config']['Image'] == 'secondbrain:m4'
    command(compose + ['stop', 'daemon'])
    settings = root('cat /etc/secondbrain/settings/config.yaml').stdout.decode()
    if '\nbackup:' not in settings:
        settings += '\nbackup:\n  key_file: /etc/secondbrain/secrets/backup.key\n  keep: 5\n'
    elif 'key_file: /etc/secondbrain/secrets/backup.key' not in settings:
        raise ValueError('The fixture has unexpected backup settings.')
    root('cat > /etc/secondbrain/settings/config.yaml; chown 1654:1654 /etc/secondbrain/settings/config.yaml; chmod 600 /etc/secondbrain/settings/config.yaml', settings.encode())
    root('chown 1654:1654 /provision/project; chmod 700 /provision/project; '
         'mkdir -p /provision/project/recovery /srv/secondbrain-incoming/m4-mounted-source /srv/secondbrain-incoming/m4-restore-parent; '
         'chown 1654:1654 /provision/project/recovery /srv/secondbrain-incoming/m4-restore-parent; '
         'chmod 700 /provision/project/recovery /srv/secondbrain-incoming/m4-restore-parent; '
         'chown 1656:1654 /srv/secondbrain-incoming/m4-mounted-source; chmod 750 /srv/secondbrain-incoming/m4-mounted-source; '
         'printf "Mounted crystalline source fixture\\n" > /srv/secondbrain-incoming/m4-mounted-source/source.md; '
         'chown 1656:1654 /srv/secondbrain-incoming/m4-mounted-source/source.md; chmod 640 /srv/secondbrain-incoming/m4-mounted-source/source.md')
    cli('maintenance', 'backup-key', '--recovery-copy', '/provision/project/recovery/backup.key')
    command(compose + ['start', 'daemon']); wait(healthy); login()
    source = next((item for item in request('GET', '/v1/sources')
                   if item.get('config', {}).get('path') == '/srv/secondbrain-incoming/m4-mounted-source'), None)
    if source is None:
        source = request('POST', '/v1/sources', {'path': '/srv/secondbrain-incoming/m4-mounted-source', 'name': 'Mounted backup fixture',
            'include': ['**/*.md'], 'exclude': [], 'recursive': True, 'mode': 'index', 'enrich': False,
            'default_type': 'note', 'type_map': {}, 'watch': 'poll', 'poll_interval_s': 3600})
    destination = '/srv/secondbrain-incoming/m4-restore-parent/source'
    mappings = {}
    for item in request('GET', '/v1/sources'):
        config = item.get('config', {})
        if item['kind'] == 'folder' and config.get('mode') == 'index':
            old = config['path']
            mappings[old] = destination if item['id'] == source['id'] else '/srv/secondbrain-incoming/m4-restore-parent/other-' + str(len(mappings))
    def source_hits():
        return request('POST', '/v1/search', {'query': 'crystalline', 'mode': 'keyword', 'filters': {'source': source['id']}})['results']
    external_id = wait(source_hits)[0]['document_id']
    note = capture('checkpoint0000 mounted backup', 'Mounted backup editable')
    trash = capture('Retained mounted backup trash', 'Mounted backup trash')
    request('DELETE', '/v1/documents/' + trash['id'], headers={'If-Match': '"' + str(trash['revision']) + '"'})
    for i in range(8): capture('x' * 131072 + ' mounted backup padding ' + str(i), 'Mounted padding ' + str(i))
    versions = {note['revision']: 'checkpoint0000 mounted backup'}
    state = {'revision': note['revision'], 'writes': 0}; failures = []; stop = threading.Event()

    def edit():
        try:
            while not stop.is_set():
                text = 'checkpoint%04d mounted backup' % (state['writes'] + 1)
                result = request('PATCH', '/v1/documents/' + note['id'], {'content': text},
                    {'If-Match': '"' + str(state['revision']) + '"'})['document']
                versions[result['revision']] = text
                state['revision'] = result['revision']; state['writes'] += 1
        except Exception as error: failures.append(error)

    thread = threading.Thread(target=edit); thread.start()
    try:
        wait(lambda: state['writes'] >= 3 or failures)
        if failures: raise failures[0]
        before = state['writes']
        backup = request('POST', '/v1/maintenance/backup')
        during = state['writes'] - before
        assert during > 0 and not backup['archive']['partial'] and backup['partialFiles'] == 0
        archive = request('GET', '/v1/maintenance/backups/' + backup['archive']['name'])
    finally:
        stop.set(); thread.join(timeout=130)
    if failures: raise failures[0]
    assert not thread.is_alive()
    live_revision = state['revision']
    root('cat > /provision/project/restore.sbk; chown 1654:1654 /provision/project/restore.sbk; chmod 600 /provision/project/restore.sbk', archive)
    command(compose + ['stop', 'daemon'])
    mapping_args = [argument for old, new in mappings.items() for argument in ('--external-root', old + '=' + new)]
    restored = cli('maintenance', 'restore', '/provision/project/restore.sbk', *mapping_args)
    command(compose + ['start', 'daemon']); wait(healthy)
    try: request('GET', '/auth/me'); raise AssertionError('Old session survived restore.')
    except urllib.error.HTTPError as error: assert error.code == 401
    login()
    recovered = request('GET', '/v1/documents/' + note['id'] + '?include=text')
    assert recovered['revision'] < live_revision and recovered['text'] == versions[recovered['revision']]
    items = request('GET', '/v1/trash')['items']; assert any(item['id'] == trash['id'] for item in items)
    source_after = request('GET', '/v1/sources/' + source['id'])
    assert source_after['config']['path'] == destination
    assert wait(source_hits)[0]['document_id'] == external_id
    assert root('cat /srv/secondbrain-incoming/m4-restore-parent/source/source.md').stdout == b'Mounted crystalline source fixture\n'
    result = {'status': 'passed', 'syntheticData': True, 'hardenedCompose': True, 'productionChanged': False,
        'httpEdits': state['writes'], 'editsDuringBackup': during, 'restoredRevision': recovered['revision'], 'laterLiveRevision': live_revision,
        'archiveBytes': len(archive), 'archiveSha256': hashlib.sha256(archive).hexdigest(), 'completeManifestVerifiedByOfflineCli': True,
        'mountedExternalRootRemapped': True, 'externalSourceIdentityRetained': True, 'externalFileBytesPreserved': True,
        'trashIncluded': True, 'oldSessionRejected': True, 'restartedSearchVerified': True,
        'restoreResult': restored, 'daemonImageId': inspected['Image']}
    args.output.parent.mkdir(parents=True, exist_ok=True); args.output.write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps({key: value for key, value in result.items() if key != 'restoreResult'}))


if __name__ == '__main__':
    main()
