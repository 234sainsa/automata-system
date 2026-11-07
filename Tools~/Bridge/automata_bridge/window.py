"""窓口（ゲームの中の HTTP の口）へ送る部品と、つなぎ先を探す係 Link。

- post は窓口へ 1 本送るだけ（状態を持たない）。
- Link はつなぎ先を探すプロジェクトの並び（projects）とつなぎ先のファイルの名前の持ち主。
  つなぎ先の絞り方・付けるセッションの ID・Play に入ったばかりかは、ゲームの差し込み（hooks）に聞く（無ければ既定）。
"""

import http.client
import json
import os
import time

from . import wire

# 送った AI のセッションの ID を載せる頭（差し込みの session_for が ID を返した時だけ付ける。
# ゲームの頼みの確かめ（IAgentRequestGuard）が読む）。
SESSION_HEADER = "X-Claude-Session"

# 窓口が頼みを走らせる前に断った時、橋の中で待って打ち直す断りと、待つ上限の秒・読み直す間（2026-10-19）。
# 訳＝確かめ係は、断りを読むと次の手で打ち直す＝そのたびに文脈（約 20 万）を全部読み直す。
# world_not_ready（世界を作っている最中）・too_soon（撮るのが速すぎる）・main_thread_busy（主スレッドが受け取る前に、
# 窓口が頼みを列から下げた）は、どの道でも頼みは走っていない。
RETRY_LIMIT_SEC = {"world_not_ready": 60, "main_thread_busy": 10, "too_soon": 3, "answer_late": 10}
RETRY_POLL_SEC = {"world_not_ready": 1.0, "main_thread_busy": 0.5, "too_soon": 0.6, "answer_late": 0.5}
# 断りの番号（answer_late だけ 504）。
RETRY_STATUSES = (429, 503, 504)
# ⚠ answer_late（504）は、主スレッドが受け取った後に答えが遅れた＝頼みは走った（まだ走っている）。
#   打ち直すと 2 回走るので、読むだけの道でだけ打ち直す。
LATE_RETRY_ROUTES = ("hello", "look", "events", "wait", "back/look", "back/entity", "back/events", "back/wait")
# 橋が窓口へ何も送らない間（ss_back_log がログのファイルを読んで待つ間など）に、hello を 1 本送って
# 「使う側はまだいる」と知らせる間の秒。訳＝窓口は、頼みが 60 秒来ないと合図の元を外し、
# Editor の録画を閉じる。ログを読む道具は窓口へ行かないので、長く待つと録画が途中で切れていた。
TOUCH_EVERY_SEC = 20
TOUCH_TIMEOUT_SEC = 3
# Play に入ったばかり（ゲームの差し込みが play_starting で言う）なら、つなぎ先が無くても窓口が開くのを待つ秒。
PLAY_START_WAIT_SEC = 60
# 窓口が「このロックを持っていない」と断る時の code（ゲームの頼みの確かめ IAgentRequestGuard が返す断り）。
# この断りを受けたら、橋は次のつなぎ先を試す（汎用の窓口は、この断りを出さない）。
LOCK_REFUSAL_CODES = ("not_lock_holder", "editor_held_by_user")


def post(endpoint, route, body_text, token, timeout, headers_extra=None, method="POST", host_header=None, session=None):
    """窓口へ 1 本送る。(番号, 本文の字) を返す。つなげなければ (None, 理由)。
    session＝付けるセッションの ID（None なら付けない）。"""
    port = int(endpoint["port"])
    conn = http.client.HTTPConnection("127.0.0.1", port, timeout=timeout)
    headers = {"Content-Type": "application/json"}
    if token is not None:
        headers["Authorization"] = "Bearer " + token
    if session:
        headers[SESSION_HEADER] = session
    if headers_extra:
        headers.update(headers_extra)
    try:
        # skip_host=True にして、Host の頭をこちらで決める（門の確かめで、違う Host を送るため）。
        conn.putrequest(method, "/v1/" + route, skip_host=True, skip_accept_encoding=True)
        conn.putheader("Host", host_header or ("127.0.0.1:%d" % port))
        data = body_text.encode("utf-8") if body_text is not None else b""
        for k, v in headers.items():
            conn.putheader(k, v)
        conn.putheader("Content-Length", str(len(data)))
        conn.endheaders(data)
        resp = conn.getresponse()
        return resp.status, resp.read().decode("utf-8", errors="replace")
    except OSError as e:
        return None, "%s: %s" % (type(e).__name__, e)
    finally:
        conn.close()


