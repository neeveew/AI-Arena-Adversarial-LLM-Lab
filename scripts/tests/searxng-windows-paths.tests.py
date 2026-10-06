"""Check bundled path validation and gateway routing without opening device files."""
import json
import os
from pathlib import Path
import sys
import types

sys.dont_write_bytecode = True
payload = Path(sys.argv[1]).resolve()
sys.path.insert(0, str(payload / "runtime" / "site-packages"))
from werkzeug.security import safe_join

def require(condition, message):
    # This also runs when an inherited PYTHONOPTIMIZE disables Python assertions.
    if not condition:
        raise RuntimeError(message)

require(os.name == "nt", "The bundled regression requires Windows path semantics.")
device_paths = ["NUL:", "CON:", "AUX:", "PRN:", "COM1:", "LPT1:",
                "nul:", "assets/NUL:", "assets/CON::$DATA", "COM1::$DATA"]
accepted = [name for name in device_paths if safe_join("public", name) is not None]
require(not accepted, f"Windows device paths bypassed validation: {accepted}")
for ordinary in ["icon.png", "images/icon.png", "console.txt", "nullable.txt"]:
    require(safe_join("public", ordinary) == "public/" + ordinary,
            f"Ordinary file path was rejected: {ordinary}")
for traversal in ["../secret.txt", "/secret.txt", "nested/../../secret.txt"]:
    require(safe_join("public", traversal) is None, f"Traversal was accepted: {traversal}")

# Inspect the exact packaged gateway, replacing only its upstream callable.
# No server or external search is started by this route regression.
calls = []
def downstream(environ, start_response):
    calls.append(dict(environ))
    start_response("200 OK", [("Content-Type", "application/json")])
    return [b'{"results":[]}']

webapp = types.ModuleType("searx.webapp")
webapp.application = downstream
sys.modules["searx"] = types.ModuleType("searx")
sys.modules["searx.webapp"] = webapp
gateway = payload / "runtime" / "arena_searxng_wsgi.py"
namespace = {}
exec(compile(gateway.read_bytes(), str(gateway), "exec"), namespace)

def probe(path, query="", method="GET"):
    statuses = []
    response = namespace["application"](
        {"PATH_INFO": path, "QUERY_STRING": query, "REQUEST_METHOD": method},
        lambda code, _headers: statuses.append(code),
    )
    body = b"".join(response)
    require(len(statuses) == 1, "The gateway returned an invalid response count.")
    return statuses[0], body

file_routes = ["/logo/NUL:", "/logo/CON:", "/logo/COM1:", "/logo/AUX::$DATA",
               "/favicon.ico", "/static/NUL:", "/NUL:", "/search/NUL:",
               "/search/../logo/NUL:", "/logo/NUL%3A", "/search%2f../logo/NUL:",
               "\\logo\\NUL:"]
for path in file_routes:
    require(probe(path)[0] == "404 Not Found", f"File route reached the gateway: {path}")
require(not calls, "A file route reached the upstream application.")
require(probe("/healthz")[0] == "200 OK", "Health route failed.")
require(probe("/search", "q=test&format=html")[0] == "403 Forbidden", "HTML format was accepted.")
require(probe("/search", "q=test&format=json&format=rss")[0] == "403 Forbidden", "Duplicate formats were accepted.")
require(probe("/search", "q=test&format=json", "POST")[0] == "405 Method Not Allowed", "POST search was accepted.")
require(not calls, "A blocked request reached the upstream application.")
require(probe("/search", "q=test&format=json")[0] == "200 OK", "JSON search route failed.")
require(len(calls) == 1 and calls[0]["PATH_INFO"] == "/search", "JSON search did not reach the expected upstream route exactly once.")
print(json.dumps({"blocked_device_paths": len(device_paths),
                  "ordinary_and_traversal_controls": "passed",
                  "blocked_file_routes": len(file_routes),
                  "health_and_json_search_controls": "passed"}))
