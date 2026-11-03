"""橋の自己試験の土台（作り物の窓口・橋を起こす係・結果の控え）。ゲームに依存しない。

- 作り物の窓口（Fake）は、本物の窓口と同じく、トークンで表と裏を分け、道ごとに決まった答えを返す。
- Client は、汎用の入口（../agent_bridge.py）を、試しの道具の表（fixture_tools.json）で起こし、1 行 1 通で話す。
- check は結果を 1 行ずつ OK / NG で出し、results に控える（run_tests.py が数える）。
"""

import json
import os
import queue
import socket
import subprocess
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

HERE = os.path.dirname(os.path.abspath(__file__))
BRIDGE_DIR = os.path.dirname(HERE)
ENTRY = os.path.join(BRIDGE_DIR, "agent_bridge.py")
TOOLS = os.path.join(HERE, "fixture_tools.json")

# 試しのトークン（本物ではない）。
SURFACE = "surface-token-test-1"
BACK = "back-token-test-2"

received = []      # 作り物の窓口が受けた頼み（route, face, body, host）
sent_texts = []    # 作り物の窓口が返した字（そのまま）
results = []       # (名前, 通ったか)

# 橋の打ち直しの試し: 道が合えば、頭から 1 つずつ (道, 番号, code) の断りを返す。
FAKE_REFUSE = []
# 長い待ちの試し（until ["loop"]）で、作り物の窓口が受けた待ちの数。
FAKE_LOOP = [0]

LOOK_TEXT = "こんにちは、旅の人。ここは静かな町です。"


def check(name, ok, detail=""):
    results.append((name, bool(ok)))
    print(("OK  " if ok else "NG  ") + name + (("  … " + detail) if detail and not ok else ""))
    sys.stdout.flush()


class Fake(BaseHTTPRequestHandler):
    """作り物の窓口。トークンで表と裏を分け、道ごとに決まった答えを返す。"""

    protocol_version = "HTTP/1.1"

    def log_message(self, *args):
        pass

    def do_POST(self):
        n = int(self.headers.get("Content-Length") or 0)
        body = self.rfile.read(n).decode("utf-8")
        auth = self.headers.get("Authorization", "")
        face = "back" if auth == "Bearer " + BACK else ("surface" if auth == "Bearer " + SURFACE else "none")
        route = self.path[len("/v1/"):]
        received.append((route, face, body, self.headers.get("Host")))
        req = json.loads(body or "{}")
        if face == "none":
            return self.reply(401, {"ok": False, "error": {"code": "unauthorized"}})
        if route.startswith("back/") and face != "back":
            return self.reply(403, {"ok": False, "error": {"code": "blind"}})
        if FAKE_REFUSE and FAKE_REFUSE[0][0] == route:
            _, status, code = FAKE_REFUSE.pop(0)
            return self.reply(status, {"api": "1.0", "ok": False, "error": {"code": code, "hint": "x"}})
        if route in ("wait", "back/wait"):
            return self.wait(req)
        if route == "look":
            return self.reply(200, {"api": "1.0", "ok": True, "texts": {"conversation": {"text": LOOK_TEXT}}, "echo": req})
        if route == "hello":
            return self.reply(200, {"api": "1.0", "ok": True, "stage": 2, "signals": ["choices.shown"]})
        if route == "back/look":
            return self.reply(200, {"api": "1.0", "ok": True, "characters": [], "echo": req})
        if route == "back/keep_alive":
            return self.reply(200, {"api": "1.0", "ok": True, "keepAlive": {"on": bool(req.get("on")), "saves": 0}})
        if route == "back/command":
            return self.reply(200, {"api": "1.0", "ok": True, "command": {"line": req.get("line"), "said": []}})
        if route == "back/shot":
            return self.reply(200, {"api": "1.0", "ok": True, "shot": {"file": "x.png", "says": []}, "echo": req})
        if route in ("reflex/arm", "reflex/ack", "pause"):
            return self.reply(200, {"api": "1.0", "ok": True, "paused": {"by": "none"}, "echo": req})
        if route == "act":
            # 窓口と同じ決まり: 裏の行き先（args.to が {pos}・{id} の形）は、裏のトークンでないと 403 back_only。
            command_args = req.get("args")
            if isinstance(command_args, dict) and isinstance(command_args.get("to"), dict) and face != "back":
                return self.reply(403, {"ok": False, "error": {"code": "back_only", "hint": "use_back_token"}})
            if req.get("do") == "slow":
                time.sleep(3)  # 走り続ける命令（取り消しの試し）
            outcome = "no-effect" if req.get("do") == "noeffect" else "done"
            return self.reply(200, {"api": "1.0", "ok": True, "act": "a1", "command": req.get("do"), "outcome": outcome, "echo": req})
        if route == "lease":
            return self.reply(200, {"api": "1.0", "ok": True, "lease": "L1", "echo": req})
        if route == "capture":
            return self.reply(200, {"api": "1.0", "ok": True, "capture": "p1-1", "file": "/tmp/captures/p1-1.png",
                                    "px": {"w": 640, "h": 360}, "frame": 120, "echo": req})
        return self.reply(404, {"api": "1.0", "ok": False, "error": {"code": "no_route", "hint": route}})

    def wait(self, req):
        seconds = req.get("timeoutSec", 20)
        until = req.get("until")
        if until == ["hang"]:
            time.sleep(seconds + 12)  # 橋の待ち（0＋10 秒）より長く黙る
        elif until == ["loop"]:
            # 長い待ち（25 秒より長い）の試し: 2 回は時間切れ（1 つずつ読めずに消えた）・3 回目に当てる。
            FAKE_LOOP[0] += 1
            n = FAKE_LOOP[0]
            if n < 3:
                return self.reply(200, {"api": "1.0", "ok": True, "outcome": "timeout", "cursor": "p1:%d" % (20 + n),
                                        "gap": 1, "echo": req})
            return self.reply(200, {"api": "1.0", "ok": True, "outcome": "hit", "cursor": "p1:30",
                                    "event": {"seq": 30, "kind": "goal.failed"}, "echo": req})
        else:
            time.sleep(min(seconds, 3))
        return self.reply(200, {"api": "1.0", "ok": True, "outcome": "timeout", "cursor": "p1:5", "echo": req})

    def reply(self, status, obj):
        text = json.dumps(obj, ensure_ascii=False, separators=(",", ":"))
        sent_texts.append(text)
        data = text.encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)


