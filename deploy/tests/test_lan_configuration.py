import importlib.util
import os
import pathlib
import tempfile
import unittest


spec = importlib.util.spec_from_file_location('configure_lan', pathlib.Path(__file__).parents[1] / 'systemd/configure-lan.py')
lan = importlib.util.module_from_spec(spec)
spec.loader.exec_module(lan)


class LanConfigurationTests(unittest.TestCase):
    def fixture(self):
        return {'data_root': '/srv/secondbrain', 'providers': {'local': {'base_url': 'http://127.0.0.1:8000', 'api_key': '${SECRET}'}},
                'server': {'listeners': [{'scheme': 'http', 'bind': '127.0.0.1', 'port': 7171}, {'scheme': 'https', 'bind': '100.67.195.36', 'port': 7443, 'certificate': '/private/tls.crt'}],
                           'hosts': ['localhost'], 'origins': ['http://localhost:7171'], 'trusted_proxies': ['127.0.0.1']}}

    def test_only_adds_lan_listener_host_and_origin(self):
        original = self.fixture()
        result, origin, _, _ = lan.configuration(original, '10.66.10.20', '10.66.10.0/24', 7171)
        self.assertEqual(len(original['server']['listeners']), 2)
        self.assertEqual(result['server']['listeners'][:2], original['server']['listeners'])
        self.assertEqual(result['providers'], original['providers'])
        self.assertEqual(result['server']['trusted_proxies'], original['server']['trusted_proxies'])
        self.assertEqual(origin, 'http://10.66.10.20:7171')
        self.assertIn('10.66.10.20', result['server']['hosts'])
        self.assertIn(origin, result['server']['origins'])

    def test_configuration_is_idempotent(self):
        first, _, _, _ = lan.configuration(self.fixture(), '10.66.10.20', '10.66.10.0/24', 7171)
        second, _, _, _ = lan.configuration(first, '10.66.10.20', '10.66.10.0/24', 7171)
        self.assertEqual(first, second)

    def test_public_loopback_and_overbroad_networks_are_rejected(self):
        for address, network in [('8.8.8.8','8.8.8.0/24'), ('127.0.0.1','127.0.0.0/8'), ('10.66.10.20','0.0.0.0/0'), ('192.0.2.1','192.0.2.0/24')]:
            with self.subTest(address=address, network=network), self.assertRaises(RuntimeError):
                lan.configuration(self.fixture(), address, network, 7171)

    def test_firewall_keeps_replies_but_drops_new_daemon_connections(self):
        _, _, rules, rich = lan.configuration(self.fixture(), '10.66.10.20', '10.66.10.0/24', 7171)
        direct = rules[0]
        self.assertIn('--uid-owner', direct)
        self.assertEqual(direct[direct.index('--uid-owner') + 1], '1654')
        self.assertEqual(direct[direct.index('--ctstate') + 1], 'NEW,INVALID,UNTRACKED')
        self.assertNotIn('ESTABLISHED', direct[direct.index('--ctstate') + 1])
        self.assertEqual(direct[-1], 'DROP')
        self.assertNotIn('source ', rich[1])
        self.assertIn('priority="-10"', rich[1])

    def test_additional_client_subnet_has_ingress_and_outbound_rules(self):
        _, _, direct, rich = lan.configuration(self.fixture(), '10.66.10.20', '10.66.10.0/24', 7171, ['10.66.20.0/24'])
        self.assertEqual([r[r.index('-d') + 1] for r in direct], ['10.66.10.0/24', '10.66.20.0/24'])
        self.assertEqual(len(rich), 3)
        self.assertIn('source address="10.66.20.0/24"', rich[1])
        self.assertTrue(rich[-1].endswith('reject'))
        with self.assertRaises(RuntimeError):
            lan.configuration(self.fixture(), '10.66.10.20', '10.66.10.0/24', 7171, ['0.0.0.0/0'])

    def test_atomic_configuration_keeps_owner_and_private_mode(self):
        with tempfile.TemporaryDirectory() as directory:
            path = pathlib.Path(directory) / 'config.yaml'
            path.write_bytes(b'old')
            lan.atomic_write(path, b'new', os.getuid(), os.getgid(), 0o600)
            self.assertEqual(path.read_bytes(), b'new')
            self.assertEqual(path.stat().st_mode & 0o777, 0o600)
            self.assertEqual(list(path.parent.iterdir()), [path])

    def test_stable_settings_link_resolves_to_the_private_file(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            (root / 'settings').mkdir()
            target = root / 'settings/config.yaml'
            target.write_bytes(b'private configuration')
            (root / 'config.yaml').symlink_to('settings/config.yaml')
            self.assertEqual(lan.configuration_path(root), target)
            self.assertEqual(lan.read_configuration(target)[0], b'private configuration')

    def test_configuration_alias_cannot_redirect_privileged_writes(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            outside = root / 'outside.yaml'; outside.write_bytes(b'unchanged')
            (root / 'config.yaml').symlink_to(outside)
            with self.assertRaisesRegex(RuntimeError, 'Unexpected configuration link'):
                lan.configuration_path(root)
            self.assertEqual(outside.read_bytes(), b'unchanged')


if __name__ == '__main__':
    unittest.main()
