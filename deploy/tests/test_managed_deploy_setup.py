"""Checks the exact privilege grant and fixed service command before installation."""
import importlib.util
import pathlib
import tempfile
import unittest


spec = importlib.util.spec_from_file_location('managed_setup', pathlib.Path(__file__).parents[1] / 'systemd/install-managed-deploy.py')
setup = importlib.util.module_from_spec(spec)
spec.loader.exec_module(setup)


class ManagedSetupTests(unittest.TestCase):
    def test_sudoers_grants_only_the_exact_service_start(self):
        self.assertEqual(setup.policy_text('example'),
                         'example ALL=(root) NOPASSWD: /usr/bin/systemctl start secondbrain-deploy.service\n')
        for name in ['root ALL=ALL', 'example\nroot', '%wheel', 'example*']:
            with self.assertRaises(ValueError):
                setup.policy_text(name)

    def test_service_uses_frozen_helper_and_isolated_python(self):
        service = setup.service_text(pathlib.Path('/home/example/Second Brain'), 1000, 1000)
        self.assertIn('/usr/bin/python3 -I /usr/local/libexec/secondbrain-deploy/upgrade.py', service)
        self.assertIn('--release chat --managed', service)
        self.assertIn('--report-uid 1000 --report-gid 1000', service)
        self.assertIn('WorkingDirectory=/', service)
        self.assertNotIn('/deploy/systemd/upgrade-m1.py', service)
        self.assertIn('"/home/example/Second Brain/artifacts/auto-deploy/merged"', service)

    def test_paths_escape_systemd_specifiers_and_reject_control_characters(self):
        self.assertEqual(setup.quoted('/tmp/a%h"b'), '"/tmp/a%%h\\"b"')
        for path in ['/tmp/a\nb', '/tmp/a\rb', '/tmp/a\x00b']:
            with self.assertRaises(ValueError):
                setup.quoted(path)


if __name__ == '__main__':
    unittest.main()
