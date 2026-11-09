# AUTOMATA SYSTEM — Game Agent API for Unity

AI のからくりたちに、人と同じ目と手で、あなたの Unity のゲームを遊ばせましょう。

[English README (README.md)](README.md)

- パッケージの名前: `monster.balloon.automatasystem`（ドメイン `balloon.monster` を逆に並べた形。UPM の名前の決まり）
- C# の名前空間: `Monster.Balloon.AutomataSystem`
- ライセンス: MIT

## 何をするか

AI（たとえば MCP でつないだ Claude）が、動いている Unity のゲームを**遊ぶ人と同じ見え方で読み**、
**遊ぶ人と同じ手で動かします**。

- ゲームは、動いている間（Editor の Play か開発ビルド）だけ、`127.0.0.1` に小さな HTTP の窓口を開きます。
- 小さな Python の台本（**橋**）が、標準入出力で AI の道具と MCP で話し、道具の呼び出しを窓口へ送ります。
- このパッケージは、どのゲームにも依存しません。「何を読めるか（道）」と「何ができるか（命令）」は、
  ゲームの側が C# の interface で登録します。

誰向けか: AI に遊びの確かめ・不具合の再現・「遊ぶ人に何が見えているか」の確かめをさせたい Unity の開発者。
遊んでいる間の AI に、遊ぶ人より多くの物を見せない作りです。

## 必要な物

| 物 | 版 |
|---|---|
| Unity | 6000.3 以上（`package.json` の `"unity": "6000.3"`） |
| Newtonsoft.Json | `com.unity.nuget.newtonsoft-json` 3.2.2（依存として自動で入ります） |
| Python（橋に使う） | 3.8 以上・標準の部品だけ（`pip install` は要りません） |
| 確かめた所 | Windows 11（Unity 6000.3.16f1・Python 3.12） |

ほかの OS について: C# と橋は、どの OS でも動く部品で書いてあります。ただし次の所があります。

- `Http/SocketHandleInheritance.cs` には Windows だけの所があります（待ち受けの口を子のプロセスへ受け継がせない）。
  ほかの OS では、その所を飛ばします。
