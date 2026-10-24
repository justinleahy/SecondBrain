#!/usr/bin/env python3
"""One-time root installation of a fixed-purpose SecondBrain deployment service."""
import os
import pathlib
import pwd
import re
import stat
import subprocess
import tempfile


HELPER_DIR = pathlib.Path('/usr/local/libexec/secondbrain-deploy')
UNIT = pathlib.Path('/etc/systemd/system/secondbrain-deploy.service')
POLICY = pathlib.Path('/etc/sudoers.d/secondbrain-deploy')


def quoted(value):
    text = str(value)
    if any(ord(c) < 32 for c in text):
        raise ValueError('Control characters are not allowed in deployment paths.')
    return '"' + text.replace('\\', '\\\\').replace('"', '\\"').replace('%', '%%') + '"'


def service_text(workspace, uid, gid):
    inputs = workspace / 'artifacts/auto-deploy'
    command = ' '.join([
        '/usr/bin/python3', '-I', str(HELPER_DIR / 'upgrade.py'),
        quoted(inputs / 'merged'), quoted(inputs / 'sha256-manifest.json'),
        '--release', 'chat', '--managed', '--workspace', quoted(workspace),
        '--report-uid', str(uid), '--report-gid', str(gid),
        '--evidence-dir', quoted(inputs / 'result')])
    return '\n'.join([
        '[Unit]', 'Description=Install the staged SecondBrain application release',
        '[Service]', 'Type=oneshot', 'User=root', 'WorkingDirectory=/', 'UMask=0077',
        'Environment=PATH=/usr/sbin:/usr/bin:/sbin:/bin', 'TimeoutStartSec=300',
        'ExecStart=' + command, ''])


def policy_text(user):
    if not re.fullmatch(r'[a-z_][a-z0-9_-]*[$]?', user):
        raise ValueError('Unsupported local user name for the sudoers rule.')
    return user + ' ALL=(root) NOPASSWD: /usr/bin/systemctl start secondbrain-deploy.service\n'


def secure_directory(path):
    for part in [*reversed(path.parents), path]:
        if not part.exists():
            part.mkdir(mode=0o755)
        metadata = part.lstat()
        if not stat.S_ISDIR(metadata.st_mode) or metadata.st_uid != 0 or metadata.st_mode & 0o022:
            raise RuntimeError('Deployment control directories must be root-owned and not writable by other users.')


def atomic_write(path, content, mode):
    secure_directory(path.parent)
    with tempfile.NamedTemporaryFile(dir=path.parent, prefix='.secondbrain-', delete=False) as stream:
        temporary = pathlib.Path(stream.name)
        try:
            stream.write(content)
            stream.flush()
            os.fsync(stream.fileno())
            temporary.chmod(mode)
            temporary.replace(path)
        finally:
            temporary.unlink(missing_ok=True)


def main():
    if os.geteuid() != 0:
        raise RuntimeError('Run this one-time installer with sudo.')
    user = os.environ.get('SUDO_USER', '')
    account = pwd.getpwnam(user)
    if account.pw_uid == 0 or account.pw_gid == 0:
        raise RuntimeError('Run through sudo from the non-root deployment account.')
    workspace = pathlib.Path(__file__).resolve().parents[2]
    source = workspace / 'deploy/systemd/upgrade-m1.py'
    # Freeze the privileged implementation in a root-only location. Future
    # deployments never import scripts or service units from the workspace.
    helper = source.read_bytes()
    unit = service_text(workspace, account.pw_uid, account.pw_gid).encode()
    policy = policy_text(user).encode()
    with tempfile.NamedTemporaryFile(prefix='secondbrain-sudoers-') as temporary:
        temporary.write(policy)
        temporary.flush()
        subprocess.run(['/usr/sbin/visudo', '-cf', temporary.name], check=True)
    files = {HELPER_DIR / 'upgrade.py': (helper, 0o600), UNIT: (unit, 0o644), POLICY: (policy, 0o440)}
    previous = {}
    for path in files:
        if path.is_symlink():
            raise RuntimeError('Refusing a symlink at a deployment control path.')
        previous[path] = (path.read_bytes(), stat.S_IMODE(path.stat().st_mode)) if path.exists() else None
    try:
        for path, (content, mode) in files.items():
            atomic_write(path, content, mode)
        subprocess.run(['/usr/sbin/restorecon', '-RF', str(HELPER_DIR), str(UNIT), str(POLICY)], check=True)
        subprocess.run(['/usr/bin/systemd-analyze', 'verify', str(UNIT)], check=True)
        subprocess.run(['/usr/bin/systemctl', 'daemon-reload'], check=True)
    except BaseException:
        for path, value in previous.items():
            if value is None:
                path.unlink(missing_ok=True)
            else:
                atomic_write(path, *value)
        subprocess.run(['/usr/bin/systemctl', 'daemon-reload'], check=False)
        raise
    print('Installed fixed-purpose SecondBrain deployment permission for ' + user + '.')
    print('Allowed command: sudo -n /usr/bin/systemctl start secondbrain-deploy.service')
    print('No application code or workspace service unit is executed as root during deployment.')


if __name__ == '__main__':
    main()
