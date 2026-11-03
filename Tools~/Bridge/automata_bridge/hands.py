"""手足（窓口の act・lease）と止める反射（reflex/arm・reflex/ack・pause）を写す係 Hands。

Hands は、答えている途中の act の頼み（act_inflight）と、手足を借りる名前（by）の持ち主。
act_inflight は道具の糸と受ける糸（取り消し）の 2 つから触るので、act_lock で守る。
"""

import secrets
import threading

from . import wire

# 1 回に続けて送れる命令の数。
ACT_SEQ_MAX = 8

# seq の途中で done だった手の答えから落とす欄。しおりと時刻は最後の手の答えに残る。
SEQ_STEP_DROP = ("api", "ok", "instance", "stamp", "act", "command", "cursor")

# pause {step:N} の答えを、窓口が持ち越す最長の秒（窓口の AgentPauseRoute.StepHold と同じ）。
REFLEX_STEP_HOLD_SEC = 30

# act の答えを、窓口が持ち越す最長の秒（窓口の AgentActRoute.ActHold と同じ＝命令の最長 60 秒＋余り）。
ACT_HOLD_SEC = 65


def seq_step_brief(reply):
    """seq の途中の手の答えから、どの手でも同じ欄を落とす（8 手で約 2,700 字が文脈に残っていた・2026-10-31）。
    done でない手は、訳が要るので窓口の答えそのまま。ほかの欄（detail など）は残す。"""
    if not isinstance(reply, dict) or reply.get("ok") is not True or reply.get("outcome") != "done":
        return reply
    brief = {k: v for k, v in reply.items() if k not in SEQ_STEP_DROP and v is not None}
    if brief.get("via") == "pad":
        del brief["via"]
    if brief.get("paused") == {"by": "none"}:
        del brief["paused"]
    return brief


def act_face(command_args):
    """命令 1 つを、どちらのトークンで送るか（2026-10-11）。
    命令の args の to が {"pos":…}・{"id":…}（裏の行き先・walk_to と fight）の時だけ裏。ほかは表。
    窓口は、表のトークンのこの形を 403 back_only で断る。"""
    return "back" if isinstance(command_args, dict) and isinstance(command_args.get("to"), dict) else "surface"


