#!/usr/bin/env python3
"""Mock expertise-api for tests/review/test-apictl-review.sh.

Serves GET /expertise/drafts from ${MOCK_STATE_DIR}/drafts.json and records
every request (method, path, Idempotency-Key, auth presence, body) as a TSV
line in ${MOCK_STATE_DIR}/requests.log. Binds an ephemeral port on 127.0.0.1
and writes it to ${MOCK_STATE_DIR}/port.

MUTATE_ON_REFETCH=1 makes every drafts fetch AFTER the first return altered
integrityHash values — the fixture for the TOCTOU re-check test.
"""
import http.server
import json
import os

STATE = os.environ["MOCK_STATE_DIR"]
DRAFTS_FILE = os.path.join(STATE, "drafts.json")
LOG = os.path.join(STATE, "requests.log")
COUNT = os.path.join(STATE, "get_count")


class Handler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *args):  # silence default stderr chatter
        pass

    def _record(self, method, body=""):
        auth = self.headers.get("Authorization", "")
        with open(LOG, "a") as f:
            f.write("\t".join([
                method,
                self.path,
                self.headers.get("Idempotency-Key", "-"),
                "auth" if auth.startswith("Bearer ") else "noauth",
                body.replace("\t", " ").replace("\n", " "),
            ]) + "\n")

    def _send_json(self, payload: bytes, status=200):
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def do_GET(self):
        if self.path != "/expertise/drafts":
            self._record("GET")
            self._send_json(b'{"title":"not found"}', 404)
            return
        n = 0
        if os.path.exists(COUNT):
            with open(COUNT) as f:
                n = int(f.read().strip() or "0")
        n += 1
        with open(COUNT, "w") as f:
            f.write(str(n))
        with open(DRAFTS_FILE) as f:
            data = json.load(f)
        if os.environ.get("MUTATE_ON_REFETCH") == "1" and n > 1:
            for entry in data:
                entry["integrityHash"] = "mutated-" + entry["integrityHash"]
        self._record("GET")
        self._send_json(json.dumps(data).encode())

    def do_POST(self):
        length = int(self.headers.get("Content-Length", 0))
        body = self.rfile.read(length).decode()
        self._record("POST", body)
        self._send_json(b"{}")


srv = http.server.HTTPServer(("127.0.0.1", 0), Handler)
with open(os.path.join(STATE, "port"), "w") as f:
    f.write(str(srv.server_address[1]))
srv.serve_forever()
