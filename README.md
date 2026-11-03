# AUTOMATA SYSTEM — Game Agent API for Unity

Let AI automata play your Unity game with the same eyes and hands as a human player.

[日本語の README はこちら (README.ja.md)](README.ja.md)

- Package name: `monster.balloon.automatasystem` (the domain `balloon.monster` written in reverse, as UPM names are)
- C# namespace: `Monster.Balloon.AutomataSystem`
- License: MIT

## What it does

An AI agent (for example Claude through MCP) reads a running Unity game **the way a player sees it**
and plays it **with the same inputs a player uses**.

- While the game runs (Editor Play Mode or a development build), it opens a small HTTP endpoint on `127.0.0.1`.
- A small Python program, the **bridge**, speaks MCP on stdin/stdout with the AI client and forwards tool calls to that endpoint.
- The package knows nothing about your game. Your game registers what can be read ("routes")
  and what can be done ("commands") through C# interfaces.

Who it is for: Unity developers who want an AI to play-test, reproduce bugs, or check what the player can see,
without giving the AI a hidden, privileged view of the game while it plays.

## Requirements

| Item | Version |
|---|---|
| Unity | 6000.3 or newer (`"unity": "6000.3"` in `package.json`) |
| Newtonsoft.Json | `com.unity.nuget.newtonsoft-json` 3.2.2 (installed automatically as a dependency) |
| Python (for the bridge) | 3.8 or newer, standard library only (no `pip install`) |
| Tested on | Windows 11 (Unity 6000.3.16f1, Python 3.12) |

Other operating systems: the C# code and the bridge use portable APIs, with these notes:

- `Http/SocketHandleInheritance.cs` has a Windows-only part (it stops child processes from inheriting the listening socket).
  On other platforms that part is skipped.
- The Editor's back-tool screen recorder (see [Safety](#safety)) writes MP4 with `UnityEditor.Media.MediaEncoder`.
  If the platform cannot encode H.264, it stops with one warning; nothing else is affected.
- `Tools~/Bridge/automata_bridge/proc.py` is a Windows-only helper for plugins (it returns `None` elsewhere).
  The generic bridge does not use it.
- On macOS and Linux, start the bridge with `python3` instead of `python`.

## Quick start (hello and look in an empty project)

### 1. Install the package

In Unity: **Window > Package Manager > + > Add package from git URL...** and enter:

```
https://github.com/234sainsa/automata-system.git#v0.1.0
```

The `#v0.1.0` part pins the version, so your project does not change when `main` moves. Leave it off to follow `main`.
Or add it to `Packages/manifest.json`:

```json
{
  "dependencies": {
    "monster.balloon.automatasystem": "https://github.com/234sainsa/automata-system.git#v0.1.0"
  }
}
```

Newtonsoft.Json is installed automatically.

### 2. Import the sample

In the Package Manager, select **AUTOMATA SYSTEM — Game Agent API for Unity**, open **Samples**, and press **Import** next to **Minimal World**.
The sample adds one world with one surface route (`/v1/look`). It starts by itself when you enter Play,
so it works in any scene (even the default `SampleScene`).

### 3. Open the endpoint

Menu **Tools > Agent API > Open on next Play (operate)**.

This writes the flag file `UserSettings/AgentApi/open` (not tracked by git). The endpoint opens from the **next** Play.
**Tools > Agent API > Close** removes the flag.

### 4. Enter Play

