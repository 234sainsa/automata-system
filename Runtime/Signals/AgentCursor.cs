using System.Globalization;
using Newtonsoft.Json.Linq;

namespace Monster.Balloon.AutomataSystem
{
    /// <summary>
    /// 帳面のしおり。表の帳面は <c>"&lt;playId&gt;:&lt;番号&gt;"</c>、裏の帳面は <c>"&lt;playId&gt;:b&lt;番号&gt;"</c>。
    /// <para>
    /// ⭐ playId を付けるのは、前の Play のしおりを、今の Play の番号と取り違えないため（違えば reset で知らせる）。
    /// ⭐ 裏の帳面に b を付けるのは、表と裏の番号を取り違えないため（取り違えは 400 wrong_cursor で断る）。
    /// </para>
    /// </summary>
    internal static class AgentCursor
    {
        internal enum Result
        {
            /// <summary>頼みに after が無い。</summary>
            Missing,

            Ok,

            /// <summary>形が違う。</summary>
            Malformed,

            /// <summary>もう片方の帳面（表と裏）のしおり。</summary>
            WrongLedger,

            /// <summary>前の Play のしおりか、今の帳面より先の番号。</summary>
            Stale,
        }

        public static string Format(string playId, AgentFace ledger, long seq) =>
            playId + ":" + (ledger == AgentFace.Back ? "b" : string.Empty) + seq.ToString(CultureInfo.InvariantCulture);

        /// <summary>頼みの after を読む。</summary>
        /// <param name="lastSeq">今の帳面の最後の番号（それより先のしおりは Stale）。</param>
        public static Result TryRead(JObject body, string playId, AgentFace ledger, long lastSeq, out long seq)
        {
            seq = 0;
            if (body == null || !body.TryGetValue("after", out JToken token) || token.Type == JTokenType.Null)
            {
                return Result.Missing;
            }

            if (token.Type != JTokenType.String)
            {
                return Result.Malformed;
            }

            string text = token.Value<string>();
            int colon = text.LastIndexOf(':');
            if (colon <= 0 || colon == text.Length - 1)
            {
                return Result.Malformed;
            }

            string id = text.Substring(0, colon);
            string number = text.Substring(colon + 1);
            bool back = number[0] == 'b';
            if (back)
            {
                number = number.Substring(1);
            }

            if (!long.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out seq))
            {
                return Result.Malformed;
            }

            if (back != (ledger == AgentFace.Back))
            {
                return Result.WrongLedger;
            }

            return id == playId && seq <= lastSeq ? Result.Ok : Result.Stale;
        }
    }
}
