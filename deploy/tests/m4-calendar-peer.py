"""Private TLS peer for the isolated M4 calendar qualification; never logs URLs."""
import hashlib
import http.server
import json
import pathlib
import ssl

root = pathlib.Path('/fixture')


class Handler(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        state = json.loads((root / 'state.json').read_text())
        body = state['body'].encode()
        etag = '"' + hashlib.sha256(body).hexdigest() + '"'
        status = state.get('status', 200)
        conditional = self.headers.get('If-None-Match') == etag
        if status == 200 and conditional:
            status = 304
        self.send_response(status)
        self.send_header('Content-Type', 'text/calendar' if state.get('calendar', True) else 'text/html')
        self.send_header('ETag', etag)
        self.send_header('Content-Length', str(0 if status == 304 else len(body)))
        self.end_headers()
        if status != 304:
            self.wfile.write(body)
        print(json.dumps({'status': status, 'conditional': conditional}), flush=True)

    def log_message(self, *args):
        pass


server = http.server.ThreadingHTTPServer(('0.0.0.0', 8443), Handler)
context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
context.load_cert_chain(root / 'server.pem', root / 'server.key')
server.socket = context.wrap_socket(server.socket, server_side=True)
server.serve_forever()
