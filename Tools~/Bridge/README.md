# MCP bridge (`automata_bridge`)

The bridge connects an MCP client (for example Claude Code) to the Game Agent API endpoint inside a running Unity game.
It is part of AUTOMATA SYSTEM — Game Agent API for Unity.

- Python 3.8 or newer, standard library only. Nothing to install.
- The client starts the bridge and speaks MCP (JSON-RPC 2.0, one message per line) on stdin/stdout.
- On every call, the bridge reads `<project>/Library/AgentApi/<endpoint file>` again (the port and the tokens change every Play)
  and sends `POST http://127.0.0.1:<port>/v1/<route>` with the right token. The tool answer text is the game's reply as is.
- The bridge never prints the tokens (not in answers, not in its log). Its own log goes to stderr only.

## Start

```
python agent_bridge.py --project <Unity project> --tools <tools.json>
```

`agent_bridge.py` adds its own folder to the import path, so it works from anywhere.
From this folder, `python -m automata_bridge ...` does the same.
On macOS and Linux use `python3`.

| Argument | Default | Meaning |
|---|---|---|
| `--project PATH` | the current folder | Unity project root to look in. Repeatable; the newest endpoint file wins, and a closed one is skipped. |
| `--tools PATH` | (required) | The tool table (see below). |
| `--profile NAME` | `main` | Which profile of the tool table to serve. |
| `--endpoint-file NAME` | `endpoint.json` | File name under `<project>/Library/AgentApi/`. |
| `--plugin MODULE:ATTR` | none | Load a plugin (repeatable). If `ATTR` is callable (a class or a function), it is called with no arguments. |
| `--plugin-path DIR` | none | Add a folder to the import path before loading plugins (repeatable). |
| `--wait-cap SEC` | `270` | Longest real wait for long waits (1–270). Longer waits return early with `capped`; call again to keep waiting. |
| `--by-prefix TEXT` | `agent-` | Prefix of the name used to borrow the hands (8 hex characters follow). |
| `--log-prefix TEXT` | `[agent-bridge] ` | Prefix of the bridge's own stderr lines. |

Example `.mcp.json` (Claude Code, project root, the bridge copied to `AgentBridge/`):

```json
{
  "mcpServers": {
    "game": {
      "command": "python",
      "args": ["AgentBridge/agent_bridge.py", "--project", ".", "--tools", "AgentBridge/examples/tools.json"]
    }
  }
}
```

## Tool table (`tools.json`)

```json
{
  "server": {"name": "game-agent-api", "title": "My Game Agent API"},
  "profiles": {
    "main": {
      "instructions": ["Text shown to the AI at initialize. ", "A list is joined without separators."],
      "tools": [
        {
          "name": "game_hello",
          "route": "hello",
          "face": "surface",
          "title": "Say hello to the game",
          "description": "What the AI reads about this tool (a string, or a list of strings that are joined).",
          "inputSchema": {"type": "object", "properties": {}, "additionalProperties": false}
        }
      ]
    }
  }
}
```

Each tool row:

| Field | Meaning |
|---|---|
| `name`, `title`, `description`, `inputSchema` | Shown in `tools/list` (required). |
| `face` | `surface` or `back` (required). Back tools are listed only while the endpoint file has a back token; when that changes, the bridge sends `notifications/tools/list_changed`. |
| `route` | The route without `/v1/` (for example `look` or `back/look`). The arguments are sent as the JSON body. |
| `handler` | Instead of (or together with) `route`: the name of a bridge handler (below) or a plugin handler. |
| `annotations` | MCP annotations. Default: `{"readOnlyHint": true, "openWorldHint": false}`. |

The bridge checks the table at start and stops with an error if a name is missing or repeated, a field is missing,
`face` is wrong, or a `handler` is unknown.

Examples: `examples/tools.json` (hello and look for the Minimal World sample) and `tests/fixture_tools.json`
(every built-in handler).

### Built-in handlers

