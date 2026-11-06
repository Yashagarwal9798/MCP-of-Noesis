"""Protocol-level smoke test: talks JSON-RPC to the server over stdio, exactly like an MCP host.

Usage: python tests/smoke.py [path-to-NoesisMcp.exe] [--out DIR]
Checks: initialize -> tools/list -> every tool, and that stdout carries nothing but JSON-RPC.
"""
import base64
import json
import os
import subprocess
import sys
import time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_EXE = os.path.join(ROOT, "src", "NoesisMcp", "bin", "Release", "net10.0", "NoesisMcp.exe")
NS = 'xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"'

args = [a for a in sys.argv[1:] if not a.startswith("--")]
exe = args[0] if args else DEFAULT_EXE
out_dir = sys.argv[sys.argv.index("--out") + 1] if "--out" in sys.argv else os.path.join(ROOT, "docs", "renders")
os.makedirs(out_dir, exist_ok=True)

proc = subprocess.Popen([exe], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
_next = 0
failures = []


def send(method, params=None, notify=False):
    global _next
    msg = {"jsonrpc": "2.0", "method": method}
    if params is not None:
        msg["params"] = params
    if not notify:
        _next += 1
        msg["id"] = _next
    proc.stdin.write((json.dumps(msg) + "\n").encode())
    proc.stdin.flush()
    if notify:
        return None
    while True:
        line = proc.stdout.readline()
        if not line:
            raise SystemExit("server closed stdout")
        try:
            resp = json.loads(line)
        except json.JSONDecodeError:
            failures.append(f"non-JSON line on stdout: {line[:120]!r}")
            continue
        if resp.get("id") == msg["id"]:
            return resp


def call(name, arguments):
    t = time.perf_counter()
    r = send("tools/call", {"name": name, "arguments": arguments})
    ms = (time.perf_counter() - t) * 1000
    if "error" in r:
        failures.append(f"{name}: protocol error {r['error']}")
        return r, ms
    return r["result"], ms


def text_of(result):
    return "\n".join(c["text"] for c in result.get("content", []) if c["type"] == "text")


def check(cond, label):
    print(("PASS " if cond else "FAIL ") + label)
    if not cond:
        failures.append(label)


init = send("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "smoke", "version": "1"}})
check(init["result"]["serverInfo"]["name"] == "noesis", "initialize returns serverInfo.name = noesis")
send("notifications/initialized", notify=True)

tools = send("tools/list")["result"]["tools"]
names = sorted(t["name"] for t in tools)
print("tools:", names)
check(names == sorted(["validate_xaml", "render_xaml", "search_docs", "list_controls", "wpf_differences"]), "tools/list has the 5 tools")

good = f'<Grid {NS} Background="#1E2233"><TextBlock Text="Hello Noesis" FontSize="64" Foreground="White" HorizontalAlignment="Center" VerticalAlignment="Center"/></Grid>'
r, ms = call("validate_xaml", {"xaml": good})
check(not r.get("isError") and text_of(r).startswith("OK"), f"validate_xaml OK on valid XAML ({ms:.0f} ms): {text_of(r)!r}")

bad = f'<Grid {NS}>\n  <Buton Content="Play"/>\n  <TextBlock Txt="x"/>\n</Grid>'
r, ms = call("validate_xaml", {"xaml": bad})
t = text_of(r)
check(r.get("isError") and "Line 2, Col 3" in t and "Buton" in t and "Line 3" in t, f"validate_xaml reports both errors with lines ({ms:.0f} ms):\n{t}")

r, ms = call("render_xaml", {"xaml": good, "width": 640, "height": 360})
imgs = [c for c in r.get("content", []) if c["type"] == "image"]
check(len(imgs) == 1 and imgs[0]["mimeType"] == "image/png", f"render_xaml returns a PNG ({ms:.0f} ms): {text_of(r)}")
if imgs:
    png = base64.b64decode(imgs[0]["data"])
    check(png[:8] == b"\x89PNG\r\n\x1a\n", "PNG signature valid")
    with open(os.path.join(out_dir, "smoke-hello.png"), "wb") as f:
        f.write(png)

r, ms = call("render_xaml", {"xaml": bad})
check(r.get("isError") and not any(c["type"] == "image" for c in r["content"]), "render_xaml refuses invalid XAML")

hud = f'<Grid {NS}><TextBlock Text="{{Binding Score, StringFormat=Score: {{0}}}}" FontSize="40" Foreground="Gold" Margin="20"/></Grid>'
r, ms = call("render_xaml", {"xaml": hud, "width": 400, "height": 120, "sample_data": json.dumps({"Score": 42})})
check(any(c["type"] == "image" for c in r.get("content", [])), f"render_xaml with sample_data ({ms:.0f} ms): {text_of(r)}")

r, _ = call("list_controls", {})
check("Panels:" in text_of(r) and "StackPanel" in text_of(r), "list_controls lists panels")
r, _ = call("list_controls", {"type_name": "TextBlock"})
check("TextWrapping" in text_of(r), "list_controls TextBlock shows TextWrapping")
r, _ = call("list_controls", {"type_name": "Viewport3D"})
check("does not exist" in text_of(r), "list_controls says Viewport3D does not exist")

r, ms = call("wpf_differences", {})
check("NOT available" in text_of(r) and "Viewport3D" in text_of(r), f"wpf_differences probes the parser ({ms:.0f} ms)")
print(text_of(r).split("\n")[0:2])

r, _ = call("search_docs", {"query": "ListBox ItemTemplate"})
print(text_of(r)[:400])
check(len(text_of(r)) > 0, "search_docs answers")

r = send("tools/call", {"name": "no_such_tool", "arguments": {}})
check("error" in r or r.get("result", {}).get("isError"), "unknown tool gives an error")

proc.stdin.close()
proc.wait(timeout=15)
print()
print("ALL PASS" if not failures else f"{len(failures)} FAILURE(S)")
sys.exit(1 if failures else 0)
