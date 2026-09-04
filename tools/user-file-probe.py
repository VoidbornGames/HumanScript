import subprocess, json, threading, queue, time

DIR = r"C:\Users\Naboodi\Documents\test"
rest = (DIR + r"\app.hs").replace("\\", "/")
URI = "file:///" + rest[0] + "%3A" + rest[2:]
SRC = open(DIR + r"\app.hs", encoding="utf-8").read()
LINES = SRC.split("\n")

# the user types a new line inside the OnAccept handler:
SRC = SRC.replace('    packet.Close();', '    packet.Close();\n    packet.Source().Split(":")[0];')

proc = subprocess.Popen([r"src\HSharp\lsp\bin\Debug\net8.0\hsharp-lsp.exe"],
                        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
q = queue.Queue()
def rd():
    out = proc.stdout
    while True:
        h = {}
        while True:
            l = out.readline()
            if not l: return
            l = l.decode().strip()
            if not l: break
            k, v = l.split(":", 1); h[k.strip()] = v.strip()
        q.put(json.loads(out.read(int(h["Content-Length"]))))
threading.Thread(target=rd, daemon=True).start()
_id = 0
def send(method, params):
    global _id
    _id += 1
    d = json.dumps({"jsonrpc":"2.0","id":_id,"method":method,"params":params}).encode()
    proc.stdin.write(b"Content-Length: %d\r\n\r\n" % len(d) + d); proc.stdin.flush()
    while True:
        m = q.get(timeout=30)
        if m.get("id") == _id: return m
def notify(method, params):
    d = json.dumps({"jsonrpc":"2.0","method":method,"params":params}).encode()
    proc.stdin.write(b"Content-Length: %d\r\n\r\n" % len(d) + d); proc.stdin.flush()

send("initialize", {"capabilities": {}, "rootUri": "file:///" + DIR.replace("\\", "/")})
notify("initialized", {})
notify("textDocument/didOpen", {"textDocument": {"uri": URI, "languageId": "hsharp", "version": 1, "text": SRC}})
while True:
    m = q.get(timeout=30)
    if "id" not in m: break

def labels(line, ch):
    r = send("textDocument/completion", {"textDocument": {"uri": URI}, "position": {"line": line, "character": ch}})
    return [i["label"] for i in r["result"]["items"]]

# line with packet.Source(). inside the OnAccept handler
target_line = next(i for i, l in enumerate(SRC.split("\n")) if "packet.Source()" in l)
dot_col = SRC.split("\n")[target_line].index("packet.Source().") + len("packet.Source().")
print("packet.Source(). ->", labels(target_line, dot_col)[:6])
proc.kill()
print("PROBE DONE")
