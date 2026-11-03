"""ゲームのログのファイルを、印で絞って読む係 LogReader（窓口は通らない・橋がファイルを読む）。

- ログの置き場・ファイルの名前の形・赤の行の形・行の ID の作り方は LogLayout に入れる（ゲームの差し込みの log_layout で替える）。
- しおりは "<ファイル名>@<バイトの位置>"。1 本が大きくなって次のファイル（名前の頭が同じで番号が大きい物）へ移っても続けて読む。
- LogReader は状態を持たない（設定の wait_cap と layout だけ）。
"""

import collections
import os
import re
import time

from . import wire
from .window import project_of

# 1 回に読むバイト・1 行の字の上限・返す行の既定と上限・しおり無しで返す終わりの行の既定と上限・待つ時の読み直しの間。
LOG_CHUNK = 8 * 1024 * 1024
LOG_LINE_MAX_CHARS = 1000
# 長い行は頭と尻を残して、真ん中を略す（10-24・診断の行は、決めた事を行の尻に書く物がある）。
LOG_LINE_HEAD_CHARS = 600
LOG_LINE_TAIL_CHARS = 400
LOG_MAX_DEFAULT = 30
LOG_MAX = 500
LOG_LAST_DEFAULT = 20
LOG_LAST_MAX = 200
LOG_POLL_SEC = 0.5
LOG_MAX_TAGS = 16
# 1 回の答えの行の字の合計の上限（10-26）。越える分は返さず、more とその行の頭のしおりで続きを渡す。
# 訳＝係は道具を使うたびに文脈を全部読み直す。
LOG_REPLY_MAX_CHARS = 12000
# 待っている間に、つなぎ先が替わっていないかを見る間（Play を抜けたら待ちを止める）。
LOG_PLAY_CHECK_SEC = 2.0


def default_line_id(name, pos):
    """その行を指す ID（"log:<ファイル名>@<行の頭のバイトの位置>"）。"""
    return "log:%s@%d" % (name, pos)


class LogLayout:
    """ログの置き場と形。
    folder＝プロジェクトの根からのフォルダ（並び）・name＝ファイルの名前の形（1 つ目の組＝Play の頭・2 つ目の組＝続きの番号）・
    error_needle と error_line＝待ちを止める赤の行（探す字と、行の頭の形・None なら赤の行では止めない）・line_id＝行の ID の作り方。"""

    def __init__(self, folder=("Logs",), name=r"^(\d{8}_\d{6})(?:_(\d+))?\.log$", error_needle=None, error_line=None,
                 line_id=default_line_id):
        self.folder = tuple(folder)
        self.name = re.compile(name) if isinstance(name, str) else name
        self.error_needle = error_needle
        self.error_line = re.compile(error_line) if isinstance(error_line, (str, bytes)) else error_line
        self.line_id = line_id


def live_logs(layout, folder):
    """ログの名前を、古い順（Play を始めた日時→続きの番号）に返す。"""
    try:
        entries = os.listdir(folder)
    except OSError:
        return []
    keyed = []
    for name in entries:
        m = layout.name.match(name)
        if m:
            keyed.append(((m.group(1), int(m.group(2) or 0)), name))
    keyed.sort()
    return [name for _, name in keyed]


def parse_log_cursor(cursor, names):
    """しおり "<ファイル名>@<バイトの位置>" を (名前, 位置) にする。形が悪い・ファイルがもう無い時は None。"""
    name, sep, offset = cursor.rpartition("@")
    if not sep or name not in names or not offset.isdigit():
        return None
    return name, int(offset)


def log_end(folder, names):
    """今の終わりのしおり（いちばん新しいファイルの、最後の改行の後＝書きかけの行の頭）。"""
    name = names[-1]
    try:
        with open(os.path.join(folder, name), "rb") as f:
            size = f.seek(0, os.SEEK_END)
            back = min(size, 65536)
            f.seek(size - back)
            tail = f.read(back)
    except OSError:
        return "%s@0" % name
    return "%s@%d" % (name, size - back + tail.rfind(b"\n") + 1)


def match_lines(data, needles, also):
    """data（行の頭で始まり、行の終わりで切った字）から、needles のどれかと also を含む行を、頭の順に返す。
    [(頭の位置, 行の字)]。⭐ 行を全部割らずに、探す字から行の両端を探す（64MB を数秒で読むため）。"""
    seen = {}
    for needle in needles:
        i = data.find(needle)
        while i != -1:
            start = data.rfind(b"\n", 0, i) + 1
            end = data.find(b"\n", i)
            if end == -1:
                end = len(data)
            if start not in seen:
                line = data[start:end]
                seen[start] = line if also is None or also in line else None
            i = data.find(needle, end)
    return [(start, seen[start]) for start in sorted(seen) if seen[start] is not None]