class Hands:
    def __init__(self, link, calls, by_prefix="claude-"):
        self.link = link
        self.calls = calls
        # 手足を借りる名前（窓口の形 [A-Za-z0-9._-]{1,40}・橋ごとに 1 つ）。
        # ⭐ 名前は字に出てよい（答えの by・合図 lease.taken に載る）＝秘密ではない。GUID の形にはしない（表の漏れの見張りが ID とみなす）。
        self.by = by_prefix + secrets.token_hex(4) if by_prefix is not None else None
        self.act_inflight = set()
        self.act_lock = threading.Lock()

    def handlers(self):
        return {"reflex": self.reflex, "act": self.act}

    def reflex(self, call):
        """止める反射の道具を窓口の 3 つの道（reflex/arm・reflex/ack・pause）へ写す。字は窓口の返事そのまま。"""
        args = call.args
        do = args.get("do")
        timeout = wire.PLAIN_TIMEOUT_SEC
        if do == "arm":
            route, body = "reflex/arm", {k: args[k] for k in ("minutes", "stopBefore", "modes", "ignore") if k in args}
        elif do == "ack":
            route, body = "reflex/ack", {k: args[k] for k in ("resume",) if k in args}
        elif do in ("pause", "resume"):
            route, body = "pause", {"on": do == "pause"}
        elif do == "step":
            # 窓口は、進め終わるまで答えを持ち越す（最長 30 秒）。
            route, body = "pause", {"step": args.get("frames", 1)}
            timeout = REFLEX_STEP_HOLD_SEC + wire.WAIT_MARGIN_SEC
        else:
            return wire.tool_text({"ok": False, "error": {"code": "bad_do", "hint": "arm_ack_pause_resume_step"}}, True)
        status, text = self.link.post_first(call.found, route, body, "surface", timeout)
        return wire.text_result(status, text)

    def _begin(self, request_id):
        with self.act_lock:
            self.act_inflight.add(request_id)

    def _end(self, request_id):
        with self.act_lock:
            self.act_inflight.discard(request_id)

    def act(self, call):
        """手足の道具を窓口の 2 つの道（act・lease）へ写す。by は橋の名前を足す。字は窓口の返事そのまま。"""
        args = call.args
        do = args.get("do")
        if "seq" in args:
            if do is not None:
                return wire.tool_text({"ok": False, "error": {"code": "bad_do", "hint": "do_or_seq_not_both"}}, True)
            return self.act_seq(call.request_id, args)
        if not isinstance(do, str) or not do:
            return wire.tool_text({"ok": False, "error": {"code": "bad_do", "hint": "command_name_or_lease"}}, True)
        if do == "lease":
            body = {"by": self.by}
            if "release" in args:
                body["release"] = args["release"]
            status, text = self.link.post_first(call.found, "lease", body, "surface", wire.PLAIN_TIMEOUT_SEC)
            return wire.text_result(status, text)
        body = {"by": self.by, "do": do}
        body.update({k: args[k] for k in ("args", "ref") if k in args})
        self._begin(call.request_id)
        try:
            # 窓口は、命令が終わるまで答えを持ち越す（最長 65 秒）。裏の行き先（to:{pos}・{id}）だけ裏のトークン。
            status, text = self.link.post_first(call.found, "act", body, act_face(args.get("args")),
                                                ACT_HOLD_SEC + wire.WAIT_MARGIN_SEC)
        finally:
            self._end(call.request_id)
        return wire.text_result(status, text)

    def act_seq(self, request_id, args):
        """seq を、1 つずつ窓口の act へ送る（前の命令の答えが来てから次）。
        outcome が done でない命令・断り（200 以外）・取り消しで止める（keepGoing:true なら、断りと取り消しだけで止める）。
        答えは {ok, seq:[{i, do, status, reply}], stoppedAt?}。途中の手で done だった物は seq_step_brief で短くする。
        ⭐ 確かめ係は、道具を 1 回呼ぶたびに自分の文脈を全部読み直す＝決まった手順は橋の中で続けて送る（2026-10-10）。"""
        seq = args.get("seq")
        if not isinstance(seq, list) or not 1 <= len(seq) <= ACT_SEQ_MAX:
            return wire.tool_text({"ok": False, "error": {"code": "bad_seq", "hint": "1..%d_steps" % ACT_SEQ_MAX}}, True)
        for step in seq:
            do = step.get("do") if isinstance(step, dict) else None
            if not isinstance(do, str) or not do or do == "lease":
                return wire.tool_text({"ok": False, "error": {"code": "bad_seq", "hint": "each_step_needs_do_not_lease"}}, True)
        keep_going = args.get("keepGoing") is True
        steps, stopped, failed = [], None, False
        self._begin(request_id)
        try:
            for i, step in enumerate(seq):
                if self.calls.is_cancelled(request_id):
                    stopped = i
                    break
                found, _ = self.link.endpoints()
                if not found:
                    steps.append({"i": i, "do": step["do"], "status": None,
                                  "reply": {"ok": False, "error": {"code": "no_endpoint"}}})
                    stopped, failed = i, True
                    break
                body = {"by": self.by, "do": step["do"]}
                body.update({k: step[k] for k in ("args", "ref") if k in step})
                status, text = self.link.post_first(found, "act", body, act_face(step.get("args")),
                                                    ACT_HOLD_SEC + wire.WAIT_MARGIN_SEC)
                reply = wire.json_or_empty(text)
                steps.append({"i": i, "do": step["do"], "status": status, "reply": reply if reply else text})
                if status != 200:
                    stopped, failed = i, True
                    break
                if reply.get("outcome") != "done" and not keep_going:
                    stopped = i
                    break
        finally:
            self._end(request_id)
        for step in steps[:-1]:
            step["reply"] = seq_step_brief(step["reply"])
        body = {"ok": not failed, "seq": steps}
        if stopped is not None:
            body["stoppedAt"] = stopped
        return wire.tool_text(body, failed)

    def on_cancel(self, request_id):
        """受ける糸から呼ぶ。取り消された頼みが act なら、手足を返して命令を止める（返すのは別の糸＝受ける糸を待たせない）。
        ⭐ 取り消しても窓口の命令は走り続ける（最長 60 秒）。"""
        with self.act_lock:
            stop_act = request_id in self.act_inflight
        if stop_act:
            threading.Thread(target=self.release_for_cancel, daemon=True).start()

    def release_for_cancel(self):
        """取り消された命令を止める（手足を返す＝窓口は走っている命令を interrupted で終える）。届かなくても黙って終わる。"""
        try:
            found, _ = self.link.endpoints()
            if found:
                # 答えは見ない（借りが無い時の 409 not_leased・反射が走っている時の 409 reflex_acting は、止める物が無いだけ）。
                self.link.post_first(found, "lease", {"by": self.by, "release": True}, "surface", wire.PLAIN_TIMEOUT_SEC)
        except Exception as e:  # noqa: BLE001  取り消しの後始末で橋を落とさない。
            wire.log("act cancelled: release failed: %s" % type(e).__name__)
