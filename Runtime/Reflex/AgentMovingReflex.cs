using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Monster.Balloon.AutomataSystem
{
    /// <summary>
    /// 動く反射の係。Play 1 回ぶん（反射の係 <see cref="AgentReflex"/> が持つ）。
    /// <para>
    /// ⭐ 世界の見張りが <see cref="Act"/> を呼ぶと、この係は合図 reflex.acted を書き、AI の命令を interrupted（by reflex.acted）で終え、
    ///    手足の係で反射の命令を走らせる（AI が借りていれば、その借りのまま。無ければ反射が借りて、終わったら返す）。
    /// ⭐ 走らせるのは、腕の間・腕で選ばれた反射だけ（arm の modes・選ばなければ見張りの全部）。
    ///    掛け金の間・止まっている間・人が触った直後（human_active）は走らせない＝人が先。
    /// ⭐ 同じ反射が 1 分に 3 回動こうとしたら、3 回目は走らせずに、止める守り reflex_loop を働かせる（反射では抜けられない所）。
    /// ⭐ 表の合図には、反射の名前・理由の字・命令の番号だけを書く。見張りが読んだ値（HP など）は裏の帳面だけ。
    /// ⚠ 主スレッドで使う。
    /// </para>
    /// </summary>
    internal sealed class AgentMovingReflex
    {
        /// <summary>同じ動く反射が、この回数（<see cref="LoopWindowMs"/> の内）動こうとしたら止める。</summary>
        public const int LoopCount = 3;

        public const long LoopWindowMs = 60_000;

        /// <summary>動く反射が抜けられない所の守り（芯が自分で見る守り）。</summary>
        public const string LoopGuard = "reflex_loop";

        /// <summary>動く反射が動いた合図。</summary>
        public const string ActedKind = "reflex.acted";

        private readonly AgentHostSession session;
        private readonly AgentReflex reflex;

        /// <summary>動く反射ごとの、動いた時刻（ミリ秒・新しい 1 分の物だけ残す）。</summary>
        private readonly Dictionary<string, List<long>> actTimes = new Dictionary<string, List<long>>(StringComparer.Ordinal);

        private string[] chosen;

        public AgentMovingReflex(AgentHostSession session, AgentReflex reflex)
        {
            this.session = session ?? throw new ArgumentNullException(nameof(session));
            this.reflex = reflex ?? throw new ArgumentNullException(nameof(reflex));
        }

        /// <summary>AI が腕で選んだ動く反射（null＝見張りの全部）。</summary>
        public IReadOnlyList<string> Chosen => this.chosen;

        /// <summary>動く反射の命令が走っているか。</summary>
        public bool IsActing => this.session.Hands.ReflexActing != null;

        /// <summary>腕で選んだ動く反射を替える（null＝見張りの全部・空＝使わない）。</summary>
        public void ChooseModes(string[] modes) => this.chosen = modes;

        /// <summary>腕で選んだ動く反射の写し（掛け直しで前の選びを続ける時に使う・null＝見張りの全部）。</summary>
        public string[] CopyChosen() => this.chosen == null ? null : (string[])this.chosen.Clone();

        /// <summary>動く反射 <paramref name="mode"/> を、AI が腕で選んでいるか（選びが無ければ全部＝true・腕と見張りは見ない）。</summary>
        public bool IsChosen(string mode) => this.chosen == null || Array.IndexOf(this.chosen, mode) >= 0;

        /// <summary>動いた回数を忘れる（新しい世界の見張りを始める時）。</summary>
        public void Forget() => this.actTimes.Clear();

        /// <summary>見張り <paramref name="watch"/> の動く反射のうち、今腕で選ばれている物を <paramref name="into"/> に足す（腕が無い時は足さない）。</summary>
        public void CopyOn(IAgentReflexWatch watch, List<string> into)
        {
            IReadOnlyList<string> offered = watch?.Modes;
            if (offered == null || !this.reflex.IsArmed(AgentSignalHub.NowMs()))
            {
                return;
            }

            foreach (string mode in offered)
            {
                if (!string.IsNullOrEmpty(mode) && (this.chosen == null || Array.IndexOf(this.chosen, mode) >= 0) && !into.Contains(mode))
                {
                    into.Add(mode);
                }
            }
        }

        /// <summary>動く反射 <paramref name="mode"/> が、腕の間で、回っている見張りが持ち、AI が選んでいるか。</summary>
        public bool IsModeOn(string mode)
        {
            if (string.IsNullOrEmpty(mode) || !this.reflex.IsArmed(AgentSignalHub.NowMs()))
            {
                return false;
            }

            IReadOnlyList<string> offered = this.reflex.Watching?.Modes;
            if (offered == null || !Has(offered, mode))
            {
                return false;
            }

            return this.chosen == null || Array.IndexOf(this.chosen, mode) >= 0;
        }

        /// <summary>動く反射が動く（<see cref="IAgentReflexContext.Act"/>）。走らせたら null・走らせなければ理由。</summary>
        public string Act(string mode, string why, IAgentCommand command, JObject args, JObject backData)
        {
            if (command == null)
            {
                throw new ArgumentNullException(nameof(command));
            }

            if (!this.IsModeOn(mode))
            {
                return "off";
            }

            if (this.reflex.Latch != null)
            {
                return "latched";
            }

            if (AgentHost.IsEditorPaused || this.reflex.Stepping)
            {
                return "paused";
            }

            if (this.IsActing)
            {
                return "acting";
            }

            IAgentHands hands = AgentReflexJson.ReadyWorld()?.Hands;
            if (hands == null)
            {
                return "no_hands";
            }

            long now = AgentSignalHub.NowMs();

            // ⭐ 走らせられない時（人が先・手足が無い）は、1 分に 3 回の数えより先に断る（走らない 3 回目で、止める守りを働かせない）。
            string early = this.session.Hands.WhyNotReflex(now, hands);
            if (early != null)
            {
                return early;
            }

            if (this.TripIfLooping(mode, why, now))
            {
                return "loop";
            }

            // ⭐ 合図は、借りが決まってから・AI の命令を終える前に書く（AI の命令の by が、この合図を指す）。
            string refused = this.session.Hands.TryStartReflex(
                mode,
                command,
                args ?? new JObject(),
                now,
                hands,
                actId => this.WriteActed(mode, why, actId, false, backData),
                out _);
            if (refused == null)
            {
                this.Count(mode, now);
            }

            return refused;
        }

        /// <summary>命令の中で動いた反射を帳面に書く（<see cref="IAgentReflexContext.NoteActed"/>）。</summary>
        public bool NoteActed(string mode, string why, JObject backData)
        {
            if (!this.IsModeOn(mode))
            {
                return false;
            }

            long now = AgentSignalHub.NowMs();
            if (this.TripIfLooping(mode, why, now))
            {
                return false;
            }

            this.Count(mode, now);
            this.WriteActed(mode, why, this.session.Hands.Running?.ActId, true, backData);
            return true;
        }

        private static bool Has(IReadOnlyList<string> list, string value)
        {
            foreach (string item in list)
            {
                if (string.Equals(item, value, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>合図 reflex.acted を書き、表の帳面の番号を返す（AI の命令の by に使う）。</summary>
        private long WriteActed(string mode, string why, string actId, bool inCommand, JObject backData)
        {
            var surface = new JObject { ["reflex"] = mode, ["why"] = why, ["act"] = actId };
            if (inCommand)
            {
                // 命令の中で動いた（今の命令は止めていない）。
                surface["inCommand"] = true;
            }

            var back = (JObject)surface.DeepClone();
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

            this.session.Signals.WriteSplit(ActedKind, surface, back);
            return this.session.Signals.Surface.LastSeq;
        }

        /// <summary>この 1 回で、同じ反射が 1 分に 3 回になるなら、止める守りを働かせて true（数えた時刻は捨てる）。</summary>
        private bool TripIfLooping(string mode, string why, long now)
        {
            if (!this.actTimes.TryGetValue(mode, out List<long> times))
            {
                return false;
            }

            times.RemoveAll(t => now - t >= LoopWindowMs);
            if (times.Count < LoopCount - 1)
            {
                return false;
            }

            times.Clear();
            this.reflex.Trip(
                LoopGuard,
                true,
                new JObject { ["reflex"] = mode, ["why"] = why, ["count"] = LoopCount, ["windowSec"] = LoopWindowMs / 1000 });
            return true;
        }

        private void Count(string mode, long now)
        {
            if (!this.actTimes.TryGetValue(mode, out List<long> times))
            {
                times = new List<long>();
                this.actTimes[mode] = times;
            }

            times.Add(now);
        }
    }
}