| Handler | What it does |
|---|---|
| `wait` | Sends the row's `route` (`wait` or `back/wait`). Waits longer than 25 seconds (up to 300) are re-sent in 25-second steps with the last cursor; the real wait stops at `--wait-cap` with `capped`. |
| `reflex` | `do`: `arm` (`minutes`, `stopBefore`, `modes`, `ignore`) to `reflex/arm`; `ack` (`resume`) to `reflex/ack`; `pause`, `resume`, `step` (`frames`) to `pause`. |
| `act` | `do` + `args` + `ref` to `act` (one command); `seq` (up to 8 steps, stops at the first step that is not `done` unless `keepGoing`); `do: "lease"` (with `release`) to `lease`. The bridge adds `by`. A target `args.to` of `{"pos":…}` or `{"id":…}` is sent with the back token. Cancelling a running act releases the hands. |
| `capture` | `sections`, `measure`, `hideDebugOverlays` (back token) to `capture`; `form` (`portrait`, `landscape`, `both`) resizes the Game view through `back/screen` first and restores it after. |
| `log` | Reads the game's own log files (back only; the lines are read from the files, not asked from the game). Arguments: `tags`, `has`, `after`, `last`, `max`, `waitSec`, `ids`. The folder and the line format come from a plugin's `log_layout`; the default is `<project>/Logs/<yyyyMMdd_HHmmss>[_<n>].log` with no error-line stop. While it reads or waits, the bridge sends one `hello` every 20 s at most, so the game keeps its signal sources and the Editor recording on. |

Retries: when the game refuses before running a call with `world_not_ready` (up to 60 s), `too_soon` (up to 3 s) or
`main_thread_busy` (up to 10 s; the game withdrew the call before the main thread took it), the bridge waits and sends again.
`504 answer_late` (the main thread took the call but the answer came too late, so the call ran or is still running) is retried
only for read-only routes (`hello`, `look`, `events`, `wait`, `back/look`, `back/entity`, `back/events`, `back/wait`).
The answer then carries `bridgeRetried`.

## Plugins

A plugin is any Python object. The bridge looks up hooks by name and calls only the hooks the plugin has.
With no plugin, the bridge runs with generic defaults. The authoritative list is at the top of `automata_bridge/hooks.py`.

| Hook | When it is called |
|---|---|
| `bind(bridge)` | Once at the end of building (gets the bridge: `link`, `calls`, `server`). |
| `handlers() -> {name: function}` | Once at building; adds handlers that tool rows can name. A handler gets a `Call` (`request_id`, `found`, `tool`, `args`, `params`) and returns an MCP tool result. |
| `narrow_projects(projects) -> (list, mark)` | Each time the endpoint is looked up (choose which projects to search). |
| `settle_found(mark, found)` | After each lookup (record why an endpoint was chosen). |
| `session_for(path) -> id or None` | Before each request; the id is sent in the `X-Claude-Session` header. |
| `play_starting() -> bool` | When no endpoint is found (return true to wait up to 60 s for Play to open it). |
| `play_wait_failed()` | When that wait ended without an endpoint. |
| `prepare_call(name, args) -> (args, refusal or None)` | Before a tool runs (remove bridge-only fields, or refuse). |
| `call_args(tool, args) -> args` | Before a plain `route` tool is sent (add fields). |
| `decorate_reply(params, reply) -> reply` | Before an answer is sent (for example, add a second text block). |
| `on_answer(request_id, delivered)` | When an answer is sent (`delivered` is false if the client cancelled). |
| `log_layout() -> LogLayout` | Once, when the log reader is made (`automata_bridge.logs.LogLayout`). |
| `log_ids_default(found) -> bool` | When a `log` call leaves out `ids`. |
| `startup_note() -> str` | Text added to the one start-up log line. |
| `start(stop_event)` | Before reading stdin (start background threads). |
| `close()` | After stdin closes (clean up). |

Example (`tests/fixture_plugin.py`):

```python
from automata_bridge.logs import LogLayout

class LiveLogPlugin:
    name = "livelog"

    def log_layout(self):
        return LogLayout(folder=("Logs", "Live"), name=r"^(\d{8}_\d{6})(?:_(\d+))?\.log$",
                         error_needle=b" [E] ", error_line=rb"^\d\d:\d\d:\d\d\.\d{3} \[E\] ")
```

```
python agent_bridge.py --project . --tools tools.json --plugin-path tests --plugin fixture_plugin:LiveLogPlugin
```

Notes:

- `proc.py` (`ancestor_process`) is a Windows-only helper for plugins; it returns `None` on other systems.
- Development builds write their endpoint file under `Application.persistentDataPath`. The bridge looks only in
  `<project>/Library/AgentApi/`; to reach a build, point `--project` at a folder whose `Library/AgentApi/` holds a copy
  of that file, or write a plugin.

## Self-test

```
python tests/run_tests.py
```

It starts a fake endpoint on `127.0.0.1`, writes temporary endpoint files, starts the bridge through `agent_bridge.py`
with `tests/fixture_tools.json`, and checks the MCP answers one by one (about 40 seconds, 90 checks).
It prints one `OK` or `NG` line per check and exits with 1 if any check fails.
