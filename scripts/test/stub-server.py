#!/usr/bin/env python3
"""
Minimal HTTP stub for the apictl restart race regression test.

Returns 200 OK on any /health* path, 404 elsewhere. Bound to 127.0.0.1.
Designed to be the *supervised* process inside the launchd/systemd unit so
that `expertise-apictl restart` genuinely exercises the readiness probe
end-to-end (kill + respawn → /health/ready returns).

Usage:
  stub-server.py [PORT]   # default: 18080
"""
import http.server
import socketserver
import sys


PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 18080


class H(http.server.BaseHTTPRequestHandler):
    def do_GET(self):  # noqa: N802 — base-class API
        if self.path.startswith("/health"):
            self.send_response(200)
            self.end_headers()
            self.wfile.write(b"OK")
        else:
            self.send_response(404)
            self.end_headers()

    def log_message(self, *_args, **_kwargs):  # silence access log
        return


class S(socketserver.TCPServer):
    allow_reuse_address = True


def main() -> int:
    with S(("127.0.0.1", PORT), H) as srv:
        srv.serve_forever()
    return 0


if __name__ == "__main__":
    sys.exit(main())
