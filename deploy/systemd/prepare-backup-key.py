#!/usr/bin/env python3
"""Prepare the reference backup key and a private operator recovery copy; never print key bytes."""
import argparse
import hmac
import json
import os
import pathlib
import pwd
import secrets
import stat
import tempfile


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def directory(path):
    require(path.is_absolute() and path.resolve(strict=True) == path, 'Directory must exist without symlink components.')
    value = path.stat()
    require(stat.S_ISDIR(value.st_mode) and value.st_mode & 0o022 == 0, 'Directory must not be writable by group or others.')


def read_key(path, owners):
    descriptor = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC | os.O_NONBLOCK)
    with os.fdopen(descriptor, 'rb') as stream:
        value = os.fstat(stream.fileno())
        require(stat.S_ISREG(value.st_mode) and value.st_nlink == 1 and value.st_size == 32
                and stat.S_IMODE(value.st_mode) == 0o600 and value.st_uid in owners,
                'Existing key must have one link, 32 bytes, private permissions and the expected owner.')
        content = stream.read(33)
        after = os.fstat(stream.fileno())
        require(len(content) == 32 and (value.st_ino, value.st_size, value.st_mtime_ns, value.st_ctime_ns)
                == (after.st_ino, after.st_size, after.st_mtime_ns, after.st_ctime_ns), 'Key changed during verification.')
        return content


def publish_new(path, content, uid, gid):
    descriptor, temporary = tempfile.mkstemp(prefix='.backup-key-', dir=path.parent)
    try:
        with os.fdopen(descriptor, 'wb') as stream:
            os.fchmod(stream.fileno(), 0o600)
            stream.write(content)
            stream.flush()
            os.fchown(stream.fileno(), uid, gid)
            os.fsync(stream.fileno())
        # Link is exclusive: an existing destination is never replaced, even in a race.
        os.link(temporary, path, follow_symlinks=False)
    finally:
        os.unlink(temporary)
        descriptor = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        try:
            os.fsync(descriptor)
        finally:
            os.close(descriptor)


def prepare(key, recovery, daemon_uid, daemon_gid, operator_uid, operator_gid):
    require(daemon_uid != 0 and operator_uid != 0, 'Use non-root daemon and recovery-owner accounts.')
    directory(key.parent)
    directory(recovery.parent)
    require(key.parent.stat().st_uid in (0, daemon_uid), 'Unexpected secret-directory owner.')
    require(recovery.parent.stat().st_uid == operator_uid
            and stat.S_IMODE(recovery.parent.stat().st_mode) == 0o700, 'Recovery directory must be private to the operator.')
    require(key.parent != recovery.parent and not recovery.is_relative_to(key.parent), 'Recovery copy must be outside the secret directory.')
    key_exists = os.path.lexists(key)
    recovery_exists = os.path.lexists(recovery)
    require(key_exists or not recovery_exists, 'A recovery copy exists without a server key; recover it explicitly rather than generating a replacement.')
    value = read_key(key, {0, daemon_uid}) if key_exists else secrets.token_bytes(32)
    if recovery_exists:
        require(hmac.compare_digest(value, read_key(recovery, {operator_uid})), 'Existing recovery copy differs; neither file was replaced.')
    if not key_exists:
        publish_new(key, value, daemon_uid, daemon_gid)
    if not recovery_exists:
        publish_new(recovery, value, operator_uid, operator_gid)
    require(hmac.compare_digest(read_key(key, {0, daemon_uid}), read_key(recovery, {operator_uid})), 'Recovery verification failed.')
    return {'status': 'prepared', 'created': not key_exists, 'keyFile': str(key), 'recoveryCopy': str(recovery),
            'matchingPrivateCopies': True, 'keyRotated': False, 'offServerCustodyConfirmed': False}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--apply', action='store_true')
    parser.add_argument('--recovery-owner', required=True, help='Existing normal account that will store the recovery copy off-server.')
    args = parser.parse_args()
    daemon = pwd.getpwnam('secondbrain')
    operator = pwd.getpwnam(args.recovery_owner)
    key = pathlib.Path('/etc/secondbrain/secrets/backup.key')
    recovery_dir = pathlib.Path('/var/lib/secondbrain-recovery')
    recovery = recovery_dir / 'backup.key'
    if not args.apply:
        print(json.dumps({'status': 'plan', 'keyFile': str(key), 'recoveryCopy': str(recovery),
                          'recoveryOwner': operator.pw_name, 'existingKeys': 'verified, never rotated or overwritten',
                          'serviceOrConfigurationChanges': False, 'nextStep': 'Store the recovery copy off-server and confirm custody.'}, indent=2))
        return
    require(os.geteuid() == 0, 'Run --apply as root after reviewing the script and its tests.')
    require(operator.pw_uid != 0 and operator.pw_uid != daemon.pw_uid, 'Recovery owner must be a distinct normal account.')
    directory(recovery_dir.parent)
    require(recovery_dir.parent.stat().st_uid == 0, 'Recovery parent must be root-owned.')
    if not os.path.lexists(recovery_dir):
        recovery_dir.mkdir(mode=0o700)
        os.chown(recovery_dir, operator.pw_uid, operator.pw_gid)
    result = prepare(key, recovery, daemon.pw_uid, daemon.pw_gid, operator.pw_uid, operator.pw_gid)
    print(json.dumps(result, indent=2))


if __name__ == '__main__':
    main()