def walk_log(folder, names, name, offset, needles, also):
    """しおり（name の offset）から新しいファイルの終わりまで、合う行を古い順に出す。
    ("line", ファイル名, 行の頭の位置, 行の字) を出し、最後に ("end", ファイル名, 読み終えた位置) を出す。
    ⭐ 閉じたファイル（新しいファイルがある物）は、改行の無い最後の行も読む。いちばん新しいファイルの書きかけの行は読まない。"""
    first = names.index(name)
    for k in range(first, len(names)):
        name = names[k]
        newest = k == len(names) - 1
        pos = offset if k == first else 0
        try:
            f = open(os.path.join(folder, name), "rb")
        except OSError:
            if newest:
                yield ("end", name, pos)
                return
            continue
        with f:
            f.seek(pos)
            while True:
                data = f.read(LOG_CHUNK)
                if not data:
                    break
                full = len(data) == LOG_CHUNK
                cut = data.rfind(b"\n") + 1
                if full and cut == 0:
                    cut = len(data)  # 8MB より長い 1 行（無いはず）は、切って読む
                elif not full and not newest:
                    cut = len(data)
                for start, line in match_lines(data[:cut], needles, also):
                    yield ("line", name, pos + start, line)
                pos += cut
                if cut < len(data):
                    if not full:
                        break  # 書きかけの行の頭で止める
                    f.seek(pos)
        if newest:
            yield ("end", name, pos)
            return


def line_text(line):
    text = line.decode("utf-8", errors="replace").rstrip("\r")
    if len(text) <= LOG_LINE_MAX_CHARS:
        return text
    cut = len(text) - LOG_LINE_HEAD_CHARS - LOG_LINE_TAIL_CHARS
    return "%s …（%d 字略）… %s" % (text[:LOG_LINE_HEAD_CHARS], cut, text[-LOG_LINE_TAIL_CHARS:])


def log_forward(layout, folder, names, begin, needles, also, most):
    """しおりの続きの合う行を、most 行まで返す。(行の字の並び, 行の ID の並び, 次のしおり, まだ続きがあるか)。
    続きがある時の次のしおりは、返さなかった最初の行の頭（次の読みはその行から）。"""
    lines = []
    ids = []
    chars = 0
    walker = walk_log(folder, names, begin[0], begin[1], needles, also)
    try:
        for kind, name, pos, *rest in walker:
            if kind == "end":
                return lines, ids, "%s@%d" % (name, pos), False
            if len(lines) >= most:
                return lines, ids, "%s@%d" % (name, pos), True
            text = line_text(rest[0])
            # 字の上限を越える行は次の読みへ回す（1 行目だけは、越えても返す＝しおりが進まなくなるため）。
            if lines and chars + len(text) > LOG_REPLY_MAX_CHARS:
                return lines, ids, "%s@%d" % (name, pos), True
            chars += len(text)
            lines.append(text)
            ids.append(layout.line_id(name, pos))
    finally:
        walker.close()
    return lines, ids, "%s@%d" % begin, False


def log_tail(layout, folder, names, needles, also, last):
    """今の Play のログ（いちばん新しいファイルと、同じ頭の前のファイル）の、終わりの last 行と、その ID と、今の終わりのしおりと、
    字の上限（LOG_REPLY_MAX_CHARS）のために古い方から略した行の数。"""
    play = layout.name.match(names[-1]).group(1)
    first = next(name for name in names if layout.name.match(name).group(1) == play)
    kept = collections.deque(maxlen=last)
    cursor = None
    for kind, name, pos, *rest in walk_log(folder, names, first, 0, needles, also):
        if kind == "end":
            cursor = "%s@%d" % (name, pos)
            break
        kept.append((layout.line_id(name, pos), rest[0]))
    if cursor is None:
        cursor = log_end(folder, names)
    texts = [(i, line_text(t)) for i, t in kept]
    cut, chars = 0, sum(len(t) for _, t in texts)
    while len(texts) > 1 and chars > LOG_REPLY_MAX_CHARS:
        chars -= len(texts[0][1])
        texts.pop(0)
        cut += 1
    return [t for _, t in texts], [i for i, _ in texts], cursor, cut


def log_error_stop(layout, folder, names, watch):
    """待ちを止める赤の行を、しおり watch の続きから探す。(止める訳か None, 次のしおり)。
    止める訳は {"why":"error_line","lines":[…],"ids":[…]}（多くて 3 行）。watch が読めない時は、今の終わりから見張り直す。
    layout に赤の行の形が無ければ、赤の行では止めない。"""
    if layout.error_needle is None or layout.error_line is None:
        return None, watch
    begin = parse_log_cursor(watch, names) if watch else None
    if begin is None:
        return None, log_end(folder, names)
    found, ids = [], []
    walker = walk_log(folder, names, begin[0], begin[1], [layout.error_needle], None)
    try:
        for kind, name, pos, *rest in walker:
            if kind == "end":
                watch = "%s@%d" % (name, pos)
                break
            if layout.error_line.match(rest[0]) and len(found) < 3:
                found.append(line_text(rest[0]))
                ids.append(layout.line_id(name, pos))
    finally:
        walker.close()
    if not found:
        return None, watch
    return {"why": "error_line", "lines": found, "ids": ids}, watch


