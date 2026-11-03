"""MCP（JSON-RPC 2.0・1 行に 1 通）の受け答え。

- Calls は、答えている途中の頼み（inflight）と、そのうち使う側が取り消した頼み（cancelled）の持ち主。
  2 つの糸（受ける糸と道具の糸）から触るので、call_lock で守る。
- McpServer は、標準出力（out）と、最後に並べた裏の道具の有無（listed_back）の持ち主。
  道具の頼みは 1 本ずつ別の糸で走らせ、答えを送る時に差し込みの on_answer を呼ぶ。
"""

import collections
import json
import threading

from . import wire
from .tools import public_tool

# 処理（handler）に渡す 1 回の呼び出し。found＝読めたつなぎ先（新しい順の (中身, 道)）。
Call = collections.namedtuple("Call", "request_id found tool args params")


class Calls:
    def __init__(self):
        self.inflight = set()
        self.cancelled = set()
        self.call_lock = threading.Lock()

    def begin(self, request_id):
        with self.call_lock:
            self.inflight.add(request_id)

    def finish(self, request_id):
        """答え終えた頼みを外す。使う側が取り消していたかを返す。"""
        with self.call_lock:
            self.inflight.discard(request_id)
            cancelled = request_id in self.cancelled
            self.cancelled.discard(request_id)
        return cancelled

    def cancel(self, request_id):
        with self.call_lock:
            # 答え終わった頼みの取り消しは、控えに残さない（控えが溜まらないように）。
            if request_id in self.inflight:
                self.cancelled.add(request_id)

    def is_cancelled(self, request_id):
        with self.call_lock:
            return request_id in self.cancelled


