#!/usr/bin/env python3
"""Upgrade the existing reference systemd installation with a verified M1 bundle.

Run as root. Private backups and command output stay under /var/backups/secondbrain.
Only sanitized verification results are written to the requested artifacts directory.
"""
import argparse
import datetime
import fcntl
import hashlib
import json
import os
import pathlib
import pwd
import shutil
import sqlite3
import stat
import subprocess
import sys
import time
import traceback
import urllib.error
import urllib.request
import uuid

INSTALL = pathlib.Path('/usr/local/lib/secondbrain')
DATA = pathlib.Path('/srv/secondbrain')
CONFIG = pathlib.Path('/etc/secondbrain')
INCOMING = pathlib.Path('/srv/secondbrain-incoming')
UNITS = pathlib.Path('/etc/systemd/system')
UNIT_NAMES = ('secondbrain.service', 'secondbrain-extractor.service', 'secondbrain-extractor.socket')
APPHOSTS = ('brain', 'SecondBrain.Server', 'SecondBrain.Extractor')
ORIGIN = 'http://127.0.0.1:7171'


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def sha(path):
    digest = hashlib.sha256()
    with path.open('rb') as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b''):
            digest.update(chunk)
    return digest.hexdigest()


def file_hashes(root):
    return {str(p.relative_to(root)): sha(p) for p in sorted(root.rglob('*')) if p.is_file()}


def verify_bundle(root, expected):
    require(root.is_dir() and not root.is_symlink(), 'Bundle directory is missing or is a symlink.')
    require(all(not p.is_symlink() for p in root.rglob('*')), 'Bundle contains a symlink.')
    require(all(not pathlib.PurePosixPath(p).is_absolute() and '..' not in pathlib.PurePosixPath(p).parts for p in expected), 'Invalid bundle manifest path.')
    actual = file_hashes(root)
    require(actual == expected, 'Bundle does not exactly match its qualification manifest.')
    require(all((root / name).is_file() and os.access(root / name, os.X_OK) for name in APPHOSTS), 'Bundle apphosts are missing or not executable.')
    require((root / 'SecondBrain.Server.staticwebassets.endpoints.json').is_file() and (root / 'wwwroot').is_dir(), 'Bundle is missing web assets.')


def open_store(data, kind):
    return sqlite3.connect((data / kind / (kind + '.db')).as_uri() + '?mode=ro', uri=True, timeout=10)


def identity_snapshot(data):
    with open_store(data, 'state') as db:
        require(db.execute('PRAGMA quick_check').fetchall() == [('ok',)], 'State database failed quick_check.')
        account = db.execute('SELECT * FROM account ORDER BY id').fetchall()
        require(len(account) == 1, 'Existing account is not initialized.')
        credentials = db.execute('SELECT id,kind,verifier,scopes,generation,kid,account_epoch,revoked_at FROM credentials ORDER BY id').fetchall()
        meta = dict(db.execute("SELECT key,value FROM meta WHERE key IN ('instance_id','account_epoch','password_parameters')"))
        documents = [row[0] for row in db.execute('SELECT id FROM documents ORDER BY id')]
    def digest(value):
        return hashlib.sha256(json.dumps(value, sort_keys=True, default=lambda x: x.hex()).encode()).hexdigest()
    return {'account': digest(account), 'credentials': {row[0]: digest(row) for row in credentials}, 'meta': meta, 'documents': documents}


def verify_identity(before, after):
    require(before['account'] == after['account'], 'Account changed during deployment.')
    require(before['meta'] == after['meta'], 'Instance identity or account policy changed during deployment.')
    require(all(after['credentials'].get(key) == value for key, value in before['credentials'].items()), 'An existing credential changed during deployment.')
    require(set(before['documents']).issubset(after['documents']), 'An existing document disappeared during deployment.')


def verify_schema(data):
    with open_store(data, 'state') as db:
        require(db.execute('SELECT phase FROM m1_migration_state WHERE version=2').fetchone() == ('applied',), 'M1 state migration is not applied.')
    with open_store(data, 'index') as db:
        require(db.execute('PRAGMA quick_check').fetchall() == [('ok',)], 'Index database failed quick_check.')
        tables = {row[0] for row in db.execute('SELECT name FROM sqlite_schema')}
        require({'document_text', 'chunks', 'chunks_fts', 'chunk_vectors', 'embedding_cache'}.issubset(tables), 'M1 index schema is incomplete.')


def request(path, body=None):
    headers = {'Content-Type': 'application/json'} if body is not None else {}
    req = urllib.request.Request(ORIGIN + path, None if body is None else json.dumps(body).encode(), headers)
    try:
        with urllib.request.urlopen(req, timeout=5) as response:
            data = response.read()
            return response.status, json.loads(data) if data else None
    except urllib.error.HTTPError as error:
        return error.code, None


