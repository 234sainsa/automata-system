# Changelog

All notable changes to this package are written here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [0.1.0] - 2026-11-03

First public release of AUTOMATA SYSTEM — Game Agent API for Unity (`monster.balloon.automatasystem`).

### Added

- Host (`AgentHost`): opens a local HTTP endpoint on `127.0.0.1` (ports 47810–47819) in the Editor and in development builds,
  and lays out the routes of the world the game attaches (`AttachWorld`, `NoteWorldLoading`, `DetachWorld`).
- Gate: two tokens (surface and back) that are new for every Play, abilities (`look`, `self`, `back`, `operate`, plus game abilities
  through `AgentAbilityCatalog`), `POST` and JSON only, `Host` and `Origin` checks, a body limit and a rate limit.
  Games can add one more check with `AgentRequestGuards`.
- Open flag: the Editor menu `Tools/Agent API` (file `UserSettings/AgentApi/open`) and the command-line argument `-agentApi <word>`.
  Closed by default; release builds have no endpoint; the define `AUTOMATA_SYSTEM_STRIP` removes the package.
- Endpoint file `Library/AgentApi/endpoint.json` (Editor) or `<persistentDataPath>/AgentApi/endpoint.json` (development builds).
- Interfaces for games: `IAgentWorld`, `IAgentRoute`, `IAgentSignalSource`, `IAgentHands` / `IAgentCommand`
  (`RunAsync` returns `Awaitable<AgentActResult>`), `IAgentReflexWatch`, `IAgentCaptureEyes`.
- Built-in routes: `hello`, `events`, `wait`, `back/events`, `back/wait`, `reflex/arm`, `reflex/ack`, `pause`, `lease`, `act`,
  `capture`, `back/screen`.
- Signal ledger with cursors (no skipped signals), the stop reflex with a latch, and screen capture with a look of the same frame.
- MCP bridge in Python (standard library only) in `Tools~/Bridge/`: a generic entry
  (`agent_bridge.py` / `python -m automata_bridge`), tool tables in JSON, plugin hooks, and a self-test (`tests/run_tests.py`).
- Sample `Minimal World` (one world with a surface `look` route) and `Tools~/Bridge/examples/tools.json`.
