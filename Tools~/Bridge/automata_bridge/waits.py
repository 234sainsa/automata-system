"""長い待ち（窓口の wait・back/wait を 25 秒ずつつなぐ）の係 LongWait。状態は持たない（設定の wait_cap だけ）。"""

import json
import time

from . import wire


class LongWait:
    def __init__(self, link, calls, send, wait_cap=wire.WAIT_CACHE_SAFE_SEC):
        self.link = link
        self.calls = calls
        self.send = send
        self.wait_cap = wait_cap

    def handlers(self):
        return {"wait": self.run}

    def run(self, call):
        """待つ道具を、道具の表の route（wait・back/wait）へ写す。25 秒までは 1 回で送り、字は窓口の返事そのまま。
        25 秒より長い頼みは、窓口の timeout の答えの cursor を after にして、残りの秒を 25 秒ずつ待ち直す
        （hit・paused・world_closing・reset が来たら、その答えに waited＝待った秒と rounds＝打った回数を足して返す）。
        待ち直しの間の gap（読めずに消えた合図の数）は足し合わせる。使う側が取り消したら、次の待ちを打たない。
        progress（使う側が _meta.progressToken を付けた時）には、待ち直すたびに進みの知らせを送る。
        ⚠ 断り（200 以外）が来たら、そこで窓口の字のまま返す（Play が閉じた・待ちが 4 本を越えた など）。"""
        request_id, found, tool, args = call.request_id, call.found, call.tool, call.args
        progress = wire.meta_progress(call.params)
        seconds = args.get("timeoutSec", wire.WAIT_DEFAULT_SEC)
        if isinstance(seconds, bool) or not isinstance(seconds, (int, float)) or not 0 <= seconds <= wire.WAIT_LONG_MAX_SEC:
            return wire.tool_text({"ok": False, "error": {"code": "bad_timeout", "hint": "0..%d" % wire.WAIT_LONG_MAX_SEC}}, True)
        if seconds <= wire.WAIT_MAX_SEC:
            status, text = self.link.post_retry(request_id, found, tool["route"], args, tool["face"],
                                                int(seconds) + wire.WAIT_MARGIN_SEC)
            return wire.text_result(status, text)

        asked = seconds
        seconds = min(seconds, self.wait_cap)
        start = time.monotonic()
        deadline = start + seconds
        body = dict(args)
        rounds, gap = 0, 0
        while True:
            body["timeoutSec"] = max(0, min(wire.WAIT_MAX_SEC, int(deadline - time.monotonic())))
            status, text = self.link.post_retry(request_id, found, tool["route"], body, tool["face"],
                                                body["timeoutSec"] + wire.WAIT_MARGIN_SEC)
            rounds += 1
            reply = wire.json_or_empty(text)
            if status != 200 or not reply:
                if rounds > 1 and reply:
                    # 待ちの途中の断り＝それまで待った秒と、続きから待ち直せるしおりを足す（使う側が初めから待ち直さない）。
                    reply["waited"] = round(time.monotonic() - start, 1)
                    reply["rounds"] = rounds
                    reply["resumeAfter"] = body.get("after")
                    text = json.dumps(reply, ensure_ascii=False)
                return wire.text_result(status, text)
            lost = reply.get("gap")
            if isinstance(lost, int) and not isinstance(lost, bool):
                gap += lost
            cursor = reply.get("cursor")
            if reply.get("outcome") != "timeout" or not isinstance(cursor, str) or deadline - time.monotonic() < 1:
                break
            if self.calls.is_cancelled(request_id):
                break
            if progress is not None:
                self.send({"jsonrpc": "2.0", "method": "notifications/progress",
                           "params": {"progressToken": progress, "progress": round(time.monotonic() - start, 1),
                                      "total": seconds}})
            body["after"] = cursor
            found, _ = self.link.endpoints()
            if not found:
                break
        reply["waited"] = round(time.monotonic() - start, 1)
        reply["rounds"] = rounds
        if gap:
            reply["gap"] = gap
        if asked > seconds and reply.get("outcome") == "timeout":
            reply["capped"] = wire.capped(asked, self.wait_cap)
        return wire.tool_text(reply, False)
