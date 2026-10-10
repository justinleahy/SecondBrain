"""Private bridge to an existing local vLLM embedder; logs metadata only.

Bind exclusively to the disposable Docker bridge gateway. Never bind publicly.
The append-only timing log contains no headers, inputs, outputs, or credentials.
"""
import argparse
import ipaddress
import http.client
import http.server
import json
import threading
import time


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--bind', default='172.28.0.1')
    parser.add_argument('--port', type=int, default=11882)
    parser.add_argument('--log', required=True)
    parser.add_argument('--upstream-host', default='127.0.0.1')
    parser.add_argument('--upstream-port', type=int, default=8001)
    args = parser.parse_args()
    if args.bind != '172.28.0.1' or args.port != 11882:
        parser.error('This fixture permits only 172.28.0.1:11882')
    try:
        address = ipaddress.IPv4Address(args.upstream_host)
    except ipaddress.AddressValueError:
        parser.error('Upstream must be a numeric private IPv4 address, without credentials')
    allowed = ('10.0.0.0/8', '172.16.0.0/12', '192.168.0.0/16', '127.0.0.0/8')
    if not any(address in ipaddress.IPv4Network(network) for network in allowed):
        parser.error('Upstream must be a private unicast IPv4 address')
    if not 1 <= args.upstream_port <= 65535:
        parser.error('Upstream port must be between 1 and 65535')
    lock = threading.Lock()

    class Handler(http.server.BaseHTTPRequestHandler):
        def log_message(self, *_):
            pass

        def do_GET(self):
            if self.path != '/v1/models':
                self.send_error(404)
                return
            started = time.monotonic()
            connection = http.client.HTTPConnection(args.upstream_host, args.upstream_port, timeout=10)
            record = {'started_monotonic': started, 'request_kind': 'model_discovery', 'intent': 'health/discovery'}
            try:
                connection.request('GET', '/v1/models')
                response = connection.getresponse()
                payload = response.read(8 * 1024 * 1024 + 1)
                if len(payload) > 8 * 1024 * 1024:
                    raise ValueError('Model discovery response exceeds bound')
                record['status'] = response.status
                self.send_response(response.status)
                self.send_header('Content-Type', 'application/json')
                self.send_header('Content-Length', str(len(payload)))
                self.end_headers()
                self.wfile.write(payload)
            except (OSError, ValueError, http.client.HTTPException) as error:
                record['error_type'] = type(error).__name__
                if not isinstance(error, (BrokenPipeError, ConnectionResetError)):
                    self.send_error(502)
            finally:
                connection.close()
                record['elapsed_seconds'] = time.monotonic() - started
                with lock, open(args.log, 'a') as output:
                    output.write(json.dumps(record, sort_keys=True) + '\n')

        def do_POST(self):
            if self.path != '/v1/embeddings':
                self.send_error(404)
                return
            length = int(self.headers.get('Content-Length', '0'))
            if length <= 0 or length > 16 * 1024 * 1024:
                self.send_error(413)
                return
            body = self.rfile.read(length)
            request = json.loads(body)
            inputs = request.get('input', [])
            count = len(inputs) if isinstance(inputs, list) else 1
            started = time.monotonic()
            record = {'started_monotonic': started, 'request_kind': 'embedding',
                      'input_items': count, 'input_bytes': length,
                      'model': request.get('model'),
                      'intent': request.get('intent') if request.get('intent') in ('query', 'document') else 'unspecified by protocol'}
            connection = http.client.HTTPConnection(args.upstream_host, args.upstream_port, timeout=180)
            try:
                connection.request('POST', self.path, body, {'Content-Type': 'application/json'})
                response = connection.getresponse()
                payload = response.read()
                record['status'] = response.status
                try:
                    decoded = json.loads(payload)
                    record['usage'] = decoded.get('usage', {})
                    record['vectors'] = len(decoded.get('data', []))
                except (ValueError, TypeError):
                    record['usage'] = {}
                self.send_response(response.status)
                self.send_header('Content-Type', 'application/json')
                self.send_header('Content-Length', str(len(payload)))
                self.end_headers()
                self.wfile.write(payload)
            except (OSError, http.client.HTTPException) as error:
                record['error_type'] = type(error).__name__
                if not isinstance(error, (BrokenPipeError, ConnectionResetError)):
                    self.send_error(502)
            finally:
                connection.close()
                record['elapsed_seconds'] = time.monotonic() - started
                with lock, open(args.log, 'a') as output:
                    output.write(json.dumps(record, sort_keys=True) + '\n')

    http.server.ThreadingHTTPServer((args.bind, args.port), Handler).serve_forever()


if __name__ == '__main__':
    main()
