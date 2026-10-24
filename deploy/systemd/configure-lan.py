#!/usr/bin/env python3
"""Add a private LAN listener while preserving the daemon's outbound boundary."""
import argparse
import copy
import datetime
import fcntl
import ipaddress
import json
import os
import pathlib
import pwd
import shutil
import stat
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def configuration(original, address, network, port, additional_networks=()):
    ip = ipaddress.IPv4Address(address)
    subnet = ipaddress.IPv4Network(network)
    private_ranges = [ipaddress.IPv4Network(x) for x in ['10.0.0.0/8', '172.16.0.0/12', '192.168.0.0/16']]
    require(any(ip in private and subnet.subnet_of(private) for private in private_ranges) and ip in subnet, 'Listener must belong to the selected private LAN.')
    networks = list(dict.fromkeys([subnet, *[ipaddress.IPv4Network(x) for x in additional_networks]]))
    require(all(any(n.subnet_of(private) for private in private_ranges) for n in networks), 'Client networks must be private IPv4 subnets.')
    require(1024 < port < 65536, 'Select an unprivileged TCP port.')
    result = copy.deepcopy(original)
    server = result['server']
    listeners = server.setdefault('listeners', [])
    matches = [x for x in listeners if x.get('bind') == address and x.get('port') == port]
    require(not matches or all(x.get('scheme', 'http') == 'http' for x in matches), 'The selected address/port already has a different listener.')
    if not matches:
        listeners.append({'scheme': 'http', 'bind': address, 'port': port})
    origin = f'http://{address}:{port}'
    for field, value in [('hosts', address), ('origins', origin)]:
        if value not in server.setdefault(field, []):
            server[field].append(value)
    # A UID-specific egress rule cannot be expressed by firewalld rich rules.
    # It blocks new LAN connections but permits replies to established clients.
    direct = [['ipv4', 'filter', 'OUTPUT', '0', '-m', 'owner', '--uid-owner', '1654', '-d', str(n),
               '-m', 'conntrack', '--ctstate', 'NEW,INVALID,UNTRACKED', '-j', 'DROP'] for n in networks]
    rich = [f'rule family="ipv4" priority="-20" source address="{n}" destination address="{ip}" port port="{port}" protocol="tcp" accept' for n in networks]
    rich.append(f'rule family="ipv4" priority="-10" destination address="{ip}" port port="{port}" protocol="tcp" reject')
    return result, origin, direct, rich


def atomic_write(path, data, uid, gid, mode):
    fd, name = tempfile.mkstemp(prefix='.' + path.name + '-', dir=path.parent)
    try:
        with os.fdopen(fd, 'wb') as stream:
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())
        os.chown(name, uid, gid)
        os.chmod(name, mode)
        os.replace(name, path)
    finally:
        if os.path.exists(name):
            os.unlink(name)


def get(url, origin=None):
    headers = {} if origin is None else {'Origin': origin}
    try:
        with urllib.request.urlopen(urllib.request.Request(url, headers=headers), timeout=5) as response:
            return response.status, response.read()
    except urllib.error.HTTPError as error:
        status = error.code
        error.close()
        return status, b''


def wait_ready(url):
    for _ in range(90):
        try:
            status, body = get(url + '/ready')
            if status == 200 and json.loads(body).get('ready'):
                return json.loads(body)
        except (OSError, ValueError):
            pass
        time.sleep(1)
    raise RuntimeError('Listener did not become ready within the startup window.')


