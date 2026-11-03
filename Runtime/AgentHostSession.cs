using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Monster.Balloon.AutomataSystem
{
    /// <summary>
    /// Play 1 回ぶんの窓口の持ち物（トークン・鍵・列・道の表・合図の帳面）。
    /// <para>
    /// ⭐ 窓口は、作られた時のこの持ち物に積む。だから、古い Play の窓口が生き残っても、新しい Play の列へは積めない。
    /// ⚠ 窓口のスレッドから呼ばれる口（<see cref="Check"/>・<see cref="AnswerTimeoutFor"/>・<see cref="SubmitAsync"/>）は、Unity の物に触らない。
    /// </para>
    /// </summary>
    internal sealed class AgentHostSession : IAgentFrontDoor
    {
        /// <summary>長い待ちの道の答えを、道が決めた長さよりさらに待つ余り（主スレッドの 1 フレームの遅れを吸う）。</summary>
        private static readonly TimeSpan HoldMargin = TimeSpan.FromSeconds(1);

        /// <summary>1 フレームで新しい頼みに使ってよい時間（ミリ秒）。越えたら、残りは次のフレームで答える。</summary>
        private const double PumpBudgetMs = 4.0;

        private readonly ConcurrentQueue<AgentCall> queue = new ConcurrentQueue<AgentCall>();
        private readonly AgentRateLimiter limiter = new AgentRateLimiter(AgentHost.RequestsPerSecond);

        /// <summary>答えを持ち越した頼み（長い待ち）。主スレッドだけが触る。</summary>
        private readonly List<AgentCall> held = new List<AgentCall>();

        /// <summary>窓口を閉じた時に取り消す印（<see cref="ClosedToken"/>）。</summary>
        private readonly CancellationTokenSource closedSource = new CancellationTokenSource();

        private volatile AgentGateKeys keys;
        private volatile RouteTable routes = RouteTable.Empty;
        private volatile bool closed;

        /// <summary>門を通った頼みを、さらにゲームの決まりで断る口（<see cref="AgentRequestGuards"/>）。null＝見ない。</summary>
        private readonly IAgentRequestGuard guard;

        public AgentHostSession(AgentAbilities abilities, string openWord)
        {
#if !(UNITY_EDITOR || DEVELOPMENT_BUILD)
            // 製品版には裏が無い。力の組に back があっても外す。
            abilities &= ~AgentAbilities.Back;
#endif
            this.Abilities = abilities;
            this.OpenWord = openWord;
            this.PlayId = AgentTokens.NewHex(3);
            this.SurfaceToken = AgentTokens.NewToken();

            // ⭐ 裏のトークンは、back の力を開いた時だけ作る（back を開かない窓口は、つなぎ先にも書かない）。
            if ((abilities & AgentAbilities.Back) != 0)
            {
                this.BackToken = AgentTokens.NewToken();
            }

            this.ProcessId = ReadProcessId();
            this.guard = AgentRequestGuards.ForSession(abilities);

            // ⭐ 裏の帳面は、裏のトークンがある時だけ作る（無い時の帳面は表だけ）。
            this.Signals = new AgentSignalHub(this.BackToken != null, abilities);
            this.Reflex = new AgentReflex(this);
            this.Hands = new AgentLeaseDesk(this.Signals);
            this.Captures = new AgentCaptureDesk(this.PlayId);
        }

        /// <summary>この窓口で開いた力の組。</summary>
        public AgentAbilities Abilities { get; }

        /// <summary>開いた印の字（例 "operate"・"look"）。hello とつなぎ先ファイルの open に書く。</summary>
        public string OpenWord { get; }

        /// <summary>Play ごとの印（秘密ではない・返事の instance に出す）。</summary>
        public string PlayId { get; }

        public string SurfaceToken { get; }

        /// <summary>裏のトークン。製品版では null。</summary>
        public string BackToken { get; }

        public int ProcessId { get; }

        /// <summary>合図の係（帳面と、世界の合図の元）。</summary>
        public AgentSignalHub Signals { get; }

        /// <summary>止める反射の係（腕・掛け金・一時停止を頼んだ者・何コマか進める）。</summary>
        public AgentReflex Reflex { get; }

        /// <summary>手足の借りと命令の係。</summary>
        public AgentLeaseDesk Hands { get; }

        /// <summary>撮る係（1 秒 1 枚・絵の置き場と枚数）。</summary>
        public AgentCaptureDesk Captures { get; }

        /// <summary>表の札の表（個体の ID と 6 字の札の行き来・主スレッドだけで使う）。</summary>
        public AgentTagTable Tags { get; } = new AgentTagTable();

        public IAgentTransport Transport { get; set; }

        public GameObject HostObject { get; set; }

        public string EndpointPath { get; set; }

        public bool IsClosed => this.closed;

        /// <summary>
        /// 窓口を閉じた時に取り消される印（どのスレッドから読んでもよい）。
        /// ⭐ 中の口（<see cref="AgentInProcessLink"/>）を使う係は、待ちをこの印で止める（Play を抜けた後に待ちを残さない）。
        /// </summary>
        public CancellationToken ClosedToken => this.closedSource.Token;

        /// <summary>
        /// この窓口を使っている中の口の数（主スレッドだけが触る）。
        /// ⭐ 待ち受けの無い窓口（<see cref="AgentHost.OpenInProcess"/> が開いた物）は、これが 0 に戻った時に閉じる。
        /// </summary>
        public int InProcessLinks { get; set; }

        /// <summary>預かっている頼みの数（主スレッドで読む）。</summary>
        public int HeldCount => this.held.Count;

        /// <summary>
        /// 裏のトークンで来た頼みの数（主スレッドだけが触る）。⭐ 世界を読まない係の道（<see cref="IAgentControlRoute"/>）は数えない。
        /// ⭐ 裏のトークンは表の道も通る＝道の顔ではなく、頼みのトークンで数える。試験の橋が、行の間の差から playedWithBack を書く。
        /// </summary>
        public long BackCalls { get; private set; }

        /// <summary>
        /// 来た頼みの数（表・裏とも・主スレッドだけが触る）。
        /// ⭐ Editor の録画の係が「まだ使われているか」を見る（裏の頼みで撮り始め、頼みが途絶えたら止める）。
        /// </summary>
        public long Calls { get; private set; }

        /// <summary>
        /// 裏の道に来た頼みの数（世界を読まない係の道＝試験の道具・keep_alive なども数える・主スレッドだけが触る）。
        /// ⭐ Editor の録画の係が撮り始める合図（<see cref="BackCalls"/> と違い、試験の確かめも撮るため）。
        /// </summary>
        public long BackRouteCalls { get; private set; }

        public int MaxBodyBytes => AgentGate.MaxBodyBytes;

        public TimeSpan MainThreadTimeout => AgentHost.MainThreadTimeout;

        /// <summary>今並んでいる道（開いた力で使える道だけ・主スレッドで読む）。</summary>
        public ICollection<IAgentRoute> AllRoutes => this.routes.Open.Values;

        public void SetKeys(AgentGateKeys value) => this.keys = value;

        /// <summary>
        /// 道の表を差し替える（⚠ 渡した表と組は、後から中身を変えない＝窓口のスレッドが読んでいる）。
        /// </summary>
        /// <param name="open">開いた力で使える道。</param>
        /// <param name="notOpen">在るが、開いた力では使えない道の名（門が 403 not_open で断る）。</param>
        public void SetRoutes(Dictionary<string, IAgentRoute> open, HashSet<string> notOpen) =>
            this.routes = new RouteTable(open, notOpen);

        public IAgentRoute FindRoute(string path) =>
            path != null && this.routes.Open.TryGetValue(path, out IAgentRoute route) ? route : null;

        public AgentGateVerdict Check(in AgentGateRequest request)
        {
            if (this.closed)
            {
                return AgentGateVerdict.Refuse(503, "closing", "reopen_after_play");
            }

            long nowMs = Stopwatch.GetTimestamp() * 1000L / Stopwatch.Frequency;
            AgentGateVerdict verdict = AgentGate.Check(in request, this.keys, this.KindOf(request.Path), this.limiter, nowMs);
            if (verdict.Passed && this.guard != null && this.guard.TryRefuse(request.Headers, out AgentRefusal refused))
            {
                return AgentGateVerdict.Refuse(refused.Status, refused.Code, refused.Hint);
            }

            return verdict;
        }

        public TimeSpan AnswerTimeoutFor(string path, JObject body)
        {
            if (this.FindRoute(path) is IAgentLongRoute longRoute)
            {
                TimeSpan hold;
                try
                {
                    hold = longRoute.MaxHold(body);
                }
                catch (Exception)
                {
                    // 読めない頼みは、道が主スレッドで断る。窓口はふつうの長さで待つ。
                    hold = TimeSpan.Zero;
                }

                if (hold > TimeSpan.Zero)
                {
                    return AgentHost.MainThreadTimeout + hold + HoldMargin;
                }
            }

            return AgentHost.MainThreadTimeout;
        }

        public Task<AgentReply> SubmitAsync(string path, AgentFace face, JObject body)
        {
            if (this.closed)
            {
                return Task.FromResult(AgentReplies.Error(503, "closing", "reopen_after_play"));
            }

            // ⭐ 使う側が来ている間だけ、Host は世界の合図の元をつなぐ（来なくなって 60 秒で外す）。
            this.Signals.NoteRequest();
            var call = new AgentCall(path, face, body);
            this.queue.Enqueue(call);

            // ⭐ 積んだ後にもう一度見る（閉じる係が列を空にした後に積んだ頼みを、待たせたままにしない）。
            //    閉じる係は、印を立ててから列を空にする＝ここで印が見えなければ、閉じる係がこの頼みを空にする。
            if (this.closed)
            {
                while (this.queue.TryDequeue(out AgentCall stale))
                {
                    stale.Completion.TrySetResult(AgentReplies.Error(503, "closing", "reopen_after_play"));
                }
            }

            return call.Completion.Task;
        }

        /// <summary>
        /// 主スレッドで、合図の元を回し、預かった頼みと新しい頼みに答える（新しい頼みは 1 フレームに決まった本数と時間まで）。
        /// ⭐ 合図を先に回す＝同じフレームで来た合図を、待っている頼みがすぐ受け取れる。
        /// </summary>
        public void Pump()
        {
            if (this.closed)
            {
                return;
            }

            // ⚠ ここで落ちると、列を回す係ごと PlayerLoop から外される（窓口が答えなくなる）。合図の係の失敗は赤 1 回に留める。
            // ⭐ 止める反射の腕の間は、使う側が黙っていても合図の元をつないでおく（AI が長く考えている間の出来事も残す）。
            long nowMs = AgentSignalHub.NowMs();
            try
            {
                this.Signals.Tick(this.Reflex.IsArmed(nowMs));
            }
            catch (Exception e)
            {
                AgentHost.ReportOnce("signals|" + AgentHost.Epoch, "[AgentApi] 合図の係が落ちた（この世界では 1 回だけ鳴らす）: " + e);
            }

            // ⭐ 人の本物の入力は、反射より先に見る（人が触ったコマに、反射の命令を走らせない＝人が先）。
            try
            {
                this.Hands.WatchHuman(nowMs, AgentReflexJson.ReadyWorld()?.Hands);
            }
            catch (Exception e)
            {
                AgentHost.ReportOnce("hands-person-tick|" + AgentHost.Epoch, "[AgentApi] 人の入力を見る係が落ちた（この世界では 1 回だけ鳴らす）: " + e);
            }

            // ⭐ 反射は合図の後に回す（同じフレームの出来事が帳面に載ってから、守りの合図を書く）。
            try
            {
                this.Reflex.Tick(nowMs);
            }
            catch (Exception e)
            {
                AgentHost.ReportOnce("reflex|" + AgentHost.Epoch, "[AgentApi] 止める反射の係が落ちた（この世界では 1 回だけ鳴らす）: " + e);
            }

            // ⭐ 手足は反射の後に回す（反射が掛け金を掛けたフレームに、命令の締め切りを数えない）。
            try
            {
                this.Hands.Tick(nowMs);
            }
            catch (Exception e)
            {
                AgentHost.ReportOnce("hands|" + AgentHost.Epoch, "[AgentApi] 手足の係が落ちた（この世界では 1 回だけ鳴らす）: " + e);
            }

            for (int i = 0; i < this.held.Count && !this.closed;)
            {
                AgentCall call = this.held[i];
                if (AgentReplyComposer.TryAnswer(call, this, nowMs, out AgentReply reply))
                {
                    this.held.RemoveAt(i);
                    call.Completion.TrySetResult(reply);
                }
                else
                {
                    i++;
                }
            }

            long started = Stopwatch.GetTimestamp();
            for (int i = 0; i < AgentHost.MaxCallsPerFrame && !this.closed; i++)
            {
                // ⭐ 1 本目は必ず答える（どの頼みも、いつかは順番が来る）。2 本目からは使った時間を見る
                //    （重い目（人の目）が 8 本並ぶと、そのフレームだけゲームが長く止まる）。
                if (i > 0 && (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency >= PumpBudgetMs)
                {
                    return;
                }

                if (!this.queue.TryDequeue(out AgentCall call))
                {
                    return;
                }

                // ⭐ 数えるのは列から初めて出した時だけ（長い待ちの頼みを、預かったフレームごとに数え直さない）。
                // ⭐ 数えるのは裏の道（返事に裏の物が入る道）だけ。表の道を裏のトークンで打った頼み（帯を消して撮る等）は、
                //    返事が表の中身だけなので数えない（証拠の絵を撮るたびに「裏を読んだ」が増えていた）。
                this.Calls++;
                IAgentRoute counted = call.Face == AgentFace.Back ? this.FindRoute(call.Path) : null;
                if (counted != null && counted.Face == AgentFace.Back)
                {
                    this.BackRouteCalls++;
                    if (!(counted is IAgentControlRoute))
                    {
                        this.BackCalls++;
                    }
                }

                if (AgentReplyComposer.TryAnswer(call, this, nowMs, out AgentReply reply))
                {
                    call.Completion.TrySetResult(reply);
                }
                else
                {
                    this.held.Add(call);
                }
            }
        }

        public void Close()
        {
            this.closed = true;
            IAgentTransport transport = this.Transport;
            this.Transport = null;
            transport?.Dispose();

            this.Signals.StopRedLog();
            this.Captures.Close();
            this.Hands.Close();
            this.Reflex.Close();
            this.Signals.DetachSources();

            // 待っている頼みには「閉じた」と答える（使う側を待たせたままにしない）。
            foreach (AgentCall call in this.held)
            {
                call.Completion.TrySetResult(AgentReplies.Error(503, "closing", "reopen_after_play"));
            }

            this.held.Clear();
            while (this.queue.TryDequeue(out AgentCall call))
            {
                call.Completion.TrySetResult(AgentReplies.Error(503, "closing", "reopen_after_play"));
            }

            // ⭐ 取り消しは最後に打つ（取り消しの続きは、このスレッドでその場に走ることがある＝列を空にした後なら、新しい頼みは closing で返る）。
            try
            {
                this.closedSource.Cancel();
            }
            catch (AggregateException e)
            {
                // 待ちの続きが落ちても、窓口は閉じ終える。
                UnityEngine.Debug.LogWarning("[AgentApi] 窓口を閉じた時、中の口の待ちの続きが落ちた: " + e.InnerException?.Message);
            }
        }

        private AgentRouteKind KindOf(string path)
        {
            RouteTable table = this.routes;
            if (path != null && table.Open.TryGetValue(path, out IAgentRoute route))
            {
                return route.Face == AgentFace.Back ? AgentRouteKind.Back : AgentRouteKind.Surface;
            }

            if (path != null && table.NotOpen.Contains(path))
            {
                return AgentRouteKind.NotOpen;
            }

            return AgentHost.WorldState == AgentWorldState.Ready ? AgentRouteKind.Missing : AgentRouteKind.WorldPending;
        }

        private static int ReadProcessId()
        {
            try
            {
                using (Process p = Process.GetCurrentProcess())
                {
                    return p.Id;
                }
            }
            catch (Exception)
            {
                // 読めない所（スマホなど）では -1 を返す。
                return -1;
            }
        }

        /// <summary>道の表の 1 組（使える道と、開いていない道の名）。2 つを 1 回で差し替えるための入れ物。</summary>
        private sealed class RouteTable
        {
            public static readonly RouteTable Empty = new RouteTable(
                new Dictionary<string, IAgentRoute>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));

            public RouteTable(Dictionary<string, IAgentRoute> open, HashSet<string> notOpen)
            {
                this.Open = open ?? throw new ArgumentNullException(nameof(open));
                this.NotOpen = notOpen ?? throw new ArgumentNullException(nameof(notOpen));
            }

            public Dictionary<string, IAgentRoute> Open { get; }

            public HashSet<string> NotOpen { get; }
        }
    }
}
