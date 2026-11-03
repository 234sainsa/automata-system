"""127.0.0.1 の WebSocket の口（標準の部品だけ）。橋から見張りの AI へ、1 行ずつ知らせを送るために使う。

- 道はランダムの印（/ と 22 字）＝知らない道・WebSocket でない頼みは 404 で閉じる。
- 口は、つながった全部へ同じ字を送る。AI からの字は読んで捨てる（閉じる・ping にだけ答える）。
- ⚠ URL（道の印）は AI へ見せるが、ログには書かない。
"""

import base64
import hashlib
import secrets
import socket
import threading

from . import wire

# WebSocket の手を握る時の決まった字（RFC 6455）。
WS_GUID = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"


def recv_exact(conn, n):
    """ちょうど n バイト読む（途中で切れたら OSError）。"""
    data = b""
    while len(data) < n:
        chunk = conn.recv(n - len(data))
        if not chunk:
            raise OSError("closed")
        data += chunk
    return data


class WsClient:
    """口につながった 1 本（送るのは 1 本ずつ＝糸をまたいで字が混ざらないように lock で守る）。"""

    def __init__(self, conn):
        self.conn = conn
        self.lock = threading.Lock()

    def send_frame(self, first, payload):
        """1 つの枠を送る（橋から送る枠は、決まりどおり覆い（mask）を掛けない）。"""
        n = len(payload)
        if n < 126:
            head = bytes([first, n])
        elif n < 65536:
            head = bytes([first, 126]) + n.to_bytes(2, "big")
        else:
            head = bytes([first, 127]) + n.to_bytes(8, "big")
        with self.lock:
            self.conn.sendall(head + payload)

    def close(self):
        try:
            self.conn.close()
        except OSError:
            pass


class WsBroadcast:
    """口の持ち主。clients（つながった並び）は受ける糸・読む糸・送る糸から触るので lock で守る。"""

    def __init__(self):
        self.path = "/" + secrets.token_urlsafe(16)
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self.sock.bind(("127.0.0.1", 0))
        self.sock.listen(4)
        self.sock.settimeout(1.0)
        self.port = self.sock.getsockname()[1]
        self.clients = []
        self.lock = threading.Lock()
        self.closed = threading.Event()

    @property
    def url(self):
        return "ws://127.0.0.1:%d%s" % (self.port, self.path)

    def count(self):
        with self.lock:
            return len(self.clients)

    def serve(self):
        """つなぎを受ける糸（閉じるまで）。つないだ後の読み取りは 1 本ずつ別の糸。"""
        while not self.closed.is_set():
            try:
                conn, _ = self.sock.accept()
            except socket.timeout:
                continue
            except OSError:
                break
            threading.Thread(target=self._greet, args=(conn,), daemon=True).start()

    def _greet(self, conn):
        try:
            conn.settimeout(5.0)
            request = b""
            while b"\r\n\r\n" not in request:
                chunk = conn.recv(4096)
                if not chunk or len(request) > 16384:
                    raise OSError("bad request")
                request += chunk
            lines = request.split(b"\r\n\r\n", 1)[0].decode("latin-1").split("\r\n")
            parts = lines[0].split(" ")
            headers = {}
            for line in lines[1:]:
                name, sep, value = line.partition(":")
                if sep:
                    headers[name.strip().lower()] = value.strip()
            key = headers.get("sec-websocket-key", "")
            # 道はバイトで比べる（字で比べると、ASCII でない道で TypeError になり、404 を返さずに糸が落ちる）。
            if (len(parts) < 3 or parts[0] != "GET"
                    or not secrets.compare_digest(parts[1].encode("latin-1"), self.path.encode("ascii"))
                    or headers.get("upgrade", "").lower() != "websocket" or not key):
                conn.sendall(b"HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")
                conn.close()
                return
            accept = base64.b64encode(hashlib.sha1((key + WS_GUID).encode("ascii")).digest()).decode("ascii")
            conn.sendall(("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
                          "Sec-WebSocket-Accept: %s\r\n\r\n" % accept).encode("ascii"))
            conn.settimeout(None)
        except (OSError, UnicodeError):
            try:
                conn.close()
            except OSError:
                pass
            return
        client = WsClient(conn)
        with self.lock:
            if self.closed.is_set():
                client.close()
                return
            self.clients.append(client)
        wire.log("watch: a watcher joined (%d)" % self.count())
        self._read(client)

    def _read(self, client):
        """AI からの枠を読む（閉じる＝閉じ返して外す・ping＝pong を返す・ほかは捨てる）。"""
        try:
            while not self.closed.is_set():
                head = recv_exact(client.conn, 2)
                opcode = head[0] & 0x0F
                n = head[1] & 0x7F
                if n == 126:
                    n = int.from_bytes(recv_exact(client.conn, 2), "big")
                elif n == 127:
                    n = int.from_bytes(recv_exact(client.conn, 8), "big")
                if n > 65536:
                    break
                mask = recv_exact(client.conn, 4) if head[1] & 0x80 else b""
                payload = recv_exact(client.conn, n) if n else b""
                if mask:
                    payload = bytes(b ^ mask[i % 4] for i, b in enumerate(payload))
                if opcode == 0x8:
                    client.send_frame(0x88, payload[:2])
                    break
                if opcode == 0x9:
                    client.send_frame(0x8A, payload[:125])
        except OSError:
            pass
        self.drop(client)

    def send_text(self, text):
        """つながった全部へ 1 通送る。送れた数を返す（送れなかった口は外す）。"""
        data = text.encode("utf-8")
        with self.lock:
            clients = list(self.clients)
        sent = 0
        for client in clients:
            try:
                client.send_frame(0x81, data)
                sent += 1
            except OSError:
                self.drop(client)
        return sent

    def drop(self, client):
        with self.lock:
            if client not in self.clients:
                return
            self.clients.remove(client)
            left = len(self.clients)
        client.close()
        wire.log("watch: a watcher left (%d)" % left)

    def close_all(self):
        """口を閉じる（橋が閉じる時）。つながっている見張りには「去る」の枠（1001）を送ってから切る。"""
        self.closed.set()
        try:
            self.sock.close()
        except OSError:
            pass
        with self.lock:
            clients = list(self.clients)
            self.clients.clear()
        for client in clients:
            try:
                client.send_frame(0x88, (1001).to_bytes(2, "big"))
            except OSError:
                pass
            client.close()
