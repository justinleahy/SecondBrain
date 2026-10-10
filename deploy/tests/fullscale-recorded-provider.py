"""Private qualification replay service. Recorded Qwen vectors, no inference; never claim provider latency."""
import argparse
import base64
import hashlib
import http.server
import json
import pathlib
import struct
import time

parser = argparse.ArgumentParser()
parser.add_argument('--bind', default='172.29.0.1')
parser.add_argument('--port', type=int, default=11881)
parser.add_argument('--evaluation', default='eval')
parser.add_argument('--metrics', required=True)
args = parser.parse_args()
root = pathlib.Path(args.evaluation)
recording = json.loads((root / 'embeddings.json').read_text())
for filename, field in [('documents.jsonl', 'documents_sha256'), ('questions.jsonl', 'questions_sha256')]:
    assert hashlib.sha256((root / filename).read_bytes()).hexdigest() == recording[field]
vectors = {entry['input_sha256']: list(struct.unpack('<1024f', base64.b64decode(entry['float32_le_base64'])))
           for entry in recording['vectors'].values()}
model = recording['descriptor']['model']

class Handler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *_): pass
    def reply(self, status, body):
        data = json.dumps(body).encode()
        self.send_response(status)
        self.send_header('Content-Type', 'application/json')
        self.send_header('Content-Length', str(len(data)))
        self.end_headers()
        self.wfile.write(data)
    def do_GET(self):
        if self.path == '/v1/models':
            self.reply(200, {'object': 'list', 'data': [{'id': model, 'object': 'model', 'owned_by': 'recorded-qwen-qualification'}]})
        else: self.reply(404, {'error': 'unknown route'})
    def do_POST(self):
        started = time.monotonic()
        try:
            if self.path != '/v1/embeddings':
                self.reply(404, {'error': 'unknown route'}); return
            body = json.loads(self.rfile.read(int(self.headers['Content-Length'])))
            assert body['model'] == model
            inputs = body['input'] if isinstance(body['input'], list) else [body['input']]
            data = [{'object': 'embedding', 'index': i, 'embedding': vectors[hashlib.sha256(text.encode()).hexdigest()]}
                    for i, text in enumerate(inputs)]
            self.reply(200, {'object': 'list', 'data': data, 'model': model, 'usage': {'prompt_tokens': 0, 'total_tokens': 0}})
            with open(args.metrics, 'a') as metrics:
                metrics.write(json.dumps({'at': time.time(), 'inputs': len(inputs), 'replay_ms': (time.monotonic()-started)*1000})+'\n')
        except (KeyError, ValueError, AssertionError):
            self.reply(422, {'error': 'Input or model is outside the hash-verified recorded qualification corpus.'})

http.server.ThreadingHTTPServer((args.bind, args.port), Handler).serve_forever()
