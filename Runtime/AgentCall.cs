using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Monster.Balloon.AutomataSystem
{
    /// <summary>
    /// One request that passed the gate: its path, face, and JSON body.
    /// 門を通った 1 本の頼み。窓口のスレッドが作って Host の列に積み、主スレッドが道へ渡す。
    /// </summary>
    public sealed class AgentCall
    {
        internal AgentCall(string path, AgentFace face, JObject body)
        {
            this.Path = path;
            this.Face = face;
            this.Body = body ?? new JObject();

            // ⭐ 答えを待つ続きは、主スレッドで走らせない（返事を書くのは窓口のスレッドの仕事）。
            this.Completion = new TaskCompletionSource<AgentReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        /// <summary>
        /// The route path.
        /// 道（例 "/v1/hello"）。
        /// </summary>
        public string Path { get; }

        /// <summary>
        /// Which face (surface or back) the caller used.
        /// 使う側がどちらのトークンで入ったか。
        /// </summary>
        public AgentFace Face { get; }

        /// <summary>
        /// The JSON body of the request.
        /// 頼みの本文（JSON のオブジェクト。本文が空なら中身の無いオブジェクト）。
        /// </summary>
        public JObject Body { get; }

        internal TaskCompletionSource<AgentReply> Completion { get; }

        /// <summary>答えを持ち越した道が、次のフレームまで置いておく控え（主スレッドだけが触る）。</summary>
        internal object State { get; set; }
    }
}
