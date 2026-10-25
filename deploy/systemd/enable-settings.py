#!/usr/bin/env python3
"""Move the reference configuration into a private writable directory, preserving its bytes."""
import argparse
import hashlib
import json
import os
import pathlib
import pwd
import shutil
import stat
import subprocess
import tempfile
import uuid


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def regular_bytes(path):
    descriptor = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC)
    with os.fdopen(descriptor, 'rb') as stream:
        metadata = os.fstat(stream.fileno())
        require(stat.S_ISREG(metadata.st_mode) and metadata.st_nlink == 1, 'Configuration must be a regular file with one hard link.')
        require(metadata.st_size <= 1024 * 1024, 'Configuration exceeds the migration size bound.')
        content = stream.read(1024 * 1024 + 1)
        require(len(content) <= 1024 * 1024, 'Configuration grew beyond the migration size bound.')
        return content, metadata


def private_directory(path):
    path.mkdir(mode=0o700, exist_ok=True)
    descriptor = os.open(path, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
    metadata = os.fstat(descriptor)
    os.fchown(descriptor, os.geteuid(), os.getegid())
    os.fchmod(descriptor, 0o700)
    return descriptor, metadata


def atomic_file(path, content, mode=0o600):
    require(not path.is_symlink(), 'Refusing to replace a symbolic link: ' + str(path))
    descriptor, temporary = tempfile.mkstemp(prefix='.' + path.name + '-', dir=path.parent)
    try:
        with os.fdopen(descriptor, 'wb') as stream:
            os.fchmod(stream.fileno(), mode)
            stream.write(content)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        if os.path.lexists(temporary):
            os.unlink(temporary)


def prepare(config_dir, dropin_dir, backup_dir, daemon_uid, daemon_gid):
    """All paths are supplied by the fixed reference layout in main; tests use a temporary tree."""
    require(daemon_uid != 0 and daemon_gid != 0, 'The daemon must use a non-root user and group.')
    require(config_dir.is_dir() and not config_dir.is_symlink(), 'Configuration directory is missing or is a symbolic link.')
    parent = config_dir.stat()
    require(parent.st_uid == os.geteuid() and parent.st_mode & 0o022 == 0, 'Configuration parent must be owned by the operator and not writable by group or others.')
    settings = config_dir / 'settings'
    alias = config_dir / 'config.yaml'
    target = settings / 'config.yaml'
    was_link = alias.is_symlink()
    require(not was_link or os.readlink(alias) == 'settings/config.yaml', 'Unexpected configuration link; migrate it explicitly before proceeding.')
    require(not settings.is_symlink(), 'Settings directory must not be a symbolic link.')
    original, _ = regular_bytes(target if was_link else alias)
    original_hash = hashlib.sha256(original).hexdigest()
    backup_dir.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
    backup_fd, _ = private_directory(backup_dir)
    try:
        if not was_link:
            backup = backup_dir / ('config-' + original_hash + '.yaml')
            if backup.exists():
                require(regular_bytes(backup)[0] == original, 'Existing backup differs from the original.')
            else:
                descriptor = os.open(backup, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
                with os.fdopen(descriptor, 'wb') as stream:
                    stream.write(original); stream.flush(); os.fsync(stream.fileno())
            os.fsync(backup_fd)
    finally:
        os.close(backup_fd)
    settings_existed = settings.exists()
    settings_fd, previous = private_directory(settings)
    handed_over = False
    try:
        if target.exists() or target.is_symlink():
            require(regular_bytes(target)[0] == original, 'Writable configuration already exists with different contents; refusing to overwrite it.')
        else:
            descriptor = os.open(target, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
            with os.fdopen(descriptor, 'wb') as stream:
                stream.write(original); stream.flush(); os.fsync(stream.fileno())
        descriptor = os.open(target, os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC)
        try:
            require(os.fstat(descriptor).st_nlink == 1, 'Writable configuration has multiple hard links.')
            os.fchown(descriptor, daemon_uid, daemon_gid)
            os.fchmod(descriptor, 0o600)
            os.fsync(descriptor)
        finally:
            os.close(descriptor)
        # The root-owned parent prevents the daemon from replacing the stable alias.
        if dropin_dir is not None:
            dropin_dir.mkdir(mode=0o755, parents=True, exist_ok=True)
            require(not dropin_dir.is_symlink() and dropin_dir.stat().st_uid == os.geteuid() and dropin_dir.stat().st_mode & 0o022 == 0, 'Unsafe service drop-in directory.')
            dropin = '[Service]\nReadWritePaths=' + str(settings) + '\n'
            if (dropin_dir / '20-settings.conf').exists():
                require(regular_bytes(dropin_dir / '20-settings.conf')[0] == dropin.encode(), 'An existing settings drop-in differs; review it before migration.')
            atomic_file(dropin_dir / '20-settings.conf', dropin.encode(), 0o644)
            directory = os.open(dropin_dir, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
            try: os.fsync(directory)
            finally: os.close(directory)
        os.fsync(settings_fd)
        os.fchown(settings_fd, daemon_uid, daemon_gid)
        os.fsync(settings_fd)
        handed_over = True
        if not was_link:
            require(regular_bytes(alias)[0] == original, 'Configuration changed during migration.')
            temporary = config_dir / ('.config-link-' + uuid.uuid4().hex)
            try:
                os.symlink('settings/config.yaml', temporary)
                os.replace(temporary, alias)
            finally:
                if os.path.lexists(temporary):
                    temporary.unlink()
        directory = os.open(config_dir, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        try: os.fsync(directory)
        finally: os.close(directory)
    finally:
        if not handed_over and settings_existed:
            os.fchown(settings_fd, previous.st_uid, previous.st_gid)
            os.fchmod(settings_fd, stat.S_IMODE(previous.st_mode))
        os.close(settings_fd)
    require(regular_bytes(target)[0] == original, 'Configuration changed during migration; inspect the private backup before retrying.')
    return {'status': 'prepared', 'already_linked': was_link, 'sha256': original_hash,
            'config': str(alias), 'writable_directory': str(settings), 'dropin': str(dropin_dir / '20-settings.conf') if dropin_dir is not None else None,
            'backup_directory': str(backup_dir), 'secrets_changed': False}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--apply', action='store_true', help='Stop the reference daemon, migrate its config, reload the unit, and restart it if it was running.')
    parser.add_argument('--compose-config-directory', type=pathlib.Path, help='Migrate an existing Compose config directory instead. Stop its daemon container first; no system services are changed.')
    args = parser.parse_args()
    if not args.apply:
        directory = args.compose_config_directory or pathlib.Path('/etc/secondbrain')
        print(json.dumps({'status': 'plan', 'config': str(directory / 'config.yaml'), 'target': str(directory / 'settings/config.yaml'),
                          'backup': '/var/backups/secondbrain-settings', 'unit': None if args.compose_config_directory else 'secondbrain.service',
                          'secrets': 'unchanged and read-only', 'apply': 'Requires root; preserves configuration bytes.'}, indent=2))
        return
    require(os.geteuid() == 0, 'Run --apply as root after reviewing this script and its tests.')
    if args.compose_config_directory is not None:
        directory = args.compose_config_directory
        require(directory.is_absolute() and directory.resolve() == directory, 'Compose configuration directory must be an absolute path without symlink components.')
        result = prepare(directory, None, pathlib.Path('/var/backups/secondbrain-settings') / ('compose-' + hashlib.sha256(str(directory).encode()).hexdigest()[:12]), 1654, 1654)
        print(json.dumps(result, indent=2))
        return
    account = pwd.getpwnam('secondbrain')
    environment = {'PATH': '/usr/sbin:/usr/bin:/sbin:/bin'}
    def systemctl(*arguments, check=True):
        return subprocess.run(['/usr/bin/systemctl', *arguments], env=environment, check=check, capture_output=True, text=True)
    running = systemctl('is-active', '--quiet', 'secondbrain.service', check=False).returncode == 0
    if running: systemctl('stop', 'secondbrain.service')
    try:
        result = prepare(pathlib.Path('/etc/secondbrain'), pathlib.Path('/etc/systemd/system/secondbrain.service.d'),
                         pathlib.Path('/var/backups/secondbrain-settings'), account.pw_uid, account.pw_gid)
        restorecon = shutil.which('restorecon', path=environment['PATH'])
        if restorecon:
            subprocess.run([restorecon, '-RF', result['writable_directory'], result['dropin']], env=environment, check=True, capture_output=True)
        systemctl('daemon-reload')
    finally:
        if running: systemctl('start', 'secondbrain.service')
    print(json.dumps(result, indent=2))


if __name__ == '__main__':
    main()
