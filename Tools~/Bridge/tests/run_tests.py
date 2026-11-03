#!/usr/bin/env python3
"""MCP の橋（automata_bridge）を、Unity 抜きで確かめる台本（約 40 秒・Python の標準だけ）。

    python Tools~/Bridge/tests/run_tests.py

- 作り物の窓口（HTTP・127.0.0.1）と一時の endpoint.json を置き、汎用の入口（../agent_bridge.py）を
  試しの道具の表（fixture_tools.json）で起こして、MCP の頼みを 1 つずつ打つ。
- 結果は 1 行ずつ OK / NG で出す。NG が 1 つでもあれば、終わりの番号は 1。
"""

import os
import shutil
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import bridge_harness  # noqa: E402
import test_calls  # noqa: E402
import test_links  # noqa: E402
import test_logs  # noqa: E402


def main():
    root = tempfile.mkdtemp(prefix="agent_bridge_test_")
    server, port = bridge_harness.start_fake()
    try:
        for part, folder in ((test_calls, "calls"), (test_logs, "logs"), (test_links, "links")):
            part.run(os.path.join(root, folder), port)
    finally:
        server.shutdown()
        shutil.rmtree(root, ignore_errors=True)
    results = bridge_harness.results
    bad = [name for name, ok in results if not ok]
    print("---")
    print("%d checks, NG: %s" % (len(results), ", ".join(bad) if bad else "none"))
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
