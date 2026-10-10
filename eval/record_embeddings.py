"""Record public seed inputs against an explicitly selected local embedding service."""
import argparse
import base64
import datetime
import hashlib
import json
import math
import pathlib
import struct
import urllib.parse
import urllib.request

parser = argparse.ArgumentParser()
parser.add_argument("--url", required=True)
parser.add_argument("--model", required=True)
parser.add_argument("--revision", required=True)
args = parser.parse_args()
url = urllib.parse.urlparse(args.url)
if url.scheme != "http" or url.hostname not in ("127.0.0.1", "localhost", "::1") or url.username or url.password:
    parser.error("Fixture recording is limited to an explicit loopback HTTP service.")
root = pathlib.Path(__file__).resolve().parent
documents_bytes = (root / "documents.jsonl").read_bytes()
questions_bytes = (root / "questions.jsonl").read_bytes()
documents = [json.loads(line) for line in documents_bytes.splitlines()]
questions = [json.loads(line) for line in questions_bytes.splitlines()]
# Labels and input hashes are fixed before the first model call; this script never ranks or edits labels.
inputs = [(item["id"], item["title"] + "\n" + item["content"]) for item in documents]
inputs += [(item["id"], item["query"]) for item in questions]
vectors = {}
dimensions = None
class NoRedirects(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, response, code, message, headers, new_url):
        raise RuntimeError("Redirects are disabled for the fixture recorder")


opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirects())
for start in range(0, len(inputs), 16):
    batch = inputs[start:start + 16]
    request = urllib.request.Request(args.url.rstrip("/") + "/embeddings",
        data=json.dumps({"model": args.model, "input": [text for _, text in batch], "encoding_format": "float"}).encode(),
        headers={"Content-Type": "application/json"})
    with opener.open(request, timeout=60) as response:
        if response.geturl() != request.full_url:
            raise RuntimeError("Unexpected redirect from the local recorder")
        result = json.load(response)
    rows = sorted(result["data"], key=lambda row: row["index"])
    if [row["index"] for row in rows] != list(range(len(batch))):
        raise ValueError("Invalid batch indexes")
    for (identifier, text), row in zip(batch, rows):
        vector = row["embedding"]
        dimensions = dimensions or len(vector)
        if len(vector) != dimensions or not vector or not all(math.isfinite(value) for value in vector):
            raise ValueError("Invalid embedding")
        vectors[identifier] = {"input_sha256": hashlib.sha256(text.encode()).hexdigest(),
            "float32_le_base64": base64.b64encode(struct.pack("<" + "f" * len(vector), *vector)).decode()}
descriptor = {"provider": "vllm", "model": args.model, "model_revision": args.revision,
    "dimensions": dimensions, "normalization": "unit-f32", "document_template": "title-newline-text-v1",
    "query_template": "raw-query-v1", "chunk_generation": 1}
output = {"recorded_at": datetime.datetime.now(datetime.timezone.utc).isoformat(), "descriptor": descriptor,
    "fingerprint": hashlib.sha256(json.dumps(descriptor, sort_keys=True, separators=(",", ":")).encode()).hexdigest(),
    "documents_sha256": hashlib.sha256(documents_bytes).hexdigest(),
    "questions_sha256": hashlib.sha256(questions_bytes).hexdigest(), "vectors": vectors}
(root / "embeddings.json").write_text(json.dumps(output, indent=2) + "\n")
print(f"Recorded {len(vectors)} embeddings with {dimensions} dimensions; labels unchanged.")