class McpServer:
    """受け答えの係。

    options:
    - track_back＝tools/list で、つなぎ先に裏のトークンがある時だけ裏の道具（face back）を並べ、並びが変わったら
      list_changed を送る（偽なら表の全部をそのまま並べ、つなぎ先を読まない）。
    - retry_plain＝route へそのまま写す道具で、走らせる前の断りを橋が待って打ち直す（偽なら 1 回だけ送る）。"""

    def __init__(self, out, calls, link, hooks, tools, handlers, instructions, server_info,
                 track_back=True, retry_plain=True, cancel_listeners=()):
        self.out = out
        self.out_lock = threading.Lock()
        self.calls = calls
        self.link = link
        self.hooks = hooks
        self.tools = list(tools)
        self.tools_by_name = {tool["name"]: tool for tool in self.tools}
        self.handlers = dict(handlers)
        self.instructions = instructions
        self.server_info = server_info
        self.track_back = track_back
        self.retry_plain = retry_plain
        self.cancel_listeners = list(cancel_listeners)
        # 最後に tools/list で裏の道具を並べたか（並びが変わった時に list_changed を送るため）。None＝まだ並べていない。
        self.listed_back = None

    # ── 送る ─────────────────────────────────────────────────────────

    def send(self, message):
        data = (json.dumps(message, ensure_ascii=False, separators=(",", ":")) + "\n").encode("utf-8")
        with self.out_lock:
            self.out.write(data)
            self.out.flush()

    def answer(self, request_id, message):
        """道具の頼みに答える。使う側が取り消した頼みには答えない（MCP の決まり）。
        差し込みの on_answer には、送る時に届くか（delivered）を渡す（取り消された答えは使う側に届かない）。"""
        cancelled = self.calls.finish(request_id)
        for hook in self.hooks.each("on_answer"):
            hook(request_id, not cancelled)
        if cancelled:
            return
        self.send(message)

    # ── 受ける ────────────────────────────────────────────────────────

    def handle_line(self, line):
        try:
            message = json.loads(line)
        except ValueError:
            self.send(wire.error(None, -32700, "Parse error"))
            return
        if isinstance(message, list):
            # 束（2025-03-26 の形）。1 通ずつ扱い、1 通ずつ答える。
            if not message:
                self.send(wire.error(None, -32600, "Invalid Request"))
            for item in message:
                self.dispatch(item)
            return
        self.dispatch(message)

    def dispatch(self, message):
        if not isinstance(message, dict) or message.get("jsonrpc") != "2.0":
            self.send(wire.error(message.get("id") if isinstance(message, dict) else None, -32600, "Invalid Request"))
            return
        method = message.get("method")
        if not isinstance(method, str):
            # 使う側からの返事（橋は頼みを出さないので、来ても捨てる）。
            return
        params = message.get("params")
        if not isinstance(params, dict):
            params = {}
        if "id" not in message:
            self.on_notification(method, params)
            return
        request_id = message["id"]
        if method == "initialize":
            wanted = params.get("protocolVersion")
            version = wanted if wanted in wire.PROTOCOL_VERSIONS else wire.PROTOCOL_VERSIONS[0]
            self.send(wire.result(request_id, {
                "protocolVersion": version,
                "capabilities": {"tools": {"listChanged": True}},
                "serverInfo": self.server_info,
                "instructions": self.instructions,
            }))
        elif method == "ping":
            self.send(wire.result(request_id, {}))
        elif method == "tools/list":
            if not self.track_back:
                self.send(wire.result(request_id, {"tools": [public_tool(t) for t in self.tools]}))
                return
            back = self.link.back_available()
            self.listed_back = back
            tools = [public_tool(t) for t in self.tools if back or t["face"] != "back"]
            self.send(wire.result(request_id, {"tools": tools}))
        elif method == "tools/call":
            # ⭐ wait は 25 秒まで返らない。そのあいだも ping や別の道具に答えられるよう、1 本ずつ別の糸で送る。
            self.calls.begin(request_id)
            threading.Thread(target=self.call_tool, args=(request_id, params), daemon=True).start()
        elif method in ("resources/list", "resources/templates/list", "prompts/list"):
            key = {"resources/list": "resources", "resources/templates/list": "resourceTemplates", "prompts/list": "prompts"}[method]
            self.send(wire.result(request_id, {key: []}))
        else:
            self.send(wire.error(request_id, -32601, "Method not found: " + method))

    def on_notification(self, method, params):
        if method == "notifications/cancelled":
            request_id = params.get("requestId")
            self.calls.cancel(request_id)
            for listener in self.cancel_listeners:
                listener(request_id)

    # ── 道具 ─────────────────────────────────────────────────────────

    def call_tool(self, request_id, params):
        try:
            self.answer(request_id, self.run_tool(request_id, params))
        except Exception as e:  # noqa: BLE001  橋が落ちても、使う側を待たせたままにしない。
            wire.log("tool failed: %s: %s" % (type(e).__name__, e))
            # 断りの答えには、差し込みが控えた物（渡した合図など）が載っていない＝届かない扱いで捨てさせる。
            for hook in self.hooks.each("on_answer"):
                hook(request_id, False)
            self.answer(request_id, wire.error(request_id, -32603, "Internal error: %s" % type(e).__name__))

    def run_tool(self, request_id, params):
        """道具を 1 つ走らせ、差し込みの decorate_reply に答えを見せる（2 つ目の字の塊を足すなど）。
        ⭐ 1 つ目の塊（窓口の返事そのままの字）は変えない。"""
        reply = self.run_tool_body(request_id, params)
        for hook in self.hooks.each("decorate_reply"):
            reply = hook(params, reply)
        return reply

    def run_tool_body(self, request_id, params):
        name = params.get("name")
        tool = self.tools_by_name.get(name)
        if tool is None:
            return wire.error(request_id, -32602, "Unknown tool: %s" % name)
        args = params.get("arguments")
        if args is None:
            args = {}
        if not isinstance(args, dict):
            return wire.error(request_id, -32602, "arguments must be an object")
        for hook in self.hooks.each("prepare_call"):
            args, refused = hook(name, args)
            if refused is not None:
                return wire.result(request_id, wire.tool_text(refused, True))

        found, checked = self.link.endpoints()
        if not found and self.link.play_starting():
            found, checked = self.link.wait_endpoints(request_id)
        if not found:
            return wire.result(request_id, wire.tool_text(
                {"ok": False, "error": {"code": "no_endpoint", "hint": "enter_play_with_agent_api_open"}, "checked": checked},
                True))

        if self.track_back:
            back = bool(found[0][0].get("backToken"))
            if self.listed_back is not None and back != self.listed_back:
                # 裏の道具の並びが変わった（製品版と Editor を取り替えた時など）。使う側に並べ直してもらう。
                self.listed_back = back
                self.send({"jsonrpc": "2.0", "method": "notifications/tools/list_changed"})

        handler = tool.get("handler")
        if handler is not None:
            return wire.result(request_id, self.handlers[handler](Call(request_id, found, tool, args, params)))
        for hook in self.hooks.each("call_args"):
            args = hook(tool, args)
        if self.retry_plain:
            status, text = self.link.post_retry(request_id, found, tool["route"], args, tool["face"], wire.PLAIN_TIMEOUT_SEC)
        else:
            status, text = self.link.post_first(found, tool["route"], args, tool["face"], wire.PLAIN_TIMEOUT_SEC)
        # ⭐ 字は窓口の返事そのまま（200 以外も、窓口の断りの形 {"ok":false,"error":{…}} のまま）。
        return wire.result(request_id, {"content": [{"type": "text", "text": text}], "isError": status != 200})
