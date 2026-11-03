using System;
using System.Globalization;
using System.Threading;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Monster.Balloon.AutomataSystem
{
    /// <summary>
    /// 手足の借りと命令の係。この係は Play 1 回ぶん居る（窓口の持ち物）。
    /// <para>
    /// ⭐ 借りは、名前（by）を持つ 1 人だけが持つ。借りの間だけ、世界の手足（<see cref="IAgentHands"/>）が仮のパッドを足す。
    /// ⭐ 借りが切れるのは: 借りた人が 5 秒頼まない（idle・命令が走っている間は数えない）・人の本物の入力（human-input）・
    ///    返した（released）・世界を畳む（world）の時。切れたら合図 lease.lost を書き、パッドを外す。窓口を閉じる時は、合図を書かずに外す。
    /// ⭐ 人の本物の入力で切れた後は、その人は act のついでには借りを取り直せない（/v1/lease で取り直す）。人が触った後 5 秒は、だれも取れない。
    /// ⭐ 命令は 1 本ずつ（重なりは busy）。同じ ref の 2 回目は、押さずに 1 回目の結果を返す（ref は新しい 64 個を覚える）。
    /// ⭐ 命令がどう終わっても、この係はパッドを中立に戻し、合図 act.done を書く。
    /// ⚠ 主スレッドで使う。
    /// </para>
    /// </summary>
    internal sealed class AgentLeaseDesk
    {
        /// <summary>借りた人が、これより長く頼まなければ借りを切る（ミリ秒・命令が走っている間は数えない）。</summary>
        public const long IdleMs = 5_000;

        /// <summary>人の本物の入力の後、これより短い間は、だれにも借りを貸さない（ミリ秒）。</summary>
        public const long HumanQuietMs = 5_000;

        /// <summary>命令 1 本の長さの上限（秒）。命令の言う長さが、これより長くても、ここで切る。</summary>
        public const float MaxActSeconds = 60f;

        /// <summary>借りる人の名前の長さの上限。</summary>
        public const int MaxNameLength = 40;

        /// <summary>人の本物の入力で切れた時の理由の字。</summary>
        public const string WhyHuman = "human-input";

        /// <summary>
        /// 動く反射が自分で借りる時の名前（AI の名前の形 [A-Za-z0-9._-] に無い字で始める＝AI が同じ名前を名乗れない）。
        /// </summary>
        public const string ReflexHolder = "#reflex";

        /// <summary>動く反射が走っている間に、AI の頼みを断る字（命令・借り）。</summary>
        public const string ReflexActingWhy = "reflex_acting";

        private const float MinActSeconds = 0.1f;

        /// <summary>手足の合図（hello で、A の手を開いた時だけ並べる）。</summary>
        internal static readonly AgentSignalKind[] SignalKinds =
        {
            new AgentSignalKind("lease.taken", AgentFace.Surface),
            new AgentSignalKind("lease.lost", AgentFace.Surface),
            new AgentSignalKind("act.done", AgentFace.Surface),
        };

        private readonly AgentSignalHub signals;
        private readonly AgentActRefMemory refs = new AgentActRefMemory();

        private int leaseCount;
        private int actCount;
        private IAgentHands hands;
        private long lastRequestMs;
        private long lastHumanMs;
        private AgentActTicket running;
        private CancellationTokenSource runningCancel;

        public AgentLeaseDesk(AgentSignalHub signals)
        {
            this.signals = signals ?? throw new ArgumentNullException(nameof(signals));
        }

        /// <summary>今の借りの印（例 "L3"）。借りが無ければ null。</summary>
        public string LeaseId { get; private set; }

        /// <summary>今の借りの人の名前。借りが無ければ null。</summary>
        public string Holder { get; private set; }

        /// <summary>最後に切れた借りの印・人・理由（切れた借りで頼んだ人に、なぜ切れたかを返すため）。</summary>
        public string LostLeaseId { get; private set; }

        public string LostHolder { get; private set; }

        public string LostWhy { get; private set; }

        /// <summary>走っている命令（無ければ null）。</summary>
        public AgentActTicket Running => this.running;

        /// <summary>動く反射が走らせている命令（無ければ null）。</summary>
        public AgentActTicket ReflexActing => this.running != null && this.running.Reflex != null ? this.running : null;

        /// <summary>借りの間の手足（借りが無ければ null）。</summary>
        public IAgentHands LeasedHands => this.hands;

        // ── 借り ────────────────────────────────────────────────

        /// <summary>
        /// 借りを取る（同じ名前で持っていれば、続ける）。取れなければ断りの字を返す。
        /// </summary>
        /// <param name="worldReady">世界ができているか。</param>
        /// <param name="worldHands">今できている世界の手足（無ければ null）。</param>
        public string TryTake(string by, long nowMs, bool worldReady, IAgentHands worldHands)
        {
            if (this.LeaseId != null)
            {
                if (!string.Equals(this.Holder, by, StringComparison.Ordinal))
                {
                    // 動く反射が自分で借りている間は、終われば返す＝待てばよい。
                    return string.Equals(this.Holder, ReflexHolder, StringComparison.Ordinal) ? ReflexActingWhy : "leased";
                }

                this.lastRequestMs = nowMs;
                return null;
            }

            if (!worldReady)
            {
                return "world_not_ready";
            }

            if (worldHands == null)
            {
                return "no_hands";
            }

            if (this.HumanWasRecent(nowMs))
            {
                return "human_active";
            }

            try
            {
                worldHands.Begin();
            }
            catch (Exception e)
            {
                AgentHost.ReportOnce("hands-begin|" + AgentHost.Epoch, "[AgentApi] 手足を始められなかった（仮のパッドを足せない）: " + e);
                EndQuietly(worldHands);
                return "hands_failed";
            }

            this.hands = worldHands;
            this.Holder = by;
            this.LeaseId = "L" + (++this.leaseCount).ToString(CultureInfo.InvariantCulture);
            this.lastRequestMs = nowMs;

            // ⭐ 新しく借りたら、前に切れた借りの控えは忘れる（人の手で切れた後の「act では取り直さない」も、ここで解ける）。
            this.LostLeaseId = null;
            this.LostHolder = null;
            this.LostWhy = null;
            this.signals.Write(AgentFace.Surface, "lease.taken", false, new JObject { ["lease"] = this.LeaseId, ["by"] = by });
            return null;
        }

        /// <summary>
        /// act の頼みの借りを決める。借りが無ければ、その場で取る（人の本物の入力で切れた後は取らない）。
        /// 使えなければ断りの字を返し、<paramref name="hint"/> に手がかりを入れる。
        /// </summary>
        /// <param name="leaseId">頼みに書かれた借りの印（無ければ null＝名前だけで決める）。</param>
        public string ResolveForAct(string by, string leaseId, long nowMs, bool worldReady, IAgentHands worldHands, out string hint)
        {
            hint = null;
            if (leaseId != null && !string.Equals(leaseId, this.LeaseId, StringComparison.Ordinal))
            {
                if (string.Equals(leaseId, this.LostLeaseId, StringComparison.Ordinal))
                {
                    hint = this.LostWhy;
                    return "lease_lost";
                }

                hint = "lease_first";
                return "not_leased";
            }

            if (this.LeaseId == null && string.Equals(this.LostWhy, WhyHuman, StringComparison.Ordinal))
            {
                // ⭐ 人が触って切れた借りは、AI が自分で取り直す（/v1/lease）まで、命令のついでには取らない。
                // ⭐ 切れたのが反射の借り・ほかの名前の借りでも同じ（名前で見ると、AI は 5 秒後に黙って取り直せてしまう）。
                hint = WhyHuman;
                return "lease_lost";
            }

            string why = this.TryTake(by, nowMs, worldReady, worldHands);
            hint = HintForTake(why);
            return why;
        }

        /// <summary><see cref="TryTake"/> の断りの手がかり（lease と act の道で同じ字にする）。</summary>
        public static string HintForTake(string why)
        {
            switch (why)
            {
                case "leased":
                    return "other_holder";
                case "human_active":
                    return "wait_human";
                case ReflexActingWhy:
                    return "wait_reflex";
                default:
                    return null;
            }
        }

        /// <summary>借りた人が頼みを送った（黙った長さを数え直す）。ほかの人の頼みでは数え直さない。</summary>
        public void Touch(string by, long nowMs)
        {
            if (this.LeaseId != null && string.Equals(this.Holder, by, StringComparison.Ordinal))
            {
                this.lastRequestMs = nowMs;
            }
        }

        /// <summary>借りを切る（持っていなければ何もしない）。合図 lease.lost を書き、走っている命令を interrupted で終え、パッドを外す。</summary>
        public void Release(string why)
        {
            if (this.LeaseId == null)
            {
                return;
            }

            string id = this.LeaseId;
            this.LostLeaseId = id;
            this.LostHolder = this.Holder;
            this.LostWhy = why;
            this.LeaseId = null;
            this.Holder = null;

            this.signals.Write(AgentFace.Surface, "lease.lost", false, new JObject { ["lease"] = id, ["why"] = why });
            this.Interrupt("lease.lost", this.signals.Surface.LastSeq);

            IAgentHands leased = this.hands;
            this.hands = null;
            EndQuietly(leased);
        }

        // ── 命令 ────────────────────────────────────────────────

        /// <summary>
        /// 同じ人（by）の同じ ref の頼みを探す（<see cref="AgentActRefMemory.Find"/>）。同じ ref で違う命令なら "ref_reused" を返す。
        /// </summary>
        public string FindByRef(string by, string reference, string command, out AgentActTicket seen) =>
            this.refs.Find(by, reference, command, out seen);

        /// <summary>
        /// 命令を始める（借りは先に <see cref="ResolveForAct"/> で決めておく）。始められなければ断りの字を返す。
        /// ⭐ 命令がその場で終われば、返す控えはもう終わっている（道はすぐ答える）。
        /// </summary>
        public string TryStart(string by, IAgentCommand command, JObject args, string reference, long nowMs, out AgentActTicket ticket)
        {
            ticket = null;
            if (this.LeaseId == null)
            {
                return "not_leased";
            }

            if (this.running != null)
            {
                return this.running.Reflex != null ? ReflexActingWhy : "busy";
            }

            ticket = this.Begin(command, args, reference, AgentActRefMemory.Key(by, reference), this.NextActId(), null, nowMs);
            return null;
        }

        /// <summary>
        /// 動く反射の命令を、今始められないなら、その理由（<see cref="TryStartReflex"/> と同じ字・何も変えない）。
        /// ⭐ 反射の係は、1 分に 3 回の数えより先にこれを見る（走らない 3 回目で、止める守りを働かせないため）。
        /// </summary>
        public string WhyNotReflex(long nowMs, IAgentHands worldHands)
        {
            if (this.ReflexActing != null)
            {
                return "acting";
            }

            if (this.LeaseId != null)
            {
                return null;
            }

            // ⭐ 人が触って切れた借りは、AI が自分で取り直す（/v1/lease）まで、反射も取らない（人が先＝人が操作を引き取った）。
            if (string.Equals(this.LostWhy, WhyHuman, StringComparison.Ordinal) || this.HumanWasRecent(nowMs))
            {
                return "human_active";
            }

            return worldHands == null ? "no_hands" : null;
        }

        /// <summary>
        /// 動く反射の命令を始める。始められなければ断りの字を返す（acting・human_active・no_hands・hands_failed）。
        /// <para>
        /// ⭐ AI が借りていれば、その借りのまま走らせる（AI の命令は interrupted＝by は <paramref name="announce"/> が書いた合図）。
        ///    借りが無ければ、反射の名前（<see cref="ReflexHolder"/>）で借り、命令が終わったら返す（reflex_done）。
        /// ⭐ <paramref name="announce"/> は、借りが決まってから、命令の印を受けて合図を書き、その表の番号を返す。
        /// </para>
        /// </summary>
        public string TryStartReflex(
            string reflex,
            IAgentCommand command,
            JObject args,
            long nowMs,
            IAgentHands worldHands,
            Func<string, long> announce,
            out AgentActTicket ticket)
        {
            ticket = null;
            string why = this.WhyNotReflex(nowMs, worldHands);
            if (why != null)
            {
                return why;
            }

            if (this.LeaseId == null)
            {
                why = this.TryTake(ReflexHolder, nowMs, true, worldHands);
                if (why != null)
                {
                    return why;
                }
            }

            string actId = this.NextActId();
            long seq = announce != null ? announce(actId) : this.signals.Surface.LastSeq;
            AgentActTicket stopped = this.running;
            if (stopped != null)
            {
                this.Complete(stopped, AgentActResult.Interrupted(), AgentMovingReflex.ActedKind, seq);
            }

            ticket = this.Begin(command, args, null, null, actId, reflex, nowMs);
            return null;
        }

        /// <summary>走っている命令を、邪魔した合図の名前と番号で終える。走っていなくても、パッドは中立に戻す（止める反射が働いた時）。</summary>
        public void Interrupt(string byKind, long bySeq)
        {
            AgentActTicket act = this.running;
            if (act == null)
            {
                this.NeutralQuietly();
                return;
            }

            this.Complete(act, AgentActResult.Interrupted(), byKind, bySeq);
        }

        /// <summary>
        /// 動く反射の命令が走っていれば、邪魔した合図の名前と番号で終える（AI の命令には触らない）。
        /// ⭐ 腕が外れた・選びから外れた時に、反射の係が呼ぶ（見張りが止まった後も、反射の命令が続かないように）。
        /// </summary>
        public void InterruptReflex(string byKind, long bySeq)
        {
            AgentActTicket act = this.ReflexActing;
            if (act != null)
            {
                this.Complete(act, AgentActResult.Interrupted(), byKind, bySeq);
            }
        }

        // ── 毎フレーム・世界の出入り・終わり ─────────────────────────────

        /// <summary>
        /// 毎フレーム（主スレッド・止まっている間も呼ばれる・⭐ 反射より先）。人の本物の入力を見て、借りの間に来たら借りを切る（human-input）。
        /// ⭐ この係は、借りが無い間も見る＝人が遊び続けている間は、最後に触った時から 5 秒、だれにも貸さない。
        /// ⭐ この係は、反射より先に見る＝人が触ったコマに、反射の命令を走らせない。
        /// </summary>
        /// <param name="worldHands">今できている世界の手足（無ければ null）。借りの間は、借りた手足を見る。</param>
        public void WatchHuman(long nowMs, IAgentHands worldHands)
        {
            IAgentHands watched = this.hands ?? worldHands;
            if (watched == null)
            {
                return;
            }

            bool human = false;
            try
            {
                human = watched.TakeHumanInput();
            }
            catch (Exception e)
            {
                AgentHost.ReportOnce("hands-person|" + AgentHost.Epoch, "[AgentApi] 人の入力の見張りが落ちた: " + e);
            }

            if (!human)
            {
                return;
            }

            this.lastHumanMs = nowMs;
            this.Release(WhyHuman);
        }

        /// <summary>毎フレーム（主スレッド・止まっている間も呼ばれる・反射の後）。命令の締め切りと、借りの人が黙った長さを見る。</summary>
        public void Tick(long nowMs)
        {
            if (this.LeaseId == null)
            {
                return;
            }

            AgentActTicket act = this.running;
            if (act != null && nowMs >= act.DeadlineMs)
            {
                this.Complete(act, AgentActResult.TimedOut(null, null), null, 0);
            }

            if (this.running == null && nowMs - this.lastRequestMs >= IdleMs)
            {
                this.Release("idle");
            }
        }

        /// <summary>世界を畳む。借りを切る（手足は世界と一緒に消える）。</summary>
        public void OnWorldClosing() => this.Release("world");

        /// <summary>窓口を閉じる（合図は書かない・預かった act は窓口が closing で答える）。</summary>
        public void Close()
        {
            AgentActTicket act = this.running;
            if (act != null)
            {
                this.running = null;
                CancelQuietly(this.runningCancel);
                this.runningCancel = null;
                act.Finish(AgentActResult.Unknown("closing"), null, 0);
            }

            IAgentHands leased = this.hands;
            this.hands = null;
            this.LeaseId = null;
            this.Holder = null;
            EndQuietly(leased);
        }

        private static void CancelQuietly(CancellationTokenSource source)
        {
            try
            {
                source?.Cancel();
            }
            catch (AggregateException e)
            {
                AgentHost.ReportOnce("act-cancel|" + AgentHost.Epoch, "[AgentApi] 命令を取り消した時に、続きが落ちた: " + e.InnerException);
            }
        }

        private static void EndQuietly(IAgentHands leased)
        {
            if (leased == null)
            {
                return;
            }

            try
            {
                leased.End();
            }
            catch (Exception e)
            {
                AgentHost.ReportOnce("hands-end|" + AgentHost.Epoch, "[AgentApi] 手足を終える時に落ちた（仮のパッドが残ったかもしれない）: " + e);
            }
        }

        private bool HumanWasRecent(long nowMs) => this.lastHumanMs != 0 && nowMs - this.lastHumanMs < HumanQuietMs;

        private string NextActId() => "a" + (++this.actCount).ToString(CultureInfo.InvariantCulture);

        /// <summary>命令を走らせる（締め切りは命令の言う長さ・0.1〜60 秒）。⭐ 命令がその場で終われば、返す控えはもう終わっている。</summary>
        private AgentActTicket Begin(IAgentCommand command, JObject args, string reference, string refKey, string actId, string reflex, long nowMs)
        {
            float seconds;
            try
            {
                seconds = command.MaxSeconds(args);
            }
            catch (Exception)
            {
                // 長さを読めない頼みは、命令が自分で断る。芯は短い締め切りで見張る。
                seconds = MinActSeconds;
            }

            if (float.IsNaN(seconds) || seconds < MinActSeconds)
            {
                seconds = MinActSeconds;
            }
            else if (seconds > MaxActSeconds)
            {
                seconds = MaxActSeconds;
            }

            var started = new AgentActTicket(
                actId,
                reference,
                command.Name,
                nowMs + (long)(seconds * 1000f),
                this.signals.Surface.LastSeq,
                this.signals.Back != null ? this.signals.Back.LastSeq : 0)
            {
                Reflex = reflex,
            };
            this.refs.Remember(refKey, started);
            this.running = started;
            this.runningCancel = new CancellationTokenSource();
            this.Run(command, args, started, this.runningCancel.Token);
            return started;
        }

        /// <summary>
        /// 命令を走らせ、終わったら結果で控えを閉じる。
        /// ⭐ 命令がその場で終われば、await はその場で続く＝この係は、Begin が戻る前に控えを閉じる。
        /// ⭐ この係は、命令が返した Awaitable を 1 回だけ待つ（Awaitable は 2 回待てない）。
        /// ⚠ async void なので、この係は例外を外へ出さない。命令の例外は結果（command_failed）にし、閉じる途中の例外は赤のログに残す。
        /// </summary>
        private async void Run(IAgentCommand command, JObject args, AgentActTicket ticket, CancellationToken cancel)
        {
            try
            {
                AgentActResult result;
                try
                {
                    Awaitable<AgentActResult> running = command.RunAsync(args, cancel);
                    result = running != null ? await running : AgentActResult.Unknown("no_result");
                }
                catch (OperationCanceledException)
                {
                    // 芯が先に結果を決めた（締め切り・止める反射・借りが切れた）。
                    return;
                }
                catch (Exception e)
                {
                    AgentHost.ReportOnce("act|" + command.Name + "|" + AgentHost.Epoch, "[AgentApi] 命令 " + command.Name + " が落ちた: " + e);
                    result = AgentActResult.Unknown("command_failed");
                }

                this.Complete(ticket, result ?? AgentActResult.Unknown("no_result"), null, 0);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        /// <summary>命令を終える（もう終わっていれば何もしない）。取り消してからパッドを中立に戻し、合図 act.done を書く。</summary>
        private void Complete(AgentActTicket ticket, AgentActResult result, string byKind, long bySeq)
        {
            if (ticket.Done)
            {
                return;
            }

            // ⭐ 邪魔した合図は、書いた直後にここへ来る＝裏の帳面の最後の番号が、同じ合図の裏の番号（裏は表の合図も全部写す）。
            long byBackSeq = byKind != null && this.signals.Back != null ? this.signals.Back.LastSeq : 0;
            ticket.Finish(result, byKind, bySeq, byBackSeq);
            if (ReferenceEquals(this.running, ticket))
            {
                this.running = null;

                // ⭐ 先に取り消す（命令が、中立に戻した後に押し直さない）。⚠ Dispose はしない（命令がまだ印を読むかもしれない）。
                CancelQuietly(this.runningCancel);
                this.runningCancel = null;
                this.NeutralQuietly();

                // ⭐ 黙った長さは、命令が終わった時から数える（長い命令の直後に借りが切れないように）。
                this.lastRequestMs = AgentSignalHub.NowMs();
            }

            this.signals.Write(AgentFace.Surface, "act.done", false, result.ToSignalData(ticket.ActId, ticket.Command, ticket.Reflex));

            // ⭐ 反射が自分で借りた借りは、反射の命令が終わったら返す（AI が次の命令で借りられるように）。
            if (ticket.Reflex != null && this.LeaseId != null && string.Equals(this.Holder, ReflexHolder, StringComparison.Ordinal))
            {
                this.Release("reflex_done");
            }
        }

        private void NeutralQuietly()
        {
            IAgentHands leased = this.hands;
            if (leased == null)
            {
                return;
            }

            try
            {
                leased.Neutral();
            }
            catch (Exception e)
            {
                AgentHost.ReportOnce("hands-neutral|" + AgentHost.Epoch, "[AgentApi] パッドを中立に戻せなかった: " + e);
            }
        }
    }
}
