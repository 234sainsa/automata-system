"""橋の組み立て（Bridge）と、標準入出力で回す口（serve）。

Bridge は部品を作ってつなぐだけ。状態はそれぞれの部品が持つ:
- Calls＝答えている途中の頼みと取り消し・McpServer＝標準出力と並べた裏の道具・Link＝探すプロジェクト・
  Hands＝走っている act と借りる名前・LongWait／Capture／LogReader＝状態なし・差し込み＝ゲームの状態。
"""

import threading

from . import __version__, wire
from .capture import Capture
from .hands import Hands
from .hooks import Hooks
from .logs import LogReader
from .rpc import Calls, McpServer
from .tools import check_tools
from .waits import LongWait
from .window import Link


class Bridge:
    """1 本の橋。

    projects＝つなぎ先を探すプロジェクトの根の並び・out＝標準出力（バイト）・endpoint_file＝つなぎ先のファイルの名前・
    tools＝道具の表（tools.tool_rows の形）・instructions＝initialize で見せる使い方・server＝{"name","title"[,"version"]}・
    plugins＝ゲームの差し込みの並び（hooks.py の口を持つ物）・track_back／retry_plain＝rpc.McpServer の options・
    wait_cap＝長い待ちで本当に待つ最長の秒・by_prefix＝手足を借りる名前の頭。"""

    def __init__(self, projects, out, endpoint_file, tools, instructions, server, plugins=(), track_back=True,
                 retry_plain=True, wait_cap=wire.WAIT_CACHE_SAFE_SEC, by_prefix="claude-", profile="main"):
        self.profile = profile
        self.endpoint_file = endpoint_file
        self.plugins = list(plugins)
        self.hooks = Hooks(self.plugins)
        self.calls = Calls()
        self.link = Link(projects, endpoint_file, self.hooks, self.calls.is_cancelled)
        self.hands = Hands(self.link, self.calls, by_prefix)
        self.waits = LongWait(self.link, self.calls, self._send, wire.clamp_wait_cap(wait_cap))
        self.capture = Capture(self.link)
        self.logs = LogReader(self.link, self.calls, self.hooks, wire.clamp_wait_cap(wait_cap))

        handlers = {}
        for part in (self.hands, self.waits, self.capture, self.logs):
            handlers.update(part.handlers())
        for hook in self.hooks.each("handlers"):
            handlers.update(hook())
        # ⚠ 表と処理が食い違えば、ここで ValueError（起動の時に止める・黙って通さない）。
        check_tools(tools, handlers)

        info = {"name": server["name"], "title": server["title"], "version": server.get("version", __version__)}
        self.server = McpServer(out, self.calls, self.link, self.hooks, tools, handlers, instructions, info,
                                track_back=track_back, retry_plain=retry_plain, cancel_listeners=[self.hands.on_cancel])
        for hook in self.hooks.each("bind"):
            hook(self)

    def _send(self, message):
        self.server.send(message)

    def plugin(self, name):
        """名前（差し込みの name）で差し込みを探す（無ければ None）。"""
        return next((p for p in self.plugins if getattr(p, "name", None) == name), None)

    def log_started(self):
        """起動の独り言（⭐ 1 行だけ＝使う側の自己試験が数える）。差し込みの startup_note を後ろに足す。"""
        notes = "".join(hook() for hook in self.hooks.each("startup_note"))
        wire.log("started v%s (%s); looking for %s under: %s%s"
                 % (self.server.server_info["version"], self.profile, self.endpoint_file,
                    ", ".join(self.link.projects), notes))

    def serve(self, stdin):
        """標準入力を 1 行ずつ読み、閉じるまで答える。差し込みの start を読み始める前に、close を閉じた後に呼ぶ。"""
        stop = threading.Event()
        for hook in self.hooks.each("start"):
            hook(stop)
        try:
            while True:
                line = stdin.readline()
                if not line:
                    break
                line = line.strip()
                if line:
                    self.server.handle_line(line.decode("utf-8", errors="replace"))
        finally:
            stop.set()
            for hook in self.hooks.each("close"):
                hook()
        return 0
