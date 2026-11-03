using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Monster.Balloon.AutomataSystem
{
    /// <summary>
    /// Play 1 回ぶんの合図の係。帳面（表と裏）を持ち、世界の合図の元をつないだり外したりする。
    /// <para>
    /// ⭐ この係が元をつなぐのは「世界ができている・使う側が 60 秒以内に頼みを送ったか、止める反射の腕が掛かっている」間だけ。
    /// ⭐ Host が自分で書く合図（world.*・paused・resumed）と赤いログは、使う側が黙っている間も書く（後から読めるように）。
    /// ⚠ 書く口（<see cref="Write"/>）は、赤いログのために主スレッドの外からも呼ばれる。
    /// </para>
    /// </summary>
    internal sealed class AgentSignalHub : IAgentSignalSink
    {
        /// <summary>使う側が黙っていても、元をつないでおく長さ。</summary>
        public const long ActiveWindowMs = 60_000;

        private const int RedTextMax = 800;
        private const int RedStackLines = 8;

        [ThreadStatic]
        private static bool writingRed;

        private readonly int mainThreadId;
        private readonly AgentAbilities abilities;
        private readonly List<IAgentSignalSource> attached = new List<IAgentSignalSource>();

        /// <summary>
        /// 合図の元が「観察ではない」と名乗った合図の名前（この Play でつないだ元の分を、外した後も覚えておく）。
        /// ⚠ 主スレッドだけが触る。
        /// </summary>
        private readonly HashSet<string> notObservation = new HashSet<string>(StringComparer.Ordinal);

        private long lastRequestMs;
        private volatile int lastFrame;
        private IAgentWorld attachedWorld;
        private bool redLogOn;

        /// <param name="withBack">裏の帳面を作るか（裏のトークンがある時だけ）。</param>
        /// <param name="abilities">窓口で開いた力。この係は、力に合う元だけをつなぐ。</param>
        public AgentSignalHub(bool withBack, AgentAbilities abilities)
        {
            this.mainThreadId = Thread.CurrentThread.ManagedThreadId;
            this.abilities = abilities;
            this.Surface = new AgentSignalLedger();
            this.Back = withBack ? new AgentSignalLedger() : null;
        }

        /// <summary>表の帳面（表の合図だけ）。</summary>
        public AgentSignalLedger Surface { get; }

        /// <summary>裏の帳面（表と裏の全部の合図）。製品版では null。</summary>
        public AgentSignalLedger Back { get; }

        /// <summary>今、世界の合図の元をつないでいるか。</summary>
        public bool IsAttached => this.attachedWorld != null;

        /// <summary>最後のフレームで、元を回すのにかかった時間（ミリ秒）。</summary>
        public double LastTickMs { get; private set; }

        /// <summary>この Play で、元を回すのに一番かかった時間（ミリ秒）。</summary>
        public double MaxTickMs { get; private set; }

        public static long NowMs() => Stopwatch.GetTimestamp() * 1000L / Stopwatch.Frequency;

        public AgentSignalLedger LedgerFor(AgentFace face) => face == AgentFace.Back ? this.Back : this.Surface;

        /// <summary>
        /// その名前の合図が、世界を見て書いた物か（元が <see cref="AgentSignalKind.Observation"/> を false にした名前なら false）。
        /// ⚠ 主スレッドから呼ぶ。
        /// </summary>
        public bool IsObservation(string kind) => kind != null && !this.notObservation.Contains(kind);

        /// <summary>使う側が 60 秒以内に頼みを送ったか。</summary>
        public bool IsActive(long nowMs)
        {
            long last = Interlocked.Read(ref this.lastRequestMs);
            return last != 0 && nowMs - last < ActiveWindowMs;
        }

        /// <summary>頼みが来た（窓口のスレッドから呼ばれる）。</summary>
        public void NoteRequest() => Interlocked.Exchange(ref this.lastRequestMs, NowMs());

        public void Write(AgentFace face, string kind, bool initial, JObject data = null)
        {
            if (string.IsNullOrEmpty(kind))
            {
                throw new ArgumentException("合図の名前が空。", nameof(kind));
            }

            string json = data?.ToString(Formatting.None);
            int frame = Thread.CurrentThread.ManagedThreadId == this.mainThreadId ? Time.frameCount : this.lastFrame;
            int epoch = AgentHost.Epoch;
            if (face == AgentFace.Surface)
            {
                this.Surface.Append(epoch, frame, kind, initial, json);
            }

            this.Back?.Append(epoch, frame, kind, initial, json);
        }

        /// <summary>裏の帳面だけに 1 つ書き、その番号を返す（裏の帳面が無ければ 0）。⚠ 主スレッドから呼ぶ。</summary>
        public long WriteBack(string kind, JObject data = null)
        {
            if (string.IsNullOrEmpty(kind))
            {
                throw new ArgumentException("合図の名前が空。", nameof(kind));
            }

            if (this.Back == null)
            {
                return 0;
            }

            return this.Back.Append(AgentHost.Epoch, Time.frameCount, kind, false, data?.ToString(Formatting.None));
        }

        /// <summary>
        /// 同じ合図を、表と裏で中身を変えて書く（表には画面で言える物だけ・裏には本当の値も＝止める反射の reflex.tripped）。
        /// ⚠ 主スレッドから呼ぶ。
        /// </summary>
        public void WriteSplit(string kind, JObject surfaceData, JObject backData)
        {
            if (string.IsNullOrEmpty(kind))
            {
                throw new ArgumentException("合図の名前が空。", nameof(kind));
            }

            int frame = Time.frameCount;
            int epoch = AgentHost.Epoch;
            this.Surface.Append(epoch, frame, kind, false, surfaceData?.ToString(Formatting.None));
            this.Back?.Append(epoch, frame, kind, false, backData?.ToString(Formatting.None));
        }

        /// <summary>毎フレーム（主スレッド）。元をつなぐか外すかを決め、つないだ元を回す。</summary>
        /// <param name="keepAttached">使う側が黙っていても元をつないでおくか（止める反射の腕の間）。</param>
        public void Tick(bool keepAttached)
        {
            this.lastFrame = Time.frameCount;
            long now = NowMs();
            IAgentWorld world = AgentHost.WorldState == AgentWorldState.Ready ? AgentHost.CurrentWorld : null;
            bool want = world != null && (keepAttached || this.IsActive(now));
            if (this.attachedWorld != null && (!want || !ReferenceEquals(this.attachedWorld, world)))
            {
                this.DetachSources();
            }

            if (want && this.attachedWorld == null)
            {
                this.AttachSources(world, now);
            }

            if (this.attached.Count == 0)
            {
                this.LastTickMs = 0;
                return;
            }

            long started = Stopwatch.GetTimestamp();
            for (int i = 0; i < this.attached.Count; i++)
            {
                IAgentSignalSource source = this.attached[i];
                try
                {
                    source.Tick(this, now);
                }
                catch (Exception e)
                {
                    ReportSourceError("tick", source, e);
                }
            }

            double ms = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
            this.LastTickMs = ms;
            if (ms > this.MaxTickMs)
            {
                this.MaxTickMs = ms;
            }
        }

        /// <summary>世界の合図の元を全部外す（世界を畳む時・窓口を閉じる時にも呼ぶ・何度呼んでもよい）。</summary>
        public void DetachSources()
        {
            this.attachedWorld = null;
            foreach (IAgentSignalSource source in this.attached)
            {
                try
                {
                    source.Detach();
                }
                catch (Exception e)
                {
                    ReportSourceError("detach", source, e);
                }
            }

            this.attached.Clear();
        }

        /// <summary>赤いログを裏の帳面へ書き始める（裏の帳面がある版だけ）。</summary>
        public void StartRedLog()
        {
            if (this.Back == null || this.redLogOn)
            {
                return;
            }

            this.redLogOn = true;
            Application.logMessageReceivedThreaded += this.OnLog;
        }

        public void StopRedLog()
        {
            if (!this.redLogOn)
            {
                return;
            }

            this.redLogOn = false;
            Application.logMessageReceivedThreaded -= this.OnLog;
        }

        private void AttachSources(IAgentWorld world, long nowMs)
        {
            this.attachedWorld = world;
            IReadOnlyList<IAgentSignalSource> sources = world.SignalSources;
            if (sources == null)
            {
                return;
            }

            foreach (IAgentSignalSource source in sources)
            {
                if (source == null)
                {
                    continue;
                }

                // ⭐ 開いた力に合わない元はつながない（look の無い窓口は、画面の字の元を回さない）。
                AgentAbilities needs;
                try
                {
                    needs = source.Needs;
                }
                catch (Exception e)
                {
                    ReportSourceError("needs", source, e);
                    continue;
                }

                if (!this.abilities.Allows(needs))
                {
                    continue;
                }

                this.NoteKinds(source);
                try
                {
                    source.Attach(this, nowMs);
                    this.attached.Add(source);
                }
                catch (Exception e)
                {
                    ReportSourceError("attach", source, e);
                    try
                    {
                        source.Detach();
                    }
                    catch (Exception)
                    {
                        // つなぐ途中で落ちた元は、外す時にも落ちうる。知らせは上で 1 回鳴らした。
                    }
                }
            }
        }

        /// <summary>元が「観察ではない」と名乗った合図の名前を覚える（この Play の間は消さない＝外した後の帳面の行も見分ける）。</summary>
        private void NoteKinds(IAgentSignalSource source)
        {
            IReadOnlyList<AgentSignalKind> kinds;
            try
            {
                kinds = source.Kinds;
            }
            catch (Exception e)
            {
                ReportSourceError("kinds", source, e);
                return;
            }

            if (kinds == null)
            {
                return;
            }

            for (int i = 0; i < kinds.Count; i++)
            {
                if (!kinds[i].Observation && !string.IsNullOrEmpty(kinds[i].Name))
                {
                    this.notObservation.Add(kinds[i].Name);
                }
            }
        }

        private void OnLog(string condition, string stackTrace, LogType type)
        {
            if ((type != LogType.Error && type != LogType.Exception && type != LogType.Assert) || writingRed)
            {
                return;
            }

            writingRed = true;
            try
            {
                var data = new JObject
                {
                    ["type"] = type.ToString().ToLowerInvariant(),
                    ["text"] = Clip(condition, RedTextMax),
                };
                string stack = FirstLines(stackTrace, RedStackLines);
                if (stack != null)
                {
                    data["stack"] = stack;
                }

                this.Write(AgentFace.Back, "log.red", false, data);
            }
            catch (Exception)
            {
                // 赤の知らせを書く途中で落ちても、ゲームのログは止めない（ここで鳴らすと、また自分が呼ばれる）。
            }
            finally
            {
                writingRed = false;
            }
        }

        private static void ReportSourceError(string what, IAgentSignalSource source, Exception e)
        {
            string name = source.GetType().Name;
            AgentHost.ReportOnce(
                "signal-" + what + "|" + AgentHost.Epoch + "|" + name,
                "[AgentApi] 合図の元 " + name + " が " + what + " で落ちた（この世界では 1 回だけ鳴らす）: " + e);
        }

        private static string Clip(string text, int max)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            return text.Length <= max ? text : text.Substring(0, max) + "…";
        }

        private static string FirstLines(string text, int lines)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            int at = -1;
            for (int i = 0; i < lines; i++)
            {
                at = text.IndexOf('\n', at + 1);
                if (at < 0)
                {
                    return text.TrimEnd();
                }
            }

            return text.Substring(0, at).TrimEnd();
        }
    }
}
