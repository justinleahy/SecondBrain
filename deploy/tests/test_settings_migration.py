"""Run unprivileged; all migration paths are temporary and no services are touched."""
import importlib.util
import os
import pathlib
import tempfile
import unittest
from unittest import mock

spec = importlib.util.spec_from_file_location('enable_settings', pathlib.Path(__file__).parents[1] / 'systemd/enable-settings.py')
settings = importlib.util.module_from_spec(spec)
spec.loader.exec_module(settings)


class SettingsMigrationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = pathlib.Path(self.temp.name)
        self.config = self.root / 'etc'
        self.config.mkdir(mode=0o750)
        self.original = b'privacy:\n  local_only: true\n# retained formatting\n'
        (self.config / 'config.yaml').write_bytes(self.original)
        self.dropins = self.root / 'units'
        self.backup = self.root / 'backup'
        self.owner = (os.geteuid() or 1654, os.getegid() or 1654)

    def apply(self):
        return settings.prepare(self.config, self.dropins, self.backup, *self.owner)

    def test_preserves_bytes_secrets_private_modes_and_narrow_write_path(self):
        secret = self.config / 'secrets'
        secret.mkdir(mode=0o700)
        (secret / 'MODEL_KEY').write_bytes(b'synthetic-private-secret')
        result = self.apply()
        self.assertFalse(result['already_linked'])
        self.assertEqual(os.readlink(self.config / 'config.yaml'), 'settings/config.yaml')
        self.assertEqual((self.config / 'config.yaml').read_bytes(), self.original)
        self.assertEqual((self.config / 'settings').stat().st_mode & 0o777, 0o700)
        self.assertEqual((self.config / 'settings/config.yaml').stat().st_mode & 0o777, 0o600)
        self.assertEqual((secret / 'MODEL_KEY').read_bytes(), b'synthetic-private-secret')
        copies = list(self.backup.glob('config-*.yaml'))
        self.assertEqual(len(copies), 1)
        self.assertEqual(copies[0].read_bytes(), self.original)
        self.assertEqual(copies[0].stat().st_mode & 0o777, 0o600)
        self.assertEqual((self.dropins / '20-settings.conf').read_text(), '[Service]\nReadWritePaths=' + str(self.config / 'settings') + '\n')

    def test_repeat_preserves_later_configuration_changes(self):
        self.apply()
        updated = self.original + b'# subsequent admin edit\n'
        (self.config / 'config.yaml').write_bytes(updated)
        self.assertTrue(self.apply()['already_linked'])
        self.assertEqual((self.config / 'config.yaml').read_bytes(), updated)
        self.assertEqual(len(list(self.backup.glob('config-*.yaml'))), 1)

    def test_refuses_unexpected_symlink_without_touching_its_target(self):
        outside = self.root / 'outside'
        outside.write_bytes(self.original)
        (self.config / 'config.yaml').unlink()
        (self.config / 'config.yaml').symlink_to(outside)
        with self.assertRaisesRegex(RuntimeError, 'Unexpected configuration link'):
            self.apply()
        self.assertEqual(outside.read_bytes(), self.original)
        self.assertFalse(self.dropins.exists())

    def test_refuses_conflicting_writable_configuration(self):
        (self.config / 'settings').mkdir(mode=0o700)
        (self.config / 'settings/config.yaml').write_bytes(b'different')
        with self.assertRaisesRegex(RuntimeError, 'different contents'):
            self.apply()
        self.assertFalse((self.config / 'config.yaml').is_symlink())
        self.assertEqual((self.config / 'config.yaml').read_bytes(), self.original)

    def test_refuses_a_hard_link(self):
        os.link(self.config / 'config.yaml', self.root / 'second-link')
        with self.assertRaisesRegex(RuntimeError, 'one hard link'):
            self.apply()
        self.assertFalse(self.backup.exists())

    def test_dropin_failure_leaves_original_config_in_place(self):
        with mock.patch.object(settings, 'atomic_file', side_effect=OSError('injected')):
            with self.assertRaisesRegex(OSError, 'injected'):
                self.apply()
        self.assertFalse((self.config / 'config.yaml').is_symlink())
        self.assertEqual((self.config / 'config.yaml').read_bytes(), self.original)

    def test_compose_migration_does_not_create_service_units(self):
        result = settings.prepare(self.config, None, self.backup, *self.owner)
        self.assertIsNone(result['dropin'])
        self.assertFalse(self.dropins.exists())
        self.assertEqual((self.config / 'settings/config.yaml').read_bytes(), self.original)


if __name__ == '__main__':
    unittest.main()
