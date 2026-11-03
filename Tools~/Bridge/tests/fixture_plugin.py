"""試しの差し込み: ログ読み（handler "log"）の置き場と形を決める（hooks.py の log_layout の見本）。

- ログは <プロジェクト>/Logs/Live/<Play を始めた日時>[_<続きの番号>].log。
- 待ちを止める赤の行＝行の頭の印が [E] の行（字の Exception は印にしない＝黄の行にも出る）。
入口に --plugin-path <この場所> --plugin fixture_plugin:LiveLogPlugin を足して使う。
"""

from automata_bridge.logs import LogLayout


class LiveLogPlugin:
    name = "livelog"

    def log_layout(self):
        return LogLayout(folder=("Logs", "Live"), name=r"^(\d{8}_\d{6})(?:_(\d+))?\.log$",
                         error_needle=b" [E] ", error_line=rb"^\d\d:\d\d:\d\d\.\d{3} \[E\] ")
