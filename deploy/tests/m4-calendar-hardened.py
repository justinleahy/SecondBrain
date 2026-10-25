"""Real TLS source downloads, conditional refresh and restore in an isolated Compose stack."""
import argparse
import hashlib
import http.cookiejar
import json
import os
import pathlib
import subprocess
import time
import urllib.error
import urllib.request


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--project', required=True)
    parser.add_argument('--host-root', required=True)
    parser.add_argument('--origin', required=True)
    parser.add_argument('--output', required=True, type=pathlib.Path)
    args = parser.parse_args()
    if not args.project.startswith('secondbrain-m4-') or not args.host_root.startswith('/tmp/secondbrain-m4-'):
        raise ValueError('Only isolated M4 stacks may be changed.')
    if not args.origin.startswith('http://127.0.0.1:18'):
        raise ValueError('Use an isolated loopback qualification port.')
    repo = pathlib.Path(__file__).resolve().parents[2]
    fixture = pathlib.Path(args.host_root) / 'tls-peer'
    fixture.mkdir(mode=0o755, exist_ok=True)
    fixture.chmod(0o755)
    env = dict(os.environ, SECONDBRAIN_HOST_ROOT=args.host_root)
    base = ['docker', 'compose', '--project-name', args.project, '-f', 'deploy/compose.yaml',
            '-f', 'deploy/tests/compose-smoke.yaml', '-f', 'deploy/tests/m4-compose.yaml']
    peer_name = args.project + '-tls-peer'
    allowed = '93.184.216.34'
    forbidden = '93.184.216.35'
    overlay = fixture / 'compose.json'
    compose = base + ['-f', str(overlay)]
    peer_started = False
    source_path = None

    def run(arguments, data=None, check=True, timeout=120):
        result = subprocess.run(arguments, cwd=repo, env=env, input=data, capture_output=True, timeout=timeout)
        if check and result.returncode:
            raise RuntimeError('Isolated fixture command failed: ' + result.stderr.decode(errors='replace')[-1200:])
        return result

    def root(script, data=None):
        return run(base + ['--profile', 'provision', 'run', '--rm', '--no-deps', '-T', '--entrypoint',
                           'sh', 'provision', '-eu', '-c', script], data)

    def wait(predicate, seconds=60):
        until = time.monotonic() + seconds
        while time.monotonic() < until:
            result = predicate()
            if result:
                return result
            time.sleep(.2)
        raise TimeoutError('The isolated TLS calendar fixture did not converge.')

    client = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
    csrf = None

    def request(method, path, body=None):
        headers = {'Origin': args.origin}
        if csrf and method != 'GET':
            headers['X-CSRF-TOKEN'] = csrf
        raw = None
        if body is not None:
            raw = json.dumps(body).encode()
            headers['Content-Type'] = 'application/json'
        with client.open(urllib.request.Request(args.origin + path, raw, headers, method=method), timeout=120) as response:
            raw = response.read(64 * 1024 * 1024 + 1)
            assert len(raw) <= 64 * 1024 * 1024
            return json.loads(raw) if 'json' in response.headers.get('Content-Type', '') and raw else raw

    def login():
        nonlocal csrf
        csrf = request('GET', '/auth/antiforgery')['token']
        request('POST', '/auth/login', {'password': 'compose-smoke-password'})
        csrf = request('GET', '/auth/antiforgery')['token']
        request('POST', '/auth/step-up', {'password': 'compose-smoke-password'})
        csrf = request('GET', '/auth/antiforgery')['token']

    def healthy():
        try:
            return request('GET', '/ready')['ready']
        except (urllib.error.URLError, ConnectionError):
            return False

    def state(title, **extra):
        body = ('BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//SecondBrain//TLS fixture//EN\r\n'
                'BEGIN:VEVENT\r\nUID:m4-tls-calendar\r\nDTSTAMP:20261024T000000Z\r\n'
                'DTSTART:20261025T130000Z\r\nDTEND:20261025T140000Z\r\nSUMMARY:' + title +
                '\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n')
        value = {'body': body, **extra}
        temporary = fixture / 'state.next'
        temporary.write_text(json.dumps(value))
        temporary.chmod(0o644)
        temporary.replace(fixture / 'state.json')

    daemon = run(base + ['ps', '-q', 'daemon']).stdout.decode().strip()
    inspected = json.loads(run(['docker', 'inspect', daemon]).stdout)[0]
    assert inspected['Config']['Labels']['com.docker.compose.project'] == args.project
    assert inspected['Config']['Image'] == 'secondbrain:m4'
    helper = run(base + ['ps', '-q', 'egress']).stdout.decode().strip()
    helper_info = json.loads(run(['docker', 'inspect', helper]).stdout)[0]
    network = next(iter(helper_info['NetworkSettings']['Networks']))
    address = helper_info['NetworkSettings']['Networks'][network]['IPAddress']
    assert address.startswith('172.31.')
    peer_address = address.rsplit('.', 1)[0] + '.3'
    original_settings = root('cat /etc/secondbrain/settings/config.yaml').stdout
    marker = 'synthetic-calendar-token'

    try:
        for name, arguments in [
            ('ca', ['req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-days', '2', '-subj', '/CN=M4 fixture CA',
                    '-keyout', str(fixture / 'ca.key'), '-out', str(fixture / 'ca.pem')]),
            ('server', ['req', '-newkey', 'rsa:2048', '-nodes', '-subj', '/CN=M4 calendar fixture',
                        '-keyout', str(fixture / 'server.key'), '-out', str(fixture / 'server.csr')])]:
            run(['openssl', *arguments])
        (fixture / 'extensions').write_text('subjectAltName=IP:' + allowed + ',IP:' + forbidden + '\nextendedKeyUsage=serverAuth\n')
        run(['openssl', 'x509', '-req', '-in', str(fixture / 'server.csr'), '-CA', str(fixture / 'ca.pem'),
             '-CAkey', str(fixture / 'ca.key'), '-CAcreateserial', '-days', '2', '-extfile', str(fixture / 'extensions'),
             '-out', str(fixture / 'server.pem')])
        for filename in ['server.key', 'server.pem', 'ca.pem']:
            (fixture / filename).chmod(0o644)
        state('M4 TLS first version')
        peer_image = 'python@sha256:a3799099b64fd333f262768c7a64733f6e4edb7bc569813710a100214bdcf278'
        run(['docker', 'run', '-d', '--name', peer_name, '--network', network, '--ip', peer_address,
             '--read-only', '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges:true', '--user', '1654:1654',
             '--memory', '128m', '--pids-limit', '32', '-v', str(fixture) + ':/fixture:ro',
             '-v', str(repo / 'deploy/tests/m4-calendar-peer.py') + ':/peer.py:ro', peer_image, 'python', '-u', '/peer.py'])
        peer_started = True
        for ip in [allowed, forbidden]:
            run(['docker', 'run', '--rm', '--network', 'container:' + peer_name, '--cap-drop', 'ALL', '--cap-add',
                 'NET_ADMIN', '--entrypoint', 'ip', 'secondbrain-egress:m4', 'addr', 'add', ip + '/32', 'dev', 'lo'])
        # Verify that both destinations are real listeners before denying one in the daemon namespace.
        probe = 'import socket; s=socket.create_connection(("%s",8443),3); s.close()'
        for ip in [allowed, forbidden]:
            run(['docker', 'exec', peer_name, 'python', '-c', probe % ip])
        overlay.write_text(json.dumps({'services': {
            'egress': {'environment': {'SECONDBRAIN_CALENDAR_ALLOWLIST': allowed + ':8443'}},
            'daemon': {'environment': {'SSL_CERT_FILE': '/fixture-ca/ca.pem'},
                       'volumes': [{'type': 'bind', 'source': str(fixture / 'ca.pem'), 'target': '/fixture-ca/ca.pem', 'read_only': True}]}}}))
        run(base + ['stop', 'daemon', 'mock-provider', 'extractor', 'egress'])
        settings = original_settings.decode().replace('sources:\n', 'sources:\n  calendar_allowed_origins: [https://' + allowed + ':8443]\n', 1)
        assert settings != original_settings.decode()
        root('cat > /etc/secondbrain/settings/config.yaml; chown 1654:1654 /etc/secondbrain/settings/config.yaml; chmod 600 /etc/secondbrain/settings/config.yaml', settings.encode())
        run(compose + ['up', '-d', '--no-deps', '--force-recreate', 'egress'])
        helper = run(compose + ['ps', '-q', 'egress']).stdout.decode().strip()
        for ip in [allowed, forbidden]:
            run(['docker', 'exec', helper, 'ip', 'route', 'add', ip + '/32', 'via', peer_address])
        run(compose + ['up', '-d', '--force-recreate', '--wait', '--wait-timeout', '90', 'daemon', 'extractor', 'mock-provider'])
        wait(healthy); login()
        # Real TCP checks distinguish a blocked destination from an unavailable listener.
        daemon = run(compose + ['ps', '-q', 'daemon']).stdout.decode().strip()
        run(['docker', 'exec', daemon, 'bash', '-c', 'timeout 3 bash -c "exec 3<>/dev/tcp/' + allowed + '/8443"'])
        blocked = run(['docker', 'exec', daemon, 'bash', '-c', 'timeout 3 bash -c "exec 3<>/dev/tcp/' + forbidden + '/8443"'], check=False)
        assert blocked.returncode != 0
        rejected = False
        try:
            request('POST', '/v1/sources', {'kind': 'calendar_subscription', 'name': 'Forbidden fixture', 'url': 'https://' + forbidden + ':8443/' + marker})
        except urllib.error.HTTPError as error:
            assert error.code == 400
            rejected = True
        assert rejected
        source = request('POST', '/v1/sources', {'kind': 'calendar_subscription', 'name': 'M4 TLS calendar',
                         'url': 'https://' + allowed + ':8443/published/2/' + marker, 'poll_interval_s': 300})
        path = '/v1/sources/' + source['id']
        source_path = path
        def current(): return request('GET', path)
        first = wait(lambda: (s if s.get('last_success_at') else None) if (s := current()) else None)
        assert marker not in json.dumps(first)
        # Query defaults may return occurrence records; source/container identity is checked separately.
        events = request('POST', '/v1/query', {'type': 'calendar_event'})['records']
        event = next(e for e in events if e['title'] == 'M4 TLS first version')
        before = request('GET', '/v1/documents/' + event['id'])
        request('POST', path + '/scan')
        second = wait(lambda: (s if s.get('last_success_at') != first['last_success_at'] else None) if (s := current()) else None)
        assert second['container_id'] == first['container_id'] and second['last_change_at'] == first['last_change_at']
        assert request('GET', '/v1/documents/' + event['id'])['revision'] == before['revision']
        assert any(json.loads(line)['status'] == 304 for line in run(['docker', 'logs', peer_name]).stdout.decode().splitlines())
        state('M4 TLS revised version')
        request('POST', path + '/scan')
        changed = wait(lambda: (s if s.get('last_change_at') != second['last_change_at'] else None) if (s := current()) else None)
        after = request('GET', '/v1/documents/' + event['id'])
        assert after['title'] == 'M4 TLS revised version' and after['revision'] > before['revision']
        state('', body='<html>Sign in</html>', calendar=False)
        request('POST', path + '/scan')
        failed = wait(lambda: (s if s.get('error') else None) if (s := current()) else None)
        assert failed['freshness'] == 'failed' and failed['last_change_at'] == changed['last_change_at']
        assert request('GET', '/v1/documents/' + event['id'])['revision'] == after['revision']
        request('PATCH', path, {'enabled': False})
        requests_before = run(['docker', 'logs', peer_name]).stdout
        try: request('POST', path + '/scan'); raise AssertionError('Paused source accepted refresh.')
        except urllib.error.HTTPError as error: assert error.code == 409
        assert run(['docker', 'logs', peer_name]).stdout == requests_before
        state('M4 TLS revised version')
        request('PATCH', path, {'enabled': True})
        request('POST', path + '/scan')
        recovered = wait(lambda: (s if s.get('freshness') == 'current' else None) if (s := current()) else None)
        archive_info = request('POST', '/v1/maintenance/backup')
        assert not archive_info['archive']['partial']
        archive = request('GET', '/v1/maintenance/backups/' + archive_info['archive']['name'])
        root('cat > /provision/project/calendar-restore.sbk; chown 1654:1654 /provision/project/calendar-restore.sbk; chmod 600 /provision/project/calendar-restore.sbk', archive)
        run(compose + ['stop', 'daemon'])
        restored = run(base + ['--profile', 'provision', 'run', '--rm', '--no-deps', '-T', '--user', '1654:1654',
                              '--entrypoint', 'dotnet', 'provision', '/app/cli/brain.dll', 'maintenance', 'restore',
                              '/provision/project/calendar-restore.sbk', '--json'], timeout=180)
        assert json.loads(restored.stdout)['exitCode'] == 0
        run(compose + ['start', 'daemon']); wait(healthy); login()
        restored_source = current()
        assert restored_source['container_id'] == first['container_id']
        request('POST', path + '/scan')
        wait(lambda: (s if s.get('last_success_at') != restored_source['last_success_at'] else None) if (s := current()) else None)
        assert request('GET', '/v1/documents/' + event['id'])['revision'] == after['revision']
        logs = run(compose + ['logs', '--no-color', 'daemon', 'extractor', 'egress']).stdout
        assert marker.encode() not in logs
        report = {'status': 'passed', 'syntheticData': True, 'realTlsCertificateVerification': True,
            'hardenedCompose': True, 'localOnlyModelsReady': True, 'forbiddenReachablePeerBlockedByFirewall': True,
            'unlistedOriginRejected': True, 'conditional304PreservedRevision': True, 'changed200KeptMemberIdentity': True,
            'invalidHtmlPreservedAcceptedEvents': True, 'pauseBlockedFetch': True, 'recoveryClearedStaleState': True,
            'encryptedRestoreResumedProtectedSubscription': True, 'sourceContainerIdentityPreserved': True,
            'tokenAbsentFromSourceResponseAndContainerLogs': True, 'backupBytes': len(archive),
            'backupSha256': hashlib.sha256(archive).hexdigest(), 'productionChanged': False,
            'limitations': ['Controlled public-address aliases exist only in the isolated Docker network; no public calendar service called.',
                            'Systemd and production deployment are separate acceptance gates.']}
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(report, indent=2) + '\n')
        print(json.dumps(report), flush=True)
    finally:
        # Remove the controlled source before revoking its origin so the retained fixture can start again.
        if source_path:
            try:
                login()
                request('DELETE', source_path + '?purge=1')
            except (urllib.error.URLError, ConnectionError):
                pass
        if overlay.exists():
            run(compose + ['stop', 'daemon', 'mock-provider', 'extractor', 'egress'], check=False)
            root('cat > /etc/secondbrain/settings/config.yaml; chown 1654:1654 /etc/secondbrain/settings/config.yaml; chmod 600 /etc/secondbrain/settings/config.yaml', original_settings)
            run(base + ['up', '-d', '--force-recreate', '--wait', '--wait-timeout', '90', 'egress', 'daemon', 'extractor', 'mock-provider'], check=False)
        if peer_started:
            run(['docker', 'rm', '-f', peer_name], check=False)


if __name__ == '__main__':
    main()