The Console shows a line starting with `[AgentApi]` with the port, the opened abilities and the endpoint file
(the package's own log lines are written in Japanese). While Play runs, the file

```
<your project>/Library/AgentApi/endpoint.json
```

holds the port (one of 47810–47819) and the tokens. It is deleted when Play ends. New tokens are made for every Play.

Optional check without any AI (copy `port` and `surfaceToken` from `endpoint.json`):

```
curl -X POST http://127.0.0.1:<port>/v1/hello -H "Authorization: Bearer <surfaceToken>" -H "Content-Type: application/json" -d "{}"
```

In Windows PowerShell 5.1, `curl` is an alias of `Invoke-WebRequest`; type `curl.exe` instead.

### 5. Get the bridge

The bridge is in the package at `Tools~/Bridge/`. Unity does not import folders whose names end in `~`,
and the package cache folder name changes with every version, so copy the bridge to a fixed place once.
Run this from your project root:

```
python -c "import glob,shutil; shutil.copytree(glob.glob('Library/PackageCache/monster.balloon.automatasystem*/Tools~/Bridge')[0], 'AgentBridge', dirs_exist_ok=True)"
```

This makes `<your project>/AgentBridge/` with `agent_bridge.py`, the `automata_bridge/` package and
`examples/tools.json` (a tool table with `game_hello` and `game_look` for the sample).
You can also clone the repository and use its `Tools~/Bridge/` folder directly.
Copy it again after you update the package.

### 6. Tell your AI client about the bridge

For Claude Code, put `.mcp.json` in your project root:

```json
{
  "mcpServers": {
    "game": {
      "command": "python",
      "args": [
        "AgentBridge/agent_bridge.py",
        "--project", ".",
        "--tools", "AgentBridge/examples/tools.json"
      ]
    }
  }
}
```

- Relative paths are read from the folder the client starts the bridge in (Claude Code: the project folder).
  If your client starts it somewhere else, use absolute paths.
- On macOS and Linux use `"command": "python3"`.
- Every argument is listed in [`Tools~/Bridge/README.md`](Tools~/Bridge/README.md).

### 7. Call hello and look

With Unity in Play, ask the AI to call `game_hello`, then `game_look`. A `game_look` answer looks like this (shortened):

```json
{"api":"…","ok":true,"instance":{…},"stamp":{…},"paused":{…},
 "scene":"SampleScene","camera":"Main Camera",
 "visible":[{"name":"Cube","dir":"center","dist":"near"}],"more":0}
```

If the tools answer `no_endpoint`, Unity is not in Play, or the endpoint is not open (step 3).

## Concepts

### Abilities (what is open)

The endpoint is **closed by default**. The flag (the Editor menu, or `-agentApi <word>`) says which abilities open.

| Word | Opens |
|---|---|
| `look` | Look (surface eyes, surface events and waits) and Back |
| `self` | Self (hands: lease, act, stop reflex, pause, capture) |
| `back` | Back (the back token and the back routes) |
| `operate` | Every registered ability (the three built-in ones and the game's own) |
| `closed`, `off` | Nothing (wins over any other word) |

Words can be joined with `+`, `,` or spaces (for example `look+self`). An empty flag or an unknown word keeps the endpoint closed.

The built-in ability flags are `Look = 1 << 0`, `Self = 1 << 1` and `Back = 1 << 3`.
A game adds its own ability on a free bit (`1 << 2`, or `1 << 4` and up) with
`AgentAbilityCatalog.Current.Register(new AgentAbilityDef(...))`. Its name then works as a flag word, and `operate` includes it.

### Surface and back (two tokens)

- **Surface** (`AgentFace.Surface`): only what a player can know from the screen and sound at that moment.
  Do not put keys, IDs, coordinates or true hit points in surface routes and signals. The AI decides how to play from the surface only.
- **Back** (`AgentFace.Back`): any game data, for development and investigation. Back route paths start with `/v1/back/`.
- `endpoint.json` holds `surfaceToken` and, only when Back is open, `backToken`.
  The surface token on a back route is refused with `403 blind`.

### The gate

Every request passes the gate before it reaches the game:

- `POST` only, `Content-Type: application/json`, a body of at most 64 KiB, about 20 requests per second.
- `Host` must be `127.0.0.1:<port>`. Requests with an `Origin` header (browsers) are refused.
- `Authorization: Bearer <token>`; the token decides the face.
- A route whose ability is not open is refused with `403 not_open`. A world route while the world is not ready answers `503 world_not_ready`.
- A game can add one more check with `AgentRequestGuards.Register(...)` (`IAgentRequestGuard`).

### Routes (`IAgentRoute`)

A route is one path. It states its `Path`, its `Face`, and the abilities it `Needs` (any one of them is enough).
`Handle(AgentCall call, AgentReplyWriter reply)` runs on the main thread. Write your fields into `reply.Json`
(the host has already written `api`, `ok`, `instance`, `stamp` and `paused`), or call `reply.Fail(status, code, hint)`.

Built-in routes: `/v1/hello`, `/v1/events`, `/v1/wait`, `/v1/back/events`, `/v1/back/wait`, `/v1/reflex/arm`,
`/v1/reflex/ack`, `/v1/pause`, `/v1/lease`, `/v1/act`, `/v1/capture`, `/v1/back/screen`.
Your game adds the rest (for example `/v1/look`).

### Signals (`IAgentSignalSource`)

Signals are short records of what happened (for example `world.ready` or `paused`). They go into a ledger
that the AI reads with `events`, or waits on with `wait` (with a cursor, so nothing is skipped).
A signal source lists its `Kinds` and `Needs`, and writes in `Tick` while an AI is connected.
Code that writes signals directly declares them first with `AgentHost.DeclareSignalKinds(...)`, then calls `AgentHost.WriteSignal(...)`.

### Hands (`IAgentHands` / `IAgentCommand`)

The AI borrows the hands (`lease`) and runs commands (`act`) that press the same virtual pad a player uses.
When a person touches the pad or the keyboard, the hands are released: the person comes first.
`IAgentCommand.RunAsync` returns `UnityEngine.Awaitable<AgentActResult>`, so a command can be written as
`async Awaitable<AgentActResult>`. A command whose result is known at once returns `AgentActAwaitable.FromResult(...)`.

### Reflexes (`IAgentReflexWatch`)

Before long play, the AI arms the stop reflex (`reflex/arm`). When something dangerous happens, the game pauses
and a latch is set; the AI looks, then acknowledges (`reflex/ack`). Red (error) log lines trip it too, unless the AI asked to ignore them (`ignore`).
A game adds its own guards and moving reflexes through `IAgentReflexWatch`.

### Capture (`IAgentCaptureEyes`)

`capture` saves a PNG of the Game view together with a surface look of **the same frame**
(Editor: `<project>/Logs/AgentApi/captures/`, the newest 200 are kept; builds: under `persistentDataPath`).

## Registration points

| What | Interface / call | When |
|---|---|---|
| A world (holds everything below) | `IAgentWorld`, then `AgentHost.AttachWorld(world)` | When the world is ready (its routes appear) |
| The world is loading (optional) | `AgentHost.NoteWorldLoading(world)` | Title screen or loading |
| The world is torn down | `AgentHost.DetachWorld(world)` | When the world is destroyed |
| Routes | `IAgentRoute` in `IAgentWorld.Routes` | Read at `AttachWorld` |
| Signal sources | `IAgentSignalSource` in `IAgentWorld.SignalSources` | Attached while an AI is connected |
| Hands | `IAgentHands` / `IAgentCommand` in `IAgentWorld.Hands` (or `null`) | Used while the hands are borrowed |
| Reflex watch | `IAgentReflexWatch` in `IAgentWorld.ReflexWatch` (or `null`) | Runs while the reflex is armed |
| Capture eyes | `IAgentCaptureEyes` in `IAgentWorld.CaptureEyes` (or `null`) | Called for each capture |
| Game abilities | `AgentAbilityCatalog.Current.Register(new AgentAbilityDef(...))` | `[RuntimeInitializeOnLoadMethod]` at `AfterAssembliesLoaded` or `BeforeSceneLoad` |
| One more request check | `AgentRequestGuards.Register(...)` (`IAgentRequestGuard`) | Same as above |
| Short project label in hello | `AgentProjectLabel.Register(...)` | Same as above |

The last three registrations are reset at the start of every Play (`SubsystemRegistration`),
and the flag is read at `AfterSceneLoad`, so register them at `AfterAssembliesLoaded` or `BeforeSceneLoad`.

Add `Monster.Balloon.AutomataSystem` to the `references` of your game's asmdef (the package assemblies are not auto-referenced).
The sample in `Samples~/MinimalWorld/` shows the smallest working world.

## Safety

- **Release builds have no endpoint.** The HTTP assembly (`Monster.Balloon.AutomataSystem.Http`) compiles only with
  `UNITY_EDITOR || DEVELOPMENT_BUILD`, and the Back ability is removed outside the Editor and development builds.
  To remove the whole package from a build, add the scripting define symbol `AUTOMATA_SYSTEM_STRIP`.
- **Development builds** open only with the command-line argument `-agentApi <word>` (for example `-agentApi look`).
  Their endpoint file is `<Application.persistentDataPath>/AgentApi/endpoint.json`. The generic bridge looks only in
  `<project>/Library/AgentApi/`, so pointing it at a build needs a plugin (see the bridge README).
- **This machine only.** The endpoint listens on `127.0.0.1` (ports 47810–47819) and cannot be reached from other machines.
- **Tokens** are 32 random bytes, new for every Play, and kept only in `endpoint.json` under `Library/` (Editor) or
  `persistentDataPath` (builds). Do not commit or share that file. The bridge never prints tokens.
- **Files the package writes**: `UserSettings/AgentApi/open` (the flag), `Library/AgentApi/endpoint.json` and `state.json`
  (while Play runs), `Logs/AgentApi/captures/*.png`. While back routes are used in the Editor, it also records the Game view
  to MP4 in `<your Videos folder>/<productName>/AgentApi/` (at most 3 GB; the oldest files are removed first).

## What is inside

| Folder | Assembly | Contents |
|---|---|---|
| `/` | `Monster.Balloon.AutomataSystem` | Host, gate (tokens, abilities, rate), signal ledger (events, wait), stop reflex, capture desk |
| `Http/` | `Monster.Balloon.AutomataSystem.Http` | The local HTTP endpoint (own `Socket`; Editor and development builds only) |
| `Editor/` | `Monster.Balloon.AutomataSystem.Editor` | The `Tools/Agent API` menu, Game view size, back-tool screen recorder |
| `Tests/Editor/` | `Monster.Balloon.AutomataSystem.Tests` | Tests for the gate, the ledger and the flag |
| `Samples~/MinimalWorld/` | `Monster.Balloon.AutomataSystem.Samples.MinimalWorld` | The sample (imported from the Package Manager) |
| `Tools~/Bridge/` | (Python) | The MCP bridge, its self-test and `examples/tools.json` |

## License

MIT. See [LICENSE.md](LICENSE.md).
Dependency: [Newtonsoft.Json](https://github.com/JamesNK/Newtonsoft.Json) (MIT), through Unity's `com.unity.nuget.newtonsoft-json` package.
