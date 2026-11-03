using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Monster.Balloon.AutomataSystem
{
    /// <summary>
    /// <c>/v1/wait</c>（表の帳面）と <c>/v1/back/wait</c>（裏の帳面）＝頼んだ合図が帳面に来るまで待つ（最大 25 秒）。
    /// <para>
    /// 頼み: <c>{"until":[{"kind":"choices.shown"},"conversation.*"],"after":"&lt;しおり&gt;","timeoutSec":20}</c>。
    ///   until が無ければ、どの合図でもよい。名前の末尾の * は前方一致。after が無ければ「今から」。
    ///   オブジェクトの kind のほかの欄は、中身の絞り（例 <c>{"kind":"reflex.tripped","guard":"hp"}</c>＝guard が hp の時だけ）。
    /// 返事: <c>{"outcome":"…","event":{…}?,"cursor":"…","gap":N?}</c>。outcome は次のどれか。
    ///   hit＝頼んだ合図が来た（event がそれ）・timeout＝時間切れ・paused＝Editor が止まっている（止まっている間は来ない）・
    ///   world_closing＝世界が畳まれた（event がそれ）・reset＝しおりが前の Play の物だった（cursor は今）。
    /// ⭐ cursor は「ここまで見た」印。次の wait と events には、この cursor を渡す（hit の時は、その合図の番号）。
    /// ⭐ until が resumed に当たる時（何でもよい時を含む）は、止まっていても返さずに待つ。
    ///    until が world.closing か world.ready に当たる時は、世界が畳まれても返さずに待つ。
    /// ⚠ 1 つの窓口で同時に待てるのは 4 本まで（越えたら 429 too_many_waits）。
    /// </para>
    /// </summary>
    internal sealed class AgentWaitRoute : IAgentRoute, IAgentLongRoute
    {
        public const int DefaultTimeoutSec = 20;
        public const int MaxTimeoutSec = 25;
        public const int MaxHeld = 4;
        public const int MaxKinds = 16;

        public AgentWaitRoute(AgentFace face)
        {
            this.Face = face;
            this.Path = face == AgentFace.Back ? "/v1/back/wait" : "/v1/wait";
        }

        public string Path { get; }

        public AgentFace Face { get; }

        /// <summary>
        /// 表の帳面は、表の合図を読める力（look と、ゲームが登録した力のうち <see cref="AgentAbilityDef.ReadsSurfaceSignals"/> の物）で待てる。
        /// look の無い時は、画面の字の元をつながないので、来るのは world.* などだけ。
        /// 裏の帳面は back が要る。
        /// </summary>
        public AgentAbilities Needs => this.Face == AgentFace.Back
            ? AgentAbilities.Back
            : AgentAbilityCatalog.Current.SurfaceSignalReaders;

        public TimeSpan MaxHold(JObject body) =>
            AgentSignalJson.TryReadInt(body, "timeoutSec", DefaultTimeoutSec, 0, MaxTimeoutSec, out int seconds)
                ? TimeSpan.FromSeconds(seconds)
                : TimeSpan.Zero;

        public bool IsDue(AgentCall call, long nowMs)
        {
            if (!(call.State is WaitState state))
            {
                return true;
            }

            AgentSignalLedger ledger = AgentHost.CurrentSession?.Signals.LedgerFor(this.Face);
            return ledger == null || Evaluate(state, ledger, nowMs);
        }

        public void Handle(AgentCall call, AgentReplyWriter reply)
        {
            AgentHostSession session = AgentHost.CurrentSession;
            AgentSignalLedger ledger = session?.Signals.LedgerFor(this.Face);
            if (ledger == null)
            {
                reply.Fail(503, "closing", "reopen_after_play");
                return;
            }

            if (!(call.State is WaitState state))
            {
                state = this.Begin(call.Body, session, ledger, reply);
                if (state == null)
                {
                    return;
                }

                if (!Evaluate(state, ledger, AgentSignalHub.NowMs()))
                {
                    if (session.HeldCount >= MaxHeld)
                    {
                        reply.Fail(429, "too_many_waits", "max_" + MaxHeld);
                        return;
                    }

                    call.State = state;
                    reply.Defer();
                    return;
                }
            }

            this.WriteOutcome(reply.Json, session.PlayId, state);
        }

        /// <summary>頼みを読み、待ちの控えを作る。形が悪ければ断りを書いて null。</summary>
        private WaitState Begin(JObject body, AgentHostSession session, AgentSignalLedger ledger, AgentReplyWriter reply)
        {
            if (!AgentSignalJson.TryReadInt(body, "timeoutSec", DefaultTimeoutSec, 0, MaxTimeoutSec, out int seconds))
            {
                reply.Fail(400, "bad_timeout", "0.." + MaxTimeoutSec);
                return null;
            }

            var targets = new List<AgentWaitTarget>();
            if (!AgentSignalJson.TryReadUntil(body, MaxKinds, targets, out string hint))
            {
                reply.Fail(400, "bad_until", hint);
                return null;
            }

            long last = ledger.LastSeq;
            AgentCursor.Result read = AgentCursor.TryRead(body, session.PlayId, this.Face, last, out long after);
            if (!AgentSignalJson.FailOnBadCursor(read, this.Face, reply))
            {
                return null;
            }

            var state = new WaitState(targets, AgentSignalHub.NowMs() + seconds * 1000L);
            if (read == AgentCursor.Result.Stale)
            {
                state.ScannedTo = last;
                state.Outcome = "reset";
                return state;
            }

            state.ScannedTo = read == AgentCursor.Result.Ok ? after : last;
            return state;
        }

        /// <summary>もう答えられるかを決める（答えられる時は Outcome を埋めて true）。</summary>
        private static bool Evaluate(WaitState state, AgentSignalLedger ledger, long nowMs)
        {
            if (state.Outcome != null)
            {
                return true;
            }

            bool found = ledger.TryFindAfter(state.ScannedTo, state.Stops, out AgentSignalEntry hit, out long scannedTo, out long lost);
            state.Lost += lost;
            if (found)
            {
                state.Hit = hit;
                state.HasHit = true;
                state.Outcome = state.MatchesEntry(hit) ? "hit" : "world_closing";
                return true;
            }

            state.ScannedTo = scannedTo;
            if (!state.WaitsThroughPause && IsPaused())
            {
                state.Outcome = "paused";
                return true;
            }

            if (nowMs >= state.DeadlineMs)
            {
                state.Outcome = "timeout";
                return true;
            }

            return false;
        }

        private static bool IsPaused()
        {
            string by = AgentHost.ReadPause();
            return by != "none" && by != "unknown";
        }

        private void WriteOutcome(JsonWriter w, string playId, WaitState state)
        {
            w.WritePropertyName("outcome");
            w.WriteValue(state.Outcome);
            if (state.HasHit)
            {
                w.WritePropertyName("event");
                AgentSignalJson.WriteEntry(w, state.Hit);
            }

            w.WritePropertyName("cursor");
            w.WriteValue(AgentCursor.Format(playId, this.Face, state.HasHit ? state.Hit.Seq : state.ScannedTo));
            if (state.Lost > 0 && state.Outcome != "reset")
            {
                w.WritePropertyName("gap");
                w.WriteValue(state.Lost);
            }
        }

        /// <summary>預かった待ち 1 本の控え（<see cref="AgentCall.State"/> に置く）。</summary>
        private sealed class WaitState
        {
            private readonly List<Target> targets = new List<Target>();
            private readonly bool any;

            public WaitState(List<AgentWaitTarget> until, long deadlineMs)
            {
                this.DeadlineMs = deadlineMs;
                this.any = until.Count == 0;
                foreach (AgentWaitTarget target in until)
                {
                    if (target.Kind == "*" && target.Filter == null)
                    {
                        this.any = true;
                    }

                    // ⭐ 前方一致の頭は、ここで 1 回だけ切り出す（毎フレームの探しで、ごみを作らないため）。
                    bool prefix = target.Kind.EndsWith("*", StringComparison.Ordinal);
                    this.targets.Add(new Target(
                        prefix ? target.Kind.Substring(0, target.Kind.Length - 1) : target.Kind, prefix, target.Filter));
                }

                // ⭐ 止まっても待つか・世界が畳まれても待つかは、名前だけで決める（絞りの有無で、止まる向きに倒さない）。
                this.WaitsThroughPause = this.MatchesKind("resumed");
                bool stopsAtClosing = !this.MatchesKind("world.closing") && !this.MatchesKind("world.ready");

                // ⭐ 探す物は 1 回だけ作る（毎フレームの探しで、ごみを作らないため）。
                this.Stops = entry => this.MatchesEntry(entry) || (stopsAtClosing && entry.Kind == "world.closing");
            }

            public long DeadlineMs { get; }

            public bool WaitsThroughPause { get; }

            /// <summary>帳面を探す時に「ここで止まる」合図か（頼んだ合図と、世界を畳んだ合図）。</summary>
            public Func<AgentSignalEntry, bool> Stops { get; }

            /// <summary>ここまで見た番号（この番号までに、止まる合図は無かった）。</summary>
            public long ScannedTo { get; set; }

            public long Lost { get; set; }

            public string Outcome { get; set; }

            public AgentSignalEntry Hit { get; set; }

            public bool HasHit { get; set; }

            /// <summary>名前だけで見て、頼んだ合図に当たりうるか（中身の絞りは見ない）。</summary>
            public bool MatchesKind(string kind)
            {
                if (this.any)
                {
                    return true;
                }

                foreach (Target target in this.targets)
                {
                    if (target.KindMatches(kind))
                    {
                        return true;
                    }
                }

                return false;
            }

            /// <summary>帳面の 1 行が、頼んだ合図か（名前と、中身の絞りの両方）。</summary>
            public bool MatchesEntry(AgentSignalEntry entry)
            {
                if (this.any)
                {
                    return true;
                }

                JObject data = null;
                bool parsed = false;
                foreach (Target target in this.targets)
                {
                    if (!target.KindMatches(entry.Kind))
                    {
                        continue;
                    }

                    if (target.Filter == null)
                    {
                        return true;
                    }

                    // ⭐ 中身は、名前が当たって絞りがある時だけ読む（読むのは 1 行に 1 回）。
                    if (!parsed)
                    {
                        data = ParseData(entry.DataJson);
                        parsed = true;
                    }

                    if (FilterMatches(target.Filter, data))
                    {
                        return true;
                    }
                }

                return false;
            }

            private static JObject ParseData(string json)
            {
                if (json == null)
                {
                    return null;
                }

                try
                {
                    return JObject.Parse(json);
                }
                catch (JsonException)
                {
                    return null;
                }
            }

            /// <summary>絞りの欄が全部、中身の同じ名前の欄と同じか（null の絞りは「欄が無いか null」）。</summary>
            private static bool FilterMatches(JObject filter, JObject data)
            {
                foreach (JProperty field in filter.Properties())
                {
                    JToken actual = null;
                    bool has = data != null && data.TryGetValue(field.Name, out actual);
                    if (field.Value.Type == JTokenType.Null)
                    {
                        if (has && actual.Type != JTokenType.Null)
                        {
                            return false;
                        }

                        continue;
                    }

                    if (!has || !JToken.DeepEquals(actual, field.Value))
                    {
                        return false;
                    }
                }

                return true;
            }

            /// <summary>待ち物 1 つ（前方一致の頭は切り出し済み）。</summary>
            private readonly struct Target
            {
                private readonly string text;
                private readonly bool prefix;

                public Target(string text, bool prefix, JObject filter)
                {
                    this.text = text;
                    this.prefix = prefix;
                    this.Filter = filter;
                }

                public JObject Filter { get; }

                public bool KindMatches(string kind) =>
                    this.prefix
                        ? kind.StartsWith(this.text, StringComparison.Ordinal)
                        : string.Equals(kind, this.text, StringComparison.Ordinal);
            }
        }
    }
}
