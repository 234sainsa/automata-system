using System.Collections.Generic;
using Newtonsoft.Json;

namespace Monster.Balloon.AutomataSystem
{
    /// <summary>
    /// <c>/v1/events</c>（表の帳面）と <c>/v1/back/events</c>（裏の帳面）＝しおりより後の合図を、古い順に返す。
    /// <para>
    /// 頼み: <c>{"after":"&lt;しおり&gt;","max":100}</c>。after が無ければ、帳面に残っている一番古い合図から返す。
    /// 返事: <c>{"events":[…],"cursor":"…","more":true?,"gap":N?,"reset":true?}</c>。
    /// ⭐ gap＝しおりより後の合図のうち、帳面（4096 行の輪）から押し出されて読めなかった数。
    /// ⭐ reset＝しおりが前の Play の物だった。この道は今の Play の頭から返す。使う側は look で今の姿を読み直す。
    /// </para>
    /// </summary>
    internal sealed class AgentEventsRoute : IAgentRoute
    {
        public const int DefaultMax = 100;
        public const int MaxMax = 500;

        private readonly List<AgentSignalEntry> buffer = new List<AgentSignalEntry>();

        public AgentEventsRoute(AgentFace face)
        {
            this.Face = face;
            this.Path = face == AgentFace.Back ? "/v1/back/events" : "/v1/events";
        }

        public string Path { get; }

        public AgentFace Face { get; }

        /// <summary>
        /// 表の帳面は、表の合図を読める力（look と、ゲームが登録した力のうち <see cref="AgentAbilityDef.ReadsSurfaceSignals"/> の物）で読める。
        /// look の無い時は、画面の字の元をつながないので、帳面に来るのは world.* などだけ。
        /// 裏の帳面は back が要る。
        /// </summary>
        public AgentAbilities Needs => this.Face == AgentFace.Back
            ? AgentAbilities.Back
            : AgentAbilityCatalog.Current.SurfaceSignalReaders;

        public void Handle(AgentCall call, AgentReplyWriter reply)
        {
            AgentHostSession session = AgentHost.CurrentSession;
            AgentSignalLedger ledger = session?.Signals.LedgerFor(this.Face);
            if (ledger == null)
            {
                reply.Fail(503, "closing", "reopen_after_play");
                return;
            }

            if (!AgentSignalJson.TryReadInt(call.Body, "max", DefaultMax, 1, MaxMax, out int max))
            {
                reply.Fail(400, "bad_max", "1.." + MaxMax);
                return;
            }

            AgentCursor.Result read = AgentCursor.TryRead(call.Body, session.PlayId, this.Face, ledger.LastSeq, out long after);
            if (!AgentSignalJson.FailOnBadCursor(read, this.Face, reply))
            {
                return;
            }

            if (read != AgentCursor.Result.Ok)
            {
                after = 0;
            }

            long lost = ledger.ReadAfter(after, max, this.buffer, out long readTo);
            JsonWriter w = reply.Json;
            w.WritePropertyName("events");
            w.WriteStartArray();
            foreach (AgentSignalEntry entry in this.buffer)
            {
                AgentSignalJson.WriteEntry(w, entry);
            }

            w.WriteEndArray();

            // 帳面の字を、次の頼みまで抱えない。
            this.buffer.Clear();

            w.WritePropertyName("cursor");
            w.WriteValue(AgentCursor.Format(session.PlayId, this.Face, readTo));
            if (readTo < ledger.LastSeq)
            {
                w.WritePropertyName("more");
                w.WriteValue(true);
            }

            // ⭐ gap は、使う側が自分のしおりから読んだ時だけ出す（しおりが無い・古い時は、読み落としとは言わない）。
            if (read == AgentCursor.Result.Ok && lost > 0)
            {
                w.WritePropertyName("gap");
                w.WriteValue(lost);
            }

            if (read == AgentCursor.Result.Stale)
            {
                w.WritePropertyName("reset");
                w.WriteValue(true);
            }
        }
    }
}