class LogReader:
    def __init__(self, link, calls, hooks, wait_cap=wire.WAIT_CACHE_SAFE_SEC):
        self.link = link
        self.calls = calls
        self.hooks = hooks
        self.wait_cap = wait_cap
        make = hooks.first("log_layout")
        self.layout = make() if make is not None else LogLayout()

    def handlers(self):
        return {"log": self.run}

    def run(self, call):
        """つなぎ先のプロジェクトのログを、印で絞って読む。
        after が無く waitSec も 0 なら、今の Play のログ（同じ頭のファイル全部）の終わりの last 行と、今の終わりのしおり。
        after があれば、その続きを古い順に max 行まで（more＝続きがある）。waitSec があれば、合う行が出るまで読み直す
        （after が無ければ今の終わりから）。
        ⭐ いちばん新しいファイルの書きかけの行（終わりに改行が無い）は読まない＝しおりはその行の頭に残る。"""
        request_id, found, args = call.request_id, call.found, call.args
        layout = self.layout
        if not found[0][0].get("backToken"):
            return wire.tool_text({"ok": False, "error": {"code": "no_back_token", "hint": "this_build_has_no_back_face"}}, True)
        tags = args.get("tags")
        if (not isinstance(tags, list) or not 1 <= len(tags) <= LOG_MAX_TAGS
                or not all(isinstance(t, str) and t for t in tags)):
            return wire.tool_text({"ok": False, "error": {"code": "bad_tags", "hint": "1..%d_strings" % LOG_MAX_TAGS}}, True)
        has = args.get("has")
        after = args.get("after")
        if (has is not None and (not isinstance(has, str) or not has)) or (after is not None and not isinstance(after, str)):
            return wire.tool_text({"ok": False, "error": {"code": "bad_args", "hint": "has_and_after_are_strings"}}, True)
        last = wire.int_arg(args, "last", LOG_LAST_DEFAULT, 1, LOG_LAST_MAX)
        most = wire.int_arg(args, "max", LOG_MAX_DEFAULT, 1, LOG_MAX)
        wait_sec = wire.int_arg(args, "waitSec", 0, 0, wire.WAIT_LONG_MAX_SEC)
        if last is None or most is None or wait_sec is None:
            return wire.tool_text({"ok": False, "error": {"code": "bad_number", "hint": "last_max_waitSec"}}, True)
        asked_wait = wait_sec
        wait_sec = min(wait_sec, self.wait_cap)
        want_ids = args.get("ids")
        if want_ids is not None and not isinstance(want_ids, bool):
            return wire.tool_text({"ok": False, "error": {"code": "bad_args", "hint": "ids_is_boolean"}}, True)
        if want_ids is None:
            # 省いた時は、差し込みに聞く（無ければ返さない）。
            hook = self.hooks.first("log_ids_default")
            want_ids = bool(hook(found)) if hook is not None else False

        folder = os.path.join(project_of(found[0][1]), *layout.folder)
        names = live_logs(layout, folder)
        if not names:
            return wire.tool_text({"ok": False, "error": {"code": "no_log", "hint": "/".join(layout.folder)}}, True)
        needles = [t.encode("utf-8") for t in tags]
        also = has.encode("utf-8") if has else None
        if after is None and wait_sec == 0:
            lines, ids, cursor, cut = log_tail(layout, folder, names, needles, also, last)
            reply = {"ok": True, "lines": lines, "ids": ids, "cursor": cursor}
            if not want_ids:
                del reply["ids"]
            if cut:
                reply["cut"] = cut
            return wire.tool_text(reply, False)

        cursor = after if after is not None else log_end(folder, names)
        # 赤の行の見張りは、待ち始めた所から（after より前の古い赤の行では止めない）。
        watch = log_end(folder, names) if wait_sec else None
        play = wire.play_key(found)
        play_head = layout.name.match(names[-1]).group(1)
        checked_play = start = time.monotonic()
        stop = None
        while True:
            begin = parse_log_cursor(cursor, names)
            if begin is None:
                return wire.tool_text({"ok": False, "error": {"code": "bad_cursor", "hint": "name@offset_of_a_kept_log"}}, True)
            lines, ids, cursor, more = log_forward(layout, folder, names, begin, needles, also, most)
            waited = time.monotonic() - start
            if lines or waited >= wait_sec:
                break
            stop, watch = log_error_stop(layout, folder, names, watch)
            if stop is None and layout.name.match(names[-1]).group(1) != play_head:
                stop = {"why": "new_play"}
            if stop is None and time.monotonic() - checked_play >= LOG_PLAY_CHECK_SEC:
                checked_play = time.monotonic()
                now, _ = self.link.endpoints()
                if not now or wire.play_key(now) != play:
                    stop = {"why": "play_ended"}
            if stop is not None:
                break
            if self.calls.is_cancelled(request_id):
                break
            time.sleep(LOG_POLL_SEC)
            names = live_logs(layout, folder)
        reply = {"ok": True, "lines": lines, "ids": ids, "cursor": cursor, "more": more}
        if not want_ids:
            del reply["ids"]
        if wait_sec:
            reply["waited"] = round(waited, 1)
        if asked_wait > wait_sec and not lines and stop is None:
            reply["capped"] = wire.capped(asked_wait, self.wait_cap)
        if stop is not None:
            reply["stop"] = stop
        return wire.tool_text(reply, False)
