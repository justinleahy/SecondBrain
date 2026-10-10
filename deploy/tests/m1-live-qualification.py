"""Run an explicitly authorized disposable live-provider qualification.

Requires prebuilt deployment images and an existing 127.0.0.1:8001 vLLM embedder.
Never builds images or touches production projects. Reports provider time apart
from end-to-end pipeline time; concurrent request sums are not wall-clock cost.
"""
import argparse
import importlib.util
import ipaddress
import json
import os
import pathlib
import secrets
import subprocess
import sys
import time
import urllib.error
import urllib.request
import uuid

ROOT = pathlib.Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('throughput', ROOT / 'deploy/tests/m1-throughput.py')
throughput = importlib.util.module_from_spec(spec)
spec.loader.exec_module(throughput)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--run', action='store_true', help='Explicitly launch disposable deployment and workload')
    parser.add_argument('--upstream-host', default='127.0.0.1', help='Operator-selected numeric private IPv4 provider address')
    parser.add_argument('--upstream-port', type=int, default=8001)
    parser.add_argument('--artifact-dir', type=pathlib.Path, default=ROOT / 'artifacts/m1-qualification/live')
    parser.add_argument('--observe-publications', action='store_true')
    args = parser.parse_args()
    if not args.run:
        parser.error('--run is required; images must already be built')
    try:
        upstream = ipaddress.IPv4Address(args.upstream_host)
    except ipaddress.AddressValueError:
        parser.error('Upstream must be numeric RFC1918 or loopback IPv4, without credentials')
    if not any(upstream in ipaddress.IPv4Network(network) for network in
               ('10.0.0.0/8', '172.16.0.0/12', '192.168.0.0/16', '127.0.0.0/8')):
        parser.error('Upstream must be numeric RFC1918 or loopback IPv4')
    if not 1 <= args.upstream_port <= 65535:
        parser.error('Upstream port must be between 1 and 65535')
    artifact = args.artifact_dir.resolve()
    if artifact.exists():
        parser.error('Artifact directory already exists; choose a fresh --artifact-dir to preserve earlier evidence')
    artifact.mkdir(parents=True)
    deployment = artifact / 'deployment'
    deployment.mkdir(exist_ok=True)
    for name in ('data', 'incoming', 'config', 'project'):
        (deployment / name).mkdir(exist_ok=True)
    (deployment / 'resolv.conf').write_text('nameserver 172.28.0.1\noptions timeout:1 attempts:1\n')
    env = dict(os.environ, SECONDBRAIN_HOST_ROOT=str(deployment),
               SECONDBRAIN_PRIVATE_ADDRESS='127.0.0.1', SECONDBRAIN_PORT='17174',
               SECONDBRAIN_DOCKER_ADDRESS='172.28.0.2', SECONDBRAIN_DOCKER_SUBNET='172.28.0.0/24',
               SECONDBRAIN_DNS_IP='172.28.0.1', SECONDBRAIN_PROVIDER_ALLOWLIST='172.28.0.1:11882',
               SECONDBRAIN_ACCESS_ALLOWLIST='', SECONDBRAIN_LOCAL_ONLY='true')
    compose = ['docker', 'compose', '--project-name', 'secondbrain-m1-live',
               '-f', str(ROOT / 'deploy/compose.yaml'), '-f', str(ROOT / 'deploy/tests/m1-live-compose.yaml')]

    def docker(*arguments, input=None):
        return subprocess.run(compose + list(arguments), cwd=ROOT, env=env, input=input,
                              check=True, stdout=subprocess.DEVNULL, timeout=120)

    origin = 'http://127.0.0.1:17174'
    password = secrets.token_urlsafe(24)
    env['SECONDBRAIN_BOOTSTRAP_PASSWORD'] = password
    env['SECONDBRAIN_QUALIFICATION_PASSWORD'] = password
    env['SECONDBRAIN_QUALIFICATION_ORIGIN'] = origin
    log = artifact / ('provider-' + uuid.uuid4().hex[:8] + '.jsonl')
    bridge = None
    bridge_command = [sys.executable, str(ROOT / 'deploy/tests/m1-live-provider.py'), '--log', str(log),
                      '--upstream-host', args.upstream_host, '--upstream-port', str(args.upstream_port)]
    topology = ('same-host loopback upstream via private Docker gateway; separate LAN reference not established'
                if upstream.is_loopback else
                'operator-selected LAN upstream via private Docker gateway; physical separation and GPU identity not independently validated')
    report = {'topology': topology,
              'upstream_host': args.upstream_host, 'upstream_port': args.upstream_port,
              'provider': 'Qwen/Qwen3-Embedding-0.6B', 'embed_dimensions': 1024,
              'provider_timing_log': str(log), 'phases': {}}

    def phase(name, action):
        begin = time.monotonic()
        result = action()
        end = time.monotonic()
        records = [json.loads(line) for line in log.read_text().splitlines()] if log.exists() else []
        selected = [record for record in records if begin <= record['started_monotonic'] <= end]
        embeddings = 0
        classified = []
        for record in selected:
            item = dict(record)
            if item.get('request_kind') == 'embedding':
                embeddings += 1
                if item['intent'] == 'unspecified by protocol':
                    if name.startswith('throughput_'):
                        item['fixture_inferred_intent'] = 'document'
                    elif name == 'healthy_small_capture_and_semantic':
                        item['fixture_inferred_intent'] = 'document' if embeddings == 1 else 'query'
                    item['intent_inference_note'] = 'Fixture phase activity; provider JSON omitted intent extension.'
            classified.append(item)
        report['phases'][name] = {'wall_seconds': end - begin, 'result': result,
                                 'provider_request_metadata': classified,
                                 'provider_requests': len(selected),
                                 'provider_request_elapsed_sum_seconds': sum(r['elapsed_seconds'] for r in selected),
                                 'provider_prompt_tokens': sum(r.get('usage', {}).get('prompt_tokens', 0) for r in selected),
                                 'provider_timing_note': 'Request durations may overlap; do not subtract their sum from wall time.'}
        (artifact / 'qualification.json').write_text(json.dumps(report, indent=2, sort_keys=True))
        return result

    try:
        docker('--profile', 'provision', 'run', '--rm', '--no-deps', '-T', 'provision')
        docker('--profile', 'provision', 'run', '--rm', '--no-deps', '-T', '--entrypoint', 'sh', 'provision', '-c',
               'cat > /etc/secondbrain/config.yaml && chown 1654:1654 /etc/secondbrain/config.yaml && chmod 0600 /etc/secondbrain/config.yaml',
               input=(ROOT / 'deploy/tests/m1-live-config.yaml').read_bytes())
        docker('up', '--detach', '--wait', '--wait-timeout', '30', 'egress')
        bridge = subprocess.Popen(bridge_command, cwd=ROOT)
        time.sleep(.3)
        assert bridge.poll() is None, 'Private provider bridge failed to bind'
        docker('run', '--rm', '--no-deps', '-T', '-e', 'SECONDBRAIN_BOOTSTRAP_PASSWORD',
               'daemon', 'dotnet', '/app/cli/brain.dll', 'init', '--json')
        docker('up', '--detach', '--wait', '--wait-timeout', '90', 'daemon', 'extractor')
        client = throughput.Client(origin)
        deadline = time.monotonic() + 90
        while True:
            try:
                if client.request('GET', '/health', timeout=2):
                    break
            except (urllib.error.URLError, TimeoutError, ConnectionError):
                pass
            if time.monotonic() >= deadline:
                raise AssertionError('Isolated daemon did not become healthy')
            time.sleep(.2)
        try:
            urllib.request.urlopen(origin + '/v1/jobs', timeout=5)
            raise AssertionError('Unauthenticated jobs unexpectedly accessible')
        except urllib.error.HTTPError as error:
            assert error.code in (401, 403), 'Unexpected anonymous jobs status'
            report['unauthenticated_jobs_status'] = error.code
        client.login(password)
        marker = 'm1live' + uuid.uuid4().hex[:12]

        def healthy():
            assert client.request('GET', '/health')
            started = time.monotonic()
            captured = client.request('POST', '/v1/documents', {'content': marker + ' The astronomer observes distant galaxies through a telescope.',
                                      'title': marker, 'tags': ['livequalification']})
            identifier = captured['document']['id']
            keyword = vectors = None
            while time.monotonic() - started < 15:
                status = client.request('GET', '/v1/documents/' + identifier)['status']
                if keyword is None and any(hit['document_id'] == identifier for hit in client.request('POST', '/v1/search', {'query': marker, 'mode': 'keyword'})['results']):
                    keyword = time.monotonic() - started
                if status == 'indexed':
                    vectors = time.monotonic() - started
                    break
                time.sleep(.02)
            semantic = client.request('POST', '/v1/search', {'query': 'looking at stars and celestial objects using optical instruments', 'mode': 'semantic', 'limit': 10})['results']
            assert any(hit['document_id'] == identifier for hit in semantic), 'Real semantic lookup missed capture'
            query = client.request('POST', '/v1/query', {'tags': ['livequalification']})
            assert any(item['id'] == identifier for item in query['records'])
            assert isinstance(client.request('GET', '/v1/jobs'), list)
            return {'capture_keyword_seconds': keyword, 'capture_vectors_seconds': vectors,
                    'keyword_under_one_second': keyword is not None and keyword < 1,
                    'vectors_under_three_seconds': vectors is not None and vectors < 3,
                    'semantic_result_contains_capture': True, 'authenticated_jobs': True}

        phase('healthy_small_capture_and_semantic', healthy)
        bridge.terminate()
        bridge.wait(timeout=10)
        bridge = None
        phase('provider_absent_structured_query', lambda: client.request('POST', '/v1/query', {'tags': ['livequalification']}) and {'query_succeeded': True, 'bridge_stopped': True})
        bridge = subprocess.Popen(bridge_command, cwd=ROOT)
        time.sleep(.3)
        assert bridge.poll() is None

        def bulk():
            result = subprocess.run([sys.executable, str(ROOT / 'deploy/tests/m1-throughput.py'), '--provider-mode', 'healthy',
                                     '--compose-project', 'secondbrain-m1-live', '--compose-file', str(ROOT / 'deploy/compose.yaml'),
                                     '--compose-file', str(ROOT / 'deploy/tests/m1-live-compose.yaml')]
                                    + (['--observe-publications'] if args.observe_publications else []), env=env, cwd=ROOT,
                                    check=False, capture_output=True, text=True, timeout=1800)
            # Retain child diagnostics before raising; never expose fixture credentials.
            for name, output in [('throughput-child-stdout.log', result.stdout), ('throughput-child-stderr.log', result.stderr)]:
                for secret in [password, client.csrf, *(cookie.value for cookie in client.cookies)]:
                    if secret:
                        output = output.replace(secret, '[redacted fixture credential]')
                with os.fdopen(os.open(artifact / name, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600), 'w') as diagnostic:
                    diagnostic.write(output)
            if result.returncode:
                raise RuntimeError('Throughput child failed; sanitized diagnostics: ' + str(artifact / 'throughput-child-stderr.log'))
            measured = json.loads(result.stdout)
            measured['provider_time_note'] = 'Instrumented separately by private bridge; end-to-end throughput includes provider time.'
            (artifact / 'throughput.json').write_text(json.dumps(measured, indent=2, sort_keys=True))
            return {'markdown': measured['markdown']['files_per_minute'], 'pdf': measured['pdf']['files_per_minute'],
                    'markdown_target_met_including_provider': measured['markdown']['observed_rate_meets_target'],
                    'pdf_target_met_including_provider': measured['pdf']['observed_rate_meets_target']}
        phase('throughput_100_markdown_20_pdf_four_workers', bulk)
    finally:
        if bridge:
            bridge.terminate()
            bridge.wait(timeout=10)
        docker('down', '--remove-orphans')
        (artifact / 'qualification.json').write_text(json.dumps(report, indent=2, sort_keys=True))
    print(json.dumps(report, sort_keys=True))


if __name__ == '__main__':
    main()
