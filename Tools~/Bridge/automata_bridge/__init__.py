"""Game Agent API の MCP の橋（Python の標準だけ・外の SDK なし）。

AI の道具（Claude Code など）が橋を標準入出力で起こす。橋は MCP（JSON-RPC 2.0・1 行に 1 通）を受け、
道具の呼び出しを、ゲームの窓口の POST /v1/… へ写す。道具の答えの字は、窓口の返事そのまま。

- 道具の表と使い方の文は、ゲームが JSON で渡す（tools.py）。
- ゲーム固有の処理は差し込み（プラグイン）で足す（hooks.py に口の一覧）。差し込みが無ければ汎用の既定で動く。
- 使い方: app.Bridge を組み立て、log_started() の後に serve(標準入力) を呼ぶ。
"""

__version__ = "0.1.0"
