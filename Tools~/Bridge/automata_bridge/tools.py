"""道具の表（JSON）を読む部品。

表のファイルの形（ゲームが書く）:
    {"server": {"name": …, "title": …},
     "profiles": {"<形の名前>": {"instructions": 字か字の並び, "tools": [道具の行, …]}, …}}
道具の行＝name・title・description（字か字の並び＝並びはつないで 1 つの字にする）・inputSchema・face（surface か back）・
route（窓口の道＝そのまま写す）か handler（処理の名前＝橋かゲームの差し込みが登録した物）・annotations（省くと読むだけ）。
"""

import json

# 道具の行に annotations が無い時の既定（読むだけ・外の世界に触らない）。
DEFAULT_ANNOTATIONS = {"readOnlyHint": True, "openWorldHint": False}
FACES = ("surface", "back")


def joined(text):
    """字か字の並びを 1 つの字にする（並びは区切りを足さずにつなぐ）。"""
    if isinstance(text, list):
        return "".join(text)
    return text


def read_table(path):
    """表のファイルを読む。壊れていれば例外のまま（起動の時に止める＝黙って空の表で動かない）。"""
    with open(path, encoding="utf-8") as f:
        data = json.load(f)
    if not isinstance(data, dict) or not isinstance(data.get("profiles"), dict):
        raise ValueError("道具の表の形が違う（profiles が無い）: %s" % path)
    return data


def tool_rows(rows):
    """表の道具の行を、橋が使う形にする（description をつなぐ）。"""
    tools = []
    for row in rows:
        tool = dict(row)
        tool["description"] = joined(tool.get("description"))
        tools.append(tool)
    return tools


def check_tools(tools, handlers):
    """道具の表が橋の処理と食い違っていないか確かめる。食い違えば ValueError（起動の時に止める）。"""
    names = set()
    for tool in tools:
        name = tool.get("name")
        if not isinstance(name, str) or not name or name in names:
            raise ValueError("道具の名前が無いか、重なっている: %r" % (name,))
        names.add(name)
        for key in ("title", "description", "inputSchema"):
            if key not in tool:
                raise ValueError("道具 %s に %s が無い" % (name, key))
        if tool.get("face") not in FACES:
            raise ValueError("道具 %s の face が %r（surface か back）" % (name, tool.get("face")))
        handler = tool.get("handler")
        if handler is not None and handler not in handlers:
            raise ValueError("道具 %s の handler %r を、橋もゲームの差し込みも登録していない" % (name, handler))
        if handler is None and not isinstance(tool.get("route"), str):
            raise ValueError("道具 %s に route も handler も無い" % name)


def public_tool(tool):
    """道具の表の 1 行を、tools/list で見せる形にする（橋だけが使う欄は見せない）。"""
    return {
        "name": tool["name"],
        "title": tool["title"],
        "description": tool["description"],
        "inputSchema": tool["inputSchema"],
        "annotations": dict(tool.get("annotations", DEFAULT_ANNOTATIONS), title=tool["title"]),
    }
