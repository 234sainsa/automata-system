"""橋の自己試験 2: ログ読み（handler "log"）と、長い待ちの上限（--wait-cap）。

ログの置き場と形は試しの差し込み（fixture_plugin.py の log_layout）で決める。run_tests.py から呼ぶ。
"""

import json
import os
import threading
import time

from bridge_harness import HERE, Client, check, received, tool_text, write_endpoint

# 入口に足す引数: ログの置き場を決める試しの差し込み。
LOG_PLUGIN = ["--plugin-path", HERE, "--plugin", "fixture_plugin:LiveLogPlugin"]


def run(root, port):
    proj_a = os.path.join(root, "A")
    write_endpoint(proj_a, port)
    c = Client([proj_a], extra=LOG_PLUGIN)
    live = os.path.join(proj_a, "Logs", "Live")
    os.makedirs(live)
    with open(os.path.join(live, "20261010_090000.log"), "wb") as f:
        f.write("09:00:00.000 [ ] [Faint] 前の Play\n".encode("utf-8"))
    with open(os.path.join(live, "20261010_100000.log"), "wb") as f:
        f.write("10:00:00.000 [ ] [Faint] A は気を失った\n10:00:01.000 [ ] [Noise] x\n"
                "10:00:02.000 [ ] [Shelf] 剣を置いた\n".encode("utf-8"))
    # 続きのファイル（64MB を越えた後）。終わりの行は書きかけ（改行が無い）＝読まない。
    with open(os.path.join(live, "20261010_100000_1.log"), "wb") as f:
        f.write("10:00:03.000 [ ] [Faint] B は気を失った\n10:00:04.000 [ ] [Faint] 書きかけ".encode("utf-8"))
    before = len(received)
    r = c.request(86, "tools/call", {"name": "agent_back_log", "arguments": {"tags": ["[Faint]", "[Shelf]"]}})
    body = json.loads(tool_text(r)) if r else {}
    end = len("10:00:03.000 [ ] [Faint] B は気を失った\n".encode("utf-8"))
    check("agent_back_log: しおり無しは今の Play（同じ日時の頭のファイル）の終わりの行と、書きかけの行の頭のしおり",
          r and r["result"]["isError"] is False and len(received) == before
          and body.get("lines") == ["10:00:00.000 [ ] [Faint] A は気を失った", "10:00:02.000 [ ] [Shelf] 剣を置いた",
                                    "10:00:03.000 [ ] [Faint] B は気を失った"]
          and body.get("cursor") == "20261010_100000_1.log@%d" % end, str(body)[:400])
    third = len("10:00:00.000 [ ] [Faint] A は気を失った\n10:00:01.000 [ ] [Noise] x\n".encode("utf-8"))
    r = c.request(101, "tools/call", {"name": "agent_back_log", "arguments": {"tags": ["[Faint]", "[Shelf]"], "ids": True}})
    body = json.loads(tool_text(r)) if r else {}
    check("agent_back_log: ids:true で、行ごとの証拠の ID（log:<ファイル>@<行の頭のバイトの位置>）",
          body.get("ids") == ["log:20261010_100000.log@0", "log:20261010_100000.log@%d" % third, "log:20261010_100000_1.log@0"],
          str(body.get("ids"))[:300])
    r = c.request(102, "tools/call", {"name": "agent_back_log", "arguments": {"tags": ["[Faint]"], "ids": "yes"}})
    check("agent_back_log: ids が真偽でなければ bad_args", r and r["result"]["isError"] is True
          and json.loads(tool_text(r))["error"]["code"] == "bad_args", str(r)[:300])
    # 11-02: 長い待ちは上限（本物は 270 秒＝係のキャッシュの 5 分より前）で返し、capped を付ける。試しは上限を 2 秒にした橋で見る。
    cw = Client([proj_a], extra=LOG_PLUGIN + ["--wait-cap", "2"])
    t0 = time.monotonic()
    r = cw.request(1, "tools/call", {"name": "agent_back_log", "arguments": {"tags": ["[NeverWritten]"], "waitSec": 20}}, timeout=15)
    took = time.monotonic() - t0
    body = json.loads(tool_text(r)) if r else {}
    check("agent_back_log waitSec: 上限を越える待ちは上限で返し、capped（asked・upTo）を付ける",
          r and r["result"]["isError"] is False and body.get("lines") == [] and took < 10
          and body.get("capped", {}).get("asked") == 20 and body.get("capped", {}).get("upTo") == 2, "%.1f %s" % (took, str(body)[:300]))
    r = cw.request(2, "tools/call", {"name": "agent_back_log", "arguments": {"tags": ["[NeverWritten]"], "waitSec": 1}}, timeout=10)
    check("agent_back_log waitSec: 上限より短い待ちには capped を付けない", r and "capped" not in json.loads(tool_text(r)), str(r)[:300])
    t0 = time.monotonic()
    r = cw.request(3, "tools/call", {"name": "agent_wait", "arguments": {"until": ["never.happens"], "timeoutSec": 40}}, timeout=20)
    took = time.monotonic() - t0
    body = json.loads(tool_text(r)) if r else {}
    check("agent_wait 40 秒: 上限で返し、timeout に capped を付ける",
          r and body.get("outcome") == "timeout" and took < 15 and body.get("capped", {}).get("upTo") == 2,
          "%.1f %s" % (took, str(body)[:300]))
    cw.close()
    r = c.request(87, "tools/call", {"name": "agent_back_log", "arguments": {
        "tags": ["[Faint]"], "after": "20261010_100000.log@0", "max": 1}})
    first = json.loads(tool_text(r)) if r else {}
    r = c.request(88, "tools/call", {"name": "agent_back_log", "arguments": {"tags": ["[Faint]"], "after": first.get("cursor", "")}})
    second = json.loads(tool_text(r)) if r else {}
    check("agent_back_log: after の続きを max 行まで返し、more としおりで続きを読める（次のファイルへも移る）",
          first.get("lines") == ["10:00:00.000 [ ] [Faint] A は気を失った"] and first.get("more") is True
          and second.get("lines") == ["10:00:03.000 [ ] [Faint] B は気を失った"] and second.get("more") is False
          and second.get("cursor") == "20261010_100000_1.log@%d" % end, "%s %s" % (str(first)[:300], str(second)[:300]))
    r = c.request(89, "tools/call", {"name": "agent_back_log", "arguments": {"tags": ["[Faint]"], "has": "B は"}})
    check("agent_back_log: has で、その字も含む行だけ", r and json.loads(tool_text(r)).get("lines") ==
          ["10:00:03.000 [ ] [Faint] B は気を失った"], str(r)[:300])

    def finish_line():
        time.sleep(1.0)
        with open(os.path.join(live, "20261010_100000_1.log"), "ab") as f:
            f.write("のまま起きた\n".encode("utf-8"))

    threading.Thread(target=finish_line, daemon=True).start()
    r = c.request(90, "tools/call", {"name": "agent_back_log", "arguments": {
        "tags": ["[Faint]"], "after": second.get("cursor", ""), "waitSec": 5}}, timeout=10)
    body = json.loads(tool_text(r)) if r else {}
    check("agent_back_log waitSec: 書きかけの行が書き終わるまで待ち、その行を 1 本で返す",
          body.get("lines") == ["10:00:04.000 [ ] [Faint] 書きかけのまま起きた"] and body.get("waited", 0) >= 0.5,
          str(body)[:300])
    r = c.request(91, "tools/call", {"name": "agent_back_log", "arguments": {"tags": ["[Faint]"], "after": "nope.log@0"}})
    check("agent_back_log: 知らないしおりは bad_cursor", r and r["result"]["isError"] is True
          and json.loads(tool_text(r))["error"]["code"] == "bad_cursor", str(r)[:300])

    # agent_back_log（10-26）: 待つ間に赤の行（頭の印が [E]）が出たら、頼んだ印に関係なく止める。黄の行の Exception では止めない。
    def write_error_line():
        time.sleep(1.0)
        with open(os.path.join(live, "20261010_100000_1.log"), "ab") as f:
            f.write("10:00:05.000 [W] [Relay] TaskCanceledException\n10:00:06.000 [E] 道を探す糸が止まらない\n".encode("utf-8"))

    threading.Thread(target=write_error_line, daemon=True).start()
    r = c.request(96, "tools/call", {"name": "agent_back_log", "arguments": {"tags": ["[Faint]"], "waitSec": 8}}, timeout=12)
    body = json.loads(tool_text(r)) if r else {}
    stop = body.get("stop") or {}
    check("agent_back_log waitSec: 赤の行で止まり、stop に why error_line とその行を返す（黄の行の Exception では止めない）",
          body.get("lines") == [] and stop.get("why") == "error_line"
          and stop.get("lines") == ["10:00:06.000 [E] 道を探す糸が止まらない"] and body.get("waited", 99) < 5,
          str(body)[:400])

    # 新しい Play のログができたら止める（前の Play の印を上限まで待たない）。
    def start_new_play():
        time.sleep(1.0)
        with open(os.path.join(live, "20261010_110000.log"), "wb") as f:
            f.write("11:00:00.000 [ ] [Noise] 新しい Play\n".encode("utf-8"))

    threading.Thread(target=start_new_play, daemon=True).start()
    r = c.request(97, "tools/call", {"name": "agent_back_log", "arguments": {"tags": ["[Faint]"], "waitSec": 8}}, timeout=12)
    body = json.loads(tool_text(r)) if r else {}
    check("agent_back_log waitSec: 新しい Play のログができたら、stop の why new_play で止める",
          (body.get("stop") or {}).get("why") == "new_play" and body.get("waited", 99) < 5, str(body)[:300])

    # 1 回の答えの字は 12000 字まで＝越える行は more と、その行の頭のしおりで次へ回す。
    long_text = "x" * 900
    with open(os.path.join(live, "20261010_110000.log"), "ab") as f:
        f.write("".join("11:00:%02d.000 [ ] [Long] %s\n" % (k, long_text) for k in range(20)).encode("utf-8"))
    r = c.request(98, "tools/call", {"name": "agent_back_log", "arguments": {"tags": ["[Long]"], "after": "20261010_110000.log@0"}})
    first = json.loads(tool_text(r)) if r else {}
    r = c.request(99, "tools/call", {"name": "agent_back_log", "arguments": {"tags": ["[Long]"], "after": first.get("cursor", "")}})
    second = json.loads(tool_text(r)) if r else {}
    r = c.request(100, "tools/call", {"name": "agent_back_log", "arguments": {"tags": ["[Long]"], "last": 20}})
    tail = json.loads(tool_text(r)) if r else {}
    got = first.get("lines", []) + second.get("lines", [])
    check("agent_back_log: 字の上限で切った続きは more としおりで読める・last は新しい方を残して cut に略した数",
          first.get("more") is True and sum(len(t) for t in first.get("lines", [])) <= 12000
          and len(got) == 20 and got[-1].startswith("11:00:19.000")
          and tail.get("cut", 0) > 0 and len(tail.get("lines", [])) + tail["cut"] == 20
          and tail["lines"][-1].startswith("11:00:19.000"),
          "%s / %s / %s" % (str(first)[:200], str(second)[:200], str(tail)[:200]))
    c.close()
