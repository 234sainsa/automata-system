"""撮る（窓口の capture・back/screen）を写す係 Capture。状態は持たない。"""

from . import wire

# capture の答えを、窓口が持ち越す最長の秒（窓口の AgentCaptureRoute.CaptureHold と同じ＝絵をファイルにし終える締め切り 8 秒＋余り）。
CAPTURE_HOLD_SEC = 10

# form（2026-10-26）＝撮る前に Game ビューを替える寸法（縦はスマホの 19.5:9・横は PC）。
# back/screen の答えを、窓口が持ち越す最長の秒（窓口の AgentScreenRoute.Hold と同じ＝そろう 5 秒＋落ち着く 5 秒＋余り）。
CAPTURE_FORMS = {"portrait": (1080, 2340), "landscape": (1920, 1080)}
SCREEN_HOLD_SEC = 11


class Capture:
    def __init__(self, link):
        self.link = link

    def handlers(self):
        return {"capture": self.run}

    def run(self, call):
        """撮る道具を窓口の capture へ写す。字は窓口の返事そのまま。
        hideDebugOverlays:true の時だけ裏のトークンで送る（窓口は、表のトークンの帯消しを 403 back_only で断る）。"""
        args = call.args
        body = {k: args[k] for k in ("sections", "measure", "hideDebugOverlays") if k in args}
        face = "back" if args.get("hideDebugOverlays") is True else "surface"
        form = args.get("form")
        if form is not None:
            return self.forms(call.request_id, call.found, body, face, form, args.get("settleSec"))
        # 窓口は、絵をファイルにし終えるまで答えを持ち越す（最長 10 秒）。
        status, text = self.link.post_retry(call.request_id, call.found, "capture", body, face,
                                            CAPTURE_HOLD_SEC + wire.WAIT_MARGIN_SEC)
        return wire.text_result(status, text)

    def forms(self, request_id, found, body, face, form, settle):
        """form（2026-10-26）＝back/screen で Game ビューの大きさを替えてから撮り、撮り終えたら元へ戻す。
        both は縦→横の 2 枚（窓口の 1 秒に 1 枚は post_retry が待って打ち直す）。
        ⭐ 戻すのは、途中で断られても必ず打つ（finally）＝替えたまま残さない。"""
        if form not in ("portrait", "landscape", "both"):
            return wire.tool_text({"ok": False, "error": {"code": "bad_form", "hint": "portrait|landscape|both"}}, True)
        if settle is not None and (isinstance(settle, bool) or not isinstance(settle, (int, float)) or not 0 <= settle <= 5):
            return wire.tool_text({"ok": False, "error": {"code": "bad_settleSec", "hint": "0..5"}}, True)
        names = ["portrait", "landscape"] if form == "both" else [form]
        shots, failed = [], False
        try:
            for name in names:
                width, height = CAPTURE_FORMS[name]
                screen = {"size": [width, height]}
                if settle is not None:
                    screen["settleSec"] = settle
                status, text = self.link.post_retry(request_id, found, "back/screen", screen, "back",
                                                    SCREEN_HOLD_SEC + wire.WAIT_MARGIN_SEC)
                if status != 200:
                    shots.append({"form": name, "screenError": wire.json_or_empty(text) or text})
                    failed = True
                    break
                status, text = self.link.post_retry(request_id, found, "capture", body, face,
                                                    CAPTURE_HOLD_SEC + wire.WAIT_MARGIN_SEC)
                shots.append({"form": name, "capture": wire.json_or_empty(text) or text})
                if status != 200:
                    failed = True
                    break
        finally:
            status, text = self.link.post_retry(request_id, found, "back/screen", {"restore": True, "settleSec": 0}, "back",
                                                SCREEN_HOLD_SEC + wire.WAIT_MARGIN_SEC)
            restored = wire.json_or_empty(text) or text
        return wire.tool_text({"ok": not failed, "forms": shots, "restored": restored}, failed)