def wait_ready(seconds=120):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        try:
            status, data = request('/ready')
            if status == 200 and data and data.get('ready'):
                return data
        except (OSError, ValueError):
            pass
        time.sleep(1)
    raise RuntimeError('The upgraded daemon did not become ready within 120 seconds.')


class Upgrade:
    def __init__(self, repo, evidence):
        self.repo = repo
        self.evidence = evidence
        self.tag = datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%SZ') + '-' + uuid.uuid4().hex[:8]
        parent = pathlib.Path('/var/backups/secondbrain')
        require(not parent.is_symlink(), 'Backup root must not be a symlink.')
        parent.mkdir(mode=0o700, exist_ok=True)
        require(parent.stat().st_uid == 0, 'Backup root must be root-owned.')
        parent.chmod(0o700)
        self.backup = parent / ('m1-' + self.tag)
        self.backup.mkdir(mode=0o700)
        self.previous = INSTALL.with_name('secondbrain.pre-m1-' + self.tag)
        self.staged = INSTALL.with_name('.secondbrain.m1-' + self.tag)
        self.stopped = False
        self.snapshotted = False
        self.swapped = False
        self.done = False
        self.data_lock = None
        self.phase = 'preflight'
        self.log = (self.backup / 'deployment.log').open('w')

    def run(self, args):
        self.phase = pathlib.Path(str(args[0])).name + ' ' + (str(args[1]) if len(args) > 1 else '')
        result = subprocess.run([str(a) for a in args], text=True, stdout=self.log, stderr=subprocess.STDOUT)
        self.log.flush()
        require(result.returncode == 0, 'A deployment command failed; details are in the private deployment log.')

    def write(self, name, result, public=False):
        path = (self.evidence if public else self.backup) / name
        path.write_text(json.dumps(result, indent=2) + '\n')
        path.chmod(0o644 if public else 0o600)

    def stop(self):
        # A failed stop can still have stopped some units; failure recovery must restart them.
        self.stopped = True
        self.run(['systemctl', 'stop', *UNIT_NAMES])
        for name in UNIT_NAMES:
            value = subprocess.check_output(['systemctl', 'show', name, '--property=ActiveState', '--value'], text=True).strip()
            require(value in ('inactive', 'failed'), 'A SecondBrain service did not stop.')

    def preflight(self, bundle, expected):
        import yaml

        verify_bundle(bundle, expected)
        for path in (INSTALL, DATA, CONFIG, INCOMING, UNITS):
            require(path.is_dir() and path.resolve() == path, 'A reference installation path is missing or traverses a symlink.')
        for name, uid in [('secondbrain', 1654), ('secondbrain-extract', 1655), ('secondbrain-sync', 1656)]:
            user = pwd.getpwnam(name)
            require((user.pw_uid, user.pw_gid) == (uid, 1654), 'Installed identities differ from the qualified reference deployment.')
        require(all((UNITS / name).is_file() and not (UNITS / name).is_symlink() for name in UNIT_NAMES), 'Expected installed base unit files are missing.')
        try:
            config = yaml.safe_load((CONFIG / 'config.yaml').read_text())
        except Exception:
            raise RuntimeError('Cannot parse the installed configuration; inspect it privately.') from None
        require(config.get('data_root') == str(DATA) and config.get('sources', {}).get('incoming_root') == str(INCOMING), 'Configuration uses non-reference roots; deployment stopped before changing services.')
        require(any(x.get('scheme', 'http') == 'http' and x.get('bind') == '127.0.0.1' and x.get('port') == 7171 for x in config.get('server', {}).get('listeners', [])), 'Expected loopback health listener is not configured.')
        if pathlib.Path('/sys/fs/selinux/enforce').exists():
            for name in APPHOSTS:
                policy = subprocess.check_output(['matchpathcon', str(INSTALL / name)], text=True)
                require(':bin_t:' in policy, 'Persistent SELinux apphost labels are missing.')
        needed = sum(p.stat().st_size for p in DATA.rglob('*') if p.is_file())
        require(shutil.disk_usage(self.backup).free > needed * 2 + 64 * 1024 * 1024, 'Insufficient free backup space.')
        self.run(['cp', '-a', str(bundle), str(self.staged)])
        self.run(['chown', '-R', 'root:root', str(self.staged)])
        self.run(['chmod', '-R', 'a+rX', str(self.staged)])
        verify_bundle(self.staged, expected)
        print('Preflight passed: verified M1 bundle, reference paths, identities, listener, and security labels.', flush=True)

    def deploy(self, bundle, expected):
        self.preflight(bundle, expected)
        before_config = file_hashes(CONFIG)
        before_keys = file_hashes(DATA / 'keyring')
        print('Stopping SecondBrain and taking a private pre-upgrade backup.', flush=True)
        self.stop()
        self.data_lock = os.fdopen(os.open(DATA / '.lock', os.O_RDWR | os.O_NOFOLLOW), 'r+')
        fcntl.flock(self.data_lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        self.run(['cp', '-a', '--reflink=auto', str(DATA), str(self.backup / 'data')])
        self.run(['cp', '-a', '--reflink=auto', str(CONFIG), str(self.backup / 'config')])
        (self.backup / 'units').mkdir()
        for name in UNIT_NAMES:
            self.run(['cp', '-a', str(UNITS / name), str(self.backup / 'units' / name)])
        inbox = INCOMING / 'inbox'
        if inbox.is_dir():
            self.run(['cp', '-a', '--reflink=auto', str(inbox), str(self.backup / 'inbox')])
        before = identity_snapshot(self.backup / 'data')
        self.write('identity-before.json', before)
        self.snapshotted = True
        self.write('rollback.json', {'previous_binaries': str(self.previous), 'backup': str(self.backup), 'incoming': str(INCOMING)})

        INSTALL.rename(self.previous)
        self.swapped = True
        self.staged.rename(INSTALL)
        self.run(['restorecon', '-RF', str(INSTALL)])
        self.run([INSTALL / 'brain', 'init', '--provision', '--deployment', 'systemd', '--templates', self.repo / 'deploy', '--daemon-uid', '1654', '--daemon-gid', '1654', '--extractor-uid', '1655', '--sync-uid', '1656', '--skip-users', '--json'])
        # Provisioning preserves existing files, so explicitly upgrade only the three base units.
        # Host drop-ins (including the Tailscale peer allowance) are retained.
        for name in UNIT_NAMES:
            self.run(['install', '-o', 'root', '-g', 'root', '-m', '0644', self.repo / 'deploy/systemd' / name, UNITS / name])
        self.run(['restorecon', '-RF', str(CONFIG), str(DATA), str(INCOMING), *[str(UNITS / n) for n in UNIT_NAMES]])
        self.run(['systemd-analyze', 'verify', *[str(UNITS / n) for n in UNIT_NAMES]])
        self.run(['systemctl', 'daemon-reload'])
        self.run(['systemctl', 'reset-failed', *UNIT_NAMES])
        self.data_lock.close()
        self.data_lock = None
        self.run(['systemctl', 'start', 'secondbrain-extractor.socket', 'secondbrain.service'])
        print('M1 installed; waiting for stores, extractor, provider roles, and egress readiness.', flush=True)
        readiness = wait_ready()
        require(all(c['ready'] for c in readiness['components'].values()), 'A readiness component is unhealthy.')
        require('Blocked' in readiness['components']['canary']['detail'], 'The enabled egress canary did not prove blocking.')
        verify_schema(DATA)
        after = identity_snapshot(DATA)
        verify_identity(before, after)
        require(before_config == file_hashes(CONFIG), 'Configuration or secrets changed during deployment.')
        after_keys = file_hashes(DATA / 'keyring')
        require(all(after_keys.get(name) == value for name, value in before_keys.items()), 'Existing key-ring material changed during deployment.')
        verify_bundle(INSTALL, expected)
        require(request('/health')[0] == 200, 'Health endpoint failed.')
        require(request('/v1/search', {'query': 'deployment-check', 'mode': 'keyword'})[0] == 401, 'Search does not enforce authentication.')
        require(request('/v1/query', {})[0] == 401, 'Query does not enforce authentication.')
        modes = {str(p): {'uid': p.stat().st_uid, 'gid': p.stat().st_gid, 'mode': oct(stat.S_IMODE(p.stat().st_mode))} for p in [DATA, INCOMING, INCOMING / 'inbox', INCOMING / 'inbox/.claimed']}
        require(modes[str(INCOMING / 'inbox')]['mode'] == '0o1770' and modes[str(INCOMING / 'inbox/.claimed')]['mode'] == '0o700', 'M1 inbox permissions are incorrect.')
        report = {'status': 'deployed', 'at_utc': datetime.datetime.now(datetime.timezone.utc).isoformat(), 'backup': str(self.backup), 'previous_binaries': str(self.previous), 'bundle_files_verified': len(expected), 'readiness': readiness, 'm1_schema': 'applied', 'account_and_credentials_preserved': True, 'configuration_and_secrets_preserved': True, 'keyring_preserved': True, 'existing_documents_preserved': len(before['documents']), 'directory_modes': modes, 'unauthenticated_search_and_query': 401, 'units': {}}
        for name in UNIT_NAMES:
            result = subprocess.check_output(['systemctl', 'show', name, '--property=ActiveState', '--property=SubState', '--property=MainPID', '--property=ExecMainStartTimestamp', '--property=MemoryMax', '--property=PrivateNetwork'], text=True)
            report['units'][name] = dict(line.split('=', 1) for line in result.splitlines() if '=' in line)
        self.write('deployment-result.json', report)
        self.write('deployment-result.json', report, public=True)
        self.done = True
        print('M1 deployed and ready. Existing account, credentials, configuration, key ring, and document identities verified.', flush=True)
        print('Private rollback backup: ' + str(self.backup), flush=True)

    def rollback(self):
        if not self.stopped or self.done:
            return
        if getattr(self, 'data_lock', None) is not None:
            self.data_lock.close()
            self.data_lock = None
        print('Deployment failed; restoring the previous installation. New M1 data will be retained privately.', flush=True)
        self.run(['systemctl', 'stop', *UNIT_NAMES])
        if self.swapped:
            failed_binary = INSTALL.with_name('secondbrain.failed-m1-' + self.tag)
            if INSTALL.exists():
                INSTALL.rename(failed_binary)
            self.previous.rename(INSTALL)
        if self.snapshotted:
            failed_data = DATA.with_name('secondbrain.failed-m1-' + self.tag)
            DATA.rename(failed_data)
            self.run(['cp', '-a', '--reflink=auto', str(self.backup / 'data'), str(DATA)])
            self.run(['cp', '-a', str(self.backup / 'config') + '/.', str(CONFIG)])
            for name in UNIT_NAMES:
                self.run(['cp', '-a', str(self.backup / 'units' / name), str(UNITS / name)])
            # Recover consumed pre-upgrade inbox entries without replacing newly delivered files.
            if (self.backup / 'inbox').is_dir():
                self.run(['cp', '-an', str(self.backup / 'inbox') + '/.', str(INCOMING / 'inbox')])
        self.run(['restorecon', '-RF', str(INSTALL), str(DATA), str(CONFIG)])
        self.run(['systemctl', 'daemon-reload'])
        self.run(['systemctl', 'reset-failed', *UNIT_NAMES])
        self.run(['systemctl', 'start', 'secondbrain-extractor.socket', 'secondbrain.service'])
        report = {'status': 'rolled_back', 'backup': str(self.backup), 'new_data_retained': str(DATA.with_name('secondbrain.failed-m1-' + self.tag)) if self.snapshotted else None}
        self.write('deployment-result.json', report, public=True)
        print('Previous installation restarted. Private recovery files: ' + str(self.backup), flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('bundle', type=pathlib.Path)
    parser.add_argument('manifest', type=pathlib.Path)
    parser.add_argument('--evidence-dir', type=pathlib.Path, required=True)
    args = parser.parse_args()
    require(os.geteuid() == 0, 'Root is required for the installed systemd upgrade.')
    repo = pathlib.Path(__file__).resolve().parents[2]
    evidence = args.evidence_dir.resolve()
    require(evidence.is_relative_to(repo / 'artifacts'), 'Sanitized evidence must be inside this repository\'s artifacts directory.')
    evidence.mkdir(parents=True, exist_ok=True)
    os.umask(0o077)
    with open('/run/lock/secondbrain-upgrade.lock', 'w') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        upgrade = Upgrade(repo, evidence)
        try:
            expected = json.loads(args.manifest.resolve().read_text())
            upgrade.deploy(args.bundle.resolve(), expected)
        except BaseException as error:
            traceback.print_exception(error, file=upgrade.log)
            upgrade.log.flush()
            try:
                upgrade.write('deployment-result.json', {'status': 'failed', 'phase': upgrade.phase, 'failure_type': type(error).__name__, 'backup': str(upgrade.backup)}, public=True)
            except OSError:
                pass
            try:
                upgrade.rollback()
            except BaseException:
                print('Automatic rollback could not finish; inspect the private backup before further changes: ' + str(upgrade.backup), file=sys.stderr)
            if upgrade.staged.exists():
                shutil.rmtree(upgrade.staged)
            print('Upgrade did not complete. Private diagnostic log: ' + str(upgrade.backup / 'deployment.log'), file=sys.stderr)
            return 1
        finally:
            upgrade.log.close()
    return 0


if __name__ == '__main__':
    try:
        sys.exit(main())
    except (RuntimeError, OSError):
        print('Upgrade preflight failed before service changes; check root access, arguments, and reference installation paths.', file=sys.stderr)
        sys.exit(1)
