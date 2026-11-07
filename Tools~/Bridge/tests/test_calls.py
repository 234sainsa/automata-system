"""橋の自己試験 1: MCP の受け答え・道具の写し方・手足・反射・撮る・打ち直し・長い待ち・取り消し・裏の出入り。

run_tests.py から呼ぶ（1 本の橋を起こし、頭から順に打つ）。
"""

import json
import os
import re
import time

from bridge_harness import FAKE_REFUSE, LOOK_TEXT, SURFACE, BACK, Client, check, received, sent_texts, tool_text, write_endpoint

# 試しの道具の表で、読むだけでない道具（annotations の readOnlyHint が false）。
WRITERS = ("agent_reflex", "agent_act", "agent_capture", "agent_back_keep_alive", "agent_back_command", "agent_back_shot")


def run(root, port):
    proj_a = os.path.join(root, "A")
    now = time.time()
    write_endpoint(proj_a, port, mtime=now - 100)

    # ── 1 本目の橋（つなぎ先は A だけ） ──
    c = Client([proj_a])
    r = c.request(1, "initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
                                    "clientInfo": {"name": "test", "version": "0"}})
    res = (r or {}).get("result", {})
    check("initialize: 頼んだ版を返す", res.get("protocolVersion") == "2025-06-18", str(r))
    check("initialize: tools.listChanged と instructions", res.get("capabilities", {}).get("tools", {}).get("listChanged") is True
          and "agent_hello" in res.get("instructions", ""), str(res)[:300])
    r = c.request(2, "initialize", {"protocolVersion": "1999-01-01"})
    check("initialize: 知らない版には最新を返す", (r or {}).get("result", {}).get("protocolVersion") == "2025-06-18", str(r))
    c.send({"jsonrpc": "2.0", "method": "notifications/initialized"})

    r = c.request(3, "tools/list")
    tools = (r or {}).get("result", {}).get("tools", [])
    names = [t["name"] for t in tools]
    check("tools/list: 形（inputSchema・readOnlyHint は WRITERS だけ false）",
          all(t["inputSchema"]["type"] == "object" and t["annotations"]["readOnlyHint"] is (t["name"] not in WRITERS)
              for t in tools))
    check("tools/list: 橋だけの欄（handler・face）を見せない", all("handler" not in t and "face" not in t for t in tools))

    # agent_capture: capture へ写す。帯を消す（hideDebugOverlays:true）時だけ裏のトークン（窓口は表の帯消しを 403 で断る）。
    for rid, args, want_face in (
            (60, {"sections": ["people"], "measure": True}, "surface"),
            (61, {}, "surface"),
            (62, {"hideDebugOverlays": False}, "surface"),
            (63, {"measure": False, "hideDebugOverlays": True}, "back")):
        r = c.request(rid, "tools/call", {"name": "agent_capture", "arguments": args})
        check("agent_capture %s: %s のトークンで capture へ、頼みをそのまま送る" % (json.dumps(args), want_face),
              r and r["result"]["isError"] is False and received[-1][:2] == ("capture", want_face)
              and json.loads(received[-1][2]) == args and tool_text(r) == sent_texts[-1],
              "%s %s" % (str(r)[:200], received[-1][:3]))

    # agent_act: 命令は act へ、lease は lease へ写す。by は橋の名前（--by-prefix の既定 "agent-" と 8 字・橋の中で同じ）を橋が足す。
    r = c.request(50, "tools/call", {"name": "agent_act", "arguments": {"do": "move", "args": {"stick": [0, 1], "sec": 1}, "ref": "r1"}})
    sent = json.loads(received[-1][2])
    by = sent.get("by", "")
    check("agent_act move: act へ by・do・args・ref を送る",
          r and r["result"]["isError"] is False and received[-1][:2] == ("act", "surface")
          and re.fullmatch(r"agent-[0-9a-f]{8}", by) is not None
          and sent == {"by": by, "do": "move", "args": {"stick": [0, 1], "sec": 1}, "ref": "r1"}, "%s %s" % (str(r)[:200], sent))
    r = c.request(51, "tools/call", {"name": "agent_act", "arguments": {"do": "lease"}})
    check("agent_act lease: lease へ同じ by だけを送る", r and r["result"]["isError"] is False
          and received[-1][:2] == ("lease", "surface") and json.loads(received[-1][2]) == {"by": by}, str(received[-1][:3]))
    r = c.request(52, "tools/call", {"name": "agent_act", "arguments": {"do": "lease", "release": True}})
    check("agent_act lease release: lease へ by と release を送る", r and received[-1][0] == "lease"
          and json.loads(received[-1][2]) == {"by": by, "release": True}, str(received[-1][:3]))
    before = len(received)
    r = c.request(53, "tools/call", {"name": "agent_act", "arguments": {"do": ""}})
    check("agent_act: 空の do は窓口へ送らずに bad_do", r and r["result"]["isError"] is True
          and json.loads(tool_text(r))["error"]["code"] == "bad_do" and len(received) == before, str(r)[:300])

    # agent_act の裏の行き先（10-11）: to が {pos}・{id} の時だけ、橋が裏のトークンで送る（札の to は表のまま）。
    for rid, do, command_args, want_face in (
            (92, "walk_to", {"to": {"pos": [1, 0, 2]}}, "back"),
            (93, "fight", {"to": {"id": "npc-7"}, "sec": 10}, "back"),
            (94, "fight", {"to": "p3"}, "surface")):
        r = c.request(rid, "tools/call", {"name": "agent_act", "arguments": {"do": do, "args": command_args}})
        check("agent_act %s %s: %s のトークンで act へ送る" % (do, json.dumps(command_args), want_face),
              r and r["result"]["isError"] is False and received[-1][:2] == ("act", want_face)
              and json.loads(received[-1][2]) == {"by": by, "do": do, "args": command_args}, "%s %s" % (str(r)[:200], received[-1][:3]))

    # agent_reflex: do ごとに、窓口の 3 つの道へ写す（橋だけの欄 do・frames は送らない）。
    for rid, args, want_route, want_body in (
            (40, {"do": "arm", "minutes": 30, "stopBefore": "18:00"}, "reflex/arm", {"minutes": 30, "stopBefore": "18:00"}),
            (41, {"do": "arm", "minutes": 0}, "reflex/arm", {"minutes": 0}),
            # stopBefore の null（時刻を消す）も、そのまま窓口へ届ける（省くのとは別＝省けば前の時刻が続く）。
            (39, {"do": "arm", "minutes": 45, "stopBefore": None}, "reflex/arm", {"minutes": 45, "stopBefore": None}),
            # modes（動く反射の選び）も、null（全部に戻す）を含めてそのまま届ける。
            (37, {"do": "arm", "minutes": 30, "modes": ["flee"]}, "reflex/arm", {"minutes": 30, "modes": ["flee"]}),
            (36, {"do": "arm", "minutes": 30, "modes": None}, "reflex/arm", {"minutes": 30, "modes": None}),
            # ignore（働いても止めない守り）も、null（どれも止めるに戻す）を含めてそのまま届ける。
            (34, {"do": "arm", "minutes": 30, "ignore": ["downed"]}, "reflex/arm", {"minutes": 30, "ignore": ["downed"]}),
            (33, {"do": "arm", "minutes": 30, "ignore": None}, "reflex/arm", {"minutes": 30, "ignore": None}),
            (42, {"do": "ack"}, "reflex/ack", {}),
            (43, {"do": "ack", "resume": False}, "reflex/ack", {"resume": False}),
            (44, {"do": "pause"}, "pause", {"on": True}),
            (45, {"do": "resume"}, "pause", {"on": False}),
            (46, {"do": "step", "frames": 5}, "pause", {"step": 5}),
            (47, {"do": "step"}, "pause", {"step": 1})):
        r = c.request(rid, "tools/call", {"name": "agent_reflex", "arguments": args})
        check("agent_reflex %s: %s へ %s" % (args["do"], want_route, json.dumps(want_body)),
              r and r["result"]["isError"] is False and received[-1][:2] == (want_route, "surface")
              and json.loads(received[-1][2]) == want_body, "%s %s" % (str(r)[:200], received[-1][:3]))
    before = len(received)
    r = c.request(48, "tools/call", {"name": "agent_reflex", "arguments": {"do": "jump"}})
    check("agent_reflex: 知らない do は窓口へ送らずに bad_do", r and r["result"]["isError"] is True
          and json.loads(tool_text(r))["error"]["code"] == "bad_do" and len(received) == before, str(r)[:300])

    r = c.request(4, "tools/call", {"name": "agent_hello", "arguments": {}})
    check("agent_hello: 字が窓口の返事と同じ", r and r["result"]["isError"] is False and tool_text(r) == sent_texts[-1],
          str(r)[:300])
    check("agent_hello: 表のトークン・Host", received[-1][:2] == ("hello", "surface") and received[-1][3] == "127.0.0.1:%d" % port,
          str(received[-1]))

    r = c.request(5, "tools/call", {"name": "agent_look", "arguments": {"sections": ["texts"]}})
    check("agent_look: 日本語の字がそのまま", r and LOOK_TEXT in tool_text(r) and tool_text(r) == sent_texts[-1],
          str(r)[:300])
    check("agent_look: 頼みの中身が届く", json.loads(received[-1][2]) == {"sections": ["texts"]}, received[-1][2])

    # 待ちの最中にほかの頼みへ答えるか（wait は 2 秒・ping はすぐ）
    c.send({"jsonrpc": "2.0", "id": 10, "method": "tools/call",
            "params": {"name": "agent_wait", "arguments": {"until": ["choices.shown"], "timeoutSec": 2}}})
    time.sleep(0.3)
    t0 = time.time()
    r_ping = c.request(11, "ping")
    ping_ms = (time.time() - t0) * 1000
    r_wait = c.wait_for(lambda m: m.get("id") == 10, 8)
    check("agent_wait の最中の ping に、すぐ答える", r_ping == {"jsonrpc": "2.0", "id": 11, "result": {}} and ping_ms < 1000,
          "%s %.0fms" % (r_ping, ping_ms))
    check("agent_wait: 答えが来る（until が届く）", r_wait and '"outcome":"timeout"' in tool_text(r_wait)
          and json.loads(tool_text(r_wait))["echo"]["until"] == ["choices.shown"], str(r_wait)[:300])

    r = c.request(12, "tools/call", {"name": "agent_back_look", "arguments": {"near": None, "fields": ["all"]}})
    check("agent_back_look: 裏のトークンで送る", r and r["result"]["isError"] is False and received[-1][1] == "back"
          and json.loads(received[-1][2]) == {"near": None, "fields": ["all"]}, str(received[-1]))
    # 数える頼み（10-11＝around・life・count・list）も、欄をそのまま送る。
    counting = {"around": "npc-1", "near": 2.6, "kinds": ["enemy"], "life": ["up", "fainted"], "count": True, "list": False}
    r = c.request(16, "tools/call", {"name": "agent_back_look", "arguments": counting})
    check("agent_back_look: around・life・count・list をそのまま送る", r and r["result"]["isError"] is False
          and received[-1][:2] == ("back/look", "back") and json.loads(received[-1][2]) == counting, str(received[-1]))

    r = c.request(13, "tools/call", {"name": "agent_back_entity", "arguments": {"id": "キャラ-1"}})
    check("200 以外は isError で、字は窓口の断りそのまま", r and r["result"]["isError"] is True and tool_text(r) == sent_texts[-1],
          str(r)[:300])
    check("日本語の引数が UTF-8 で届く", json.loads(received[-1][2]) == {"id": "キャラ-1"}, received[-1][2])

    r = c.request(54, "tools/call", {"name": "agent_back_keep_alive", "arguments": {"on": True}})
    check("agent_back_keep_alive: 裏のトークンで back/keep_alive へ on を送る",
          r and r["result"]["isError"] is False and received[-1][:2] == ("back/keep_alive", "back")
          and json.loads(received[-1][2]) == {"on": True}, "%s %s" % (str(r)[:200], received[-1][:3]))

    # 10-14: 開発のコマンドを打つ道と、一時のカメラで撮る道は、裏のトークンでそのまま写す。
    r = c.request(55, "tools/call", {"name": "agent_back_command", "arguments": {"line": "/time noon"}})
    check("agent_back_command: 裏のトークンで back/command へ line を送る",
          r and r["result"]["isError"] is False and received[-1][:2] == ("back/command", "back")
          and json.loads(received[-1][2]) == {"line": "/time noon"}, "%s %s" % (str(r)[:200], received[-1][:3]))
    r = c.request(56, "tools/call", {"name": "agent_back_shot", "arguments": {"at": "キャラ-1", "dist": 2.5}})
    check("agent_back_shot: 裏のトークンで back/shot へ at と dist を送る",
          r and r["result"]["isError"] is False and received[-1][:2] == ("back/shot", "back")
          and json.loads(received[-1][2]) == {"at": "キャラ-1", "dist": 2.5}, "%s %s" % (str(r)[:200], received[-1][:3]))

    # 10-19: 窓口が頼みを走らせる前に断った時は、橋の中で待って打ち直す。
    # main_thread_busy は窓口が頼みを下げた＝どの道でも打ち直す。answer_late（走った後の遅れ）は読むだけの道だけ。
    def refused_then(name, args, refusals):
        FAKE_REFUSE[:] = refusals
        before = len(received)
        r = c.request(57, "tools/call", {"name": name, "arguments": args})
        FAKE_REFUSE[:] = []
        return r, (json.loads(tool_text(r)) if r else {}), len(received) - before
    r, body, sent = refused_then("agent_look", {}, [("look", 503, "main_thread_busy")] * 2)
    check("agent_look: main_thread_busy を 2 回受けても橋の中で打ち直し、bridgeRetried を足して返す",
          r and r["result"]["isError"] is False and sent == 3 and body.get("ok") is True
          and body.get("bridgeRetried", {}).get("n") == 2 and body["bridgeRetried"]["code"] == "main_thread_busy", "%s %d" % (body, sent))
    r, body, sent = refused_then("agent_back_command", {"line": "/time noon"}, [("back/command", 503, "main_thread_busy")])
    check("agent_back_command: main_thread_busy は打ち直す（窓口が頼みを下げた＝走っていない）",
          r and r["result"]["isError"] is False and sent == 2 and body.get("bridgeRetried", {}).get("code") == "main_thread_busy",
          "%s %d" % (body, sent))
    r, body, sent = refused_then("agent_back_command", {"line": "/time noon"}, [("back/command", 504, "answer_late")])
    check("agent_back_command: answer_late は打ち直さない（頼みは走った＝打ち直すと 2 回走る）",
          r and r["result"]["isError"] is True and sent == 1 and "bridgeRetried" not in body, "%s %d" % (body, sent))
    r, body, sent = refused_then("agent_look", {}, [("look", 504, "answer_late")])
    check("agent_look: answer_late は読むだけの道なので打ち直す",
          r and r["result"]["isError"] is False and sent == 2 and body.get("bridgeRetried", {}).get("code") == "answer_late",
          "%s %d" % (body, sent))
    r, body, sent = refused_then("agent_back_command", {"line": "/time noon"}, [("back/command", 503, "world_not_ready")])
    check("agent_back_command: world_not_ready は打ち直す（走る前の断り）",
          r and r["result"]["isError"] is False and sent == 2 and body.get("bridgeRetried", {}).get("n") == 1, "%s %d" % (body, sent))
    r, body, sent = refused_then("agent_back_shot", {"at": "キャラ-1"}, [("back/shot", 429, "too_soon")])
    check("agent_back_shot: too_soon は打ち直す", r and r["result"]["isError"] is False and sent == 2
          and body.get("bridgeRetried", {}).get("code") == "too_soon", "%s %d" % (body, sent))
    r, body, sent = refused_then("agent_look", {}, [("look", 503, "closing")])
    check("agent_look: 決めた断り（RETRY_LIMIT_SEC）の外は打ち直さない",
          r and r["result"]["isError"] is True and sent == 1 and "bridgeRetried" not in body, "%s %d" % (body, sent))

    # 長い待ち（10-10）: 25 秒より長い頼みは、橋が timeout の cursor を after にして 25 秒ずつ待ち直す。
    before = len(received)
    r = c.request(80, "tools/call", {"name": "agent_back_wait", "arguments": {"until": ["loop"], "timeoutSec": 120}}, timeout=10)
    body = json.loads(tool_text(r)) if r else {}
    sent = [(x[:2], json.loads(x[2])) for x in received[before:]]
    check("agent_back_wait 120 秒: 窓口へ 25 秒ずつ待ち直し、2 回目から前の cursor を after に渡し、hit で返す（gap は足す）",
          r and r["result"]["isError"] is False and len(sent) == 3 and all(s[0] == ("back/wait", "back") for s in sent)
          and [s[1].get("after") for s in sent] == [None, "p1:21", "p1:22"]
          and all(s[1]["timeoutSec"] <= 25 and s[1]["until"] == ["loop"] for s in sent)
          and body.get("outcome") == "hit" and body.get("rounds") == 3 and body.get("gap") == 2
          and isinstance(body.get("waited"), (int, float)), "%s %s" % (str(body)[:300], sent))
    before = len(received)
    r = c.request(81, "tools/call", {"name": "agent_wait", "arguments": {"timeoutSec": 301}})
    check("agent_wait: 300 秒より長い頼みは窓口へ送らずに bad_timeout", r and r["result"]["isError"] is True
          and json.loads(tool_text(r))["error"]["code"] == "bad_timeout" and len(received) == before, str(r)[:300])

    # agent_act の seq（10-10）: 1 つずつ act へ送り、done でない命令で止める（keepGoing なら続ける）。
    before = len(received)
    r = c.request(82, "tools/call", {"name": "agent_act", "arguments": {"seq": [
        {"do": "walk_to", "args": {"to": "guide"}}, {"do": "noeffect", "ref": "s2"}, {"do": "hotbar", "args": {"i": 1}}]}})
    body = json.loads(tool_text(r)) if r else {}
    sent = [json.loads(x[2]) for x in received[before:]]
    check("agent_act seq: 順に act へ by・do・args・ref を送り、no-effect の 2 つ目で止める（stoppedAt 1）",
          r and r["result"]["isError"] is False and [x[0] for x in received[before:]] == ["act", "act"]
          and sent == [{"by": by, "do": "walk_to", "args": {"to": "guide"}}, {"by": by, "do": "noeffect", "ref": "s2"}]
          and body.get("stoppedAt") == 1 and [s["do"] for s in body.get("seq", [])] == ["walk_to", "noeffect"]
          and body["seq"][1]["reply"]["outcome"] == "no-effect", "%s %s" % (str(body)[:300], sent))
    before = len(received)
    r = c.request(83, "tools/call", {"name": "agent_act", "arguments": {"keepGoing": True, "seq": [
        {"do": "noeffect"}, {"do": "jump"}]}})
    body = json.loads(tool_text(r)) if r else {}
    check("agent_act seq keepGoing: done でなくても続け、stoppedAt を付けない",
          r and r["result"]["isError"] is False and len(received) - before == 2 and "stoppedAt" not in body
          and len(body.get("seq", [])) == 2, str(body)[:300])
    # agent_act の seq（10-31）: 途中の done の手は同じ欄を落とす。done でない手と最後の手は窓口の答えそのまま。
    r = c.request(86, "tools/call", {"name": "agent_act", "arguments": {"keepGoing": True, "seq": [
        {"do": "jump"}, {"do": "noeffect"}, {"do": "jump"}]}})
    body = json.loads(tool_text(r)) if r else {}
    steps = [s["reply"] for s in body.get("seq", [])]
    check("agent_act seq: 途中の done の手だけ短くし、done でない手と最後の手は窓口の答えそのまま",
          len(steps) == 3 and "act" not in steps[0] and "api" not in steps[0] and "ok" not in steps[0]
          and steps[0]["outcome"] == "done" and "echo" in steps[0]
          and steps[1].get("act") == "a1" and steps[1]["outcome"] == "no-effect"
          and steps[2].get("act") == "a1" and steps[2].get("api") == "1.0", str(body)[:400])
    before = len(received)
    r = c.request(84, "tools/call", {"name": "agent_act", "arguments": {"do": "jump", "seq": [{"do": "jump"}]}})
    r2 = c.request(85, "tools/call", {"name": "agent_act", "arguments": {"seq": [{"do": "lease"}]}})
    check("agent_act seq: do と seq の両方・seq の中の lease は窓口へ送らずに断る",
          r and r2 and json.loads(tool_text(r))["error"]["code"] == "bad_do"
          and json.loads(tool_text(r2))["error"]["code"] == "bad_seq" and len(received) == before,
          "%s %s" % (str(r)[:200], str(r2)[:200]))
    before = len(received)
    r = c.request(95, "tools/call", {"name": "agent_act", "arguments": {"seq": [
        {"do": "jump"}, {"do": "walk_to", "args": {"to": {"id": "npc-7"}}}, {"do": "walk_to", "args": {"to": "guide"}}]}})
    check("agent_act seq: 裏の行き先の手だけ裏のトークン・ほかは表のトークン",
          r and r["result"]["isError"] is False
          and [x[:2] for x in received[before:]] == [("act", "surface"), ("act", "back"), ("act", "surface")],
          str(received[before:])[:300])

    r = c.request(14, "tools/call", {"name": "agent_nothing", "arguments": {}})
    check("知らない道具は -32602", r and r.get("error", {}).get("code") == -32602, str(r))
    r = c.request(15, "tools/call", {"name": "agent_hello", "arguments": [1, 2]})
    check("arguments が object でなければ -32602", r and r.get("error", {}).get("code") == -32602, str(r))
    r = c.request(16, "foo/bar")
    check("知らない method は -32601", r and r.get("error", {}).get("code") == -32601, str(r))
    c.send_raw(b"{not json\n")
    r = c.wait_for(lambda m: m.get("error", {}).get("code") == -32700, 3)
    check("読めない行は -32700（id null）", r is not None and r.get("id") is None, str(r))
    c.send({"jsonrpc": "1.0", "id": 18, "method": "ping"})
    r = c.wait_for(lambda m: m.get("id") == 18, 3)
    check("jsonrpc が 2.0 でなければ -32600", r and r.get("error", {}).get("code") == -32600, str(r))
    r = c.request(19, "resources/list")
    check("resources/list は空", r and r.get("result") == {"resources": []}, str(r))

    # 取り消し: 取り消した頼みには答えない
    c.send({"jsonrpc": "2.0", "id": 20, "method": "tools/call",
            "params": {"name": "agent_wait", "arguments": {"timeoutSec": 2}}})
    time.sleep(0.2)
    c.send({"jsonrpc": "2.0", "method": "notifications/cancelled", "params": {"requestId": 20, "reason": "test"}})
    r = c.wait_for(lambda m: m.get("id") == 20, 5)
    check("取り消した頼みには答えない", r is None, str(r))
    r = c.request(21, "ping")
    check("取り消しの後も答える", r == {"jsonrpc": "2.0", "id": 21, "result": {}}, str(r))

    # 取り消し: agent_act の命令を取り消したら、橋が手足を返して（lease release）ゲームの命令を止める
    released_before = sum(1 for x in received if x[0] == "lease" and json.loads(x[2]).get("release") is True)
    c.send({"jsonrpc": "2.0", "id": 22, "method": "tools/call",
            "params": {"name": "agent_act", "arguments": {"do": "slow"}}})
    time.sleep(0.5)
    c.send({"jsonrpc": "2.0", "method": "notifications/cancelled", "params": {"requestId": 22, "reason": "test"}})
    deadline = time.time() + 5
    released = []
    while time.time() < deadline:
        released = [json.loads(x[2]) for x in received if x[0] == "lease" and json.loads(x[2]).get("release") is True]
        if len(released) > released_before:
            break
        time.sleep(0.1)
    r = c.wait_for(lambda m: m.get("id") == 22, 4)
    check("agent_act を取り消すと、橋が同じ by で手足を返す（答えは送らない）",
          len(released) > released_before and released[-1].get("by") == by and r is None, "%s %s" % (released, r))

    # 裏のトークンが無いつなぎ先（製品版の形）: list_changed を送り、裏の道具を断る
    write_endpoint(proj_a, port, back=False, mtime=now - 50)
    r = c.request(30, "tools/call", {"name": "agent_back_look", "arguments": {}})
    note = c.wait_for(lambda m: m.get("method") == "notifications/tools/list_changed", 2)
    check("裏が消えたら list_changed を送る", note is not None, str(note))
    check("裏の道具は no_back_token で断る", r and r["result"]["isError"] is True
          and json.loads(tool_text(r))["error"]["code"] == "no_back_token", str(r)[:300])
    before = len(received)
    r = c.request(33, "tools/call", {"name": "agent_capture", "arguments": {"hideDebugOverlays": True}})
    check("agent_capture の帯消しも、裏が無ければ窓口へ送らずに no_back_token", r and r["result"]["isError"] is True
          and json.loads(tool_text(r))["error"]["code"] == "no_back_token" and len(received) == before, str(r)[:300])
    r = c.request(38, "tools/call", {"name": "agent_act", "arguments": {"do": "walk_to", "args": {"to": {"pos": [1, 0, 2]}}}})
    check("agent_act の裏の行き先も、裏が無ければ窓口へ送らずに no_back_token", r and r["result"]["isError"] is True
          and json.loads(tool_text(r))["error"]["code"] == "no_back_token" and len(received) == before, str(r)[:300])
    r = c.request(31, "tools/list")
    names = [t["name"] for t in (r or {}).get("result", {}).get("tools", [])]
    check("tools/list: 裏が無ければ 7 本", names == ["agent_hello", "agent_look", "agent_events", "agent_wait", "agent_reflex", "agent_act",
                                                "agent_capture"], str(names))
    write_endpoint(proj_a, port, back=True, mtime=now - 40)
    r = c.request(32, "tools/call", {"name": "agent_hello", "arguments": {}})
    note = c.wait_for(lambda m: m.get("method") == "notifications/tools/list_changed", 2)
    check("裏が戻ったら、また list_changed を送る", note is not None and r and r["result"]["isError"] is False, str(note))

    # 入力が閉じたら、待ちの途中でもすぐ終わる
    c.send({"jsonrpc": "2.0", "id": 40, "method": "tools/call",
            "params": {"name": "agent_wait", "arguments": {"timeoutSec": 3}}})
    time.sleep(0.2)
    t0 = time.time()
    code = c.close()
    check("入力が閉じたら 0 で終わる（待ちの途中でも）", code == 0 and time.time() - t0 < 2, "code=%s %.1fs" % (code, time.time() - t0))
    stderr_text = "".join(c.err)
    all_out = "".join(c.raw_out)
    check("トークンが字に出ない（標準出力・標準エラー）", SURFACE not in all_out + stderr_text and BACK not in all_out + stderr_text)
    check("標準エラーは起動の 1 行だけ", len(c.err) == 1 and "started v" in c.err[0], stderr_text[:300])
