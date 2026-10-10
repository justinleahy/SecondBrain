"""Full corpus qualification on a disposable real Compose deployment; no live deployment mutation.

The replay provider serves recorded Qwen vectors, so measurements establish retrieval and
provider-excluded rebuild processing only. They do not establish separate-LAN inference gates.
Run from repository root after building current production images and the heap hook.
"""
import argparse
from datetime import datetime
import hashlib
import http.cookiejar
import importlib.util
import json
import math
import os
import pathlib
import platform
import re
import secrets
import sqlite3
import subprocess
import threading
import time
import urllib.request

parser = argparse.ArgumentParser()
parser.add_argument('--root', default='artifacts/m1-qualification/fullscale')
parser.add_argument('--project', default='secondbrain-fullscale-qualification')
parser.add_argument('--documents', type=int, default=100000)
parser.add_argument('--port', type=int, default=17173)
parser.add_argument('--skip-seed', action='store_true')
parser.add_argument('--invalidate-first-query', action='store_true', help='Profiling fixture only: invalidate actual loaded daemon registry before first HTTP search')
parser.add_argument('--profile-warm-repeats', type=int, default=0, help='Profiling fixture only: warm requests per filter after baseline validation')
parser.add_argument('--require-baseline-checkpoint', help='Require exact search fingerprints from an earlier immutable qualification checkpoint')
args = parser.parse_args()
root = pathlib.Path(args.root).resolve()
assert 'm1-qualification' in str(root)
assert args.project.startswith('secondbrain-fullscale-')
root.mkdir(parents=True, exist_ok=True)
env = dict(os.environ, SECONDBRAIN_PRIVATE_ADDRESS='127.0.0.1', SECONDBRAIN_HOST_ROOT=str(root),
           SECONDBRAIN_DOCKER_SUBNET='172.29.0.0/24', SECONDBRAIN_DOCKER_ADDRESS='172.29.0.2',
           SECONDBRAIN_PORT=str(args.port), SECONDBRAIN_PROVIDER_ALLOWLIST='172.29.0.1:11881',
           SECONDBRAIN_ACCESS_ALLOWLIST='', SECONDBRAIN_DNS_IP='172.29.0.1',
           SECONDBRAIN_FULLSCALE_HOOK_DIRECTORY=str(pathlib.Path('deploy/tests/fullscale-heap-hook/bin/Release/net10.0').resolve()),
           SECONDBRAIN_FULLSCALE_SANDBOX_HELPER_DIRECTORY=str(pathlib.Path('deploy/tests/fullscale-sandbox-helper/bin/Release/net10.0').resolve()),
           SECONDBRAIN_FULLSCALE_PDF_DIRECTORY=str(root/'sandbox-pdf-fixtures'))
for directory_key, assembly in [('SECONDBRAIN_FULLSCALE_HOOK_DIRECTORY', 'FullScaleHeapHook.dll'),
                                ('SECONDBRAIN_FULLSCALE_SANDBOX_HELPER_DIRECTORY', 'FullScaleSandboxHelper.dll')]:
    assert (pathlib.Path(env[directory_key])/assembly).is_file(), 'Build Release qualification assembly: ' + assembly
compose = ['docker', 'compose', '--project-name', args.project, '-f', 'deploy/compose.yaml', '-f', 'deploy/tests/compose-smoke.yaml', '-f', 'deploy/tests/fullscale-compose.yaml']
password_path = root / '.qualification-password'
if password_path.exists(): password = password_path.read_text()
else:
    password = secrets.token_urlsafe(32)
    password_path.write_text(password)
    password_path.chmod(0o600)
origin = 'http://127.0.0.1:' + str(args.port)
provider = None
helper_process = None
memory = []
errors = []
stop_sample = threading.Event()

def run(arguments, **kwargs):
    return subprocess.run(arguments, env=env, check=True, **kwargs)

def docker(*arguments, **kwargs): return run(compose + list(arguments), **kwargs)

def provision(script, input=None):
    return docker('--profile', 'provision', 'run', '--rm', '--no-deps', '-T', '--entrypoint', 'sh', 'provision', '-c', script,
                  input=input, stdout=subprocess.DEVNULL)

def read_cgroup_memory():
    values = {}
    for service in ('daemon', 'extractor'):
        result = docker('exec', '-T', service, 'sh', '-c',
                        'cat /sys/fs/cgroup/memory.current /sys/fs/cgroup/memory.peak /sys/fs/cgroup/memory.max',
                        capture_output=True, text=True)
        current, peak, limit = result.stdout.splitlines()
        values[service] = {'current_bytes': int(current), 'peak_bytes': int(peak), 'limit': limit}
    return values

def sample_memory():
    while not stop_sample.is_set():
        values = {}
        try:
            values = read_cgroup_memory()
            memory.append({'at': time.time(), 'containers': values})
        except (subprocess.SubprocessError, ValueError) as error: errors.append(type(error).__name__)
        stop_sample.wait(1)

client = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
csrf = None
def request(method, path, body=None, timeout=3600):
    headers = {'Origin': origin}
    if csrf and method != 'GET': headers['X-CSRF-TOKEN'] = csrf
    data = None
    if body is not None: data = json.dumps(body).encode(); headers['Content-Type'] = 'application/json'
    with client.open(urllib.request.Request(origin + path, data, headers, method=method), timeout=timeout) as response:
        result = response.read()
        return json.loads(result) if result else None

def login():
    global csrf
    request('POST', '/auth/login', {'password': password})
    csrf = request('GET', '/auth/antiforgery')['token']
    request('POST', '/auth/step-up', {'password': password})
    csrf = request('GET', '/auth/antiforgery')['token']

def fingerprint(results):
    return [(hit['document_id'], hit['chunk_id'], hit['revision'], hit.get('path'), hit.get('page'), hit['snippet'], hit['context'])
            for hit in results]

def wait_health():
    started = time.monotonic()
    while time.monotonic()-started < 120:
        try:
            request('GET', '/health', timeout=2)
            return time.monotonic()-started
        except Exception: time.sleep(.5)
    raise AssertionError('Isolated daemon startup exceeded 120 seconds')

def percentile(values, fraction=.95): return sorted(values)[math.ceil(len(values)*fraction)-1]

def ulid(index):
    alphabet = '0123456789ABCDEFGHJKMNPQRSTVWXYZ'
    suffix = ''.join(alphabet[(index >> (5*n)) & 31] for n in reversed(range(6)))
    return '01K00000000000000000' + suffix

