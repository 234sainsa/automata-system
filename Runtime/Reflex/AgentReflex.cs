using System;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Monster.Balloon.AutomataSystem
{
    /// <summary>
    /// 反射の係（止める守り・動く反射）。Play 1 回ぶん（窓口の持ち物）。腕・掛け金・赤いログ・何コマか進める、を持つ。
    /// <para>
    /// ⭐ この係は、腕（<c>POST /v1/reflex/arm</c>）が掛かっている間だけ、世界の見張り（<see cref="IAgentReflexWatch"/>）を回し、赤いログを聞く。
    ///    腕の間は、使う側が黙っていても合図の元をつないでおく（AI が長く考えている間の出来事も、帳面に残る）。
    /// ⭐ 守りが働くと、この係は合図 reflex.tripped を書き、掛け金を掛ける（gameover のように止めない守りも、掛け金は掛ける）。
    ///    止める守りなら、Editor も止める（<c>EditorApplication.isPaused</c>）。
    /// ⭐ 腕の ignore で選んだ守りは、働いても止めない＝この係は合図 reflex.ignored だけを書く（掛け金・一時停止・手足の中断をしない）。
    ///    例＝ボス戦の確かめで、倒れる（downed）たびに止まらないようにする。⚠ 見張りが落ちた時の red_log だけは、選んでいても止める。
    ///    掛け金は <c>POST /v1/reflex/ack</c> でだけ外れる（手足は、掛け金の間 act を 409 で断る）。
    /// ⭐ 動く反射（逃げる等）は <see cref="AgentMovingReflex"/> が持つ（見張りが <see cref="Act"/> を呼ぶと、手足の命令を走らせる）。
    /// ⚠ この係は <c>Time.timeScale</c> を書かない（窓の切り替えが毎回 0 か 1 に書き直すため）。
    /// ⚠ ビルドには Editor の止め方が無い＝掛け金と合図だけ（ビルドの止め方は今は無い）。
    /// ⚠ 主スレッドで使う（赤いログの受け口だけは、どのスレッドからも呼ばれる）。
    /// </para>
    /// </summary>
    internal sealed class AgentReflex : IAgentReflexContext
    {
        public const int DefaultArmMinutes = 30;
        public const int MaxArmMinutes = 120;
        public const int MaxStepFrames = 120;

        /// <summary>
        /// 何コマか進める時の上限の秒（ミリ秒）。これを過ぎたら、進んだ所で止め直して答える。
        /// ⭐ 窓口が答えを持ち越せるのは 30 秒まで（<c>AgentPauseRoute</c>）＝それより前に必ず答える（AI が使う Editor の 10fps で 120 コマは約 12 秒）。
        /// </summary>
        public const long StepDeadlineMs = 20_000;

        /// <summary>赤いログの守り（芯が自分で見る守り）。</summary>
        public const string RedLogGuard = "red_log";

        /// <summary>止めた・動かした人の名（合図 paused・resumed の by）。反射＝reflex・AI の頼み＝agent・ほか（人・Error Pause）＝editor。</summary>
        public const string ByReflex = "reflex";
        public const string ByAgent = "agent";
        public const string ByEditor = "editor";

        private const int RedTextMax = 400;

        private readonly AgentHostSession session;
        private readonly object redGate = new object();

        private long armedUntilMs;
        private IAgentReflexWatch watching;
        private bool redHooked;
        private bool redPending;
        private string redType;
        private string redText;

        private string pendingPauseBy;
        private string pendingResumeBy;
        private AgentStepTicket step;
        private int stepStartFrame;
        private int stepTargetFrame;

        public AgentReflex(AgentHostSession session)
        {
            this.session = session ?? throw new ArgumentNullException(nameof(session));
            this.Moving = new AgentMovingReflex(session, this);
        }

        /// <summary>動く反射の係（腕で選んだ反射・1 分に 3 回の守り・合図 reflex.acted）。</summary>
        public AgentMovingReflex Moving { get; }

        public int StopBeforeMinute { get; private set; } = -1;

        /// <summary>掛け金を掛けた守りの名前（掛かっていなければ null）。</summary>
        public string Latch { get; private set; }

        /// <summary>今の一時停止を頼んだ者（"reflex"・"agent"）。人や Error Pause が止めた時・動いている時は null。</summary>
        public string PauseOwner { get; private set; }

        /// <summary>
        /// 今の一時停止を頼んだ者。止めたばかりで Editor の知らせがまだ来ていない時は、頼んだ者を返す
        /// （<c>pause {on:true}</c> の返事の封筒が "editor" にならないように）。
        /// </summary>
        public string PauseOwnerOrPending => this.PauseOwner ?? this.pendingPauseBy;

        /// <summary>止めたまま何コマか進めている途中か（<c>pause {step:N}</c>）。</summary>
        public bool Stepping => this.step != null;

        /// <summary>今見張っている世界の見張り（腕が無い・世界が無い・見張りが落ちた時は null）。</summary>
        public IAgentReflexWatch Watching => this.watching;

        public bool IsArmed(long nowMs) => this.armedUntilMs != 0 && nowMs < this.armedUntilMs;

        /// <summary>腕が外れるまでの秒（腕が無ければ 0）。</summary>
        public int LeftSeconds(long nowMs) => this.IsArmed(nowMs) ? (int)((this.armedUntilMs - nowMs + 999) / 1000) : 0;

        public bool IsActing => this.Moving.IsActing;

        public bool IsAgentActing => this.session.Hands.Running != null && this.session.Hands.Running.Reflex == null;

        public bool IsModeOn(string mode) => this.Moving.IsModeOn(mode);

        /// <summary>腕の ignore＝働いても止めない守りの選び（腕が無ければ空）。</summary>
        public AgentReflexIgnore Ignore { get; } = new AgentReflexIgnore();

        public string Act(string mode, string why, IAgentCommand command, JObject args, JObject backData = null) =>
            this.Moving.Act(mode, why, command, args, backData);

        public bool NoteActed(string mode, string why, JObject backData = null) => this.Moving.NoteActed(mode, why, backData);

        // ── 腕・掛け金 ───────────────────────────────────────────────

        /// <summary>
        /// 腕を掛ける（もう掛かっていれば、長さと決まりを替える）。minutes が 0 なら外す。
        /// ⭐ 見張りが回っていなければ（世界ができる前に掛けた・見張りが落ちた）、ここで始め直す。回っていれば続ける（基準を取り直さない）。
        /// </summary>
        /// <param name="stopBefore">「この時刻より前に止める」（その日の分・無ければ -1）。</param>
        /// <param name="modes">使う動く反射（null＝見張りの全部・空＝使わない）。</param>
        /// <param name="ignore">働いても止めない守りの名前（null・空＝どれも止める）。</param>
        /// <param name="readyWorld">今できている世界（無ければ null＝世界ができた時に見張りを始める）。</param>
        public void Arm(int minutes, int stopBefore, string[] modes, string[] ignore, long nowMs, IAgentWorld readyWorld)
        {
            if (minutes <= 0)
            {
                this.Disarm("asked");
                return;
            }

            bool was = this.IsArmed(nowMs);
            this.armedUntilMs = nowMs + (minutes * 60_000L);
            this.StopBeforeMinute = stopBefore;
            this.Moving.ChooseModes(modes);
            this.Ignore.Choose(ignore);

            var data = new JObject { ["minutes"] = minutes };
            if (stopBefore >= 0)
            {
                data["stopBefore"] = FormatMinute(stopBefore);
            }

            if (modes != null)
            {
                data["modes"] = new JArray(modes);
            }

            if (this.Ignore.Count > 0)
            {
                data["ignore"] = new JArray(this.Ignore.Copy());
            }

            this.session.Signals.Write(AgentFace.Surface, "reflex.armed", false, data);

            // ⭐ 選びから外れた動く反射が走っていれば、ここで終える。
            string acting = this.session.Hands.ReflexActing?.Reflex;
            if (acting != null && !this.Moving.IsChosen(acting))
            {
                this.session.Hands.InterruptReflex("reflex.armed", this.session.Signals.Surface.LastSeq);
            }

            this.HookRedLog();
            if (!was || this.watching == null)
            {
                this.BeginWatch(readyWorld);
            }
        }

        /// <summary>腕を外す（掛かっていなければ何もしない）。⭐ 掛け金は残す（外すのは ack だけ）。</summary>
        public void Disarm(string why)
        {
            if (this.armedUntilMs == 0)
            {
                return;
            }

            this.armedUntilMs = 0;
            this.StopBeforeMinute = -1;
            this.Moving.ChooseModes(null);
            this.Ignore.Clear();
            this.EndWatch();
            this.UnhookRedLog();
            this.session.Signals.Write(AgentFace.Surface, "reflex.disarmed", false, new JObject { ["why"] = why });

            // ⭐ 腕が外れたら、走っている動く反射の命令も終える（見張りが止まった後も食べ続け・逃げ続けない）。
            this.session.Hands.InterruptReflex("reflex.disarmed", this.session.Signals.Surface.LastSeq);
        }

        /// <summary>
        /// 掛け金を外す。外した守りの名前を返す（掛かっていなければ null・何もしない）。
        /// <paramref name="resume"/> なら、止まっている Editor を動かす（何コマか進めている途中なら、止め直さずに進めるのをやめる）。
        /// </summary>
        public string Ack(bool resume)
        {
            string guard = this.Latch;
            if (guard == null)
            {
                return null;
            }

            this.Latch = null;
            this.session.Signals.Write(AgentFace.Surface, "reflex.acked", false, new JObject { ["guard"] = guard });
            if (resume)
            {
                this.CancelStep();
                this.RequestResume(ByAgent);
            }

            AgentHost.NoteStateChanged();
            return guard;
        }

        /// <summary>
        /// 守りが働いた（<see cref="IAgentReflexContext.Trip"/>）。合図を書き、掛け金を掛け、止める守りなら Editor を止める。
        /// </summary>
        public void Trip(string guard, bool stop, JObject backData = null) => this.Trip(guard, stop, backData, true);

        /// <param name="mayIgnore">腕の ignore で選んだ守りなら、止めずに reflex.ignored だけ書くか（見張りが落ちた時は false）。</param>
        private void Trip(string guard, bool stop, JObject backData, bool mayIgnore)
        {
            if (string.IsNullOrEmpty(guard))
            {
                throw new ArgumentException("守りの名前が空。", nameof(guard));
            }

            // ⭐ 表には守りの名前と止めたかだけ。裏には見張りが読んだ値も（反射は読んだ値を表の AI へ渡さない）。
            var surface = new JObject { ["guard"] = guard, ["stop"] = stop };
            var back = new JObject { ["guard"] = guard, ["stop"] = stop };
            if (backData != null)
            {
                foreach (JProperty field in backData.Properties())
                {
                    if (back[field.Name] == null)
                    {
                        back[field.Name] = field.Value.DeepClone();
                    }
                }
            }

            // ⭐ 止めない守りは、合図を書くだけで帰る（掛け金・一時停止・手足の中断をしない）。
            if (mayIgnore && this.Ignore.Contains(guard))
            {
                this.session.Signals.WriteSplit(AgentReflexIgnore.Kind, surface, back);
                return;
            }

            this.session.Signals.WriteSplit("reflex.tripped", surface, back);

            // ⭐ 手足の命令を、この合図で邪魔されたと終え、パッドを中立に戻す。
            this.session.Hands.Interrupt("reflex.tripped", this.session.Signals.Surface.LastSeq);

            // ⭐ 止めない守り（gameover）も掛け金は掛ける＝AI が見て ack するまで、返事の封筒に守りの名前が出る。
            if (this.Latch == null)
            {
                this.Latch = guard;
                Debug.Log("[AgentApi] 止める反射が働いた（" + guard + "）。掛け金を掛けた（外すのは /v1/reflex/ack）");
            }

            AgentHost.NoteStateChanged();
            if (stop)
            {
                this.RequestPause(ByReflex);
            }
        }

        // ── 一時停止（Editor だけ） ─────────────────────────────────────────

        /// <summary>Editor を止める（もう止まっていれば何もしない）。止め方が無い（ビルド）なら false。</summary>
        public bool RequestPause(string by)
        {
            if (!AgentHost.CanWritePause)
            {
                return false;
            }

            if (AgentHost.IsEditorPaused)
            {
                return true;
            }

            this.pendingPauseBy = by;
            AgentHost.WriteEditorPause(true);
            return true;
        }

        /// <summary>止まっている Editor を動かす（動いていれば何もしない）。止め方が無い（ビルド）なら false。</summary>
        public bool RequestResume(string by)
        {
            if (!AgentHost.CanWritePause)
            {
                return false;
            }

            if (!AgentHost.IsEditorPaused)
            {
                return true;
            }

            this.pendingResumeBy = by;
            AgentHost.WriteEditorPause(false);
            return true;
        }

        /// <summary>
        /// 止めたまま <paramref name="frames"/> コマ進める（動かして、進め終えたら止め直す）。始められなければ断りの字を返す。
        /// <paramref name="ticket"/> は、この step だけの控え（進め終えた数が入る＝ほかの step の数と混ざらない）。
        /// </summary>
        public string TryStartStep(int frames, long nowMs, out AgentStepTicket ticket)
        {
            ticket = null;
            if (!AgentHost.CanWritePause)
            {
                return "cannot_pause";
            }

            // ⭐ 進めている間は Editor が動いている＝「止まっていない」より先に見る（Play で、2 本目が pause_first と言われた）。
            if (this.step != null)
            {
                return "stepping";
            }

            if (!AgentHost.IsEditorPaused)
            {
                return "not_paused";
            }

            ticket = new AgentStepTicket(frames, nowMs + StepDeadlineMs);
            this.step = ticket;
            this.stepStartFrame = Time.frameCount;
            this.stepTargetFrame = Time.frameCount + frames;

            // ⭐ 動かす前に「進めている途中」を記録へ書く（進め終えると止まる＝撮影のフックが、その間の撮影を止める）。
            AgentHost.NoteStateChanged();
            this.RequestResume(ByAgent);
            return null;
        }

        /// <summary>何コマか進めている途中なら、止め直さずに、そこで進めるのをやめる（AI が動かすと頼んだ時）。</summary>
        public void CancelStep()
        {
            if (this.step == null)
            {
                return;
            }

            this.FinishStep();
            AgentHost.NoteStateChanged();
        }

        /// <summary>Editor が止まった・動き出した（Editor の係が知らせる）。合図 paused・resumed を、頼んだ者の名で書く。</summary>
        public void OnPauseChanged(bool paused)
        {
            string by;
            if (paused)
            {
                by = this.pendingPauseBy ?? ByEditor;
                this.pendingPauseBy = null;
                this.PauseOwner = by == ByEditor ? null : by;

                // ⭐ 何コマか進める途中に、だれかが止めた時も、そこで進めるのをやめる（進んだ数を答える）。
                // ⚠ 今も止まっている時だけ（前の一時停止の知らせが遅れて届いても、進め始めた step を終えない）。
                if (this.step != null && AgentHost.IsEditorPaused)
                {
                    this.FinishStep();
                }
            }
            else
            {
                by = this.pendingResumeBy ?? ByEditor;
                this.pendingResumeBy = null;
                this.PauseOwner = null;
            }

            this.session.Signals.Write(AgentFace.Surface, paused ? "paused" : "resumed", false, new JObject { ["by"] = by });
        }

        // ── 毎フレーム・世界の出入り・終わり ─────────────────────────────────

        /// <summary>毎フレーム（主スレッド・止まっている間も呼ばれる）。腕の時間切れ・何コマか進める・赤いログ・世界の見張り。</summary>
        public void Tick(long nowMs)
        {
            if (this.armedUntilMs != 0 && nowMs >= this.armedUntilMs)
            {
                this.Disarm("expired");
            }

            bool editorPaused = AgentHost.IsEditorPaused;
            AgentStepTicket running = this.step;
            if (running != null)
            {
                if (!editorPaused)
                {
                    // 進め終えた・上限の秒を過ぎた＝止め直す（止まった知らせで step を終える）。
                    if (Time.frameCount >= this.stepTargetFrame || nowMs >= running.DeadlineMs)
                    {
                        this.RequestPause(ByAgent);
                    }
                }
                else if (nowMs >= running.DeadlineMs)
                {
                    // 止まっているのに、止まった知らせで終わらなかった（念のため・答えを待つ頼みを残さない）。
                    this.CancelStep();
                }
            }

            if (!this.IsArmed(nowMs))
            {
                return;
            }

            // ⭐ 掛け金の間の赤は、合図を重ねずに止めるだけ（同じ赤が毎フレーム出ると帳面が埋まる・赤の字は裏の log.red に残る）。
            if (this.TakeRedLog(out string type, out string text))
            {
                if (this.Latch == null)
                {
                    this.Trip(RedLogGuard, true, new JObject { ["type"] = type, ["text"] = text });
                }
                else if (!this.Ignore.Contains(RedLogGuard))
                {
                    this.RequestPause(ByReflex);
                }
            }

            // ⭐ 世界の見張りは、ゲームが動いている間だけ回す（止まっている間は値が変わらない）。
            IAgentReflexWatch watch = this.watching;
            if (watch == null || editorPaused)
            {
                return;
            }

            try
            {
                watch.Tick(this);
            }
            catch (Exception e)
            {
                // ⚠ 落ちた見張りは、もう回さない（毎フレーム落ちるのを防ぐ）。腕を掛け直すと始め直す。
                // ⭐ 守りが減ったので、赤いログの守りとして止める（裏の無い窓口では知らせが黄に落ちて、赤いログの守りが働かない）。
                this.EndWatch();
                AgentHost.ReportOnce("reflex-tick|" + AgentHost.Epoch, "[AgentApi] 止める反射の見張りが落ちた（腕を掛け直すまで回さない）: " + e);
                // ⚠ 守りが全部止まった印なので、ignore で red_log を選んでいても止める。
                this.Trip(RedLogGuard, true, new JObject { ["type"] = "exception", ["text"] = Clip("reflex watch: " + e.Message) }, false);
            }
        }

        /// <summary>世界ができた。腕が掛かっていれば、この世界の見張りを始める。</summary>
        public void OnWorldReady(IAgentWorld world)
        {
            if (this.IsArmed(AgentSignalHub.NowMs()))
            {
                this.BeginWatch(world);
            }
        }

        /// <summary>世界を畳む。見張りをやめる（見張りは世界と一緒に消える）。</summary>
        public void OnWorldClosing() => this.EndWatch();

        /// <summary>窓口を閉じる（合図は書かない・預かった step は窓口が closing で答える）。</summary>
        public void Close()
        {
            this.armedUntilMs = 0;
            this.Ignore.Clear();
            this.FinishStep();
            this.EndWatch();
            this.UnhookRedLog();
        }

        /// <summary>分（0〜1439）を "HH:MM" にする。</summary>
        public static string FormatMinute(int minute) => (minute / 60).ToString("00") + ":" + (minute % 60).ToString("00");

        private static string Clip(string text) =>
            text == null ? string.Empty : text.Length <= RedTextMax ? text : text.Substring(0, RedTextMax) + "…";

        /// <summary>今の step を、進んだコマの数で終える（止め直しはしない）。</summary>
        private void FinishStep()
        {
            AgentStepTicket finished = this.step;
            if (finished == null)
            {
                return;
            }

            this.step = null;
            finished.Finish(Time.frameCount - this.stepStartFrame);
        }

        private void BeginWatch(IAgentWorld world)
        {
            this.EndWatch();
            this.Moving.Forget();
            IAgentReflexWatch watch = world?.ReflexWatch;
            if (watch == null)
            {
                return;
            }

            try
            {
                watch.Begin(this);
                this.watching = watch;
            }
            catch (Exception e)
            {
                AgentHost.ReportOnce("reflex-begin|" + AgentHost.Epoch, "[AgentApi] 止める反射の見張りを始められなかった: " + e);
                try
                {
                    watch.End();
                }
                catch (Exception)
                {
                    // 始める途中で落ちた見張りは、やめる時にも落ちうる。知らせは上で 1 回鳴らした。
                }
            }
        }

        private void EndWatch()
        {
            IAgentReflexWatch watch = this.watching;
            this.watching = null;
            if (watch == null)
            {
                return;
            }

            try
            {
                watch.End();
            }
            catch (Exception e)
            {
                AgentHost.ReportOnce("reflex-end|" + AgentHost.Epoch, "[AgentApi] 止める反射の見張りをやめる時に落ちた: " + e);
            }
        }

        private void HookRedLog()
        {
            if (this.redHooked)
            {
                return;
            }

            // ⚠ 前に外した時に、ほかのスレッドで走っていた受け口が残した赤を、新しい腕へ持ち込まない。
            lock (this.redGate)
            {
                this.redPending = false;
            }

            this.redHooked = true;
            Application.logMessageReceivedThreaded += this.OnLog;
        }

        private void UnhookRedLog()
        {
            if (!this.redHooked)
            {
                return;
            }

            this.redHooked = false;
            Application.logMessageReceivedThreaded -= this.OnLog;
            lock (this.redGate)
            {
                this.redPending = false;
            }
        }

        /// <summary>赤いログを聞く（どのスレッドからも呼ばれる）。次の Tick で 1 つだけ守りにする。</summary>
        private void OnLog(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert)
            {
                return;
            }

            lock (this.redGate)
            {
                if (this.redPending)
                {
                    return;
                }

                this.redPending = true;
                this.redType = type.ToString().ToLowerInvariant();
                this.redText = Clip(condition);
            }
        }

        private bool TakeRedLog(out string type, out string text)
        {
            lock (this.redGate)
            {
                type = this.redType;
                text = this.redText;
                if (!this.redPending)
                {
                    return false;
                }

                this.redPending = false;
                return true;
            }
        }
    }
}
