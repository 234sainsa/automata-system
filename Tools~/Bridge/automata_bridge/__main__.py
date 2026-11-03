"""汎用の入口（どのゲームでも使える形）。

    python -m automata_bridge --project <Unity のプロジェクト> --tools <tools.json>

- 橋は MCP の使う側（Claude Code など）が標準入出力で起こす。橋は <プロジェクト>/Library/AgentApi/<つなぎ先のファイル> を
  呼ばれるたびに読み直し、そこに書かれた 127.0.0.1 の口とトークンで、ゲームの窓口へ送る。
- 道具の表（tools.json）の形は tools.py の頭に書いた。差し込み（プラグイン）の口の一覧は hooks.py の頭に書いた。
- 差し込みは --plugin "モジュール:名前" で足す（名前が呼べる物なら、引数なしで呼んだ答えを差し込みにする）。
  モジュールを探す場所は --plugin-path で足せる（何度でも）。
- ⚠ 標準出力は MCP の通り道。橋の独り言は標準エラーだけに書く（トークンは書かない）。
"""

import argparse
import importlib
import os
import sys

from . import app, tools, wire

# つなぎ先のファイルの既定の名前（窓口の AgentEndpointFile.DefaultFileName と同じ）。
DEFAULT_ENDPOINT_FILE = "endpoint.json"
# 独り言の頭の字の既定。
DEFAULT_LOG_PREFIX = "[agent-bridge] "
# 手足を借りる名前の頭の既定（後ろに 8 字の 16 進が付く）。
DEFAULT_BY_PREFIX = "agent-"


def load_plugin(spec):
    """"モジュール:名前" から差し込みを 1 つ作る。名前が呼べる物（クラスや関数）なら、引数なしで呼んだ答えを使う。
    ⚠ 読めない時は例外のまま（起動の時に止める＝黙って差し込み無しで動かない）。"""
    module_name, sep, attr = spec.partition(":")
    if not sep or not module_name or not attr:
        raise ValueError("--plugin は \"module:attribute\" の形で書く: %r" % spec)
    target = getattr(importlib.import_module(module_name), attr)
    return target() if callable(target) else target


def parse_args(argv):
    ap = argparse.ArgumentParser(
        prog="automata_bridge",
        description="MCP bridge (stdio) for the Balloon Game Agent API. "
                    "Reads <project>/Library/AgentApi/<endpoint file> and forwards tool calls to the game.")
    ap.add_argument("--project", action="append", default=[],
                    help="Unity project root to look for the endpoint file in (repeatable). Default: the current folder.")
    ap.add_argument("--tools", required=True,
                    help="Path to the tool table (tools.json).")
    ap.add_argument("--profile", default="main",
                    help="Which profile of the tool table to serve (default: main).")
    ap.add_argument("--endpoint-file", default=DEFAULT_ENDPOINT_FILE,
                    help="File name under <project>/Library/AgentApi/ (default: endpoint.json).")
    ap.add_argument("--plugin", action="append", default=[],
                    help="Plugin to load, as module:attribute (repeatable). See hooks.py for the hook names.")
    ap.add_argument("--plugin-path", action="append", default=[],
                    help="Folder to add to the Python import path before loading plugins (repeatable).")
    ap.add_argument("--wait-cap", type=int, default=wire.WAIT_CACHE_SAFE_SEC,
                    help="Longest real wait in seconds for long waits (1-270, default 270).")
    ap.add_argument("--by-prefix", default=DEFAULT_BY_PREFIX,
                    help="Prefix of the name the bridge uses to borrow the hands (default: agent-).")
    ap.add_argument("--log-prefix", default=DEFAULT_LOG_PREFIX,
                    help="Prefix of the bridge's own log lines on stderr.")
    return ap.parse_args(argv)


def build(args, out):
    """引数から橋を 1 本組み立てる（まだ起こさない）。
    ⚠ 表と処理が食い違えば ValueError（起動の時に止める）。"""
    table = tools.read_table(args.tools)
    shape = table["profiles"].get(args.profile)
    if not isinstance(shape, dict) or not isinstance(shape.get("tools"), list):
        raise ValueError("道具の表に profile %r が無いか、tools が無い: %s" % (args.profile, args.tools))
    for folder in args.plugin_path:
        sys.path.insert(0, os.path.abspath(folder))
    plugins = [load_plugin(spec) for spec in args.plugin]
    projects = [os.path.abspath(p) for p in (args.project or [os.getcwd()])]
    return app.Bridge(projects, out, args.endpoint_file, tools.tool_rows(shape["tools"]),
                      tools.joined(shape.get("instructions", "")), table["server"], plugins=plugins,
                      wait_cap=wire.clamp_wait_cap(args.wait_cap), by_prefix=args.by_prefix, profile=args.profile)


def main(argv=None):
    wire.setup_stderr()
    args = parse_args(argv)
    wire.set_log_prefix(args.log_prefix)
    bridge = build(args, sys.stdout.buffer)
    # ⭐ 起動の独り言は 1 行だけ。
    bridge.log_started()
    return bridge.serve(sys.stdin.buffer)


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        sys.exit(0)
