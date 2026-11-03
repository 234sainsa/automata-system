using Newtonsoft.Json;

namespace Monster.Balloon.AutomataSystem
{
    /// <summary>
    /// Writes one reply as JSON; a route fills it in.
    /// 道が返事を書く口。Host が封筒（api・ok・instance・stamp・paused）を先に書き、道は自分の欄だけを足す。
    /// </summary>
    public sealed class AgentReplyWriter
    {
        internal AgentReplyWriter(JsonWriter json)
        {
            this.Json = json;
        }

        /// <summary>
        /// The JSON writer of the reply body.
        /// 返事の根のオブジェクトの中に居る書き手。道は、ここへ欄（プロパティ）を書く。
        /// </summary>
        public JsonWriter Json { get; }

        internal bool Failed { get; private set; }

        internal int FailStatus { get; private set; }

        internal string FailCode { get; private set; }

        internal string FailHint { get; private set; }

        /// <summary>道が答えを持ち越した（<see cref="Defer"/>）。</summary>
        internal bool Deferred { get; private set; }

        /// <summary>
        /// 今は答えず、答えを持ち越す（<see cref="IAgentLongRoute"/> の道だけが呼ぶ）。
        /// Host は書きかけの欄を捨てて頼みを預かり、道の <see cref="IAgentLongRoute.IsDue"/> が true になった時に、もう一度道を呼ぶ。
        /// </summary>
        internal void Defer() => this.Deferred = true;

        /// <summary>
        /// Writes an error reply with a status, a code, and a hint.
        /// 頼みを断る。Host は書きかけの欄を捨て、断りの形（<c>"ok":false,"error":{…}</c>）で返す。
        /// </summary>
        public void Fail(int status, string code, string hint = null)
        {
            this.Failed = true;
            this.FailStatus = status;
            this.FailCode = code;
            this.FailHint = hint;
        }
    }
}
