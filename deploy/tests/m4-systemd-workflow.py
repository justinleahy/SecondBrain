#!/usr/bin/env python3
"""M4 workflow for systemd-m1-qualification.sh's disposable numeric-UID installation."""
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
    root = pathlib.Path(os.environ['SECONDBRAIN_QUALIFICATION_ROOT'])
    unit = os.environ['SECONDBRAIN_QUALIFICATION_UNIT']
    origin = os.environ['SECONDBRAIN_QUALIFICATION_ORIGIN']
    assert root.parent == pathlib.Path('/srv') and root.name.startswith('sb-m1-qualification.')
    assert unit == root.name + '.service' and origin.startswith('http://127.0.0.1:')
    assert os.geteuid() == 0
    binary = pathlib.Path('/opt') / root.name / 'brain'
    output = pathlib.Path(os.environ['SECONDBRAIN_M4_SYSTEMD_EVIDENCE'])
    password = os.environ['SECONDBRAIN_QUALIFICATION_PASSWORD']
    client = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
    csrf = None

    def command(*args):
        return subprocess.check_output(args, stderr=subprocess.STDOUT, timeout=300)

    def cli(*args):
        raw = command('setpriv', '--reuid', '1654', '--regid', '1654', '--clear-groups',
            'env', '-i', 'PATH=/usr/bin:/bin', 'DOTNET_EnableDiagnostics=0',
            str(binary), '--config', str(root / 'config/config.yaml'),
            '--secrets-dir', str(root / 'secrets'), '--json', *args)
        result = json.loads(raw)
        assert result['exitCode'] == 0, result.get('code')
        return result.get('data')

    def request(method, path, body=None, headers=None):
        supplied = {'Origin': origin, **(headers or {})}
        if csrf and method != 'GET': supplied['X-CSRF-TOKEN'] = csrf
        data = None
        if body is not None:
            data = json.dumps(body).encode(); supplied['Content-Type'] = 'application/json'
        with client.open(urllib.request.Request(origin + path, data, supplied, method=method), timeout=120) as response:
            raw = response.read(64 * 1024 * 1024 + 1)
            assert len(raw) <= 64 * 1024 * 1024
            return json.loads(raw) if 'json' in response.headers.get('Content-Type', '') and raw else raw

    def login():
        nonlocal csrf
        csrf = request('GET', '/auth/antiforgery')['token']
        request('POST', '/auth/login', {'password': password})
        csrf = request('GET', '/auth/antiforgery')['token']
        request('POST', '/auth/step-up', {'password': password})
        csrf = request('GET', '/auth/antiforgery')['token']

    def wait(predicate, seconds=90):
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline:
            value = predicate()
            if value: return value
            time.sleep(.2)
        raise TimeoutError('Disposable systemd workflow did not converge.')

    def healthy():
        try: return request('GET', '/ready')['ready']
        except (urllib.error.URLError, ConnectionError): return False

    command('systemctl', 'stop', unit)
    recovery = root / 'results/recovery'
    recovery.mkdir(mode=0o700); os.chown(recovery, 1654, 1654)
    cli('maintenance', 'backup-key', '--recovery-copy', str(recovery / 'backup.key'))
    command('systemctl', 'start', unit); wait(healthy); login()

    def capture(text, title, kind='note'):
        return request('POST', '/v1/documents', {'content': text, 'title': title, 'type': kind})['document']

    # Multipart imports cross the actual socket-activated, offline extractor boundary.
    def upload(name, text):
        boundary = 'SecondBrainSystemdM4Boundary'
        data = ('--' + boundary + '\r\nContent-Disposition: form-data; name="file"; filename="' + name
                + '"\r\nContent-Type: application/octet-stream\r\n\r\n' + text
                + '\r\n--' + boundary + '--\r\n').encode()
        with client.open(urllib.request.Request(origin + '/v1/documents?wait=30', data,
                {'Origin': origin, 'X-CSRF-TOKEN': csrf, 'Content-Type': 'multipart/form-data; boundary=' + boundary}), timeout=120) as response:
            return json.load(response)['document']

    transcript = upload('meeting.vtt', 'WEBVTT\n\n00:00:01.000 --> 00:00:04.000\n<v Ada>Crystalline systemd meeting fixture.\n')
    assert transcript['type'] == 'transcript'
    calendar = upload('calendar.ics', 'BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//SecondBrain//M4//EN\r\nBEGIN:VEVENT\r\nUID:systemd-m4\r\nDTSTAMP:20261024T000000Z\r\nDTSTART:20261025T130000Z\r\nDTEND:20261025T140000Z\r\nSUMMARY:Crystalline systemd calendar\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n')
    assert calendar['id']
    note = capture('checkpoint0000 crystalline systemd', 'Systemd editable')
    trash = capture('Systemd retained trash', 'Systemd trash')
    request('DELETE', '/v1/documents/' + trash['id'], headers={'If-Match': '"1"'})
    for i in range(8): capture('x' * 131072 + ' systemd padding', 'Padding ' + str(i))
    versions = {note['revision']: 'checkpoint0000 crystalline systemd'}
    state = {'revision': note['revision'], 'writes': 0}; stop = threading.Event(); failures = []

    def edit():
        try:
            while not stop.is_set():
                text = 'checkpoint%04d crystalline systemd' % (state['writes'] + 1)
                result = request('PATCH', '/v1/documents/' + note['id'], {'content': text},
                    {'If-Match': '"' + str(state['revision']) + '"'})['document']
                versions[result['revision']] = text
                state['revision'] = result['revision']; state['writes'] += 1
        except Exception as error: failures.append(error)

    thread = threading.Thread(target=edit); thread.start()
    try:
        wait(lambda: state['writes'] >= 3 or failures)
        assert not failures
        before = state['writes']; backup = request('POST', '/v1/maintenance/backup')
        during = state['writes'] - before
        assert during > 0 and backup['partialFiles'] == 0 and not backup['archive']['partial']
        archive = request('GET', '/v1/maintenance/backups/' + backup['archive']['name'])
    finally:
        stop.set(); thread.join(timeout=130)
    assert not failures and not thread.is_alive()
    path = root / 'results/restore.sbk'
    with path.open('xb') as stream:
        os.fchmod(stream.fileno(), 0o600); stream.write(archive); os.fchown(stream.fileno(), 1654, 1654)
    command('systemctl', 'stop', unit)
    restored = cli('maintenance', 'restore', str(path))
    command('systemctl', 'start', unit); wait(healthy)
    try: request('GET', '/auth/me'); raise AssertionError('Old session survived restore.')
    except urllib.error.HTTPError as error: assert error.code == 401
    login()
    recovered = request('GET', '/v1/documents/' + note['id'] + '?include=text')
    assert recovered['text'] == versions[recovered['revision']] and recovered['revision'] < state['revision']
    assert any(x['id'] == trash['id'] for x in request('GET', '/v1/trash')['items'])
    assert any(x['document_id'] == note['id'] for x in request('POST', '/v1/search', {'query': 'crystalline', 'mode': 'keyword'})['results'])
    ready = request('GET', '/ready'); assert ready['ready']
    daemon = command('systemctl', 'show', unit, '-p', 'User', '-p', 'Group', '-p', 'ProtectSystem', '-p', 'NoNewPrivileges', '-p', 'MemoryMax', '-p', 'TasksMax').decode()
    extractor = command('systemctl', 'show', root.name + '-extractor.service', '-p', 'PrivateNetwork', '-p', 'ProtectSystem', '-p', 'NoNewPrivileges', '-p', 'User').decode()
    assert 'User=1654\n' in daemon and 'ProtectSystem=strict\n' in daemon and 'NoNewPrivileges=yes\n' in daemon
    assert 'PrivateNetwork=yes\n' in extractor and 'User=1655\n' in extractor
    result = {'status': 'passed', 'syntheticData': True, 'productionChanged': False,
        'systemdNumericUidDeployment': True, 'socketActivatedTranscriptAndCalendarImport': True,
        'editsDuringBackup': during, 'totalEdits': state['writes'], 'restoredRevision': recovered['revision'],
        'laterLiveRevision': state['revision'], 'archiveBytes': len(archive),
        'archiveSha256': hashlib.sha256(archive).hexdigest(), 'offlineCliManifestVerified': True,
        'trashPreserved': True, 'oldSessionRejected': True, 'rebuiltSearchPassed': True,
        'restoreResult': restored, 'readiness': ready, 'daemonProperties': daemon, 'extractorProperties': extractor}
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps({'status': 'passed', 'editsDuringBackup': during, 'productionChanged': False}))


if __name__ == '__main__':
    main()