class QuietServer(ThreadingHTTPServer):
    daemon_threads = True

    def handle_error(self, request, client_address):
        # 時間切れの試しでは、橋が先に接続を切る。その後で作り物の窓口が書こうとして落ちるのは、わざとなので黙る。
        pass


def start_fake():
    """作り物の窓口を 127.0.0.1 の空いた口で起こす。(server, port) を返す。"""
    server = QuietServer(("127.0.0.1", 0), Fake)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return server, server.server_address[1]


class Client:
    """橋を 1 本起こし、1 行 1 通で話す。extra＝入口に足す引数（--plugin など）。"""

    def __init__(self, projects, extra=None):
        env = dict(os.environ)
        env["PYTHONIOENCODING"] = "utf-8"
        args = [sys.executable, "-u", ENTRY, "--tools", TOOLS]
        for p in projects:
            args += ["--project", p]
        args += list(extra or [])
        self.p = subprocess.Popen(args, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, env=env)
        self.inbox = queue.Queue()
        self.stash = []
        self.raw_out = []
        self.err = []
        threading.Thread(target=self._read_out, daemon=True).start()
        threading.Thread(target=self._read_err, daemon=True).start()

    def _read_out(self):
        for line in self.p.stdout:
            text = line.decode("utf-8")
            self.raw_out.append(text)
            self.inbox.put(json.loads(text))
        self.inbox.put(None)

    def _read_err(self):
        for line in self.p.stderr:
            self.err.append(line.decode("utf-8", errors="replace"))

    def send(self, obj):
        self.send_raw((json.dumps(obj, ensure_ascii=False) + "\n").encode("utf-8"))

    def send_raw(self, data):
        self.p.stdin.write(data)
        self.p.stdin.flush()

    def wait_for(self, pred, timeout=5.0):
        for i, m in enumerate(self.stash):
            if pred(m):
                return self.stash.pop(i)
        deadline = time.time() + timeout
        while True:
            left = deadline - time.time()
            if left <= 0:
                return None
            try:
                m = self.inbox.get(timeout=left)
            except queue.Empty:
                return None
            if m is None:
                return None
            if pred(m):
                return m
            self.stash.append(m)

    def request(self, rid, method, params=None, timeout=5.0):
        msg = {"jsonrpc": "2.0", "id": rid, "method": method}
        if params is not None:
            msg["params"] = params
        self.send(msg)
        return self.wait_for(lambda m: m.get("id") == rid and "method" not in m, timeout)

    def close(self):
        try:
            self.p.stdin.close()
        except OSError:
            pass
        try:
            return self.p.wait(timeout=5)
        except subprocess.TimeoutExpired:
            self.p.kill()
            return None


def write_endpoint(project, port, back=True, mtime=None, name="endpoint.json"):
    folder = os.path.join(project, "Library", "AgentApi")
    os.makedirs(folder, exist_ok=True)
    data = {"port": port, "surfaceToken": SURFACE, "pid": 1, "playId": "p1"}
    if back:
        data["backToken"] = BACK
    path = os.path.join(folder, name)
    with open(path, "w", encoding="utf-8") as f:
        json.dump(data, f)
    if mtime is not None:
        os.utime(path, (mtime, mtime))
    return path


def free_port():
    s = socket.socket()
    s.bind(("127.0.0.1", 0))
    port = s.getsockname()[1]
    s.close()
    return port


def tool_text(reply):
    return reply["result"]["content"][0]["text"]


def body_of(reply):
    """道具の答えの 1 つ目の字を JSON として読む（答えが無ければ空）。"""
    return json.loads(tool_text(reply)) if reply else {}