- Editor で裏の道具を使った間の録画（[安全](#安全) を見てください）は、`UnityEditor.Media.MediaEncoder` で MP4 を書きます。
  その OS で H.264 を書けない時は、黄色の知らせを 1 行出して録画をやめます。ほかには響きません。
- `Tools~/Bridge/automata_bridge/proc.py` は、差し込み用の Windows だけの補助です（ほかの OS では `None` を返します）。
  汎用の橋は使いません。
- macOS と Linux では、橋を `python` ではなく `python3` で起こします。

## すぐ始める（空のプロジェクトで hello と look を通す）

### 1. パッケージを入れる

Unity で **Window > Package Manager > + > Add package from git URL...** を開き、次を入れます。

```
https://github.com/234sainsa/automata-system.git#v0.2.3
```

終わりの `#v0.2.3` で版を決めます。こうすると、`main` が変わっても、あなたのプロジェクトの中身は変わりません。`main` に付いていきたい時は、`#v0.2.3` を外します。
`Packages/manifest.json` に書いても入ります。

```json
{
  "dependencies": {
    "monster.balloon.automatasystem": "https://github.com/234sainsa/automata-system.git#v0.2.3"
  }
}
```

Newtonsoft.Json は自動で入ります。

### 2. 見本を入れる

Package Manager で **AUTOMATA SYSTEM — Game Agent API for Unity** を選び、**Samples** を開いて、**Minimal World** の **Import** を押します。
見本は、表の道（`/v1/look`）を 1 本だけ持つ世界を足します。見本は Play に入ると自分で動き出すので、
どのシーンでも使えます（新しいプロジェクトの `SampleScene` でも）。

### 3. 窓口を開く

メニュー **Tools > Agent API > Open on next Play (operate)** を押します。

開く印のファイル `UserSettings/AgentApi/open` が置かれます（git には載りません）。窓口は**次の** Play から開きます。
**Tools > Agent API > Close** で印を外せます。

### 4. Play に入る

Console に `[AgentApi]` で始まる行が出ます（口の番号・開いた力・つなぎ先のファイル。パッケージのログは日本語です）。
Play の間は、次のファイルに口の番号（47810〜47819 のどれか）とトークンが書かれます。

```
<あなたのプロジェクト>/Library/AgentApi/endpoint.json
```

Play を抜けると消えます。トークンは Play のたびに新しくなります。

AI を使わずに確かめる時（`endpoint.json` の `port` と `surfaceToken` を写します）:

```
curl -X POST http://127.0.0.1:<port>/v1/hello -H "Authorization: Bearer <surfaceToken>" -H "Content-Type: application/json" -d "{}"
```

Windows PowerShell 5.1 では `curl` が `Invoke-WebRequest` の別名なので、`curl.exe` と打ってください。

### 5. 橋を置く

橋は、パッケージの `Tools~/Bridge/` にあります。Unity は名前が `~` で終わるフォルダを読みません。
また、パッケージを置く場所（PackageCache）のフォルダの名前は版ごとに変わります。そこで、橋を決まった場所へ 1 回写します。
プロジェクトの根で、次を打ちます。

```
python -c "import glob,shutil; shutil.copytree(glob.glob('Library/PackageCache/monster.balloon.automatasystem*/Tools~/Bridge')[0], 'AgentBridge', dirs_exist_ok=True)"
```

`<あなたのプロジェクト>/AgentBridge/` ができ、中に `agent_bridge.py`・`automata_bridge/`・
`examples/tools.json`（見本用の `game_hello` と `game_look` の道具の表）が入ります。
リポジトリを clone して、その `Tools~/Bridge/` をそのまま使ってもかまいません。
パッケージを新しくした後は、もう一度写します。

### 6. AI の道具に橋を教える

Claude Code なら、プロジェクトの根に `.mcp.json` を置きます。

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

- 相対の道は、AI の道具が橋を起こしたフォルダから数えます（Claude Code はプロジェクトのフォルダ）。
  ほかの所で起こす道具なら、絶対の道を書きます。
- macOS と Linux では `"command": "python3"` にします。
- 引数の全部は [`Tools~/Bridge/README.md`](Tools~/Bridge/README.md)（英語）にあります。

### 7. hello と look を呼ぶ

Unity を Play にしたまま、AI に `game_hello`、次に `game_look` を呼ばせます。`game_look` の答えは次のような形です（略してあります）。

```json
{"api":"…","ok":true,"instance":{…},"stamp":{…},"paused":{…},
 "scene":"SampleScene","camera":"Main Camera",
 "visible":[{"name":"Cube","dir":"center","dist":"near"}],"more":0}
```

道具が `no_endpoint` を返す時は、Unity が Play に入っていないか、窓口が開いていません（3 を見てください）。

## 考え方

### 力（何を開くか）

窓口は**既定では閉じています**。開く印（Editor のメニューか、起動の引数 `-agentApi <語>`）が、どの力を開くかを決めます。

| 語 | 開く物 |
|---|---|
| `look` | Look（表の目・表の合図の読みと待ち）と Back |
| `self` | Self（手足: lease・act・止める反射・一時停止・撮る） |
| `back` | Back（裏のトークンと裏の道） |
| `operate` | 登録された全部の力（土台の 3 つと、ゲームが足した力） |
| `closed`・`off` | 何も開かない（ほかの語と混ざっていても、こちらが勝ちます） |

語は `+`・`,`・空白でつなげます（例 `look+self`）。印が空の時や、知らない語がある時は、窓口は閉じたままです。

土台の力の位は `Look = 1 << 0`・`Self = 1 << 1`・`Back = 1 << 3` です。
ゲームは、空いている位（`1 << 2`、または `1 << 4` から上）に自分の力を置き、
`AgentAbilityCatalog.Current.Register(new AgentAbilityDef(...))` で登録します。登録した力の名前は開く印の語になり、`operate` にも入ります。

### 表と裏（2 つのトークン）

- **表**（`AgentFace.Surface`）: 遊ぶ人が、その時の画面と音で知れる物だけ。
  表の道と合図には、キー・ID・座標・本当の HP を入れません。AI は、表だけを見て遊び方を決めます。
- **裏**（`AgentFace.Back`）: ゲームのデータを何でも読めます（開発と調べ物に使います）。裏の道は `/v1/back/` で始まります。
- `endpoint.json` には `surfaceToken` と、Back を開いた時だけ `backToken` が書かれます。
  表のトークンで裏の道を呼ぶと、`403 blind` で断られます。

### 門

どの頼みも、ゲームに届く前に門を通ります。

- `POST` だけ・`Content-Type: application/json`・本文は 64 KiB まで・1 秒に 20 回くらいまで。
- `Host` は `127.0.0.1:<口の番号>` に限ります。`Origin` の付いた頼み（ブラウザ）は断ります。
- `Authorization: Bearer <トークン>`。どちらのトークンかで、表か裏かが決まります。
- 開いていない力の道は `403 not_open` で断ります。世界ができる前に世界の道を呼ぶと `503 world_not_ready` が返ります。
- ゲームは、`AgentRequestGuards.Register(...)`（`IAgentRequestGuard`）で、確かめを 1 つ足せます。

### 道（`IAgentRoute`）

道は 1 本の道筋です。道は、自分の `Path`・`Face`・要る力 `Needs`（どれか 1 つが開いていれば使えます）を名乗ります。
`Handle(AgentCall call, AgentReplyWriter reply)` は主スレッドで呼ばれます。道は自分の欄を `reply.Json` に書きます
（窓口が `api`・`ok`・`instance`・`stamp`・`paused` を先に書いています）。断る時は `reply.Fail(status, code, hint)` を呼びます。

土台の道: `/v1/hello`・`/v1/events`・`/v1/wait`・`/v1/back/events`・`/v1/back/wait`・`/v1/reflex/arm`・
`/v1/reflex/ack`・`/v1/pause`・`/v1/lease`・`/v1/act`・`/v1/capture`・`/v1/back/screen`。
ほかの道（たとえば `/v1/look`）は、ゲームが足します。

### 合図（`IAgentSignalSource`）

合図は、起きた事の短い記録です（例 `world.ready`・`paused`）。合図は帳面に書かれ、
AI は `events` で読むか、`wait` で待ちます（しおりを使うので、読み飛ばしがありません）。
合図の元は、自分の `Kinds` と `Needs` を名乗り、AI がつないでいる間、`Tick` で書きます。
帳面へ直に書くコードは、先に `AgentHost.DeclareSignalKinds(...)` で名乗り、それから `AgentHost.WriteSignal(...)` を呼びます。

### 手足（`IAgentHands`・`IAgentCommand`）

AI は手足を借り（`lease`）、遊ぶ人と同じ仮のパッドを押す命令を走らせます（`act`）。
人がパッドやキーボードに触ると、手足は切れます＝人が先です。
`IAgentCommand.RunAsync` は `UnityEngine.Awaitable<AgentActResult>` を返します。命令は `async Awaitable<AgentActResult>` で書けます。
待たずに結果が決まる命令は `AgentActAwaitable.FromResult(...)` を返します。

### 反射（`IAgentReflexWatch`）

長く遊ばせる前に、AI は止める反射の腕を掛けます（`reflex/arm`）。危ない事が起きると、ゲームが止まり、掛け金が掛かります。
AI は様子を見てから、掛け金を外します（`reflex/ack`）。赤のログの行でも止まります（AI が `ignore` で外した時は止まりません）。
ゲームは、`IAgentReflexWatch` で自分の守りと、動く反射を足します。
ふつうの遊びの中で何度も動く反射（例＝会話の行を送る）は、同じ見張りに任意の `IAgentRoutineReflexes` を付けて名前を並べます。
その反射は、守り `reflex_loop` で数えず、AI が `modes` で名指しした時だけ動きます。

### 撮る（`IAgentCaptureEyes`）

`capture` は、Game の画面の PNG と、**同じフレーム**の表の look を一緒に返します
（Editor: `<プロジェクト>/Logs/AgentApi/captures/`・新しい 200 枚を残します。ビルド: `persistentDataPath` の下）。

## 登録の口

| 何を | interface・呼ぶ関数 | いつ |
|---|---|---|
| 世界（下の物をまとめる入れ物） | `IAgentWorld` を作り、`AgentHost.AttachWorld(world)` | 世界ができた時（道が並びます） |
| 世界を読み込み中（任意） | `AgentHost.NoteWorldLoading(world)` | タイトル・読み込み中 |
| 世界を畳んだ | `AgentHost.DetachWorld(world)` | 世界を壊す時 |
| 道 | `IAgentWorld.Routes` に `IAgentRoute` | `AttachWorld` の時に読みます |
| 合図の元 | `IAgentWorld.SignalSources` に `IAgentSignalSource` | AI がつないでいる間、つながります |
| 手足 | `IAgentWorld.Hands` に `IAgentHands`・`IAgentCommand`（無ければ `null`） | 手足を借りている間に使います |
| 反射の見張り | `IAgentWorld.ReflexWatch` に `IAgentReflexWatch`（無ければ `null`） | 腕を掛けている間、回ります |
| 撮る口 | `IAgentWorld.CaptureEyes` に `IAgentCaptureEyes`（無ければ `null`） | 撮るたびに呼ばれます |
| ゲームの力 | `AgentAbilityCatalog.Current.Register(new AgentAbilityDef(...))` | `[RuntimeInitializeOnLoadMethod]` の `AfterAssembliesLoaded` か `BeforeSceneLoad` |
| 頼みの確かめを 1 つ足す | `AgentRequestGuards.Register(...)`（`IAgentRequestGuard`） | 上と同じ |
| hello に出す短い名乗り | `AgentProjectLabel.Register(...)` | 上と同じ |

終わりの 3 つの登録は、Play の頭（`SubsystemRegistration`）で消えます。開く印は `AfterSceneLoad` で読みます。
そのため、`AfterAssembliesLoaded` か `BeforeSceneLoad` で登録します。

ゲームの asmdef の `references` に `Monster.Balloon.AutomataSystem` を足します（パッケージのアセンブリは自動では参照されません）。
いちばん小さい、動く世界は `Samples~/MinimalWorld/` の見本にあります。

## Sign in with ChatGPT（任意）

アセンブリ `Monster.Balloon.AutomataSystem.ChatGpt`（`Runtime/ChatGpt/`）を使うと、遊ぶ人が自分の ChatGPT のアカウントで
ログインし、**遊ぶ人の ChatGPT のプランの使える量**で、ゲームの中の AI を動かせます。会話（`ChatGptSession`）は
`IAgentToolHost` の道具を呼べます。外の AI に渡す道具を、そのままゲームの中の AI にも渡せます。

何ができるか:

- ログイン＝OAuth 2.0 と PKCE。遊ぶ人のブラウザで行い、戻りは `http://127.0.0.1:<port>/auth/callback` で受けます
  （port は既定 1455・塞がっていれば空いた port）。client secret も、前もっての登録も要りません。初回のログインで
  この端末が登録され、発行された client_id を保存して、以後はそれを使います。
- ID トークンを確かめます（発行元の JWKS による RS256 の署名・`iss`・`aud`・`exp`・`nonce`）。
- アクセストークンは、期限の前に refresh します。refresh は 1 本ずつに並べ、替わった refresh_token を毎回保存します。
- モデル一覧（`ListModelsAsync`・`visibility == "list"` の物だけ。モデルの名前は決め打ちしません）。
- `/v1/responses` での会話（流れ・function tools・`store: false`・考える深さは任意）。
- 失敗は `ChatGptException` で返ります。訳の印は `ChatGptErrorKind`（`UsageLimit`・`NotEligible`・`Unauthorized`・
  `Unavailable`・`Unsupported`・`Interrupted`・`Network`・`Cancelled` など）です。

どこまで確かめたか: 本物の OpenAI では、許可の画面・初回の登録・ループバックの戻りまでを確かめました。トークンの交換から先は、
OpenAI が公開している決まりどおりに作った偽のサーバー（`Tests/Editor/ChatGpt/`）で確かめました。有料の ChatGPT のプランで試した方の報告を待っています。

### 使える相手

OpenAI の説明書から: 「ChatGPT plan usage is available to open-source projects, personal projects that run locally,
and selected private apps.」 有料のアプリ・サーバーで動くアプリ・中身を公開しないアプリは、
<https://openai.com/form/sign-in-with-chatgpt-interest/> で申し込み、選ばれてから出します。

**ゲームの中身を公開しない場合**は、申し込みが通るまで、製品のビルドの Scripting Define Symbols に
`AUTOMATA_SYSTEM_NO_CHATGPT` を入れます。この define で、ChatGPT のアセンブリが丸ごとビルドから外れます
（`AUTOMATA_SYSTEM_STRIP` でも外れます）。

### いちばん小さい使い方

asmdef の `references` に `Monster.Balloon.AutomataSystem` と `Monster.Balloon.AutomataSystem.ChatGpt` を足します。

```csharp
using Monster.Balloon.AutomataSystem.ChatGpt;

// StorageDirectory を決めない時は、主スレッドで作る（Application.persistentDataPath を読むため）。
var account = new ChatGptAccount(new ChatGptSettings("My Game"));

// 「Continue with ChatGPT」のボタン: ブラウザを開き、遊ぶ人がログインを済ませるまで待つ。
ChatGptAccountState state = await account.SignInAsync();
if (state == ChatGptAccountState.PlanNotGranted)
{
    state = await account.SignInAsync(requestPlanConsent: true); // プランの同意を取り直す（prompt=consent）
}

if (account.TakePlanNotice()) { /* 「You're using your ChatGPT plan」を 1 回出す */ }

IReadOnlyList<ChatGptModelInfo> models = await account.ListModelsAsync();
var session = new ChatGptSession(account, models[0].Slug, "You are a shopkeeper in this game.", myToolHost);

try
{
    ChatGptTurnResult turn = await session.RunTurnAsync("Hello!", e =>
    {
        // 主スレッドでないスレッドで呼ばれる: TextDelta・ToolCall・ToolResult・TurnEnd。
    });
    Debug.Log(turn.Text);
}
catch (ChatGptException e) when (e.Kind == ChatGptErrorKind.UsageLimit)
{
    // 「Manage usage」（e.ActionUrl）を主な行動にする。使えるようになる時刻は推し量らない。
}

await account.SignOutAsync(); // refresh_token を取り消して、トークンを消す
```

注意:

- どのメソッドも、どのスレッドから呼んでもよく、主スレッドを止めません。知らせ（`StateChanged`・番の出来事）は
  主スレッドでないスレッドで来ます。Unity の物に触る前に、主スレッドへ移します。
- `ChatGptSettings.OpenBrowser` は、主スレッドで `Application.OpenURL` を呼んで頁を開きます。差し替えるか、`null` にして
  `ChatGptSignIn.AuthorizeUrl`（`BeginSignInAsync` の返り）を自分で見せます。
- 1 つの番で頼むのは `MaxRequestsPerTurn` 回まで（既定 24）です。失敗した番は会話に残らないので、同じ言葉で送り直せます。
- サーバーが `include: ["reasoning.encrypted_content"]` を断ったら、会話は `CarryReasoning` を切り、考えの項目を落として、
  1 回だけ送り直します。

### 見せ方

パッケージは UI を持ちません。ゲームの UI で、次の決まりを守ります。

- ログインのボタンの字は **「Continue with ChatGPT」**。
- 初回のログインの後に **「You're using your ChatGPT plan」** を 1 回出します（`TakePlanNotice()` が 1 回だけ true を返します）。
- 使っている間は **「Using ChatGPT plan」** と、<https://chatgpt.com/settings/usage> への **「Manage usage」** を出します
  （`ChatGptSettings.ManageUsageUrl`）。
- `UsageLimit` の時は **Manage usage** を主な行動にします。`NotEligible` の時は、ログインし直しを求めません。

### 保存する物と場所

ファイルは `account.json` の 1 つです。場所は既定で `Application.persistentDataPath/AutomataSystem/ChatGpt/`
（`ChatGptSettings.StorageDirectory` で変えられます）。一時ファイルに書いてから置き換えます。

中身: 乱数の端末の印（`ext_agent_host_id`・`urn:uuid:...`）・発行された client_id・アクセストークンと refresh_token・
許された scope・期限・アカウントの `sub`・プランの種類。ID トークン・メールアドレス・名前は**持ちません**。
トークンはログにも例外の字にも出しません。ログアウトするとトークンを消し、端末の印と client_id は残します。

## 安全

- **製品のビルドには窓口がありません。** HTTP のアセンブリ（`Monster.Balloon.AutomataSystem.Http`）は
  `UNITY_EDITOR || DEVELOPMENT_BUILD` の時だけ作られます。Editor と開発ビルドの外では、Back の力も外れます。
  パッケージを丸ごとビルドから外すには、Scripting Define Symbols に `AUTOMATA_SYSTEM_STRIP` を足します。
- **開発ビルド**は、起動の引数 `-agentApi <語>`（例 `-agentApi look`）がある時だけ開きます。
  つなぎ先のファイルは `<Application.persistentDataPath>/AgentApi/endpoint.json` です。汎用の橋は
  `<プロジェクト>/Library/AgentApi/` しか見ないので、ビルドへつなぐには差し込みが要ります（橋の README を見てください）。
- **同じ PC の中だけ。** 窓口は `127.0.0.1`（口の番号 47810〜47819）で待ちます。ほかの機械からは届きません。
- **トークン**は 32 バイトの乱数で、Play のたびに新しくなります。置き場は `endpoint.json`（Editor は `Library/` の下・
  ビルドは `persistentDataPath` の下）だけです。このファイルを commit したり人に渡したりしないでください。橋はトークンを字に出しません。
- **パッケージが書くファイル**: `UserSettings/AgentApi/open`（開く印）・`Library/AgentApi/endpoint.json` と `state.json`
  （Play の間）・`Logs/AgentApi/captures/*.png`。Editor で裏の道が使われている間は、Game の画面を MP4 に録って
  `<ビデオのフォルダ>/<productName>/AgentApi/` にも置きます（3 GB まで・古い物から消します）。

## 入っている物

| フォルダ | アセンブリ | 中身 |
|---|---|---|
| `/` | `Monster.Balloon.AutomataSystem` | 窓口の芯・門（トークン・力・回数の上限）・合図の帳面（events・wait）・止める反射・撮る受付 |
| `Http/` | `Monster.Balloon.AutomataSystem.Http` | ローカルの HTTP の窓口（自前の `Socket`・Editor と開発ビルドだけ） |
| `Editor/` | `Monster.Balloon.AutomataSystem.Editor` | `Tools/Agent API` のメニュー・Game の窓の大きさ・裏の道具を使った間の録画 |
| `Tests/Editor/` | `Monster.Balloon.AutomataSystem.Tests` | 門・帳面・開く印のテスト |
| `Runtime/ChatGpt/` | `Monster.Balloon.AutomataSystem.ChatGpt` | Sign in with ChatGPT と、遊ぶ人のプランでの会話（任意） |
| `Tests/Editor/ChatGpt/` | `Monster.Balloon.AutomataSystem.ChatGpt.Tests` | 偽の OpenAI のサーバーを相手にしたテスト |
| `Samples~/MinimalWorld/` | `Monster.Balloon.AutomataSystem.Samples.MinimalWorld` | 見本（Package Manager から入れます） |
| `Tools~/Bridge/` | （Python） | MCP の橋・橋の自己試験・`examples/tools.json` |

## ライセンス

MIT です。[LICENSE.md](LICENSE.md) を見てください。
依存: [Newtonsoft.Json](https://github.com/JamesNK/Newtonsoft.Json)（MIT）。Unity の `com.unity.nuget.newtonsoft-json` パッケージを通して入ります。