def is_timeout(reason):
    """post の「つなげなかった理由」が、時間切れか（窓口は受けたが、答えが来なかった）。"""
    return reason.startswith(("TimeoutError", "timeout"))


def lock_refusal(text):
    """窓口の断りが「このロックを持っていない」か。"""
    try:
        code = (json.loads(text).get("error") or {}).get("code")
    except (ValueError, AttributeError):
        return False
    return code in LOCK_REFUSAL_CODES


def project_of(endpoint_path):
    """つなぎ先の道（<プロジェクト>/Library/AgentApi/<ファイル>）から、プロジェクトの根を返す。"""
    return os.path.dirname(os.path.dirname(os.path.dirname(endpoint_path)))


def endpoint_path(project, file_name):
    return os.path.join(project, "Library", "AgentApi", file_name)


class Link:
    """つなぎ先を探して送る係。持ち物は projects（探すプロジェクトの並び）と endpoint_file（つなぎ先のファイルの名前）。

    差し込み（hooks）に聞くこと（どれも無ければ既定）:
    - narrow_projects(projects) → (探す並び, 印)。印は settle_found(印, found) に渡る（つなぎ先を決めた訳を残すため）。
    - session_for(path) → そのつなぎ先へ付けるセッションの ID（既定 None＝付けない）。
    - play_starting() → Play に入ったばかりか（既定 False）・play_wait_failed() → 待っても開かなかった時に呼ぶ。"""

    def __init__(self, projects, endpoint_file, hooks, is_cancelled):
        self.projects = list(projects)
        self.endpoint_file = endpoint_file
        self.hooks = hooks
        self.is_cancelled = is_cancelled
        # 最後に窓口が頼みを受けた時刻（time.monotonic・touch が見る）。
        self.last_sent_at = 0.0

    def touch(self, found):
        """前に窓口へ送ってから TOUCH_EVERY_SEC 秒たっていれば、hello を 1 本送る（答えは見ない）。
        hello は世界を読まない道＝世界も、裏を読んだ数も変えない。"""
        if not found or time.monotonic() - self.last_sent_at < TOUCH_EVERY_SEC:
            return
        self.last_sent_at = time.monotonic()
        self.post_first(found, "hello", {}, "surface", TOUCH_TIMEOUT_SEC)

    def endpoints(self):
        """読めたつなぎ先を新しい順に返す。((中身, 道) の並び, 見た道の並び)。"""
        projects = self.projects
        mark = None
        narrow = self.hooks.first("narrow_projects")
        if narrow is not None:
            projects, mark = narrow(self.projects)
        found, checked = [], []
        for project in projects:
            path = endpoint_path(project, self.endpoint_file)
            checked.append(path)
            try:
                mtime = os.path.getmtime(path)
                with open(path, encoding="utf-8") as f:
                    data = json.load(f)
            except (OSError, ValueError):
                # 無い・書きかけ・壊れている時は、そのプロジェクトを飛ばす。
                continue
            if isinstance(data, dict) and "port" in data and "surfaceToken" in data:
                found.append((mtime, data, path))
        found.sort(key=lambda item: item[0], reverse=True)
        found = [(data, path) for _, data, path in found]
        settle = self.hooks.first("settle_found")
        if narrow is not None and settle is not None:
            settle(mark, found)
        return found, checked

    def back_available(self):
        found, _ = self.endpoints()
        return not found or bool(found[0][0].get("backToken"))

    def session_for(self, path):
        hook = self.hooks.first("session_for")
        return hook(path) if hook is not None else None

    def post_first(self, found, route, args, face, timeout):
        """新しいつなぎ先から順に送る。(番号, 字) を返す。つなげなかった時は番号が None で、字は断りの JSON。"""
        body = json.dumps(args, ensure_ascii=False)
        failure = None
        refused = None
        for endpoint, path in found:
            token = endpoint.get("backToken") if face == "back" else endpoint.get("surfaceToken")
            if not token:
                failure = {"ok": False, "error": {"code": "no_back_token", "hint": "this_build_has_no_back_face"}}
                break
            status, text = post(endpoint, route, body, token, timeout, session=self.session_for(path))
            if status is not None:
                self.last_sent_at = time.monotonic()
            if status == 403 and lock_refusal(text):
                # その Editor のロックは別のセッションかユーザーの物＝窓口が断った。次のつなぎ先を試す（無ければこの断りを返す）。
                refused = (status, text)
                continue
            if status is not None:
                return status, text
            if is_timeout(text):
                # 窓口は受けたが、答えが来なかった。別のつなぎ先へ送り直すと、また同じだけ待たせるので、ここで断る。
                failure = {"ok": False, "error": {"code": "timeout", "hint": "no_answer_in_%ds" % timeout}}
                break
            # つなげなかった（古いつなぎ先のファイルが残っていた時など）。次に新しいつなぎ先を試す。
            failure = {"ok": False, "error": {"code": "connect", "hint": text}}
        if refused is not None and failure is None:
            return refused
        return None, json.dumps(failure, ensure_ascii=False)

    def post_retry(self, request_id, found, route, args, face, timeout):
        """post_first と同じ。ただし、窓口が頼みを走らせる前に断った時（RETRY_LIMIT_SEC の断り）は、橋の中で待って打ち直す。
        answer_late は、読むだけの道（LATE_RETRY_ROUTES）でだけ打ち直す。使う側が取り消したら、次を打たない。
        打ち直した時は、答えの JSON に bridgeRetried（code＝最後の断り・n＝打ち直した数・waitedSec）を足す。
        ⭐ 上限の秒は、最初の断りが来た時から数える（送る前から数えると、長い wait の後の断りを 1 回も打ち直さない）。"""
        start = time.monotonic()
        refused_at = None
        tries, last_code = 0, None
        while True:
            status, text = self.post_first(found, route, args, face, timeout)
            code = wire.error_code(wire.json_or_empty(text)) if status in RETRY_STATUSES else None
            if code == "answer_late" and route not in LATE_RETRY_ROUTES:
                code = None
            if code not in RETRY_LIMIT_SEC:
                break
            if refused_at is None:
                refused_at = time.monotonic()
            poll = RETRY_POLL_SEC[code]
            if time.monotonic() - refused_at + poll > RETRY_LIMIT_SEC[code]:
                break
            if self.is_cancelled(request_id):
                break
            time.sleep(poll)
            found, _ = self.endpoints()
            if not found:
                break
            tries, last_code = tries + 1, code
        if tries:
            waited = round(time.monotonic() - start, 1)
            reply = wire.json_or_empty(text)
            if reply:
                reply["bridgeRetried"] = {"code": last_code, "n": tries, "waitedSec": waited}
                text = json.dumps(reply, ensure_ascii=False)
        return status, text

    def play_starting(self):
        hook = self.hooks.first("play_starting")
        return bool(hook()) if hook is not None else False

    def wait_endpoints(self, request_id):
        """Play に入ったばかりの時、窓口が開くまで PLAY_START_WAIT_SEC 秒まで待つ。endpoints() と同じ形を返す。
        開かなければ、差し込みの play_wait_failed を呼ぶ（同じ Play で 2 度待たないため）。"""
        start = time.monotonic()
        found, checked = self.endpoints()
        while not found and time.monotonic() - start < PLAY_START_WAIT_SEC:
            if self.is_cancelled(request_id):
                return found, checked
            time.sleep(1.0)
            found, checked = self.endpoints()
        if not found:
            wire.log("play started but no endpoint in %ds" % PLAY_START_WAIT_SEC)
            for hook in self.hooks.each("play_wait_failed"):
                hook()
        return found, checked
