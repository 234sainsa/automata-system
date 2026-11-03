"""橋の自己試験 3: つなぎ先の選び方（閉じたつなぎ先を飛ばす・時間切れで送り直さない・つなぎ先が無い）。

run_tests.py から呼ぶ。
"""

import json
import os
import time

from bridge_harness import Client, check, free_port, received, tool_text, write_endpoint

# 試しの道具の表の道具の数（表 7・裏 8）。
TOOL_COUNT = 15


def run(root, port):
    proj_a = os.path.join(root, "A")
    proj_b = os.path.join(root, "B")
    proj_c = os.path.join(root, "C")
    proj_empty = os.path.join(root, "Empty")
    os.makedirs(proj_empty)
    now = time.time()
    # ── 2 本目の橋: 新しい方のつなぎ先が古い（窓口が閉じている）→ 次の新しさのつなぎ先へ ──
    write_endpoint(proj_b, free_port(), mtime=now)
    write_endpoint(proj_a, port, mtime=now - 30)
    c2 = Client([proj_a, proj_b])
    r = c2.request(1, "tools/call", {"name": "agent_hello", "arguments": {}})
    check("閉じたつなぎ先を飛ばして、次を使う", r and r["result"]["isError"] is False and '"stage":2' in tool_text(r), str(r)[:300])
    c2.close()

    # 時間切れの時は、別のつなぎ先へ送り直さない（A と C は同じ窓口・B は閉じている）
    write_endpoint(proj_c, port, mtime=now - 60)
    c3 = Client([proj_a, proj_b, proj_c])
    before = sum(1 for x in received if x[0] == "wait" and json.loads(x[2]).get("until") == ["hang"])
    t0 = time.time()
    r = c3.request(2, "tools/call", {"name": "agent_wait", "arguments": {"until": ["hang"], "timeoutSec": 0}}, timeout=20)
    spent = time.time() - t0
    after = sum(1 for x in received if x[0] == "wait" and json.loads(x[2]).get("until") == ["hang"])
    check("時間切れは timeout で断り、送り直さない", r and json.loads(tool_text(r))["error"]["code"] == "timeout"
          and after - before == 1 and 9 < spent < 13, "%s sent=%d %.1fs" % (str(r)[:200], after - before, spent))
    c3.close()

    # ── 3 本目の橋: つなぎ先が無い（Play の前） ──
    c4 = Client([proj_empty])
    r = c4.request(1, "tools/list")
    check("つなぎ先が無い時も %d 本並べる" % TOOL_COUNT,
          len((r or {}).get("result", {}).get("tools", [])) == TOOL_COUNT, str(r)[:200])
    r = c4.request(2, "tools/call", {"name": "agent_hello", "arguments": {}})
    body = json.loads(tool_text(r)) if r else {}
    check("つなぎ先が無ければ no_endpoint と見た道", r and r["result"]["isError"] is True
          and body.get("error", {}).get("code") == "no_endpoint" and body.get("checked"), str(r)[:300])
    c4.close()
