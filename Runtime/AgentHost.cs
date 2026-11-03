using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Monster.Balloon.AutomataSystem
{
    /// <summary>
    /// The long-lived host of the Game Agent API: opens and closes the endpoint, tracks the world, and lays out the routes.
    /// Game Agent API の消えない係。窓口の開け閉め・世界の出入り・道の並べ方を持つ（この係は Play の間ずっと居る）。
    /// <para>
    /// ⭐ この係は DI の外に居る。世界を作り直す時、ゲームが DI の根を消すことがあるので、
    ///    DI の中に置くと、窓口もトークンも作り直しのたびに消える。世界ごとの係（Binder）は DI の中に居て、世界の出入りをここへ知らせる。
    /// ⭐ 窓口が閉じている間（既定）は、この係は待ち受けも列も作らない。世界の出入りでは、この係は static の値を数個書くだけ。
    /// ⚠ Domain Reload を切っていてもよいように、この係は Play の頭（SubsystemRegistration）で前の Play の窓口を止めてから static を戻す。
    /// </para>
    /// <para>
    /// 役目の置き場: 列・鍵・道の表＝<see cref="AgentHostSession"/>／返事の組み立て＝<see cref="AgentReplyComposer"/>／
    /// 列を回す係＝<see cref="AgentHostPump"/>／hello＝<see cref="AgentHelloRoute"/>／合図の帳面と元＝<see cref="AgentSignalHub"/>／
    /// 表の札＝<see cref="AgentTagTable"/>／中の口（HTTP を通らない窓口）＝AgentHost.InProcess.cs／
    /// Editor の一時停止＝AgentHost.Pause.cs／止める反射＝<see cref="AgentReflex"/>／手足の借りと命令＝<see cref="AgentLeaseDesk"/>／
    /// 撮る＝<see cref="AgentCaptureDesk"/>。
    /// </para>
    /// </summary>
    public static partial class AgentHost
    {
        /// <summary>The build stage reported by hello. 今どの段まで作ったか（hello で返す）。</summary>
        public const int BuiltStage = 7;

        /// <summary>
        /// The suggested interval, in milliseconds, between heavy polls by a signal source (four times a second).
        /// 合図の元が、重い見張り（画面の字を読むなど）を回す間の目安（ミリ秒＝1 秒に 4 回）。
        /// </summary>
        public const int SignalPollIntervalMs = 250;

        internal const int FirstPort = 47810;
        internal const int PortCount = 10;
        internal const int RequestsPerSecond = 20;
        internal const int MaxCallsPerFrame = 8;
        internal static readonly TimeSpan MainThreadTimeout = TimeSpan.FromSeconds(5);

        /// <summary>Host が自分で書く合図（hello で並べる）。missed は、どの元も書く「見落とした」の印。</summary>
        internal static readonly AgentSignalKind[] HostSignalKinds =
        {
            new AgentSignalKind("world.ready", AgentFace.Surface),
            new AgentSignalKind("world.closing", AgentFace.Surface),
            new AgentSignalKind("paused", AgentFace.Surface),
            new AgentSignalKind("resumed", AgentFace.Surface),
            new AgentSignalKind("missed", AgentFace.Surface),
            new AgentSignalKind("log.red", AgentFace.Back),
        };

        /// <summary>
        /// 止める反射と動く反射の合図。表には守りと反射の名前だけ・裏には見張りが読んだ値も載る（reflex.tripped・reflex.ignored・reflex.acted）。
        /// ⭐ 反射の道は A の手（self）の力でしか開かない＝hello も self の時だけ並べる（書かれない合図を見せないため）。
        /// </summary>
        internal static readonly AgentSignalKind[] ReflexSignalKinds =
        {
            new AgentSignalKind("reflex.armed", AgentFace.Surface),
            new AgentSignalKind("reflex.disarmed", AgentFace.Surface),
            new AgentSignalKind("reflex.tripped", AgentFace.Surface),
            new AgentSignalKind("reflex.acked", AgentFace.Surface),
            new AgentSignalKind(AgentReflexIgnore.Kind, AgentFace.Surface),
            new AgentSignalKind(AgentMovingReflex.ActedKind, AgentFace.Surface),
        };

        private static readonly IAgentRoute Hello = new AgentHelloRoute();
        private static readonly IAgentRoute ReflexArm = new AgentReflexArmRoute();
        private static readonly IAgentRoute ReflexAck = new AgentReflexAckRoute();
        private static readonly IAgentRoute Pause = new AgentPauseRoute();
        private static readonly IAgentRoute Lease = new AgentLeaseRoute();
        private static readonly IAgentRoute Act = new AgentActRoute();
        private static readonly IAgentRoute Capture = new AgentCaptureRoute();
        private static readonly IAgentRoute ScreenSize = new AgentScreenRoute();
        private static readonly IAgentRoute SurfaceEvents = new AgentEventsRoute(AgentFace.Surface);
        private static readonly IAgentRoute SurfaceWait = new AgentWaitRoute(AgentFace.Surface);
        private static readonly IAgentRoute BackEvents = new AgentEventsRoute(AgentFace.Back);
        private static readonly IAgentRoute BackWait = new AgentWaitRoute(AgentFace.Back);
        private static readonly HashSet<string> ReportedErrors = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>ゲームの側が帳面へ直に書くと名乗った合図（名前→顔と要る力）。Play の頭で空に戻す。</summary>
        private static readonly Dictionary<string, DeclaredSignal> DeclaredSignals =
            new Dictionary<string, DeclaredSignal>(StringComparer.Ordinal);

        private static AgentHostSession session;

        private static IAgentWorld world;
        private static volatile AgentWorldState worldState;
        private static int epoch;

        /// <summary>Whether the endpoint is open. 窓口が開いているか。</summary>
        public static bool IsOpen => session != null;

        /// <summary>The abilities opened on the endpoint (None while closed). 窓口で開いた力の組（閉じていれば None）。</summary>
        public static AgentAbilities Abilities => session != null ? session.Abilities : AgentAbilities.None;

        /// <summary>The word the endpoint was opened with ("closed" while closed). 開いた印の字（例 "operate"・"look"）。閉じていれば "closed"。</summary>
        public static string OpenWord => session != null ? session.OpenWord : "closed";

        /// <summary>
        /// Whether the given ability is open right now.
        /// その力が今開いているか（例 <c>AgentHost.IsOpenFor(AgentAbilities.Self)</c>・ゲームが登録した力の位も渡せる）。
        /// ⭐ 窓口が閉じている間は、どの力も開いていない（ゲームの係やキーは、何もしない）。
        /// </summary>
        public static bool IsOpenFor(AgentAbilities ability) => session != null && (session.Abilities & ability) != 0;

        /// <summary>How many times a world has become ready in this Play. 世界ができた回数（Play の頭で 0 に戻る）。古い世界の答えを見分けるのに使う。</summary>
        public static int Epoch => epoch;

        /// <summary>The current state of the world. 今の世界の様子。</summary>
        public static AgentWorldState WorldState => worldState;

        internal static AgentHostSession CurrentSession => session;

        internal static IAgentWorld CurrentWorld => world;

        // ── 世界の出入り（世界ごとの係が呼ぶ） ─────────────────────────────

        /// <summary>Tells the host that a world binder exists but the world is still loading. 世界の係ができた（まだ世界はできていない＝タイトル・読み込み中）。</summary>
        public static void NoteWorldLoading(IAgentWorld loading)
        {
            if (loading == null)
            {
                throw new ArgumentNullException(nameof(loading));
            }

            // ⭐ 作り直しでは、新しい世界の係が、古い世界を畳む知らせより先に名乗ることがある（その時、古い知らせは捨てる）。
            //    その時は、ここで古い世界を畳んだと書き、古い世界の元を外す。
            if (worldState == AgentWorldState.Ready && !ReferenceEquals(world, loading) && session != null)
            {
                session.Signals.Write(AgentFace.Surface, "world.closing", false);
                session.Reflex.OnWorldClosing();
                session.Hands.OnWorldClosing();
                session.Signals.DetachSources();
            }

            world = loading;
            worldState = AgentWorldState.Loading;
            RebuildRoutes(null);
        }

        /// <summary>Tells the host that the world is ready. 世界ができた。Host は epoch を 1 上げ、この世界の道を並べる。</summary>
        public static void AttachWorld(IAgentWorld ready)
        {
            if (ready == null)
            {
                throw new ArgumentNullException(nameof(ready));
            }

            world = ready;
            epoch++;

            // ⭐ 道を先に並べてから Ready にする。窓口のスレッドは Ready を見て「無い道＝404」と決めるので、
            //    逆の順だと、世界の道がまだ無い一瞬に、在るはずの道へ 404 no_route を返す。
            //    畳む時（NoteWorldLoading・DetachWorld）は逆に、先に Ready を外してから道を外す（その一瞬は 503 で答える）。
            RebuildRoutes(ready);
            worldState = AgentWorldState.Ready;
            if (session != null)
            {
                session.Signals.Write(AgentFace.Surface, "world.ready", false);
                session.Reflex.OnWorldReady(ready);
                Debug.Log("[AgentApi] 世界ができた（epoch=" + epoch + "）");
            }
        }

        /// <summary>Tells the host that the world was torn down. 世界を畳んだ。Host はこの世界の道を外す。</summary>
        public static void DetachWorld(IAgentWorld closing)
        {
            // ⭐ 作り直しでは、新しい世界の係が先に名乗ることがある。その時、古い世界の知らせは捨てる。
            if (closing == null || !ReferenceEquals(world, closing))
            {
                return;
            }

            // ⭐ 畳む合図を書いてから、元を外す（畳まれる世界の物が、まだ壊れていないうちに購読をやめる）。
            if (session != null && worldState == AgentWorldState.Ready)
            {
                session.Signals.Write(AgentFace.Surface, "world.closing", false);
            }

            session?.Reflex.OnWorldClosing();
            session?.Hands.OnWorldClosing();
            session?.Signals.DetachSources();
            world = null;
            worldState = AgentWorldState.None;
            RebuildRoutes(null);
            if (session != null)
            {
                Debug.Log("[AgentApi] 世界を畳んだ（epoch=" + epoch + "）");
            }
        }

        // ── ゲームの側が使う口（札・帳面へ直に書く・Host の子） ───────────

        /// <summary>
        /// Returns a short public tag for an instance ID (null while closed).
        /// 個体の ID に、表で見せてよい札（6 字）を付けて返す。窓口が閉じていれば null。
        /// ⭐ 同じ世界（epoch）の間は同じ札。世界を作り直すと札は全部替わる。札は Play ごとの秘密の塩から作るので、札から ID は当てられない。
        /// ⚠ 主スレッドから呼ぶ。
        /// </summary>
        public static string TagFor(string instanceId) => session?.Tags.TagFor(instanceId, epoch);

        /// <summary>
        /// Turns a tag issued in the current world back into its instance ID.
        /// 札を個体の ID に戻す。今の世界で <see cref="TagFor"/> が出した札でなければ false（前の世界の札・知らない札・窓口が閉じている）。
        /// ⚠ 主スレッドから呼ぶ。
        /// </summary>
        public static bool TryResolveTag(string tag, out string instanceId)
        {
            instanceId = null;
            AgentHostSession s = session;
            return s != null && s.Tags.TryResolve(tag, epoch, out instanceId);
        }

        /// <summary>
        /// Declares signal kinds that the game writes to the ledger directly.
        /// 帳面へ直に書く合図の名前を名乗る（hello で並べ、<see cref="WriteSignal"/> で書けるようにする）。
        /// ⭐ 名乗りは Play の間ずっと残る（世界を作り直しても消えない）。同じ名前を名乗り直すと、後の方で上書きする。
        /// </summary>
        /// <param name="needs">この合図を帳面へ書くのに要る力（どれか 1 つ）。窓口がその力を開いていなければ、書こうとしても捨てる。</param>
        /// <param name="kinds">合図の名前と顔。</param>
        public static void DeclareSignalKinds(AgentAbilities needs, IReadOnlyList<AgentSignalKind> kinds)
        {
            if (kinds == null)
            {
                throw new ArgumentNullException(nameof(kinds));
            }

            foreach (AgentSignalKind kind in kinds)
            {
                if (string.IsNullOrEmpty(kind.Name))
                {
                    throw new ArgumentException("合図の名前が空。", nameof(kinds));
                }

                DeclaredSignals[kind.Name] = new DeclaredSignal(kind.Face, needs);
            }
        }

        /// <summary>
        /// Writes one declared signal to the ledger directly; returns true when written.
        /// 合図を 1 つ帳面へ直に書く（合図の元を作らない係のための口）。書けたら true。
        /// <para>
        /// ⭐ 窓口が閉じている・その合図の力が開いていない・裏の帳面が無い（裏の合図）時は、書かずに false を返す（知らせない）。
        /// ⭐ 合図の元と違い、使う側が黙っている間も書く（預かりの切れ目のように、後から読みたい物のため）。
        /// ⚠ 名乗っていない名前と、名乗りと違う顔は、書かずに 1 回だけ知らせる（<see cref="DeclareSignalKinds"/>）。
        /// ⚠ 表の合図の中身には、画面に描かれている物だけを入れる（キー・ID・座標・本当の HP を入れない）。
        /// ⚠ 主スレッドから呼ぶ。
        /// </para>
        /// </summary>
        public static bool WriteSignal(AgentFace face, string kind, JObject data = null)
        {
            AgentHostSession s = session;
            if (s == null || string.IsNullOrEmpty(kind))
            {
                return false;
            }

            if (!DeclaredSignals.TryGetValue(kind, out DeclaredSignal declared) || declared.Face != face)
            {
                ReportOnce("undeclared-signal|" + kind + "|" + face,
                    "[AgentApi] 名乗っていない合図か、名乗りと顔が違う合図なので、帳面に書かない（AgentHost.DeclareSignalKinds で名乗る）: "
                    + kind + " face=" + face);
                return false;
            }

            if (!s.Abilities.Allows(declared.Needs) || (face == AgentFace.Back && s.Signals.Back == null))
            {
                return false;
            }

            s.Signals.Write(face, kind, false, data);
            return true;
        }

        /// <summary>
        /// Writes one back signal and returns an evidence ID that points to it (null when not written).
        /// 裏の合図を 1 つ帳面へ書き、その合図を指す証拠の ID（<c>&lt;playId&gt;:b&lt;番号&gt;</c>）を返す（書けなければ null）。
        /// ⭐ この口は、裏の目で読んだ物を、ゲームの試験の道具が判定の証拠に挙げられるようにする。
        /// ⚠ 名乗っていない名前・裏でない名前は書かずに 1 回だけ知らせる（<see cref="WriteSignal"/> と同じ）。⚠ 主スレッドから呼ぶ。
        /// </summary>
        public static string WriteBackEvidence(string kind, JObject data = null)
        {
            AgentHostSession s = session;
            if (s == null || string.IsNullOrEmpty(kind))
            {
                return null;
            }

            if (!DeclaredSignals.TryGetValue(kind, out DeclaredSignal declared) || declared.Face != AgentFace.Back)
            {
                ReportOnce("undeclared-signal|" + kind + "|" + AgentFace.Back,
                    "[AgentApi] 名乗っていない合図か、裏でない合図なので、帳面に書かない（AgentHost.DeclareSignalKinds で名乗る）: " + kind);
                return null;
            }

            if (!s.Abilities.Allows(declared.Needs) || s.Signals.Back == null)
            {
                return null;
            }

            long seq = s.Signals.WriteBack(kind, data);
            return seq > 0 ? s.PlayId + ":b" + seq.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        }

        /// <summary>
        /// Places a single child component T under the host object and returns it (null while closed).
        /// Host の GameObject の子に T を 1 つだけ置いて返す（もう在れば、それを返す）。窓口が閉じていれば null を返し、何も作らない。
        /// ⭐ 子は Host と一緒に消える（窓口を閉じた時・Play を抜けた時）。置く側は DontSave も残り物の片付けも書かなくてよい。
        /// ⚠ 子は世界の作り直しを越えて残る＝子に世界の物を握らせない。⚠ 主スレッドから呼ぶ。
        /// </summary>
        public static T AttachChild<T>(string name) where T : Component
        {
            GameObject host = session != null ? session.HostObject : null;
            if (host == null)
            {
                return null;
            }

            T found = host.GetComponentInChildren<T>(true);
            if (found != null)
            {
                return found;
            }

            var child = new GameObject(string.IsNullOrEmpty(name) ? typeof(T).Name : name);
            child.transform.SetParent(host.transform, false);
            return child.AddComponent<T>();
        }

        /// <summary>hello に並べる、名乗られた合図の名前（開いた力で書ける物だけ・表の顔には表の合図だけ）。</summary>
        internal static void CollectDeclaredKinds(AgentAbilities abilities, AgentFace face, HashSet<string> into)
        {
            foreach (KeyValuePair<string, DeclaredSignal> pair in DeclaredSignals)
            {
                if (abilities.Allows(pair.Value.Needs) && (pair.Value.Face == AgentFace.Surface || face == AgentFace.Back))
                {
                    into.Add(pair.Key);
                }
            }
        }

        /// <summary>窓口を閉じる（何度呼んでもよい）。</summary>
        internal static void Shutdown(string reason)
        {
            AgentHostSession s = session;
            if (s == null)
            {
                return;
            }

            session = null;
            Application.quitting -= OnQuitting;
            s.Close();
            if (s.EndpointPath != null)
            {
                AgentEndpointFile.TryDelete(s.EndpointPath);
            }

            // ⭐ 止まっているかの記録は消さない（Play の途中で窓口が閉じても、Editor は止まりうる）。窓口の無い姿で書き直してもらう。
            NoteStateChanged();

            GameObject go = s.HostObject;
            s.HostObject = null;
            if (go != null)
            {
                if (Application.isPlaying)
                {
                    Object.Destroy(go);
                }
                else
                {
                    Object.DestroyImmediate(go);
                }
            }

            Debug.Log("[AgentApi] 窓口を閉じた（" + reason + "・playId=" + s.PlayId + "）");
        }

        /// <summary>
        /// 同じ知らせを 1 回だけ赤で鳴らす。
        /// ⚠ Claude 用の Editor は Error Pause で止まるので、赤は「直すべき物」だけにし、同じ物を繰り返さない。
        /// ⭐ 裏の無い窓口（back を開かない窓口・製品版）では、黄に落とす。遊んでいる人の Play を赤で止めないため。
        /// </summary>
        internal static void ReportOnce(string key, string message)
        {
            if (!ReportedErrors.Add(key))
            {
                return;
            }

            AgentHostSession s = session;
            if (s != null && s.BackToken == null)
            {
                Debug.LogWarning(message);
            }
            else
            {
                Debug.LogError(message);
            }
        }

        // ── Play の出入り ────────────────────────────────────────────────

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetForPlay()
        {
            // ① 前の Play の窓口が残っていたら止める。static を戻す前に止める（戻すと、止める手がかりが消える）。
            Shutdown("play-start");

            // ② 世界の記録を戻す。⚠ pauseReader と pauseWriter は戻さない（Editor の係が、スクリプトを読み込んだ時に 1 回だけ差す）。
            world = null;
            worldState = AgentWorldState.None;
            epoch = 0;
            ReportedErrors.Clear();

            // ③ ゲームの側の名乗りを外す（係は、世界ができた時に名乗り直す）。
            DeclaredSignals.Clear();

            // ④ ゲームの登録（力の書き付け・頼みを通すか決める口・Editor の名乗り）を土台だけに戻す。
            //    ⭐ ゲームは、この後の AfterAssembliesLoaded か BeforeSceneLoad で登録し直す＝印を読む AfterSceneLoad（OpenIfFlagged）より前。
            //    ⚠ 同じ SubsystemRegistration で登録すると、順が決まらず、ここで消えることがある。
            AgentAbilityCatalog.Current.ResetToBuiltIns();
            AgentRequestGuards.ResetForPlay();
            AgentProjectLabel.ResetForPlay();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void OpenIfFlagged()
        {
            if (session != null)
            {
                return;
            }

            AgentAbilities abilities = AgentOpenFlag.Read(out string word);
            if (abilities == AgentAbilities.None)
            {
                return;
            }

            Open(abilities, word);
        }

        private static void OnQuitting() => Shutdown("quit");

        private static void Open(AgentAbilities abilities, string word)
        {
            IAgentTransport transport = AgentTransports.TryCreate();
            if (transport == null)
            {
                Debug.LogWarning("[AgentApi] 開く印はあるが、このビルドに窓口の部品（Http のアセンブリ）が無い。窓口は開かない。");
                return;
            }

            var s = new AgentHostSession(abilities, word);
            session = s;
            RebuildRoutes(worldState == AgentWorldState.Ready ? world : null);

            if (!transport.TryStart(s, FirstPort, PortCount, out string why))
            {
                session = null;
                s.Close();
                transport.Dispose();
                Debug.LogWarning("[AgentApi] 窓口を開けなかった: " + why);
                return;
            }

            s.Transport = transport;
            s.SetKeys(new AgentGateKeys(
                "127.0.0.1:" + transport.Port.ToString(CultureInfo.InvariantCulture), s.SurfaceToken, s.BackToken));
            StartSession(s);

            // ⭐ 専用のつなぎ先を持つ力だけの窓口は、別の名前のつなぎ先に書く（ふだんの橋は endpoint.json しか読まない＝その窓口を拾わない）。
            s.EndpointPath = AgentEndpointFile.PathFor(s.Abilities);
            AgentEndpointFile.Write(
                s.EndpointPath, transport.Port, s.SurfaceToken, s.BackToken, s.ProcessId, s.PlayId, s.OpenWord, s.Abilities);

            // ⭐ 止まっているかの記録も、開いた時の姿（playId）で書き直してもらう。
            NoteStateChanged();
#if UNITY_EDITOR
            // ⭐ 使わない方のつなぎ先に前の Play の残りがあれば消す（橋が、もう居ない窓口を読んで迷わないように）。
            //    Editor だけ＝Library はプロジェクトに 1 つ。ビルドの persistentDataPath は、同時に動くほかのビルドと同じ所なので消さない。
            foreach (string other in AgentEndpointFile.OtherPathsFor(s.Abilities))
            {
                AgentEndpointFile.TryDelete(other);
            }
#endif

            Debug.Log("[AgentApi] 窓口を開いた（127.0.0.1:" + transport.Port + "・" + s.OpenWord + "＝"
                + string.Join(",", s.Abilities.ToWireList()) + "・playId=" + s.PlayId + "）。つなぎ先＝" + s.EndpointPath);
        }

        /// <summary>
        /// 開いた窓口を動かし始める（HTTP の窓口と、待ち受けの無い窓口で同じ）。Host の GameObject・列を回す係・帳面の頭・終わる時の片付け。
        /// </summary>
        private static void StartSession(AgentHostSession s)
        {
            var go = new GameObject("AgentHost (Game Agent API)");
            Object.DontDestroyOnLoad(go);
            var behaviour = go.AddComponent<AgentHostBehaviour>();
            behaviour.Destroyed = () =>
            {
                if (ReferenceEquals(session, s))
                {
                    Shutdown("host-destroyed");
                }
            };
            s.HostObject = go;

            // ⭐ 列を回すのは PostLateUpdate（そのフレームの LateUpdate が全部済み、値がそろった後）。
            //    列を回す係は、PlayerLoop に自分の system が無い時だけ差し込む（Play を何回入っても 1 つ）。
            AgentHostPump.EnsureInstalled();

            // ⭐ 開いた時の姿を帳面の頭に書く（Play の途中で開いた時も、使う側が今の姿から読めるように）。
            s.Signals.StartRedLog();
            if (worldState == AgentWorldState.Ready)
            {
                s.Signals.Write(AgentFace.Surface, "world.ready", true);
            }

            string pause = ReadPause();
            if (pause != "none" && pause != "unknown")
            {
                s.Signals.Write(AgentFace.Surface, "paused", true, new JObject { ["by"] = pause });
            }

            Application.quitting -= OnQuitting;
            Application.quitting += OnQuitting;
        }

        // ── 道の並べ方 ─────────────────────────────────────────────────

        /// <param name="readyWorld">道を並べる世界（できていなければ null＝世界の道を並べない）。</param>
        private static void RebuildRoutes(IAgentWorld readyWorld)
        {
            AgentHostSession s = session;
            if (s == null)
            {
                return;
            }

            var table = new RouteTableBuilder(s.Abilities);
            table.Add(Hello);

            // ⭐ 止める反射と一時停止の道は、世界が無くても並べる（腕は世界ができた時に見張りを始める）。
            table.Add(ReflexArm);
            table.Add(ReflexAck);
            table.Add(Pause);

            // ⭐ 手足の道も世界が無くても並べる（世界ができるまでは 503 world_not_ready で断る）。
            table.Add(Lease);
            table.Add(Act);

            // ⭐ 撮る道も世界が無くても並べる（世界ができるまでは 503 world_not_ready で断る）。
            table.Add(Capture);

            // ⭐ 画面の大きさ（Game ビュー）を替える道も世界が無くても並べる（裏だけ・Editor だけ＝ビルドでは 501 editor_only）。
            table.Add(ScreenSize);

            // ⭐ 合図の道は世界が無くても並べる（世界を作り直す間も、使う側は world.ready を待てる）。
            table.Add(SurfaceEvents);
            table.Add(SurfaceWait);
            if (s.Signals.Back != null)
            {
                table.Add(BackEvents);
                table.Add(BackWait);
            }

            if (readyWorld != null && readyWorld.Routes != null)
            {
                foreach (IAgentRoute route in readyWorld.Routes)
                {
                    table.Add(route);
                }
            }

            s.SetRoutes(table.Open, table.NotOpen);
        }

        /// <summary>名乗られた合図 1 つの顔と、書くのに要る力。</summary>
        private readonly struct DeclaredSignal
        {
            public DeclaredSignal(AgentFace face, AgentAbilities needs)
            {
                this.Face = face;
                this.Needs = needs;
            }

            public AgentFace Face { get; }

            public AgentAbilities Needs { get; }
        }

        /// <summary>道の表を組む係（開いた力で使える道と、在るが開いていない道の名を分ける）。</summary>
        private sealed class RouteTableBuilder
        {
            private readonly AgentAbilities abilities;

            public RouteTableBuilder(AgentAbilities abilities)
            {
                this.abilities = abilities;
            }

            public Dictionary<string, IAgentRoute> Open { get; } = new Dictionary<string, IAgentRoute>(StringComparer.Ordinal);

            public HashSet<string> NotOpen { get; } = new HashSet<string>(StringComparer.Ordinal);

            public void Add(IAgentRoute route)
            {
                if (route == null)
                {
                    return;
                }

                string path = route.Path;
                bool wellFormed = path != null && path.StartsWith(AgentGate.RoutePrefix, StringComparison.Ordinal);
                bool backPath = wellFormed && path.StartsWith(AgentGate.BackPrefix, StringComparison.Ordinal);
                if (!wellFormed || backPath != (route.Face == AgentFace.Back))
                {
                    ReportOnce("shape|" + path, "[AgentApi] 道の名と顔が食い違うので並べない（/v1/back/ の道は Back・ほかは Surface）: "
                        + path + " face=" + route.Face + " type=" + route.GetType().Name);
                    return;
                }

                if (this.Open.ContainsKey(path) || this.NotOpen.Contains(path))
                {
                    ReportOnce("dup|" + path, "[AgentApi] 同じ道が 2 つあるので、後の方を並べない: " + path + " type=" + route.GetType().Name);
                    return;
                }

                // ⭐ 開いた力で使えない道は並べず、名だけを控える（門が 404 でなく 403 not_open で断る）。
                if (!this.abilities.Allows(route.Needs))
                {
                    this.NotOpen.Add(path);
                    return;
                }

                this.Open.Add(path, route);
            }
        }
    }
}
