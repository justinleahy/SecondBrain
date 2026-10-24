"""Unprivileged checks for the reference installation upgrade's safeguards."""
import importlib.util
import json
import pathlib
import shutil
import sqlite3
import subprocess
import tempfile
import unittest
from unittest import mock


spec = importlib.util.spec_from_file_location('upgrade_m1', pathlib.Path(__file__).parents[1] / 'systemd/upgrade-m1.py')
upgrade = importlib.util.module_from_spec(spec)
spec.loader.exec_module(upgrade)


class UpgradeTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = pathlib.Path(self.temp.name)

    def bundle(self):
        root = self.root / 'bundle'
        root.mkdir()
        for name in upgrade.APPHOSTS:
            (root / name).write_text('binary ' + name)
            (root / name).chmod(0o755)
        (root / 'SecondBrain.Server.staticwebassets.endpoints.json').write_text('{}')
        (root / 'wwwroot').mkdir()
        return root, upgrade.file_hashes(root)

    def test_backup_creation_handles_missing_parent_directory(self):
        base = self.root / 'missing-parent' / 'secondbrain'
        with mock.patch.object(upgrade, 'BACKUP_BASE', base):
            runner = upgrade.Upgrade(self.root, self.root)
        self.addCleanup(runner.log.close)
        self.assertTrue(runner.backup.is_dir())
        self.assertEqual(base.stat().st_mode & 0o777, 0o700)
        self.assertEqual((runner.backup / 'deployment.log').stat().st_mode & 0o777, 0o600)

    def test_bundle_rejects_modified_extra_and_symlinked_content(self):
        root, hashes = self.bundle()
        upgrade.verify_bundle(root, hashes)
        (root / 'extra').write_text('unexpected')
        with self.assertRaisesRegex(RuntimeError, 'manifest'):
            upgrade.verify_bundle(root, hashes)
        (root / 'extra').unlink()
        (root / 'brain').write_text('changed')
        with self.assertRaisesRegex(RuntimeError, 'manifest'):
            upgrade.verify_bundle(root, hashes)
        (root / 'brain').unlink()
        (root / 'brain').symlink_to(root / 'SecondBrain.Server')
        with self.assertRaisesRegex(RuntimeError, 'symlink'):
            upgrade.verify_bundle(root, hashes)

    def test_manifest_cannot_escape_bundle(self):
        root, hashes = self.bundle()
        hashes['../outside'] = 'unexpected'
        with self.assertRaisesRegex(RuntimeError, 'manifest path'):
            upgrade.verify_bundle(root, hashes)

    def test_authentication_probe_uses_get_without_request_body(self):
        def unauthenticated(request, timeout):
            self.assertEqual(request.get_method(), 'GET')
            self.assertIsNone(request.data)
            raise upgrade.urllib.error.HTTPError(request.full_url, 401, 'Authentication required', {}, None)
        with mock.patch.object(upgrade.urllib.request, 'urlopen', side_effect=unauthenticated):
            self.assertEqual(upgrade.request('/v1/jobs'), (401, None))

    def test_identity_allows_new_documents_but_preserves_account_keys_and_existing_documents(self):
        data = self.root / 'data'
        (data / 'state').mkdir(parents=True)
        with sqlite3.connect(data / 'state/state.db') as db:
            db.executescript('''
                CREATE TABLE account(id INTEGER PRIMARY KEY, password_hash TEXT);
                INSERT INTO account VALUES(1,'private-test-hash');
                CREATE TABLE meta(key TEXT PRIMARY KEY,value TEXT);
                INSERT INTO meta VALUES('account_epoch','1'),('instance_id','test-instance');
                CREATE TABLE credentials(id TEXT,kind TEXT,verifier TEXT,scopes TEXT,generation INTEGER,kid TEXT,account_epoch INTEGER,revoked_at TEXT);
                INSERT INTO credentials VALUES('key','api_key','private-test-verifier','read',1,'kid',1,NULL);
                CREATE TABLE documents(id TEXT PRIMARY KEY);
                INSERT INTO documents VALUES('old-document');
            ''')
        before = upgrade.identity_snapshot(data)
        self.assertNotIn('private-test-verifier', json.dumps(before))
        self.assertNotIn('private-test-hash', json.dumps(before))
        with sqlite3.connect(data / 'state/state.db') as db:
            db.execute("INSERT INTO documents VALUES('new-document')")
        upgrade.verify_identity(before, upgrade.identity_snapshot(data))
        with sqlite3.connect(data / 'state/state.db') as db:
            db.execute("UPDATE credentials SET generation=2")
        with self.assertRaisesRegex(RuntimeError, 'credential'):
            upgrade.verify_identity(before, upgrade.identity_snapshot(data))

    def test_failed_upgrade_restores_old_files_and_retains_new_data(self):
        data, install, config, incoming, units = [self.root / name for name in ['srv/data','lib/install','etc/config','srv/incoming','etc/units']]
        for path in [data, install, config, incoming / 'inbox', units]:
            path.mkdir(parents=True)
        (data / 'new-content').write_text('preserve this new write')
        (install / 'binary').write_text('M1')
        (config / 'config.yaml').write_text('current-config')
        runner = upgrade.Upgrade.__new__(upgrade.Upgrade)
        runner.tag = 'test'
        runner.backup = self.root / 'backup'
        runner.backup.mkdir()
        runner.evidence = self.root / 'evidence'
        runner.evidence.mkdir()
        runner.previous = self.root / 'old-install'
        runner.previous.mkdir()
        (runner.previous / 'binary').write_text('M0')
        for name in ['data','config','units','inbox']:
            (runner.backup / name).mkdir()
        (runner.backup / 'data/old-content').write_text('old data')
        (runner.backup / 'config/config.yaml').write_text('old config')
        (runner.backup / 'inbox/consumed.txt').write_text('original input')
        (incoming / 'inbox/new.txt').write_text('new input')
        for name in upgrade.UNIT_NAMES:
            (runner.backup / 'units' / name).write_text('old unit')
            (units / name).write_text('new unit')
        runner.stopped = runner.snapshotted = runner.swapped = True
        runner.done = False
        calls = []
        def run(args):
            calls.append([str(x) for x in args])
            if str(args[0]) == 'cp':
                subprocess.run(args, check=True, capture_output=True)
        runner.run = run
        with mock.patch.multiple(upgrade, DATA=data, INSTALL=install, CONFIG=config, INCOMING=incoming, UNITS=units):
            runner.rollback()
        self.assertEqual((install / 'binary').read_text(), 'M0')
        self.assertEqual((data / 'old-content').read_text(), 'old data')
        self.assertEqual((data.parent / 'secondbrain.failed-m1-test/new-content').read_text(), 'preserve this new write')
        self.assertEqual((incoming / 'inbox/new.txt').read_text(), 'new input')
        self.assertEqual((incoming / 'inbox/consumed.txt').read_text(), 'original input')
        self.assertEqual((config / 'config.yaml').read_text(), 'old config')
        self.assertIn(['systemctl','start','secondbrain-extractor.socket','secondbrain.service'], calls)

    def test_partial_stop_failure_is_eligible_for_recovery(self):
        runner = upgrade.Upgrade.__new__(upgrade.Upgrade)
        runner.stopped = False
        runner.run = mock.Mock(side_effect=RuntimeError('stop failed'))
        with self.assertRaises(RuntimeError):
            runner.stop()
        self.assertTrue(runner.stopped)


if __name__ == '__main__':
    unittest.main()
