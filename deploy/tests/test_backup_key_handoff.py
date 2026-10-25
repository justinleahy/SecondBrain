"""Unprivileged backup-key handoff checks; never use live secrets or services."""
import importlib.util
import os
import pathlib
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('backup_handoff', pathlib.Path(__file__).parents[1] / 'systemd/prepare-backup-key.py')
handoff = importlib.util.module_from_spec(spec)
spec.loader.exec_module(handoff)


class BackupHandoffTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = pathlib.Path(self.temporary.name)
        for name in ['secrets', 'recovery']:
            (self.root / name).mkdir(mode=0o700)
        self.key = self.root / 'secrets/backup.key'
        self.copy = self.root / 'recovery/backup.key'

    def prepare(self):
        return handoff.prepare(self.key, self.copy, os.geteuid(), os.getegid(), os.geteuid(), os.getegid())

    def test_creates_matching_private_copies_and_repeat_never_rotates(self):
        self.assertTrue(self.prepare()['created'])
        original = self.key.read_bytes()
        self.assertEqual(len(original), 32)
        self.assertEqual(self.copy.read_bytes(), original)
        self.assertEqual(self.key.stat().st_mode & 0o777, 0o600)
        self.assertEqual(self.copy.stat().st_mode & 0o777, 0o600)
        self.assertFalse(self.prepare()['created'])
        self.assertEqual(self.key.read_bytes(), original)

    def test_missing_copy_reuses_existing_key(self):
        self.prepare()
        original = self.key.read_bytes()
        self.copy.unlink()
        self.assertFalse(self.prepare()['created'])
        self.assertEqual(self.copy.read_bytes(), original)

    def test_mismatch_never_replaces_either_file(self):
        self.prepare()
        original = self.key.read_bytes()
        self.copy.write_bytes(b'x' * 32)
        with self.assertRaisesRegex(RuntimeError, 'differs'):
            self.prepare()
        self.assertEqual(self.key.read_bytes(), original)
        self.assertEqual(self.copy.read_bytes(), b'x' * 32)

    def test_orphan_recovery_does_not_generate_another_key(self):
        self.copy.write_bytes(b'x' * 32)
        self.copy.chmod(0o600)
        with self.assertRaisesRegex(RuntimeError, 'without a server key'):
            self.prepare()
        self.assertFalse(self.key.exists())

    def test_symlink_and_hardlink_are_rejected(self):
        self.prepare()
        self.copy.unlink()
        self.copy.symlink_to(self.key)
        with self.assertRaises(OSError):
            self.prepare()
        self.copy.unlink()
        os.link(self.key, self.copy)
        with self.assertRaisesRegex(RuntimeError, 'one link'):
            self.prepare()

    def test_public_file_and_wrong_owner_are_rejected(self):
        self.prepare()
        self.key.chmod(0o644)
        with self.assertRaisesRegex(RuntimeError, 'private permissions'):
            self.prepare()
        self.key.chmod(0o600)
        with self.assertRaisesRegex(RuntimeError, 'expected owner'):
            handoff.read_key(self.key, {os.geteuid() + 10000})

    def test_symlinked_parent_and_public_recovery_directory_are_rejected(self):
        alias = self.root / 'alias'
        alias.symlink_to(self.key.parent, target_is_directory=True)
        with self.assertRaisesRegex(RuntimeError, 'symlink components'):
            handoff.prepare(alias / 'backup.key', self.copy, os.geteuid(), os.getegid(), os.geteuid(), os.getegid())
        self.copy.parent.chmod(0o755)
        with self.assertRaisesRegex(RuntimeError, 'private to the operator'):
            self.prepare()

    def test_atomic_publication_does_not_overwrite_racing_destination(self):
        self.key.write_bytes(b'original')
        with self.assertRaises(FileExistsError):
            handoff.publish_new(self.key, b'x' * 32, os.geteuid(), os.getegid())
        self.assertEqual(self.key.read_bytes(), b'original')
        self.assertEqual(list(self.key.parent.glob('.backup-key-*')), [])


if __name__ == '__main__':
    unittest.main()
