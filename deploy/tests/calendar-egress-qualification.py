#!/usr/bin/env python3
"""Exercise calendar firewall pins against real listeners in disposable namespaces."""
import argparse
import json
import pathlib
import shutil
import subprocess
import tempfile
import time
import uuid


def command(*args, check=True, timeout=30):
    result = subprocess.run(['docker', *args], capture_output=True, text=True, timeout=timeout)
    if check and result.returncode:
        raise RuntimeError('Disposable Docker fixture operation failed: ' + result.stderr.strip())
    return result


def qualify(image, output):
    suffix = uuid.uuid4().hex[:10]
    firewall = 'm4-cal-' + suffix
    qualification_image = 'secondbrain-calendar-qualification:' + suffix
    repository = pathlib.Path(__file__).resolve().parents[2]
    checks = []
    started = time.monotonic()
    with tempfile.TemporaryDirectory(prefix='m4-calendar-egress-') as directory:
        fixtures = pathlib.Path(directory)
        fixtures.chmod(0o755)  # Synthetic scripts must be readable without DAC_OVERRIDE.
        shutil.copyfile(repository / 'deploy/docker/egress-entrypoint.sh', fixtures / 'egress.sh')
        responder = fixtures / 'respond.sh'
        responder.write_text("#!/bin/sh\nprintf 'HTTP/1.1 200 OK\\r\\nContent-Length: 2\\r\\nConnection: close\\r\\n\\r\\nOK'\n")
        responder.chmod(0o755)
        # Both sides of the test link live inside a network-none container.
        # Public test addresses and routes never enter the host namespace.
        (fixtures / 'peer.sh').write_text('''#!/bin/sh
set -eu
echo $$ > /run/peer-pid
while [ ! -f /run/peer-configure ]; do sleep .1; done
ip link set lo up
ip link set cal-peer up
ip addr add 172.31.249.2/30 dev cal-peer
ip -6 addr add fd42:5342::2/64 dev cal-peer
ip addr add 93.184.216.34/32 dev cal-peer
ip addr add 93.184.216.35/32 dev cal-peer
ip addr add 1.1.1.1/32 dev cal-peer
ip -6 addr add 2606:4700:4700::1001/128 dev cal-peer
ip -6 addr add 2606:4700:4700::1002/128 dev cal-peer
sleep 2
serve() { while :; do /fixture/respond.sh | nc -N -l -s "$1" -p "$2"; done; }
serve 0.0.0.0 8443 &
serve 0.0.0.0 8444 &
serve 2606:4700:4700::1001 8443 &
serve 2606:4700:4700::1002 8443 &
wait
''')
        (fixtures / 'firewall.sh').write_text('''#!/bin/sh
set -eu
unshare -n sh /fixture/peer.sh &
while [ ! -s /run/peer-pid ]; do sleep .1; done
ip link add cal-main type veth
ip link set veth0 name cal-peer
ip link set cal-peer netns "$(cat /run/peer-pid)"
ip link set lo up
ip link set cal-main up
ip addr add 172.31.249.1/30 dev cal-main
ip -6 addr add fd42:5342::1/64 dev cal-main
touch /run/peer-configure
ip route add 93.184.216.34/32 via 172.31.249.2
ip route add 93.184.216.35/32 via 172.31.249.2
ip route add 1.1.1.1/32 via 172.31.249.2
ip -6 route add 2606:4700:4700::1001/128 via fd42:5342::2
ip -6 route add 2606:4700:4700::1002/128 via fd42:5342::2
sleep 3
exec sh /fixture/egress.sh
''')
        try:
            # Namespace/listener/client utilities belong to this disposable test
            # image. The shipped helper keeps its smaller production tool set.
            if any(c in image for c in '\r\n '):
                raise ValueError('Use a single Docker image reference.')
            (fixtures / 'Dockerfile').write_text('FROM ' + image + '\n'
                'RUN apk add --no-cache util-linux-misc=2.42.4-r1 wget=1.25.0-r20 netcat-openbsd=1.239-r0\n'
                'RUN command -v unshare && command -v wget && command -v nc\n')
            command('build', '--tag', qualification_image, directory, timeout=180)
            # SYS_ADMIN permits unshare only in this disposable fixture. Shipping
            # egress helpers retain NET_ADMIN alone and the default seccomp policy.
            command('run', '-d', '--name', firewall, '--network', 'none', '--cap-drop', 'ALL', '--cap-add', 'NET_ADMIN',
                    '--cap-add', 'SYS_ADMIN', '--security-opt', 'seccomp=unconfined',
                    '-e', 'SECONDBRAIN_DNS_IP=10.8.0.1', '-e', 'SECONDBRAIN_LOCAL_ONLY=true',
                    '-e', 'SECONDBRAIN_CALENDAR_ALLOWLIST=93.184.216.34:8443 [2606:4700:4700::1001]:8443',
                    '-v', directory + ':/fixture:ro,Z', '--entrypoint', 'sh', qualification_image, '/fixture/firewall.sh')
            for _ in range(50):
                if command('exec', firewall, 'test', '-f', '/run/egress-ready', check=False).returncode == 0:
                    break
                time.sleep(.1)
            else:
                logs = command('logs', firewall, check=False)
                raise RuntimeError('Firewall did not become ready: ' + logs.stdout + logs.stderr)
            endpoints = {
                'allowed_ipv4': 'http://93.184.216.34:8443',
                'other_port': 'http://93.184.216.34:8444',
                'other_ipv4': 'http://93.184.216.35:8443',
                'canary': 'http://1.1.1.1:8443',
                'allowed_ipv6': 'http://[2606:4700:4700::1001]:8443',
                'other_ipv6': 'http://[2606:4700:4700::1002]:8443',
            }
            # Establish that every listener is reachable in the namespace. Remove
            # this root-only diagnostic allowance before checking application UIDs.
            for binary in ['iptables', 'ip6tables']:
                command('exec', firewall, binary, '-I', 'OUTPUT', '1', '-m', 'owner', '--uid-owner', '0', '-j', 'ACCEPT')
            for name, url in endpoints.items():
                response = command('exec', '--user', '0', firewall, 'wget', '-q', '-t', '1', '-T', '2', '-O', '-', url, check=False)
                if response.returncode or response.stdout != 'OK':
                    raise RuntimeError('Baseline fixture listener is unreachable: ' + name)
            for binary in ['iptables', 'ip6tables']:
                command('exec', firewall, binary, '-D', 'OUTPUT', '-m', 'owner', '--uid-owner', '0', '-j', 'ACCEPT')
            for uid in ['1654', '65532', '1655']:
                for name, url in endpoints.items():
                    response = command('exec', '--user', uid, firewall, 'wget', '-q', '-t', '1', '-T', '1', '-O', '-', url, check=False)
                    connected = response.returncode == 0 and response.stdout == 'OK'
                    expected = uid == '1654' and name in ('allowed_ipv4', 'allowed_ipv6')
                    if connected != expected:
                        raise RuntimeError('Calendar firewall check failed for UID ' + uid + ': ' + name)
                    checks.append({'uid': int(uid), 'destination': name, 'connected': connected, 'expected': expected})
            offline = command('run', '--rm', '--network', 'none', '--user', '1655', '--cap-drop', 'ALL',
                              '--entrypoint', 'wget', qualification_image, '-q', '-t', '1', '-T', '1', '-O', '-', endpoints['allowed_ipv4'], check=False)
            if offline.returncode == 0:
                raise RuntimeError('Offline extractor namespace unexpectedly reached a calendar')
            result = {'status': 'passed', 'imageId': json.loads(command('image', 'inspect', image).stdout)[0]['Id'],
                      'qualificationImageId': json.loads(command('image', 'inspect', qualification_image).stdout)[0]['Id'],
                      'fixtureOnlyPackages': ['util-linux-misc=2.42.4-r1', 'wget=1.25.0-r20', 'netcat-openbsd=1.239-r0'],
                      'script': 'deploy/docker/egress-entrypoint.sh', 'baselineListenersReachable': True,
                      'localOnly': True, 'networkNoneWithNestedNamespace': True, 'publicAliasesInsideContainersOnly': True,
                      'checks': checks, 'extractorNetworkNoneBlocked': True, 'seconds': round(time.monotonic() - started, 2)}
            output.parent.mkdir(parents=True, exist_ok=True)
            output.write_text(json.dumps(result, indent=2) + '\n')
            return result
        finally:
            command('rm', '-f', firewall, check=False)
            command('image', 'rm', qualification_image, check=False)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--image', default='secondbrain-egress:local')
    parser.add_argument('--output', type=pathlib.Path, required=True)
    arguments = parser.parse_args()
    result = qualify(arguments.image, arguments.output)
    print(json.dumps({'status': result['status'], 'checks': len(result['checks']), 'output': str(arguments.output)}))
