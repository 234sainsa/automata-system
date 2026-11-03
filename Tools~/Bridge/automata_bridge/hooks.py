"""ゲームの差し込み（プラグイン）を「あれば呼ぶ」ための小さな係。

差し込みは、ふつうの Python の物（クラスの実体）。橋は名前で口を探し、持っている差し込みだけを呼ぶ。
どの差し込みも口を持っていなければ、橋は汎用の既定で動く。

口の一覧（いつ呼ぶか）は README ではなく、ここに並べる:
- bind(bridge)                         組み立ての終わりに 1 回（橋の部品 link・calls・server を受け取る）
- handlers() → {名前: 関数}            組み立ての時に 1 回（道具の表の handler の名前に処理を足す）
- narrow_projects(projects) → (並び, 印) つなぎ先を探すたび（探すプロジェクトを絞る）
- settle_found(印, found)               つなぎ先を探し終えるたび（決めた訳を残す）
- session_for(path) → ID か None        窓口へ送るたび（頼みに付けるセッションの ID）
- play_starting() → 真偽                 つなぎ先が無い時（Play に入ったばかりなら待つ）
- play_wait_failed()                     待っても窓口が開かなかった時
- prepare_call(name, args) → (args, 断りか None)  道具を走らせる前（橋だけが読む欄を外す・断る）
- call_args(tool, args) → args           route へそのまま写す道具を送る前（欄を足す）
- decorate_reply(params, reply) → reply  道具の答えを送る前（2 つ目の字の塊を足すなど）
- on_answer(request_id, delivered)       道具の答えを送る時（delivered＝使う側に届くか）
- log_layout() → LogLayout               ログ読みの係を作る時に 1 回（ログの置き場と赤の行の形）
- log_ids_default(found) → 真偽           ログ読みで ids を省いた時（行の ID を返すか）
- startup_note() → 字                    起動の独り言の後ろに足す字
- start(stop)                            標準入力を読み始める前（心拍・見張りの口など）
- close()                                標準入力が閉じた後（後始末）
"""


class Hooks:
    def __init__(self, plugins):
        self.plugins = list(plugins)

    def first(self, name):
        """その口を持つ最初の差し込みの口（無ければ None）。"""
        for plugin in self.plugins:
            hook = getattr(plugin, name, None)
            if callable(hook):
                return hook
        return None

    def each(self, name):
        """その口を持つ差し込みの口を、登録の順に全部。"""
        return [hook for hook in (getattr(p, name, None) for p in self.plugins) if callable(hook)]
