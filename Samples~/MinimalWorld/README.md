# Minimal World (sample)

The smallest integration of AUTOMATA SYSTEM — Game Agent API for Unity.

| File | What it shows |
|---|---|
| `MinimalAgentWorld.cs` | An `IAgentWorld` with one route and no signals, hands, reflex watch or capture eyes. |
| `MinimalLookRoute.cs` | A surface route `/v1/look` (needs `Look`): the scene name, the camera name, and up to 8 objects in the camera view with words for direction and distance (no coordinates). |
| `MinimalAgentWorldBinder.cs` | A `MonoBehaviour` that calls `AgentHost.AttachWorld` when enabled and `AgentHost.DetachWorld` when disabled. If no scene has one, it creates one after the scene loads, so the sample works in any scene. |
| `Monster.Balloon.AutomataSystem.Samples.MinimalWorld.asmdef` | References `Monster.Balloon.AutomataSystem` and `Newtonsoft.Json.dll`. |

Use it:

1. Menu **Tools > Agent API > Open on next Play (operate)** (or **look only**).
2. Enter Play.
3. Start the bridge with `Tools~/Bridge/examples/tools.json` and call `game_hello`, then `game_look`.

The package README has every step. When your game has its own world, delete this sample folder.

---

いちばん小さい組み込みの見本です。`/v1/look` は、シーンの名前・カメラの名前・カメラの視野に入っている物（近い順に 8 つまで、
向きと遠さは言葉で）を返します。見本はシーンに置かなくても Play の頭に自分で動き出します。自分の世界を作ったら、このフォルダは消してかまいません。
