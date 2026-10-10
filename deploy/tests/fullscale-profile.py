#!/usr/bin/env python3
"""Read-only retrieval profiling of an already qualified authoritative fixture; no seed/rebuild."""
import pathlib

# Share only the isolated deployment/session definitions, before the workload entry point.
source = pathlib.Path('deploy/tests/fullscale-daemon.py').read_text()
exec(compile(source.split('\ntry:\n    pdf_root =', 1)[0], 'fullscale-daemon-definitions', 'exec'))
assert args.skip_seed, 'Profiling requires an existing validated seed'
baseline_report = json.loads((root/'fullscale-pre-rebuild-checkpoint.json').read_text())
expected = {(row['filtered'], row['question_id']): row['results'] for row in baseline_report['baseline']}
try:
    provision('chown -R 1654:1654 /srv/secondbrain')
    docker('up', '--detach', '--wait', '--wait-timeout', '45', 'egress', stdout=subprocess.DEVNULL)
    provider = subprocess.Popen(['python3', 'deploy/tests/fullscale-recorded-provider.py', '--metrics', str(root/'provider-profile.jsonl')],
                                stdout=subprocess.DEVNULL, stderr=open(root/'provider-profile-errors.log', 'a'))
    time.sleep(.5)
    assert provider.poll() is None
    docker('up', '--detach', '--wait', '--wait-timeout', '120', 'daemon', 'extractor', 'mock-provider', stdout=subprocess.DEVNULL)
    wait_health(); login(); assert request('GET', '/ready')['ready']
    sampler = threading.Thread(target=sample_memory, daemon=True); sampler.start()
    daemon_id = docker('ps', '-q', 'daemon', capture_output=True, text=True).stdout.strip()
    image_id = subprocess.run(['docker', 'inspect', '--format', '{{.Image}}', daemon_id], env=env, check=True, capture_output=True, text=True).stdout.strip()
    production_hash_lines = docker('exec', '-T', 'daemon', 'sha256sum', '/app/server/SecondBrain.Storage.dll', '/app/server/SecondBrain.Core.dll', capture_output=True, text=True).stdout.splitlines()
    provenance = {'image_id': image_id, 'production_assemblies': [{'path': line.split()[1], 'sha256': line.split()[0]} for line in production_hash_lines],
                  'qualification_assemblies': [{'path': str(pathlib.Path(env[key])/name), 'sha256': hashlib.sha256((pathlib.Path(env[key])/name).read_bytes()).hexdigest(), 'configuration': 'Release'}
                                              for key, name in [('SECONDBRAIN_FULLSCALE_HOOK_DIRECTORY', 'FullScaleHeapHook.dll'), ('SECONDBRAIN_FULLSCALE_SANDBOX_HELPER_DIRECTORY', 'FullScaleSandboxHelper.dll')]]}
    questions = [json.loads(line) for line in pathlib.Path('eval/questions.jsonl').read_text().splitlines() if line]
    invalidation = None
    primer = None
    if args.invalidate_first_query:
        question = questions[0]; began = time.monotonic()
        response = request('POST', '/v1/search', {'query': question['query'], 'mode': 'hybrid', 'filters': {'type': 'article'}})
        assert not response['degraded']
        assert json.loads(json.dumps(fingerprint(response['results']))) == expected[(True, question['id'])]
        primer = {'question_id': question['id'], 'filtered': True, 'http_ms': (time.monotonic()-began)*1000,
                  'baseline_equal': True, 'purpose': 'prime lazy retrieval state before no-write invalidation; excluded from warm and post-invalidation timings'}
        (root/'fullscale-profile-primer.json').write_text(json.dumps(primer, indent=2)+'\n')
        docker('exec', '-T', 'daemon', 'sh', '-c', 'rm -f /srv/secondbrain/qualification-heap/invalidated-all-documents.json; touch /srv/secondbrain/qualification-heap/request-invalidate-all-documents', stdout=subprocess.DEVNULL)
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline:
            probe = subprocess.run(compose + ['exec', '-T', 'daemon', 'cat', '/srv/secondbrain/qualification-heap/invalidated-all-documents.json'], env=env, capture_output=True, text=True)
            if probe.returncode == 0:
                invalidation = json.loads(probe.stdout)
                assert invalidation['success'] and invalidation['documents'] == args.documents and not invalidation['acquire_after_invalidation']
                break
            time.sleep(.1)
        assert invalidation is not None, 'Actual daemon registry invalidation acknowledgement missing'
        (root/'fullscale-invalidation-ack.json').write_text(json.dumps(invalidation, indent=2)+'\n')
    timings = []
    for filtered in (True, False):
        for question in questions:
            began = time.monotonic()
            response = request('POST', '/v1/search', {'query': question['query'], 'mode': 'hybrid', 'filters': {'type': 'article'} if filtered else None})
            assert not response['degraded']
            actual = json.loads(json.dumps(fingerprint(response['results'])))
            assert actual == expected[(filtered, question['id'])], 'Retained-corpus retrieval changed baseline results/order/locators'
            timings.append({'filtered': filtered, 'question_id': question['id'], 'http_ms': (time.monotonic()-began)*1000, 'baseline_equal': True,
                            'first_after_all_document_invalidation': bool(invalidation and not timings)})
            (root/'fullscale-profile-progress.json').write_text(json.dumps({'invalidation': invalidation, 'timings': timings}, indent=2)+'\n')
    for filtered in (True, False):
        for repeat in range(args.profile_warm_repeats):
            question = questions[repeat % len(questions)]
            began = time.monotonic()
            response = request('POST', '/v1/search', {'query': question['query'], 'mode': 'hybrid', 'filters': {'type': 'article'} if filtered else None})
            assert not response['degraded']
            assert json.loads(json.dumps(fingerprint(response['results']))) == expected[(filtered, question['id'])]
            timings.append({'filtered': filtered, 'question_id': question['id'], 'warm': True, 'http_ms': (time.monotonic()-began)*1000, 'baseline_equal': True})
            (root/'fullscale-profile-progress.json').write_text(json.dumps({'invalidation': invalidation, 'timings': timings}, indent=2)+'\n')
    result = docker('--profile', 'qualification', 'run', '--rm', '--no-deps', '-T', 'sandbox-load-helper', '--read-traces', '/data/index/index.db', capture_output=True, text=True)
    traces = [json.loads(raw) for raw in json.loads(result.stdout)][-len(timings):]
    assert len(traces) == len(timings)
    for timing, trace in zip(timings, traces):
        timing['trace'] = trace
    structured = []
    for _ in range(20):
        began = time.monotonic(); page = request('POST', '/v1/query', {'type': 'article'})
        structured.append((time.monotonic()-began)*1000)
        assert len(page['records']) == 50
    active_started = time.time()
    active_before = read_cgroup_memory()
    helper_arguments = compose + ['--profile', 'qualification', 'run', '--rm', '--no-deps', '-T', 'sandbox-load-helper',
                                  '/run/secondbrain/extractor.sock'] + ['/fixtures/sandbox-' + str(i) + '.pdf' for i in range(4)]
    with open(root/'sandbox-helper-final-profile-errors.log', 'w') as helper_errors:
        helper_process = subprocess.Popen(helper_arguments, env=env, stdout=subprocess.PIPE, stderr=helper_errors, text=True)
        helper_start = json.loads(helper_process.stdout.readline()); assert helper_start['phase'] == 'rpc-starting'
        active_queries = []
        while helper_process.poll() is None and len(active_queries) < 64:
            question = questions[len(active_queries) % len(questions)]
            query_started = time.time(); began = time.monotonic()
            response = request('POST', '/v1/search', {'query': question['query'], 'mode': 'hybrid'})
            assert not response['degraded']
            assert json.loads(json.dumps(fingerprint(response['results']))) == expected[(False, question['id'])]
            active_queries.append({'question_id': question['id'], 'http_ms': (time.monotonic()-began)*1000,
                                   'started_at_epoch': query_started, 'completed_at_epoch': time.time()})
            time.sleep(.05)
        helper_stdout, _ = helper_process.communicate(timeout=180)
        assert helper_process.returncode == 0, 'Actual sandbox descriptor workload failed'
    helper_result = json.loads(helper_stdout)
    active_finished = time.time(); active_after = read_cgroup_memory()
    memory.append({'at': time.time(), 'containers': active_after})
    overlapping = [row for row in active_queries if any(row['started_at_epoch'] < result['completed_at_epoch'] and row['completed_at_epoch'] > result['started_at_epoch'] for result in helper_result['results'])]
    assert overlapping, 'No actual hybrid query overlapped descriptor extraction'
    active_samples = [row for row in memory if active_started <= row['at'] <= active_finished]
    active_sandbox = {'admission_path': 'four concurrent80-page PDFs via direct descriptor helper, not daemon publication', 'corpus_unchanged': True,
                      'helper': helper_result, 'queries': active_queries, 'overlapping_queries': len(overlapping), 'cgroups_before': active_before, 'cgroups_after': active_after,
                      'combined_sampled_peak_bytes': max((sum(v['current_bytes'] for v in row['containers'].values()) for row in active_samples), default=None)}
    forced_requested = time.time()
    docker('exec', '-T', 'daemon', 'touch', '/srv/secondbrain/qualification-heap/request-full-gc', stdout=subprocess.DEVNULL)
    forced = None
    for _ in range(30):
        time.sleep(1)
        raw_heap = docker('exec', '-T', 'daemon', 'cat', '/srv/secondbrain/qualification-heap/heap.jsonl', capture_output=True, text=True).stdout
        heap_rows = [json.loads(line) for line in raw_heap.splitlines()]
        candidates = [row for row in heap_rows if row['forced_full_gc'] and datetime.fromisoformat(row['at']).timestamp() >= forced_requested]
        if candidates: forced = candidates[-1]; break
    assert forced is not None, 'Final-image forced GC sample missing'
    assert forced['vector_statistics']['LiveSlots'] == args.documents*10
    assert forced['vector_statistics']['RetainedSlots'] == 0
    assert forced['vector_statistics']['AllocatedSlots'] <= math.ceil(args.documents*10/65536)*65536
    startup_logs = docker('logs', '--no-color', 'daemon', capture_output=True, text=True).stdout
    preload = re.findall(r'Loaded (\d+) committed vectors before readiness: dimensions=(\d+) elapsed_ms=([0-9.]+)', startup_logs)[-1]
    assert int(preload[0]) == args.documents*10 and int(preload[1]) == 1024
    preload_ms = float(preload[2]); vector_bytes = forced['vector_statistics']['VectorBytes']; payload_bytes = args.documents*10*1024*4
    memory_report = {'assembly_provenance': provenance, 'forced_gc': forced, 'startup_vector_load_ms': preload_ms,
                     'process_to_health_seconds_upper_bound': forced['process_to_health_seconds_upper_bound'],
                     'startup_below_3s_plus_actual_preload': forced['process_to_health_seconds_upper_bound'] < 3 + preload_ms/1000,
                     'logical_vector_payload_bytes': payload_bytes, 'actual_vector_array_bytes': vector_bytes, 'vector_padding_bytes': vector_bytes-payload_bytes,
                     'gc_heap_minus_actual_vector_array_bytes': forced['gc_heap_size_bytes']-vector_bytes,
                     'gc_heap_minus_logical_vector_payload_bytes': forced['gc_heap_size_bytes']-payload_bytes,
                     'gc_heap_below_1gb_plus_actual_vector_array': forced['gc_heap_size_bytes'] < 1_000_000_000+vector_bytes,
                     'active_sandbox': active_sandbox, 'structured_http_p95_ms': percentile(structured),
                     'combined_daemon_sandbox_sampled_peak_bytes': max(sum(v['current_bytes'] for v in row['containers'].values()) for row in memory),
                     'combined_separate_cgroup_peak_upper_bound_bytes': sum(max(row['containers'][name]['peak_bytes'] for row in memory) for name in ('daemon', 'extractor')),
                     'cgroup_sample_errors': sorted(set(errors)), 'limitations': ['one-second cgroup/heap sampling', 'direct descriptor workload does not measure daemon publication', 'recorded provider replay is not real inference or separate-LAN topology']}
    (root/'fullscale-final-image-startup-memory.json').write_text(json.dumps(memory_report, indent=2)+'\n')
    (root/'fullscale-final-image-heap.jsonl').write_text(raw_heap)
    (root/'fullscale-final-image-cgroup-memory.json').write_text(json.dumps(memory, indent=2)+'\n')
    (root/'fullscale-final-image-startup.log').write_text(startup_logs)
    report = {'workload': 'retained100k/1M profiling-only; no seed, DB publication or rebuild', 'timings': timings, 'primer_before_invalidation': primer, 'invalidation': invalidation, 'assembly_provenance': provenance,
              'all_baseline_fingerprints_equal': True, 'recorded_provider_profile': 'exact recorded Qwen query-vector replay; provider time separate', 'startup_memory': memory_report}
    if args.profile_warm_repeats:
        warm_runtime = [row['trace'].get('Runtime', {}) for row in timings if row.get('warm')]
        if any('LexicalContributionHit' in metadata for metadata in warm_runtime):
            assert all('LexicalContributionHit' in metadata and 'LexicalPeakBytes' in metadata for metadata in warm_runtime)
            report['lexical_cache'] = {'all_warm_samples_used_contributions': all(metadata['LexicalContributionHit'] for metadata in warm_runtime),
                                       'maximum_derived_bytes': max(metadata['LexicalDerivedBytes'] for metadata in warm_runtime),
                                       'maximum_peak_bytes': max(metadata['LexicalPeakBytes'] for metadata in warm_runtime),
                                       'maximum_primitive_loads': max(metadata['LexicalPrimitiveLoads'] for metadata in warm_runtime),
                                       'accounted_peak_below_384_mib': max(metadata['LexicalPeakBytes'] for metadata in warm_runtime) <= 384*1024*1024}
            assert report['lexical_cache']['all_warm_samples_used_contributions'] and report['lexical_cache']['accounted_peak_below_384_mib']
        report['warm_repository_p95_ms'] = {str(filtered): percentile([row['trace']['ElapsedMilliseconds'] for row in timings if row['filtered'] == filtered and row.get('warm')]) for filtered in (True, False)}
        report['gates'] = {'filtered_repository_p95_below_150ms': report['warm_repository_p95_ms']['True'] < 150,
                           'unfiltered_repository_p95_below_400ms': report['warm_repository_p95_ms']['False'] < 400,
                           'structured_http_p95_below_50ms': memory_report['structured_http_p95_ms'] < 50,
                           'startup_below_3s_plus_actual_preload': memory_report['startup_below_3s_plus_actual_preload'],
                           'gc_heap_below_1gb_plus_actual_vector_array': memory_report['gc_heap_below_1gb_plus_actual_vector_array']}
    if invalidation:
        report['first_hybrid_after_all_document_invalidation'] = timings[0]
        report.setdefault('gates', {})['first_hybrid_after_all_document_invalidation_completed_and_equal'] = True
    (root/'fullscale-retrieval-profile.json').write_text(json.dumps(report, indent=2)+'\n')
    print(json.dumps(report, indent=2))
except Exception as failure:
    (root/'fullscale-profile-failure.json').write_text(json.dumps({'error_type': type(failure).__name__, 'error': str(failure),
        'http_status': getattr(failure, 'code', None), 'at': time.time(), 'invalidation': locals().get('invalidation'), 'completed_timings': locals().get('timings', [])}, indent=2)+'\n')
    raise
finally:
    stop_sample.set()
    if 'sampler' in locals(): sampler.join(timeout=10)
    if helper_process is not None and helper_process.poll() is None: helper_process.terminate(); helper_process.wait(timeout=10)
    if provider is not None: provider.terminate(); provider.wait(timeout=10)
    docker('stop', 'daemon', stdout=subprocess.DEVNULL)
