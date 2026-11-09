# Changelog

All notable changes to this package are written here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [0.2.3] - 2026-11-09

### Added

- `IAgentRoutineReflexes` (optional, on the reflex watch): names the moving reflexes that run again and again in normal
  play, such as advancing dialogue lines. The `reflex_loop` guard does not count them, and they run only when `modes`
  names them (an arm without `modes`, or with `null`, leaves them off).

## [0.2.2] - 2026-11-07

### Fixed

- When the main thread does not answer in time, a call that has not started is withdrawn and never runs, and the answer is
  `503 main_thread_busy` (safe to send again). Before, the call stayed queued and ran later, so a caller that sent it again
  ran it twice.
- A call that the main thread already took but answered too late now gets `504 answer_late` (hint
  `taken_look_before_resend`) instead of `503 main_thread_busy`.
- Bridge: `log` sends one `hello` every 20 s at most while it reads or waits, so the game does not drop its signal sources
  and the Editor recording does not stop during long log waits.

### Changed

- Bridge: `main_thread_busy` is retried for every route (the call did not run). `answer_late` is retried only for
  read-only routes.
- The HTTP endpoint and `AgentInProcessLink` wait for answers the same way, so both give the same codes.

## [0.2.1] - 2026-11-04

### Changed

- ChatGPT: before a refresh, `ChatGptAccount` reads the saved sign-in again and uses a refresh token that another
  `ChatGptAccount` on the same folder has already rotated, instead of sending the old one.
- ChatGPT: the "You're using your ChatGPT plan" notice is set when the install is registered, so it still shows once when
  the first code exchange fails and the next sign-in succeeds.
- The ChatGPT test server now follows OpenAI's published rules strictly: one-time codes with an expiry, PKCE, `redirect_uri`,
  `resource` and issued client id checks on the code exchange, single-use refresh tokens, revoke, re-auth callbacks without
  `client_id`, Bearer checks, and the request limits of `/v1/responses`.

### Fixed

- ChatGPT: a re-auth callback that carries a different `client_id` is refused (code `client_id_mismatch`) and the saved
  client id is kept.
- ChatGPT: refusals from the token endpoint keep the OAuth `error` code and the `error_description` text in
  `ChatGptException`, and an `error_description` on the sign-in callback is added to the message.

## [0.2.0] - 2026-11-04

### Added

- Optional assembly `Monster.Balloon.AutomataSystem.ChatGpt` (`Runtime/ChatGpt/`): Sign in with ChatGPT, so an in-game AI
  can run on the player's own ChatGPT plan usage.
  - `ChatGptAccount`: sign-in through the browser with PKCE and a loopback callback on `127.0.0.1`
    (first sign-in registers the install and saves the issued client id), id token checks (RS256, `iss`, `aud`, `exp`, `nonce`),
    token refresh one at a time with the rotated refresh token saved, sign-out with revoke, the model list, and the state
    (`SignedOut`, `SigningIn`, `SignedIn`, `PlanNotGranted`).
  - `ChatGptSession`: a conversation on `/v1/responses` (streaming, `store: false`, function tools from any `IAgentToolHost`,
    optional reasoning effort, encrypted reasoning carried between requests with a one-time fallback when it is refused).
  - `ChatGptException` with `ChatGptErrorKind` for usage limits, eligibility, sign-in, network and stream failures.
  - The define `AUTOMATA_SYSTEM_NO_CHATGPT` removes the assembly (for closed-source games until their application is accepted).
- Tests for the ChatGPT assembly against a fake OpenAI server (`Tests/Editor/ChatGpt/`).

### Fixed

- Recording (Editor): each MP4 segment now opens at its first captured frame and takes the screen size from that frame,
  so segments are no longer left as 0-byte files when another window was in front. A segment closed with no frames logs
  one warning with the reasons it could not capture.

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