try:
    pdf_root = pathlib.Path(env['SECONDBRAIN_FULLSCALE_PDF_DIRECTORY'])
    pdf_root.mkdir(exist_ok=True); pdf_root.chmod(0o755)
    fixture_module_spec = importlib.util.spec_from_file_location('fullscale_pdf_fixtures', 'deploy/tests/m1-throughput.py')
    fixture_module = importlib.util.module_from_spec(fixture_module_spec)
    fixture_module_spec.loader.exec_module(fixture_module)
    for ordinal in range(4):
        path = pdf_root / ('sandbox-' + str(ordinal) + '.pdf')
        fixture_bytes = fixture_module.pdf_bytes('fullscale-sandbox-' + str(ordinal), 80)
        if path.exists(): assert path.read_bytes() == fixture_bytes, 'Existing immutable PDF fixture differs'
        else: path.write_bytes(fixture_bytes)
        path.chmod(0o444)
    assert pathlib.Path(env['SECONDBRAIN_FULLSCALE_SANDBOX_HELPER_DIRECTORY'], 'FullScaleSandboxHelper.dll').exists(), 'Build the isolated sandbox helper before deploying'
    if not args.skip_seed:
        for name in ('data', 'incoming', 'config', 'project'): (root/name).mkdir(exist_ok=True)
        (root/'resolv.conf').write_text('nameserver 172.29.0.1\noptions timeout:1 attempts:1\n')
        docker('--profile', 'provision', 'run', '--rm', 'provision', stdout=subprocess.DEVNULL)
        config = pathlib.Path('deploy/tests/config.compose-smoke.yaml').read_text()
        config = config.replace('172.30.0.2', '172.29.0.2')
        config = config.replace('providers:\n', 'providers:\n  vllm:\n    kind: openai_compatible\n    endpoint: http://172.29.0.1:11881/v1\n    trusted: true\n')
        config = config.replace('embed: { provider: mock', 'embed: { provider: vllm')
        config = config.replace('trusted_services: [http://127.0.0.1:8181]', 'trusted_services: [http://127.0.0.1:8181, http://172.29.0.1:11881]')
        config = config.replace('model: mock-embed', 'model: Qwen/Qwen3-Embedding-0.6B').replace('embed_dimensions: 4', 'embed_dimensions: 1024')
        config = config.replace('http://127.0.0.1:7171]', 'http://127.0.0.1:7171, ' + origin + ']')
        config += '\n' if not config.endswith('\n') else ''
        config = config.replace('debug_bodies: false', 'debug_bodies: false\n  retrieval_trace: true')
        provision('cat > /etc/secondbrain/config.yaml; chown 1654:1654 /etc/secondbrain/config.yaml; chmod 0600 /etc/secondbrain/config.yaml', config.encode())
    docker('up', '--detach', '--wait', '--wait-timeout', '45', 'egress', stdout=subprocess.DEVNULL)
    provider = subprocess.Popen(['python3', 'deploy/tests/fullscale-recorded-provider.py', '--metrics', str(root/'provider-replay.jsonl')],
                                stdout=subprocess.DEVNULL, stderr=open(root/'provider-errors.log', 'a'))
    time.sleep(.5)
    assert provider.poll() is None, 'Recorded private provider failed to start'
    if not args.skip_seed:
        subprocess.run(compose + ['run', '--rm', '--no-deps', '-T', '-e', 'SECONDBRAIN_BOOTSTRAP_PASSWORD',
                        'daemon', 'dotnet', '/app/cli/brain.dll', 'init', '--json'],
                       env=dict(env, SECONDBRAIN_BOOTSTRAP_PASSWORD=password), check=True, stdout=subprocess.DEVNULL)
        # Transfer only the stopped disposable fixture to the invoking UID for the seed process.
        provision(f'chown -R {os.getuid()}:{os.getgid()} /srv/secondbrain')
        seed_env = dict(env, SECONDBRAIN_FULLSCALE_ROOT=str(root/'data'), SECONDBRAIN_FULLSCALE_DOCUMENTS=str(args.documents),
                        SECONDBRAIN_FULLSCALE_PROVIDER_ENDPOINT='http://172.29.0.1:11881/v1')
        with open(root/'seed-test.log', 'w') as log:
            subprocess.run(['dotnet', 'test', 'tests/SecondBrain.Storage.Tests', '-c', 'Release', '--no-build', '--no-restore',
                            '--filter', 'FullyQualifiedName~FullScaleDaemonSeedQualification', '--logger', 'console;verbosity=normal'],
                           env=seed_env, check=True, stdout=log, stderr=subprocess.STDOUT)
        provision('chown -R 1654:1654 /srv/secondbrain; mkdir -p /srv/secondbrain/qualification-heap; chown 1654:1654 /srv/secondbrain/qualification-heap; chmod 0700 /srv/secondbrain/qualification-heap')
    if args.skip_seed:
        provision(f'chown -R {os.getuid()}:{os.getgid()} /srv/secondbrain')
        seed_report = json.loads((root/'data/fullscale-seed.json').read_text())
        assert seed_report['documents'] == args.documents and seed_report['chunks'] == args.documents*10 and seed_report['dimensions'] == 1024
        assert seed_report['profile'] == 'authoritative-managed-recorded-qwen'
        provision('chown -R 1654:1654 /srv/secondbrain; mkdir -p /srv/secondbrain/qualification-heap; chown 1654:1654 /srv/secondbrain/qualification-heap; chmod 0700 /srv/secondbrain/qualification-heap')
    started = time.monotonic()
    docker('up', '--detach', '--wait', '--wait-timeout', '120', 'daemon', 'extractor', stdout=subprocess.DEVNULL)
    startup_seconds = wait_health()
    startup_seconds = time.monotonic()-started
    sampler = threading.Thread(target=sample_memory, daemon=True); sampler.start()
    login()
    readiness = request('GET', '/ready')
    assert readiness['ready'], 'Actual supported daemon readiness failed'
    questions = [json.loads(line) for line in pathlib.Path('eval/questions.jsonl').read_text().splitlines() if line]
    corpus = [json.loads(line) for line in pathlib.Path('eval/documents.jsonl').read_text().splitlines() if line]
    baseline = {}
    required_baseline = None
    if args.require_baseline_checkpoint:
        required_baseline = {(row['filtered'], row['question_id']): row['results']
                             for row in json.loads(pathlib.Path(args.require_baseline_checkpoint).read_text())['baseline']}
    timings = []
    for filtered in (True, False):
        for question in questions:
            body = {'query': question['query'], 'mode': 'hybrid', 'filters': {'type': 'article'} if filtered else None}
            began = time.monotonic(); response = request('POST', '/v1/search', body)
            elapsed = (time.monotonic()-began)*1000
            assert not response['degraded'], response['degraded']
            assert len(response['results']) == 8
            baseline[(filtered, question['id'])] = fingerprint(response['results'])
            if required_baseline is not None:
                assert json.loads(json.dumps(baseline[(filtered, question['id'])])) == required_baseline[(filtered, question['id'])], 'Final-image baseline differs from immutable earlier result order/locators'
            alphabet = '0123456789ABCDEFGHJKMNPQRSTVWXYZ'
            topics = set()
            for hit in response['results']:
                index = 0
                for char in hit['document_id'][-6:]: index = index*32 + alphabet.index(char)
                topics.add(corpus[index % len(corpus)]['id'])
            timings.append({'filtered': filtered, 'question_id': question['id'], 'http_ms': elapsed,
                            'scaled_topic_recall_at8': len(set(question['expected']) & topics)/len(question['expected'])})
    # Warm repeats are captured separately; repository traces exclude query embedding replay time.
    for filtered in (True, False):
        for i in range(32):
            question = questions[i % len(questions)]
            began = time.monotonic()
            response = request('POST', '/v1/search', {'query': question['query'], 'mode': 'hybrid', 'filters': {'type': 'article'} if filtered else None})
            assert not response['degraded']
            timings.append({'filtered': filtered, 'question_id': question['id'], 'http_ms': (time.monotonic()-began)*1000, 'warm': True})
    structured = []
    for _ in range(20):
        began = time.monotonic(); page = request('POST', '/v1/query', {'type': 'article'})
        structured.append((time.monotonic()-began)*1000)
        assert len(page['records']) == 50
    active_sandbox_started = time.time()
    active_before = read_cgroup_memory()
    helper_arguments = compose + ['--profile', 'qualification', 'run', '--rm', '--no-deps', '-T', 'sandbox-load-helper',
                                  '/run/secondbrain/extractor.sock'] + ['/fixtures/sandbox-' + str(i) + '.pdf' for i in range(4)]
    with open(root/'sandbox-helper-errors.log', 'w') as helper_errors:
        helper_process = subprocess.Popen(helper_arguments, env=env, stdout=subprocess.PIPE, stderr=helper_errors, text=True)
        helper_start = json.loads(helper_process.stdout.readline())
        assert helper_start['phase'] == 'rpc-starting', 'Sandbox helper did not reach actual RPC initialization'
        active_queries = []
        while helper_process.poll() is None and len(active_queries) < 64:
            question = questions[len(active_queries) % len(questions)]
            query_started = time.time(); began = time.monotonic()
            response = request('POST', '/v1/search', {'query': question['query'], 'mode': 'hybrid'})
            query_finished = time.time()
            assert not response['degraded']
            row = {'filtered': False, 'question_id': question['id'], 'http_ms': (time.monotonic()-began)*1000,
                   'phase': 'active_sandbox', 'started_at_epoch': query_started, 'completed_at_epoch': query_finished}
            timings.append(row); active_queries.append(row)
            time.sleep(0.05)
        helper_stdout, _ = helper_process.communicate(timeout=180)
        assert helper_process.returncode == 0, 'Actual sandbox descriptor workload failed; see isolated helper error log'
    helper_result = json.loads(helper_stdout)
    active_sandbox_finished = time.time()
    active_after = read_cgroup_memory()
    memory.append({'at': time.time(), 'containers': active_after})
    overlapping_queries = [row for row in active_queries if any(row['started_at_epoch'] < result['completed_at_epoch'] and row['completed_at_epoch'] > result['started_at_epoch'] for result in helper_result['results'])]
    assert overlapping_queries, 'No actual daemon query overlapped the descriptor extraction RPCs'
    active_samples = [row for row in memory if active_sandbox_started <= row['at'] <= active_sandbox_finished]
    active_sandbox = {'workload': 'four concurrent 80-page synthetic text PDFs through actual descriptor RPC plus known hybrid daemon queries',
                      'admission_path': 'direct UID1654 descriptor helper, not daemon upload/publication',
                      'corpus_unchanged': True, 'helper': helper_result,
                      'overlapping_http_queries': len(overlapping_queries), 'queries': len(active_queries),
                      'cgroups_before': active_before, 'cgroups_after': active_after,
                      'combined_sampled_peak_bytes': max((sum(v['current_bytes'] for v in row['containers'].values()) for row in active_samples), default=None),
                      'combined_separate_cgroup_peak_upper_bound_bytes': sum(v['peak_bytes'] for v in active_after.values())}
    docker('exec', '-T', 'daemon', 'touch', '/srv/secondbrain/qualification-heap/request-full-gc', stdout=subprocess.DEVNULL)
    time.sleep(3)
    heap_before = docker('exec', '-T', 'daemon', 'cat', '/srv/secondbrain/qualification-heap/heap.jsonl', capture_output=True, text=True).stdout
    samples = [json.loads(line) for line in heap_before.splitlines()]
    forced = [row for row in samples if row['forced_full_gc']]
    assert forced, 'Startup hook failed to record forced full GC'
    assert forced[-1]['vector_statistics']['LiveSlots'] == args.documents*10, 'Forced GC did not retain the complete live vector array'
    assert forced[-1]['vector_statistics']['RetainedSlots'] == 0, 'Qualification force-GC still retains retired vector slots'
    assert forced[-1]['vector_statistics']['AllocatedSlots'] <= math.ceil(args.documents*10/65536)*65536, 'Allocated vector arrays exceed bounded dense-slot padding'
    checkpoint_trace_result = docker('--profile', 'qualification', 'run', '--rm', '--no-deps', '-T', 'sandbox-load-helper',
                                     '--read-traces', '/data/index/index.db', capture_output=True, text=True)
    checkpoint_traces = [json.loads(raw) for raw in json.loads(checkpoint_trace_result.stdout)]
    assert len(checkpoint_traces) >= len(timings), 'Checkpoint traces do not cover all measured requests'
    checkpoint_traces = checkpoint_traces[-len(timings):]
    for timing, trace in zip(timings, checkpoint_traces): timing['repository_ms'] = trace['ElapsedMilliseconds']
    lexical_cache = None
    warm_runtime = [trace.get('Runtime', {}) for timing, trace in zip(timings, checkpoint_traces) if timing.get('warm')]
    if any('LexicalContributionHit' in metadata for metadata in warm_runtime):
        assert all('LexicalContributionHit' in metadata and 'LexicalPeakBytes' in metadata for metadata in warm_runtime)
        lexical_cache = {'all_warm_samples_used_contributions': all(metadata['LexicalContributionHit'] for metadata in warm_runtime),
                         'maximum_derived_bytes': max(metadata['LexicalDerivedBytes'] for metadata in warm_runtime),
                         'maximum_peak_bytes': max(metadata['LexicalPeakBytes'] for metadata in warm_runtime),
                         'maximum_primitive_loads': max(metadata['LexicalPrimitiveLoads'] for metadata in warm_runtime),
                         'accounted_peak_below_384_mib': max(metadata['LexicalPeakBytes'] for metadata in warm_runtime) <= 384*1024*1024}
        assert lexical_cache['all_warm_samples_used_contributions'] and lexical_cache['accounted_peak_below_384_mib']
    startup_checkpoint_logs = docker('logs', '--no-color', 'daemon', capture_output=True, text=True).stdout
    (root/'daemon-startup.log').write_text(startup_checkpoint_logs)
    startup_matches = re.findall(r'Loaded (\d+) committed vectors before readiness: dimensions=(\d+) elapsed_ms=([0-9.]+)', startup_checkpoint_logs)
    assert startup_matches
    startup_match = startup_matches[-1]
    assert int(startup_match[0]) == args.documents*10 and int(startup_match[1]) == 1024
    preload_ms = float(startup_match[2])
    checkpoint_latency = {str(filtered): {'warm_repository_p95_ms': percentile([row['repository_ms'] for row in timings if row['filtered']==filtered and row.get('warm')]),
                                          'warm_http_p95_ms': percentile([row['http_ms'] for row in timings if row['filtered']==filtered and row.get('warm')])} for filtered in (True,False)}
    startup_gate = forced[-1]['process_to_health_seconds_upper_bound'] < 3 + preload_ms / 1000
    heap_gate = forced[-1]['gc_heap_size_bytes'] < 1_000_000_000 + forced[-1]['vector_statistics']['VectorBytes']
    checkpoint = {'stage': 'pre-rebuild', 'documents': args.documents, 'chunks': args.documents*10, 'timings': timings,
                  'baseline': [{'filtered': key[0], 'question_id': key[1], 'results': value} for key, value in baseline.items()],
                  'latency': checkpoint_latency, 'structured_http_p95_ms': percentile(structured),
                  'startup_vector_load_ms': preload_ms, 'process_to_health_seconds_upper_bound': forced[-1]['process_to_health_seconds_upper_bound'],
                  'forced_gc': forced[-1], 'active_sandbox_phase': active_sandbox,
                  'lexical_cache': lexical_cache,
                  'gates': {'filtered_repository_p95_below_150ms': checkpoint_latency['True']['warm_repository_p95_ms'] < 150,
                            'unfiltered_repository_p95_below_400ms': checkpoint_latency['False']['warm_repository_p95_ms'] < 400,
                            'structured_http_p95_below_50ms': percentile(structured) < 50,
                            'startup_below_3s_plus_measured_vector_load': startup_gate,
                            'gc_heap_below_1gb_plus_actual_vector_array': heap_gate}}
    (root/'fullscale-pre-rebuild-checkpoint.json').write_text(json.dumps(checkpoint, indent=2))
    print(json.dumps({key:value for key,value in checkpoint.items() if key not in ('timings','baseline','active_sandbox_phase')}, indent=2), flush=True)
    before_documents = {ulid(i): request('GET', '/v1/documents/'+ulid(i)+'?include=text,chunks')
                        for i in (0, args.documents//10-1, args.documents//2, args.documents-1)}
    rebuild_wall_start = time.time()
    began = time.monotonic(); rebuilt = request('POST', '/v1/maintenance/rebuild'); rebuild_seconds = time.monotonic()-began
    rebuild_wall_end = time.time()
    replay_requests_during_rebuild = [json.loads(line) for line in (root/'provider-replay.jsonl').read_text().splitlines()
                                     if rebuild_wall_start <= json.loads(line)['at'] <= rebuild_wall_end]
    assert not replay_requests_during_rebuild, 'Cached rebuild unexpectedly called the embedding provider'
    assert rebuild_seconds < 3600
    assert rebuilt['documents'] == args.documents, rebuilt
    (root/'fullscale-rebuild-api-checkpoint.json').write_text(json.dumps({'rebuild_api_completed': True, 'rebuild_response': rebuilt,
        'rebuild_seconds': rebuild_seconds, 'embedding_provider_requests_during_rebuild': len(replay_requests_during_rebuild),
        'immediate_hybrid_equality_established': False}, indent=2)+'\n')
    for identifier, before in before_documents.items():
        after = request('GET', '/v1/documents/'+identifier+'?include=text,chunks')
        assert [before[k] for k in ('id','revision','text','chunks','path')] == [after[k] for k in ('id','revision','text','chunks','path')]
    post_rebuild_timings = []
    for (filtered, question_id), expected in baseline.items():
        question = next(q for q in questions if q['id'] == question_id)
        query_began = time.monotonic()
        response = request('POST', '/v1/search', {'query': question['query'], 'mode': 'hybrid', 'filters': {'type': 'article'} if filtered else None})
        assert not response['degraded']
        assert fingerprint(response['results']) == expected, 'Rebuild changed hybrid result locators'
        post_rebuild_timings.append({'filtered': filtered, 'question_id': question_id, 'http_ms': (time.monotonic()-query_began)*1000, 'baseline_equal': True})
        (root/'fullscale-post-rebuild-search-checkpoint.json').write_text(json.dumps(post_rebuild_timings, indent=2)+'\n')
    post_gc_requested = time.time()
    docker('exec', '-T', 'daemon', 'touch', '/srv/secondbrain/qualification-heap/request-full-gc', stdout=subprocess.DEVNULL)
    post_rebuild_forced = None
    for _ in range(30):
        time.sleep(1)
        post_rebuild_heap = docker('exec', '-T', 'daemon', 'cat', '/srv/secondbrain/qualification-heap/heap.jsonl', capture_output=True, text=True).stdout
        candidates = [json.loads(line) for line in post_rebuild_heap.splitlines()
                      if json.loads(line)['forced_full_gc'] and datetime.fromisoformat(json.loads(line)['at']).timestamp() >= post_gc_requested]
        if candidates: post_rebuild_forced = candidates[-1]; break
    assert post_rebuild_forced is not None, 'Post-rebuild full GC sample missing'
    assert post_rebuild_forced['vector_statistics']['LiveSlots'] == args.documents*10 and post_rebuild_forced['vector_statistics']['RetainedSlots'] == 0
    assert post_rebuild_forced['vector_statistics']['AllocatedSlots'] <= math.ceil(args.documents*10/65536)*65536
    (root/'fullscale-post-rebuild-heap.jsonl').write_text(post_rebuild_heap)
    stop_sample.set(); sampler.join(timeout=10)
    docker('stop', 'daemon', stdout=subprocess.DEVNULL)
    # Offline inspection after clean shutdown: no inconsistent copies of live WAL databases.
    provision(f'chown -R {os.getuid()}:{os.getgid()} /srv/secondbrain')
    index = sqlite3.connect(root/'data/index/index.db')
    state = sqlite3.connect(root/'data/state/state.db')
    assert index.execute('select count(*) from chunks').fetchone()[0] == args.documents*10
    assert index.execute('select count(*) from chunk_vectors').fetchone()[0] == args.documents*10
    vector_mismatches = index.execute('''SELECT COUNT(*) FROM chunk_vectors v JOIN chunks c ON c.rowid=v.chunk_rowid
        LEFT JOIN embedding_cache e ON e.space_id=v.space_id AND e.input_hash=c.input_hash
        WHERE e.input_hash IS NULL OR v.vector IS NOT e.vector''').fetchone()[0]
    assert vector_mismatches == 0, 'Rebuilt vectors differ byte-for-byte from their exact recorded-input cache'
    owner_mismatches = index.execute('''SELECT COUNT(*) FROM chunks c LEFT JOIN embedding_cache_owners o
        ON o.document_id=c.document_id AND o.input_hash=c.input_hash WHERE o.document_id IS NULL''').fetchone()[0]
    assert owner_mismatches == 0, 'Rebuilt chunk cache ownership is missing'
    assert index.execute('select count(*) from document_text').fetchone()[0] == args.documents
    assert index.execute('select count(*) from indexed_document_sources').fetchone()[0] == args.documents
    assert index.execute('select count(*) from indexed_documents').fetchone()[0] == args.documents
    assert state.execute('select count(*) from document_projections').fetchone()[0] == args.documents
    assert state.execute("select count(*) from documents where status='indexed' and indexed_revision=revision and publish_state='published'").fetchone()[0] == args.documents
    state.execute('ATTACH DATABASE ? AS idx', (str(root/'data/index/index.db'),))
    # Compare every rebuilt chunk/locator to its authoritative durable projection, not just sampled API records.
    mismatches = state.execute('''
        SELECT COUNT(*) FROM document_projections d JOIN payloads p ON p.id=d.payload_id
        JOIN json_each(CAST(p.bytes AS TEXT),'$.Chunks') j
        LEFT JOIN idx.chunks c ON c.id=json_extract(j.value,'$.Id')
        WHERE c.id IS NULL OR c.document_id<>d.document_id OR c.revision<>d.revision
           OR c.ordinal<>json_extract(j.value,'$.Ordinal') OR c.context<>json_extract(j.value,'$.Context')
           OR c.text<>json_extract(j.value,'$.Text') OR c.start_offset<>json_extract(j.value,'$.start_offset')
           OR c.end_offset<>json_extract(j.value,'$.end_offset') OR c.input_hash<>json_extract(j.value,'$.input_hash')
           OR c.token_count<>json_extract(j.value,'$.token_count')
           OR c.page IS NOT json_extract(j.value,'$.Page')
    ''').fetchone()[0]
    assert mismatches == 0, 'Full rebuilt corpus differs from authoritative durable chunk locators'
    text_mismatches = state.execute('''
        SELECT COUNT(*) FROM document_projections d JOIN payloads p ON p.id=d.payload_id
        LEFT JOIN idx.document_text t ON t.document_id=d.document_id
        LEFT JOIN idx.indexed_document_sources s ON s.document_id=d.document_id
        WHERE t.document_id IS NULL OR s.document_id IS NULL OR t.revision<>d.revision OR t.generation_json<>d.generation_json
           OR t.lossless_text<>json_extract(CAST(p.bytes AS TEXT),'$.lossless_text')
           OR t.search_text<>json_extract(CAST(p.bytes AS TEXT),'$.search_text')
           OR CAST(t.offset_map AS TEXT)<>json_extract(CAST(p.bytes AS TEXT),'$.offset_map')
           OR s.path IS NOT json_extract(CAST(p.bytes AS TEXT),'$.Path')
    ''').fetchone()[0]
    assert text_mismatches == 0
    assert state.execute('select count(*) from payload_refs where referrer_kind=\'document-projection\'').fetchone()[0] == args.documents
    assert index.execute('select count(*) from embedding_cache_owners').fetchone()[0] == args.documents
    assert len(list((root/'data/vault/originals').glob('*.md'))) == args.documents
    traces = [json.loads(row[0]) for row in index.execute('select trace_json from retrieval_traces order by id')]
    assert len(traces) >= len(timings)
    timed_traces = traces[-(len(timings)+len(baseline)):-len(baseline)]
    assert len(timed_traces) == len(timings)
    for timing, trace in zip(timings, timed_traces): timing['repository_ms'] = trace['ElapsedMilliseconds']
    daemon_id = docker('ps', '-aq', 'daemon', capture_output=True, text=True).stdout.strip()
    daemon_image = run(['docker','inspect','--format','{{.Image}}',daemon_id], capture_output=True, text=True).stdout.strip()
    daemon_finished_at = run(['docker','inspect','--format','{{.State.FinishedAt}}',daemon_id], capture_output=True, text=True).stdout.strip()
    equality_shutdown_upper_bound = datetime.fromisoformat(daemon_finished_at.replace('Z', '+00:00')).timestamp() - (root/'fullscale-pre-rebuild-checkpoint.json').stat().st_mtime
    assert equality_shutdown_upper_bound > rebuild_seconds
    checkpoint['gates']['rebuild_api_below_one_hour'] = rebuild_seconds < 3600
    checkpoint['gates']['rebuild_through_all_verified_equal_results_upper_bound_below_one_hour'] = equality_shutdown_upper_bound < 3600
    startup_logs = docker('logs', '--no-color', 'daemon', capture_output=True, text=True).stdout
    (root/'daemon-startup.log').write_text(startup_logs)
    vector_loads = re.findall(r'Loaded (\d+) committed vectors before readiness: dimensions=(\d+) elapsed_ms=([0-9.]+)', startup_logs)
    assert vector_loads, 'Actual startup vector preload duration metadata was not recorded'
    vector_load = vector_loads[-1]
    loaded_count, loaded_dimensions, load_ms = int(vector_load[0]), int(vector_load[1]), float(vector_load[2])
    assert loaded_count == args.documents*10 and loaded_dimensions == 1024
    process_health = forced[-1]['process_to_health_seconds_upper_bound']
    report = {'measured_at': time.time(), 'daemon_image_id': daemon_image,
              'host': {'os': platform.platform(), 'logical_cpus': os.cpu_count(), 'memory_total_kib': int(next(line.split()[1] for line in pathlib.Path('/proc/meminfo').read_text().splitlines() if line.startswith('MemTotal:')))},
              'compiled_heap_hook_sha256': hashlib.sha256(pathlib.Path(env['SECONDBRAIN_FULLSCALE_HOOK_DIRECTORY'],'FullScaleHeapHook.dll').read_bytes()).hexdigest(),
              'recorded_embeddings_sha256': hashlib.sha256(pathlib.Path('eval/embeddings.json').read_bytes()).hexdigest(),
              'profile': 'actual isolated production daemon; recorded Qwen replay; heap startup hook',
              'lexical_cache': lexical_cache,
              'readiness_passed': True, 'provider_fixtures': {'embed': 'hash-verified recorded Qwen1024d HTTP replay on host bridge', 'chat_enrich': 'existing mock-provider sidecar'},
              'documents': args.documents, 'chunks': args.documents*10, 'dimensions': 1024,
              'compose_end_to_end_startup_seconds': startup_seconds,
              'process_to_health_seconds_upper_bound': forced[-1]['process_to_health_seconds_upper_bound'],
              'cold_repository_search_ms': timings[0]['repository_ms'],
              'startup_vector_load_ms': load_ms, 'startup_vectors_loaded': loaded_count, 'startup_vector_dimensions': loaded_dimensions,
              'startup_process_health_below_3s_plus_vector_load': process_health < 3 + load_ms / 1000,
              'rebuild_seconds_excluding_provider_inference': rebuild_seconds,
              'rebuild_verified_results_and_clean_shutdown_upper_bound_seconds': equality_shutdown_upper_bound,
              'rebuild_bound_from_checkpoint_epoch': (root/'fullscale-pre-rebuild-checkpoint.json').stat().st_mtime,
              'rebuild_bound_to_daemon_finished_at': daemon_finished_at,
              'first_post_rebuild_repository_ms': traces[-len(baseline)]['ElapsedMilliseconds'], 'embedding_provider_requests_during_rebuild': len(replay_requests_during_rebuild),
              'post_rebuild_timings': post_rebuild_timings, 'post_rebuild_forced_gc': post_rebuild_forced,
              'post_rebuild_gc_heap_below_1gb_plus_actual_vector_array': post_rebuild_forced['gc_heap_size_bytes'] < 1_000_000_000 + post_rebuild_forced['vector_statistics']['VectorBytes'],
              'rebuild_results_and_locators_equal': True, 'all_rebuilt_durable_chunks_and_text_verified': True, 'all_rebuilt_vectors_equal_recorded_cache_bytes': True, 'all_rebuilt_chunks_have_cache_ownership': True, 'structured_http_p95_ms': percentile(structured),
              'forced_gc_live_managed_bytes_with_vector_array': forced[-1]['managed_live_bytes'],
              'forced_gc_heap_size_bytes': forced[-1]['gc_heap_size_bytes'],
              'forced_gc_fragmented_bytes': forced[-1]['gc_fragmented_bytes'],
              'forced_gc_committed_bytes': forced[-1]['gc_committed_bytes'],
              'gc_heap_size_minus_allocated_vector_array': forced[-1]['gc_heap_size_bytes'] - forced[-1]['vector_statistics']['VectorBytes'],
              'gc_heap_size_below_1gb_plus_allocated_vector_array': forced[-1]['gc_heap_size_bytes'] < 1_000_000_000 + forced[-1]['vector_statistics']['VectorBytes'],
              'live_vector_float32_payload_bytes': args.documents*10*1024*4,
              'actual_live_slot_array_allocated_bytes': forced[-1]['vector_statistics']['VectorBytes'],
              'allocated_vector_slots': forced[-1]['vector_statistics']['AllocatedSlots'],
              'vector_padding_bytes': forced[-1]['vector_statistics']['VectorBytes'] - args.documents*10*1024*4,
              'managed_live_bytes_minus_allocated_vector_array': forced[-1]['managed_live_bytes'] - forced[-1]['vector_statistics']['VectorBytes'],
              'managed_heap_below_1gb_plus_allocated_vector_array': forced[-1]['managed_live_bytes'] < 1_000_000_000 + forced[-1]['vector_statistics']['VectorBytes'],
              'managed_live_bytes_minus_vector_payload': forced[-1]['managed_live_bytes'] - args.documents*10*1024*4,
              'managed_heap_below_1gb_plus_live_vector_payload': forced[-1]['managed_live_bytes'] < 1_000_000_000 + args.documents*10*1024*4,
              'combined_daemon_sandbox_sampled_peak_bytes': max(sum(v['current_bytes'] for v in row['containers'].values()) for row in memory),
              'combined_separate_cgroup_peak_upper_bound_bytes': sum(max(row['containers'][name]['peak_bytes'] for row in memory) for name in ('daemon','extractor')),
              'gates': checkpoint['gates'], 'active_sandbox_phase': active_sandbox, 'cgroup_sample_errors': sorted(set(errors)), 'timings': timings,
              'latency': {str(filtered): {'warm_repository_p95_ms': percentile([row['repository_ms'] for row in timings if row['filtered']==filtered and row.get('warm')]),
                                          'warm_http_p95_ms': percentile([row['http_ms'] for row in timings if row['filtered']==filtered and row.get('warm')])} for filtered in (True,False)},
              'limitations': ['32-topic replicated seed, topic recall is not independent 100k-document relevance',
                              'Private host bridge replay is not separate-LAN inference topology or provider latency',
                              'One-second heap sampler adds isolated qualification instrumentation overhead',
                              'Combined simultaneous cgroup peak is sampled; separate lifetime peaks give an upper bound',
                              'Active sandbox phase is four concurrent 80-page direct descriptor RPCs with queries; it does not establish every extraction workload or daemon admission throughput']}
    (root/'fullscale-daemon-results.json').write_text(json.dumps(report, indent=2))
    (root/'cgroup-memory.json').write_text(json.dumps(memory, indent=2))
    print(json.dumps({k:v for k,v in report.items() if k not in ('timings',)}, indent=2))
except Exception as failure:
    (root/'fullscale-failure.json').write_text(json.dumps({'error_type': type(failure).__name__, 'error': str(failure), 'at': time.time()}, indent=2))
    (root/'cgroup-memory.json').write_text(json.dumps(memory, indent=2))
    raise
finally:
    stop_sample.set()
    if helper_process is not None and helper_process.poll() is None: helper_process.terminate(); helper_process.wait(timeout=10)
    if provider is not None: provider.terminate(); provider.wait(timeout=10)