def main():
    import yaml

    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--address', default='10.66.10.20')
    parser.add_argument('--network', default='10.66.10.0/24')
    parser.add_argument('--allow-network', action='append', default=[], help='Additional private client subnet; repeat as needed.')
    parser.add_argument('--interface', default='enp8s0')
    parser.add_argument('--port', type=int, default=7171)
    parser.add_argument('--evidence-dir', type=pathlib.Path, required=True)
    args = parser.parse_args()
    require(os.geteuid() == 0, 'Root is required to update the installed listener and firewall.')
    lock = open('/run/lock/secondbrain-lan-config.lock', 'w')
    fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
    require(pwd.getpwnam('secondbrain').pw_uid == 1654, 'The daemon must use the reference UID 1654.')
    repo = pathlib.Path(__file__).resolve().parents[2]
    evidence = args.evidence_dir.resolve()
    require(evidence.is_relative_to(repo / 'artifacts'), 'Evidence must stay under repository artifacts.')
    evidence.mkdir(parents=True, exist_ok=True)
    config_path = pathlib.Path('/etc/secondbrain/config.yaml')
    dropin = pathlib.Path('/etc/systemd/system/secondbrain.service.d/20-lan.conf')
    require(not config_path.is_symlink() and not dropin.is_symlink(), 'Configuration targets must not be symlinks.')
    original_bytes = config_path.read_bytes()
    info = config_path.stat()
    try:
        original = yaml.safe_load(original_bytes)
    except yaml.YAMLError:
        raise RuntimeError('Cannot parse the installed configuration; inspect it privately.') from None
    updated, origin, direct, rich = configuration(original, args.address, args.network, args.port, args.allow_network)
    networks = [rule[rule.index('-d') + 1] for rule in direct]
    addresses = json.loads(subprocess.check_output(['ip', '-j', '-4', 'address', 'show', 'dev', args.interface], text=True))
    require(any(a.get('local') == args.address and ipaddress.IPv4Network(f"{args.address}/{a['prefixlen']}", strict=False) == ipaddress.IPv4Network(args.network)
                for i in addresses for a in i.get('addr_info', [])), 'The selected address and subnet are not assigned to this interface.')
    for provider in original.get('providers', {}).values():
        host = urllib.parse.urlsplit(provider.get('base_url', provider.get('endpoint', ''))).hostname
        try:
            require(not any(ipaddress.ip_address(host) in ipaddress.ip_network(n) for n in networks), 'An existing provider is on an allowed client LAN; its exact egress allowance must be planned first.')
        except ValueError:
            pass
    zone = subprocess.check_output(['firewall-cmd', '--get-zone-of-interface=' + args.interface], text=True).strip()
    require(zone and zone != 'no zone', 'The LAN interface must belong to an active firewalld zone.')
    os.umask(0o077)
    backup = pathlib.Path('/var/backups/secondbrain') / ('lan-' + datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%SZ'))
    backup.mkdir(mode=0o700, parents=True)
    shutil.copy2(config_path, backup / 'config.yaml')
    old_dropin = dropin.read_bytes() if dropin.exists() else None
    if old_dropin is not None:
        (backup / '20-lan.conf').write_bytes(old_dropin)
    log = (backup / 'configuration.log').open('w')
    additions = []
    changed = False

    def run(command):
        result = subprocess.run(command, stdout=log, stderr=subprocess.STDOUT)
        log.flush()
        require(result.returncode == 0, 'Configuration command failed; inspect the private configuration log.')

    def firewall(prefix, query, add, remove):
        check = subprocess.run(['firewall-cmd', *prefix, *query], capture_output=True)
        require(check.returncode in (0, 1), 'Cannot inspect the existing firewall rule.')
        if check.returncode == 1:
            additions.append(['firewall-cmd', *prefix, *remove])
            run(['firewall-cmd', *prefix, *add])

    try:
        # Install restrictions before the daemon gains an inbound LAN allowance.
        for permanent in [[], ['--permanent']]:
            for rule in direct:
                firewall([*permanent, '--direct'], ['--query-rule', *rule], ['--add-rule', *rule], ['--remove-rule', *rule])
            for rule in rich:
                firewall([*permanent, '--zone=' + zone], ['--query-rich-rule=' + rule], ['--add-rich-rule=' + rule], ['--remove-rich-rule=' + rule])
        run(['firewall-cmd', '--check-config'])
        changed = True
        atomic_write(config_path, yaml.safe_dump(updated, sort_keys=False, allow_unicode=True).encode(), info.st_uid, info.st_gid, stat.S_IMODE(info.st_mode))
        dropin.parent.mkdir(parents=True, exist_ok=True)
        atomic_write(dropin, ('[Unit]\nRequires=firewalld.service\nAfter=firewalld.service\n\n[Service]\n' + ''.join('IPAddressAllow=' + n + '\n' for n in networks)).encode(), 0, 0, 0o644)
        run(['restorecon', str(config_path), str(dropin)])
        run(['systemctl', 'daemon-reload'])
        run(['systemctl', 'restart', 'secondbrain.service'])
        readiness = wait_ready('http://127.0.0.1:7171')
        lan_ready = wait_ready(origin)
        require(get(origin + '/login', origin)[0] == 200, 'LAN login page did not load.')
        require(get(origin + '/v1/jobs', origin)[0] == 401, 'LAN authentication boundary is not enforced.')
        # This UID probe is outside the daemon cgroup, proving the host rule itself.
        probe = '''import socket,sys
try:
 s=socket.create_connection((sys.argv[1],int(sys.argv[2])),2)
except OSError:
 sys.exit(0)
else:
 s.close();sys.exit(1)
'''
        run(['setpriv', '--reuid', '1654', '--regid', '1654', '--clear-groups', 'python3', '-c', probe, args.address, str(args.port)])
        require('Blocked' in readiness['components']['canary']['detail'], 'The public egress canary is not blocked.')
        report = {'status': 'configured', 'origin': origin, 'network': args.network, 'allowed_networks': networks, 'zone': zone, 'backup': str(backup),
                  'loopback_ready': readiness, 'lan_ready': lan_ready, 'login_status': 200, 'unauthenticated_jobs_status': 401,
                  'daemon_uid_new_lan_connections': 'blocked by observed host firewall test',
                  'rich_rules': rich, 'direct_rules': direct, 'configuration_preserved_except': ['server.listeners', 'server.hosts', 'server.origins']}
        (evidence / 'lan-result.json').write_text(json.dumps(report, indent=2) + '\n')
        (evidence / 'lan-result.json').chmod(0o644)
        print('LAN listener configured and verified: ' + origin)
        print('Private configuration backup: ' + str(backup))
    except BaseException:
        print('Configuration failed; restoring the previous listener configuration.', file=sys.stderr)
        if changed:
            atomic_write(config_path, original_bytes, info.st_uid, info.st_gid, stat.S_IMODE(info.st_mode))
            if old_dropin is None:
                dropin.unlink(missing_ok=True)
            else:
                atomic_write(dropin, old_dropin, 0, 0, 0o644)
            run(['restorecon', str(config_path)])
            run(['systemctl', 'daemon-reload'])
            run(['systemctl', 'restart', 'secondbrain.service'])
        for command in reversed(additions):
            subprocess.run(command, stdout=log, stderr=subprocess.STDOUT)
        raise
    finally:
        log.close()
        lock.close()


if __name__ == '__main__':
    main()
