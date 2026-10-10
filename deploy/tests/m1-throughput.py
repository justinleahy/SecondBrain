"""Measure an explicitly selected isolated daemon; never print credentials.

Example: SECONDBRAIN_QUALIFICATION_ORIGIN=http://127.0.0.1:7171
and SECONDBRAIN_QUALIFICATION_PASSWORD supplied by the operator, then
python3 deploy/tests/m1-throughput.py --provider-mode providerless.
Optional Docker/cgroup samples measure container memory, not managed heap.
The fixtures are synthetic text Markdown/PDF, with no OCR or image workload.
"""
import argparse
import concurrent.futures
import contextlib
import copy
import http.cookiejar
import json
import math
import os
import pathlib
import subprocess
import threading
import time
import urllib.error
import urllib.request
import uuid


def pdf_bytes(marker, pages):
    """Produce valid text PDFs without an extra generator dependency."""
    objects = [b"", b"", b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"]
    children = []
    for page in range(pages):
        page_number = len(objects) + 1
        content_number = page_number + 1
        children.append(f"{page_number} 0 R")
        objects.append((f"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                        f"/Resources << /Font << /F1 3 0 R >> >> /Contents {content_number} 0 R >>").encode())
        lines = [f"{marker} page {page + 1} line {line + 1} spectroscopy calibration observation evidence"
                 for line in range(45)]
        content = ("BT /F1 9 Tf 40 750 Td 14 TL\n" +
                   "\n".join(f"({line}) Tj T*" for line in lines) + "\nET").encode()
        objects.append(b"<< /Length " + str(len(content)).encode() + b" >>\nstream\n" + content + b"\nendstream")
    objects[0] = b"<< /Type /Catalog /Pages 2 0 R >>"
    objects[1] = (f"<< /Type /Pages /Kids [{' '.join(children)}] /Count {pages} >>").encode()
    result = bytearray(b"%PDF-1.4\n")
    offsets = []
    for number, item in enumerate(objects, 1):
        offsets.append(len(result))
        result.extend(str(number).encode() + b" 0 obj\n" + item + b"\nendobj\n")
    xref = len(result)
    result.extend(f"xref\n0 {len(objects) + 1}\n0000000000 65535 f \n".encode())
    for offset in offsets:
        result.extend(f"{offset:010d} 00000 n \n".encode())
    result.extend((f"trailer\n<< /Size {len(objects) + 1} /Root 1 0 R >>\n"
                   f"startxref\n{xref}\n%%EOF\n").encode())
    return bytes(result)


def percentile(values, fraction):
    ordered = sorted(values)
    return ordered[max(0, math.ceil(len(ordered) * fraction) - 1)]


class Client:
    def __init__(self, origin, cookies=None, csrf=None, search_slots=None):
        self.origin = origin.rstrip("/")
        self.cookies = http.cookiejar.CookieJar()
        for cookie in cookies or []:
            self.cookies.set_cookie(copy.copy(cookie))
        self.opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(self.cookies))
        self.csrf = csrf
        self.search_slots = search_slots or threading.BoundedSemaphore(4)

    def request(self, method, path, body=None, raw=None, headers=None, timeout=130):
        supplied = {"Origin": self.origin, **(headers or {})}
        if self.csrf and method != "GET":
            supplied["X-CSRF-TOKEN"] = self.csrf
        if body is not None:
            raw = json.dumps(body).encode()
            supplied["Content-Type"] = "application/json"
        # All clones share the shipped per-credential concurrent-search budget.
        slot = self.search_slots if path in ("/v1/search", "/v1/query") else contextlib.nullcontext()
        with slot:
            with self.opener.open(urllib.request.Request(self.origin + path, raw, supplied, method=method), timeout=timeout) as response:
                data = response.read()
                return json.loads(data) if data else response.status

    def login(self, password):
        self.request("POST", "/auth/login", {"password": password})
        self.csrf = self.request("GET", "/auth/antiforgery")["token"]
        self.request("POST", "/auth/step-up", {"password": password})
        self.csrf = self.request("GET", "/auth/antiforgery")["token"]

    def clone(self):
        return Client(self.origin, self.cookies, self.csrf, self.search_slots)

    def upload(self, payload, filename):
        boundary = "fixture" + uuid.uuid4().hex
        body = (f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="{filename}"\r\n'
                f"Content-Type: application/octet-stream\r\n\r\n").encode() + payload + f"\r\n--{boundary}--\r\n".encode()
        # File processing still invokes embedding completion in the background. wait=0
        # avoids request-level waiting; keyword convergence includes that scheduling cost.
        return self.request("POST", "/v1/documents?wait=0", raw=body,
                            headers={"Content-Type": "multipart/form-data; boundary=" + boundary})


class MemorySampler:
    def __init__(self, args):
        self.args = args
        self.samples = []
        self.errors = set()
        self.stop = threading.Event()
        self.thread = threading.Thread(target=self.run, daemon=True)

    def compose(self, *arguments):
        command = ["docker", "compose", "--project-name", self.args.compose_project]
        for filename in self.args.compose_file:
            command.extend(["-f", filename])
        return subprocess.check_output(command + list(arguments), text=True, timeout=10).strip()

    def sample(self):
        values = {}
        for name in ("daemon", "extractor"):
            path = getattr(self.args, name + "_cgroup")
            if path:
                root = pathlib.Path(path)
                values[name] = {field: (root / field).read_text().strip()
                                for field in ("memory.current", "memory.peak", "memory.max")
                                if (root / field).exists()}
            elif self.args.compose_project:
                identifiers = self.compose("ps", "-q", name).splitlines()
                if not identifiers:
                    self.errors.add(name + " container not found")
                    continue
                identifier = identifiers[0]
                # Docker's reported usage subtracts inactive file cache on Linux. Preserve
                # that distinction; these samples cannot establish a managed-heap gate.
                stats = subprocess.check_output(["docker", "stats", "--no-stream", "--format", "{{json .}}", identifier], text=True, timeout=10)
                raw = json.loads(stats)
                limit = subprocess.check_output(["docker", "inspect", "--format", "{{.HostConfig.Memory}}", identifier], text=True, timeout=10).strip()
                values[name] = {"docker_memory_usage": raw["MemUsage"], "docker_memory_percent": raw["MemPerc"], "configured_limit_bytes": int(limit)}
        return {"elapsed_seconds": time.monotonic() - self.started, "containers": values}

    def run(self):
        while not self.stop.is_set():
            try:
                self.samples.append(self.sample())
            except (OSError, ValueError, KeyError, subprocess.SubprocessError) as error:
                self.errors.add(type(error).__name__)
            self.stop.wait(self.args.memory_sample_seconds)

    def __enter__(self):
        self.started = time.monotonic()
        self.thread.start()
        return self

    def __exit__(self, *_):
        self.stop.set()
        self.thread.join(timeout=25)

    def report(self):
        return {"measurement": "container/cgroup memory; managed heap not measured",
                "samples": self.samples, "sample_errors": sorted(self.errors),
                "managed_heap_qualification": "not established",
                "full_scale_memory_qualification": "not established by 120-file throughput workload"}


class PublicationObserver:
    """Validate query projections/locators while actual extraction jobs are running."""
    def __init__(self, authenticated):
        self.client = authenticated.clone()
        self.stop = threading.Event()
        self.thread = threading.Thread(target=self.run, daemon=True)
        self.errors = []
        self.samples = []
        self.locators = set()

    def running(self):
        return {job['id'] for job in self.client.request('GET', '/v1/jobs?status=running', timeout=10)
                if job['stage'] == 'extract' and job['status'] == 'running'}

    def run(self):
        try:
            while not self.stop.is_set():
                before = self.running()
                started = time.monotonic()
                hits = self.client.request('POST', '/v1/search',
                                           {'query': 'calibration', 'mode': 'keyword', 'limit': 3}, timeout=10)['results']
                records = self.client.request('POST', '/v1/query', {'limit': 3}, timeout=10)['records']
                after = self.running()
                query_seconds = time.monotonic() - started
                verified = 0
                for hit in hits:
                    document = self.client.request('GET', '/v1/documents/' + hit['document_id'] + '?include=text,chunks', timeout=10)
                    assert document['revision'] == hit['revision'], 'Search revision differs from immutable fixture document'
                    chunks = {chunk['id']: chunk for chunk in document['chunks']}
                    assert hit['chunk_id'] in chunks, 'Search locator is absent from the committed document'
                    chunk = chunks[hit['chunk_id']]
                    assert (chunk['text'], chunk['context'], chunk.get('page')) == (hit['snippet'], hit['context'], hit.get('page')), 'Search locator resolves different content'
                    assert (document['type'], document['title']) == (hit['type'], hit['title']), 'Search metadata differs from its document'
                    self.locators.add(hit['chunk_id'])
                    verified += 1
                checked_records = 0
                for record in records:
                    if record['status'] not in ('indexed_partial', 'indexed'):
                        continue
                    document = self.client.request('GET', '/v1/documents/' + record['id'], timeout=10)
                    assert (document['revision'], document['type'], document['title']) == (record['revision'], record['type'], record['title']), 'Query metadata differs from immutable fixture document'
                    checked_records += 1
                self.samples.append({'elapsed_seconds': query_seconds,
                                     'running_extract_jobs_before': len(before), 'running_extract_jobs_after': len(after),
                                     'same_running_jobs_across_queries': len(before & after),
                                     'verified_chunk_locators': verified, 'verified_query_records': checked_records})
                self.stop.wait(.1)
        except Exception as error:
            self.errors.append(type(error).__name__ + ': ' + str(error))

    def finish(self, require_overlap):
        self.stop.set()
        self.thread.join(timeout=50)
        assert not self.thread.is_alive(), 'Publication observer did not finish'
        assert not self.errors, self.errors
        overlaps = [sample for sample in self.samples
                    if sample['same_running_jobs_across_queries'] and sample['verified_chunk_locators'] and sample['verified_query_records']]
        if require_overlap:
            assert overlaps, 'No validated queries overlapped a demonstrably running extraction job'
        return {'scope': 'supplemental deployed query/publication overlap; immutable fixture documents',
                'overlap_required': require_overlap, 'query_samples': len(self.samples), 'validated_overlap_samples': len(overlaps),
                'unique_verified_chunk_locators': len(self.locators), 'errors': self.errors, 'samples': self.samples}


def run_batch(authenticated, kind, fixtures, args):
    local = threading.local()
    started = time.monotonic()

    def process(fixture):
        if not hasattr(local, "client"):
            local.client = authenticated.clone()
        client = local.client
        marker, filename, payload, pages = fixture
        submitted = time.monotonic()
        admitted = client.upload(payload, filename)
        identifier = admitted["document"]["id"]
        admission_seconds = time.monotonic() - submitted
        deadline = submitted + args.completion_timeout
        keyword_seconds = None
        vector_seconds = None
        while time.monotonic() < deadline:
            document = client.request("GET", "/v1/documents/" + identifier)
            status = document["status"]
            if status in ("failed", "skipped", "needs_ocr"):
                raise AssertionError(kind + " extraction ended as " + status)
            if status in ("indexed_partial", "indexed") and keyword_seconds is None:
                results = client.request("POST", "/v1/search", {"query": marker, "mode": "keyword", "limit": 200})["results"]
                if any(hit["document_id"] == identifier for hit in results):
                    keyword_seconds = time.monotonic() - submitted
                    if args.provider_mode != "healthy":
                        break
            if status == "indexed" and keyword_seconds is not None:
                vector_seconds = time.monotonic() - submitted
                # indexed is document-vector coverage, not a timed query-provider call.
                break
            time.sleep(args.poll_seconds)
        if keyword_seconds is None:
            raise AssertionError(kind + " did not become keyword searchable before timeout")
        if args.provider_mode == "healthy" and vector_seconds is None:
            raise AssertionError(kind + " did not obtain complete vector coverage before timeout")
        return {"document_id": identifier, "bytes": len(payload), "pages": pages,
                "admission_seconds": admission_seconds, "keyword_seconds": keyword_seconds,
                "vector_coverage_seconds": vector_seconds}

    observer = PublicationObserver(authenticated) if args.observe_publications else None
    if observer:
        observer.thread.start()
    try:
        with concurrent.futures.ThreadPoolExecutor(max_workers=4) as executor:
            records = list(executor.map(process, fixtures))
    finally:
        if observer:
            observer.stop.set()
    observations = observer.finish(require_overlap=kind == "pdf") if observer else None
    seconds = time.monotonic() - started
    rate = len(records) * 60 / seconds
    target = 100 if kind == "markdown" else 20
    return {"files": len(records), "workers": 4, "elapsed_seconds": seconds,
            "completion_mode": "keyword plus complete vectors" if args.provider_mode == "healthy" else "keyword only",
            "files_per_minute": rate, "target_files_per_minute": target,
            "observed_rate_meets_target": rate >= target,
            "keyword_p95_seconds": percentile([item["keyword_seconds"] for item in records], .95),
            "documents": records, "publication_observer": observations}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--origin", default=os.environ.get("SECONDBRAIN_QUALIFICATION_ORIGIN"))
    parser.add_argument("--provider-mode", choices=("providerless", "healthy", "unknown"), default="unknown")
    parser.add_argument("--observe-publications", action="store_true")
    parser.add_argument("--compose-project")
    parser.add_argument("--compose-file", action="append", default=[])
    parser.add_argument("--daemon-cgroup")
    parser.add_argument("--extractor-cgroup")
    parser.add_argument("--memory-sample-seconds", type=float, default=2)
    parser.add_argument("--completion-timeout", type=float, default=900)
    parser.add_argument("--poll-seconds", type=float, default=.1)
    parser.add_argument("--measure-restart", action="store_true")
    parser.add_argument("--systemd-unit", default=os.environ.get("SECONDBRAIN_QUALIFICATION_UNIT"))
    parser.add_argument("--measure-rebuild", action="store_true")
    parser.add_argument("--expected-rebuild-documents", type=int, default=100000)
    args = parser.parse_args()
    if not args.origin:
        parser.error("An isolated daemon origin is required")
    if min(args.memory_sample_seconds, args.completion_timeout, args.poll_seconds) <= 0:
        parser.error("Intervals and timeouts must be positive")
    if args.measure_restart and not (args.compose_project or args.systemd_unit):
        parser.error("Restart measurement needs a Compose project or systemd unit")
    if not args.compose_file:
        args.compose_file = ["deploy/compose.yaml", "deploy/tests/compose-smoke.yaml"]
    client = Client(args.origin)
    client.login(os.environ["SECONDBRAIN_QUALIFICATION_PASSWORD"])
    prefix = "m1throughput" + uuid.uuid4().hex[:12]
    markdown = []
    for i in range(100):
        marker = prefix + "markdown" + str(i)
        body = "# " + marker + "\n\n" + "\n".join(
            f"{marker} observation {line} calibration evidence with independent text extraction."
            for line in range(80)) + "\n"
        markdown.append((marker, marker + ".md", body.encode(), None))
    # Nearest-rank p95 of 18 twelve-page and two eighty-page fixtures is 80.
    pdf = []
    for i, pages in enumerate([12] * 18 + [80] * 2):
        marker = prefix + "pdf" + str(i)
        pdf.append((marker, marker + ".pdf", pdf_bytes(marker, pages), pages))
    metrics = {"profile": "isolated deployed daemon; synthetic text-only upload workload",
               "provider_mode": args.provider_mode,
               "provider_time_seconds": None,
               "provider_time_note": "not instrumented; no provider-excluded throughput or rebuild pass claim",
               "pipeline_note": "wait=0 uploads; keyword timing includes background embedding deadline/scheduling cost even in providerless mode",
               "pdf_fixture_pages_median": 12, "pdf_fixture_pages_p95": percentile([item[3] for item in pdf], .95)}
    with MemorySampler(args) as memory:
        assert client.request("GET", "/health")
        metrics["markdown"] = run_batch(client, "markdown", markdown, args)
        metrics["pdf"] = run_batch(client, "pdf", pdf, args)
        if args.measure_restart:
            started = time.monotonic()
            if args.compose_project:
                memory.compose("restart", "daemon")
            else:
                subprocess.run(["systemctl", "restart", args.systemd_unit], check=True, timeout=60, stdout=subprocess.DEVNULL)
            while time.monotonic() - started < 180:
                try:
                    if client.request("GET", "/health", timeout=2):
                        break
                except (urllib.error.URLError, TimeoutError, ConnectionError):
                    pass
                time.sleep(.1)
            else:
                raise AssertionError("Daemon did not become live after restart")
            live_seconds = time.monotonic() - started
            client.login(os.environ["SECONDBRAIN_QUALIFICATION_PASSWORD"])
            client.request("POST", "/v1/search", {"query": markdown[0][0], "mode": "keyword"})
            metrics["restart"] = {"command_to_liveness_seconds": live_seconds,
                                  "command_to_authenticated_keyword_request_seconds": time.monotonic() - started,
                                  "reference_100k_startup_qualification": "not established; corpus size and full reference deployment not independently verified"}
        if args.measure_rebuild:
            started = time.monotonic()
            result = client.request("POST", "/v1/maintenance/rebuild", timeout=3700)
            metrics["rebuild"] = {"elapsed_seconds": time.monotonic() - started,
                                  "documents_reported": result["documents"],
                                  "expected_documents": args.expected_rebuild_documents,
                                  "expected_count_matches": result["documents"] == args.expected_rebuild_documents,
                                  "provider_excluded_reference_qualification": "not established; provider time not independently instrumented"}
    metrics["memory"] = memory.report()
    print(json.dumps(metrics, sort_keys=True))


if __name__ == "__main__":
    main()
