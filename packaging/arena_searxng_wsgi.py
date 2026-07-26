# SPDX-License-Identifier: AGPL-3.0-or-later
"""Narrow the bundled SearXNG server to AI Arena's local JSON API."""

import json
from urllib.parse import parse_qs

from searx.webapp import application as _searx_application


def _json_response(start_response, status, message, extra_headers=()):
    key = "status" if status.startswith("2") else "error"
    payload = json.dumps({key: message}, separators=(",", ":")).encode("utf-8")
    headers = [
        ("Content-Type", "application/json; charset=utf-8"),
        ("Content-Length", str(len(payload))),
        ("Cache-Control", "no-store"),
        ("X-Content-Type-Options", "nosniff"),
        *extra_headers,
    ]
    start_response(status, headers)
    return [payload]


def application(environ, start_response):
    """Expose health and JSON search; reject upstream UI and feed routes."""

    path = str(environ.get("PATH_INFO", ""))
    method = str(environ.get("REQUEST_METHOD", "GET")).upper()

    if path == "/healthz":
        if method != "GET":
            return _json_response(
                start_response,
                "405 Method Not Allowed",
                "Method not allowed.",
                (("Allow", "GET"),),
            )

        return _json_response(start_response, "200 OK", "ok")

    if path != "/search":
        return _json_response(start_response, "404 Not Found", "Not found.")

    if method != "GET":
        return _json_response(
            start_response,
            "405 Method Not Allowed",
            "Method not allowed.",
            (("Allow", "GET"),),
        )

    parameters = parse_qs(str(environ.get("QUERY_STRING", "")), keep_blank_values=True)
    if parameters.get("format") != ["json"]:
        return _json_response(start_response, "403 Forbidden", "JSON format required.")

    return _searx_application(environ, start_response)
