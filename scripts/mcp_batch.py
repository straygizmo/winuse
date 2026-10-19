#!/usr/bin/env python3
"""Drive `winuse --mcp` over stdio from a JSON calls file (stdlib only).

usage: python scripts/mcp_batch.py --exe <winuse.exe | "dotnet run --project src/WinUse --"> --calls calls.json [--out out/dir]

calls.json is a JSON array of {"tool": "...", "args": {...}}. One server process serves the whole file,
so refs (u{n}/w{n}/win{n}) and step ids persist across calls. Each result is printed as one JSON line:
{"i": n, "tool": ..., "result": <text content, parsed as JSON when possible>, "isError": bool}
Image content is saved to <out>/<n>-<tool>.png and replaced by its path. Server stderr goes to <out>/server.log.
Optional per-call key "stop_on_error": true aborts the batch when that call fails.
An argument string of the form "$<n>.<path>" (e.g. "$5.target.ref") is replaced by that field of the
result of call n, so a later call can use a ref returned earlier in the same batch.
"""
import argparse
import base64
import json
import os
import shlex
import subprocess
import sys
import threading
import time

PROTOCOL_VERSION = "2025-06-18"


class McpClient:
    def __init__(self, cmd, log_path):
        self._log = open(log_path, "ab")
        self._proc = subprocess.Popen(
            cmd, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=self._log, bufsize=0)
        self._next_id = 0

    def _send(self, msg):
        # The C# SDK stdio transport frames messages as newline-delimited JSON.
        self._proc.stdin.write((json.dumps(msg, ensure_ascii=False) + "\n").encode("utf-8"))
        self._proc.stdin.flush()

    def _read_message(self):
        while True:
            line = self._proc.stdout.readline()
            if not line:
                raise RuntimeError("server closed stdout (see server.log)")
            line = line.strip()
            if not line:
                continue
            if line.lower().startswith(b"content-length:"):
                # Header framing fallback: read headers, then the body.
                length = int(line.split(b":", 1)[1])
                while self._proc.stdout.readline().strip():
                    pass
                return json.loads(self._proc.stdout.read(length).decode("utf-8"))
            return json.loads(line.decode("utf-8"))

    def request(self, method, params):
        self._next_id += 1
        rid = self._next_id
        self._send({"jsonrpc": "2.0", "id": rid, "method": method, "params": params})
        while True:
            msg = self._read_message()
            if msg.get("id") == rid and ("result" in msg or "error" in msg):
                return msg
            if "method" in msg and "id" in msg:
                # Server-to-client request (e.g. ping/roots): answer minimally.
                self._send({"jsonrpc": "2.0", "id": msg["id"], "result": {}})
            # Notifications (logging, progress) are ignored.

    def notify(self, method, params=None):
        msg = {"jsonrpc": "2.0", "method": method}
        if params is not None:
            msg["params"] = params
        self._send(msg)

    def close(self, timeout=15):
        try:
            self._proc.stdin.close()
        except OSError:
            pass
        try:
            self._proc.wait(timeout=timeout)
        except subprocess.TimeoutExpired:
            self._proc.kill()
            self._proc.wait()
        self._log.close()
        return self._proc.returncode


def compact(result, i, tool, out_dir):
    texts = []
    images = []
    for n, block in enumerate(result.get("content", [])):
        if block.get("type") == "text":
            text = block.get("text", "")
            try:
                texts.append(json.loads(text))
            except ValueError:
                texts.append(text)
        elif block.get("type") == "image":
            suffix = "" if n == 0 and len(result["content"]) <= 2 else f"-{n}"
            path = os.path.join(out_dir, f"{i}-{tool}{suffix}.png")
            with open(path, "wb") as f:
                f.write(base64.b64decode(block.get("data", "")))
            images.append(path)
        else:
            texts.append(block)
    value = texts[0] if len(texts) == 1 else texts
    if images:
        value = {"result": value, "images": images}
    return value


def lookup(results, expr):
    head, _, path = expr[1:].partition(".")
    value = results[int(head)]
    for key in filter(None, path.split(".")):
        value = value[int(key)] if isinstance(value, list) else value[key]
    return value


def substitute(node, results):
    if isinstance(node, dict):
        return {k: substitute(v, results) for k, v in node.items()}
    if isinstance(node, list):
        return [substitute(v, results) for v in node]
    if isinstance(node, str) and len(node) > 1 and node[0] == "$" and node[1].isdigit():
        return lookup(results, node)
    return node


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--exe", required=True, help='winuse.exe path or a command such as "dotnet run --project src/WinUse --"')
    ap.add_argument("--calls", required=True, help="JSON array of {tool, args}")
    ap.add_argument("--out", default="out/mcp_batch", help="directory for images and server.log")
    a = ap.parse_args()

    os.makedirs(a.out, exist_ok=True)
    with open(a.calls, encoding="utf-8") as f:
        calls = json.load(f)
    cmd = [os.path.abspath(a.exe)] if os.path.isfile(a.exe) else shlex.split(a.exe, posix=False)
    cmd.append("--mcp")

    client = McpClient(cmd, os.path.join(a.out, "server.log"))
    failed = 0
    try:
        init = client.request("initialize", {
            "protocolVersion": PROTOCOL_VERSION,
            "capabilities": {},
            "clientInfo": {"name": "mcp_batch", "version": "1.0"},
        })
        if "error" in init:
            print(json.dumps({"initialize_error": init["error"]}), flush=True)
            return 2
        info = init["result"].get("serverInfo", {})
        print(json.dumps({"server": info, "protocolVersion": init["result"].get("protocolVersion")}), flush=True)
        client.notify("notifications/initialized")

        results = {}
        for i, call in enumerate(calls, start=1):
            tool = call["tool"]
            try:
                args = substitute(call.get("args", {}), results)
            except (KeyError, IndexError, TypeError, ValueError) as e:
                print(json.dumps({"i": i, "tool": tool, "result": f"substitution failed: {e!r}", "isError": True}), flush=True)
                failed += 1
                if call.get("stop_on_error"):
                    break
                continue
            started = time.monotonic()
            resp = client.request("tools/call", {"name": tool, "arguments": args})
            ms = int((time.monotonic() - started) * 1000)
            if "error" in resp:
                line = {"i": i, "tool": tool, "result": resp["error"], "isError": True, "ms": ms}
            else:
                r = resp["result"]
                line = {"i": i, "tool": tool, "result": compact(r, i, tool, a.out),
                        "isError": bool(r.get("isError")), "ms": ms}
            r = line["result"]
            results[i] = r["result"] if isinstance(r, dict) and "images" in r else r
            print(json.dumps(line, ensure_ascii=False), flush=True)
            if line["isError"]:
                failed += 1
                if call.get("stop_on_error"):
                    break
    finally:
        client.close()
    return 1 if failed else 0


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    sys.exit(main())
