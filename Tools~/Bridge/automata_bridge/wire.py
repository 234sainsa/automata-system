"""橋の小さな部品（MCP の答えの包み・窓口の答えの読み方・独り言・待つ秒の決まり）。

どの部品も状態を持たない（独り言の頭の字だけは、ゲームが起動の時に 1 回決める）。
"""

import calendar
import json
import sys
import time

# 橋が話せる MCP の版（新しい順）。使う側が頼んだ版を話せればそれを、話せなければいちばん新しい版を返す。
PROTOCOL_VERSIONS = ("2025-06-18", "2025-03-26", "2024-11-05")

# ふつうの道の答えを待つ秒（窓口は主スレッドを 5 秒まで待つ）。
PLAIN_TIMEOUT_SEC = 15

# wait の答えを待つ秒＝頼んだ待ち（既定 20・最大 25）に足す余り（窓口の 5 秒＋1 秒の余りより長く）。
WAIT_MARGIN_SEC = 10
WAIT_DEFAULT_SEC = 20
WAIT_MAX_SEC = 25
# 長い待ちで頼める最長の秒（25 秒を越える分は、橋が窓口の wait を打ち直してつなぐ）。
WAIT_LONG_MAX_SEC = 300
# 長い待ちで本当に待つ最長の秒の既定（11-02）。Claude Code の係（サブエージェント）のキャッシュは 5 分で切れる
# （親だけ 1 時間）。300 秒待つと、係の次の手が文脈を全部書き直す。
# ∴ 270〜300 を頼まれても 270 で返し、答えに capped を付ける（使う側はもう一度待てばよい）。
# ゲームは Bridge の wait_cap で短くできる（自己試験のため・延ばせない）。
WAIT_CACHE_SAFE_SEC = 270

# 独り言の頭の字（ゲームが set_log_prefix で替える）。
_LOG_PREFIX = ["[agent-bridge] "]


def set_log_prefix(prefix):
    _LOG_PREFIX[0] = prefix


def log(text):
    # ⚠ 標準出力は MCP の通り道。橋の独り言は標準エラーだけに書く（トークンは書かない）。
    sys.stderr.write(_LOG_PREFIX[0] + text + "\n")
    sys.stderr.flush()


def setup_stderr():
    # ⭐ 標準エラーの字は UTF-8 に決める（Windows の既定の cp932 のままだと、日本語の理由を書いた時に落ちる）。
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")


def clamp_wait_cap(value):
    """長い待ちの上限を 1〜270 に収める（270 より長くはできない）。読めない値は既定の 270。"""
    try:
        return max(1, min(WAIT_CACHE_SAFE_SEC, int(value)))
    except (TypeError, ValueError):
        return WAIT_CACHE_SAFE_SEC


def result(request_id, body):
    return {"jsonrpc": "2.0", "id": request_id, "result": body}


def error(request_id, code, message):
    return {"jsonrpc": "2.0", "id": request_id, "error": {"code": code, "message": message}}


def tool_text(obj, is_error):
    return {"content": [{"type": "text", "text": json.dumps(obj, ensure_ascii=False)}], "isError": is_error}


def text_result(status, text):
    """窓口の返事の字を、そのまま道具の答えにする（200 以外は isError）。"""
    return {"content": [{"type": "text", "text": text}], "isError": status != 200}


def json_or_empty(text):
    try:
        data = json.loads(text)
    except (TypeError, ValueError):
        return {}
    return data if isinstance(data, dict) else {}


def error_code(reply):
    """窓口の断りの error.code（読めなければ None）。"""
    err = reply.get("error") if isinstance(reply, dict) else None
    return err.get("code") if isinstance(err, dict) else None


def utc_seconds(text):
    """"2026-10-19T12:56:24.1891194Z" の形（秒の下は何桁でもよい）を、エポック秒にする。読めなければ None。"""
    if not isinstance(text, str) or len(text) < 19:
        return None
    try:
        return calendar.timegm(time.strptime(text[:19], "%Y-%m-%dT%H:%M:%S"))
    except ValueError:
        return None


def is_paused(reply):
    """窓口の答えの包みの paused.by が none 以外か（Editor か反射が止めている）。"""
    paused = reply.get("paused") if isinstance(reply, dict) else None
    return isinstance(paused, dict) and paused.get("by") not in (None, "none")


def capped(asked, cap):
    """長い待ちを cap 秒で切って返した印（使う側は同じ頼みをもう一度打てば続きを待てる）。"""
    return {"asked": asked, "upTo": cap, "why": "subagent_cache_5min_wait_again"}


def play_key(found):
    """つなぎ先の並びの頭が、どの Play か（つなぎ先のファイルの道と表のトークン＝Play ごとに替わる）。"""
    return (found[0][1], found[0][0].get("surfaceToken"))


def play_of(reply):
    """窓口の答えの包み（instance.playId）から、どの Play の答えかを読む（読めなければ None）。"""
    instance = reply.get("instance") if isinstance(reply, dict) else None
    return instance.get("playId") if isinstance(instance, dict) else None


def meta_progress(params):
    """使う側が頼みに付けた進みの印（_meta.progressToken）。無ければ None。"""
    meta = params.get("_meta") if isinstance(params, dict) else None
    token = meta.get("progressToken") if isinstance(meta, dict) else None
    return token if isinstance(token, (str, int)) and not isinstance(token, bool) else None


def int_arg(args, key, default, low, high):
    """整数の欄を読む（無ければ default・整数でない／範囲の外なら None）。"""
    value = args.get(key, default)
    if isinstance(value, bool) or not isinstance(value, int) or not low <= value <= high:
        return None
    return value


def read_json_file(path, retry=True):
    """小さな JSON のファイルを読む。無い・壊れている時は None（書きかけに当たった時は 1 回だけ読み直す）。"""
    for attempt in (0, 1):
        try:
            with open(path, encoding="utf-8-sig") as f:
                data = json.load(f)
            return data if isinstance(data, dict) else None
        except OSError:
            return None
        except ValueError:
            if not retry or attempt:
                return None
            time.sleep(0.05)
    return None
