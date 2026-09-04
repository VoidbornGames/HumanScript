import subprocess, json, os, threading, time, sys, queue

LSP = r"src\HSharp\lsp\bin\Release\net8.0\win-x64\publish\hsharp-lsp.exe"
DOC = os.path.abspath(r"rt\demo-oop.hs")
URI = "file:///" + DOC.replace("\\", "/")
text = open(DOC, encoding="utf-8").read()

proc = subprocess.Popen([LSP], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)

def dr():
    for line in proc.stderr:
        print("STDERR:", line.decode(errors="replace").rstrip(), flush=True)
threading.Thread(target=dr, daemon=True).start()

_msgq = queue.Queue()
def _reader():
    out = proc.stdout
    while True:
        headers = {}
        while True:
            line = out.readline()
            if not line:
                return
            line = line.decode().strip()
            if not line:
                break
            k, v = line.split(":", 1)
            headers[k.strip()] = v.strip()
        n = int(headers["Content-Length"])
        _msgq.put(json.loads(out.read(n)))
threading.Thread(target=_reader, daemon=True).start()

def send(obj):
    data = json.dumps(obj).encode()
    proc.stdin.write(b"Content-Length: %d\r\n\r\n" % len(data) + data)
    proc.stdin.flush()

def read_msg(timeout=30):
    return _msgq.get(timeout=timeout)

t0 = time.time()
send({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {"capabilities": {}}})
init = read_msg()
print("init ok %.1fs" % (time.time() - t0), flush=True)

send({"jsonrpc": "2.0", "method": "initialized", "params": {}})
send({"jsonrpc": "2.0", "method": "textDocument/didOpen", "params": {
    "textDocument": {"uri": URI, "languageId": "hsharp", "version": 1, "text": text}}})
t0 = time.time()
diags = read_msg()
n = len(diags["params"]["diagnostics"])
print("didOpen diags: %d (%.1fs)" % (n, time.time() - t0), flush=True)
for d in diags["params"]["diagnostics"][:5]:
    print("  ", d["message"], flush=True)

send({"jsonrpc": "2.0", "id": 2, "method": "textDocument/completion", "params": {
    "textDocument": {"uri": URI}, "position": {"line": 1, "character": 1}}})
comp = read_msg()
labels = [i["label"] for i in comp["result"]["items"]]
print("completions:", len(labels), [x for x in labels if x in ("print", "var")], flush=True)

# context-true completion: an expression position must not offer statements
lines = text.split("\n")
expr_line = next(i for i, l in enumerate(lines) if l.strip().startswith("return "))
# just before the ';' so the cursor is still inside the return expression
col = len(lines[expr_line].rstrip())
print("probe line", expr_line, repr(lines[expr_line]), "col", col, flush=True)
# LSP positions are 0-based: 1-based col 21 == character 20
send({"jsonrpc": "2.0", "id": 7, "method": "textDocument/completion", "params": {
    "textDocument": {"uri": URI}, "position": {"line": expr_line, "character": col - 1}}})
comp2 = read_msg()
print("comp2 id:", comp2.get("id"), flush=True)
labels2 = [i["label"] for i in comp2["result"]["items"]]
print("expression-position completions:", len(labels2),
      "statements leaked:", [x for x in labels2 if x in ("if", "while", "var", "return", "lock")], flush=True)
assert not [x for x in labels2 if x in ("if", "while", "var", "return", "lock")], "statement keywords leaked into expression context"

# member access on a known type offers its methods
member_line = next(i for i, l in enumerate(text.split("\n")) if "pt." in l or "u." in l) if any("pt." in l or "u." in l for l in text.split("\n")) else None
if member_line is not None:
    line = text.split("\n")[member_line]
    dcol = line.index(".") + 1
    send({"jsonrpc": "2.0", "id": 8, "method": "textDocument/completion", "params": {
        "textDocument": {"uri": URI}, "position": {"line": member_line, "character": dcol}}})
    comp3 = read_msg()
    labels3 = [i["label"] for i in comp3["result"]["items"]]
    print("member completions:", labels3, flush=True)

for name, mid in [("foldingRange", 3), ("formatting", 4), ("documentSymbol", 5)]:
    send({"jsonrpc": "2.0", "id": mid, "method": "textDocument/" + name, "params": {"textDocument": {"uri": URI}}})
    r = read_msg()
    res = r["result"]
    if name == "formatting":
        res = res[0]["newText"].split("\n")[0]
    print(name, "->", (len(res) if isinstance(res, list) else res), flush=True)

send({"jsonrpc": "2.0", "id": 6, "method": "shutdown"})
print("shutdown:", read_msg()["result"], flush=True)
proc.kill()
print("SMOKE OK")
