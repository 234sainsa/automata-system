using System.Globalization;
using System.IO;
using Newtonsoft.Json;

namespace Monster.Balloon.AutomataSystem
{
    /// <summary>
    /// One reply: an HTTP-style status and a JSON text.
    /// 窓口が返す 1 つの返事（HTTP の番号と JSON の本文）。
    /// </summary>
    public readonly struct AgentReply
    {
        /// <summary>
        /// Creates a reply.
        /// 返事を作る。
        /// </summary>
        public AgentReply(int status, string json)
        {
            this.Status = status;
            this.Json = json;
        }

        /// <summary>
        /// The HTTP-style status code.
        /// HTTP 風の状態の数。
        /// </summary>
        public int Status { get; }

        /// <summary>
        /// The JSON text of the reply.
        /// 返事の JSON の字。
        /// </summary>
        public string Json { get; }
    }

    /// <summary>断りの返事を作る口。形は全部そろえる（<c>{"api","ok":false,"error":{"code","hint"}}</c>）。</summary>
    internal static class AgentReplies
    {
        public static AgentReply Error(int status, string code, string hint) => new AgentReply(status, ErrorJson(code, hint));

        public static string ErrorJson(string code, string hint)
        {
            var sw = new StringWriter(CultureInfo.InvariantCulture);
            using (var w = new JsonTextWriter(sw))
            {
                w.WriteStartObject();
                w.WritePropertyName("api");
                w.WriteValue(ApiVersion.Current.ToString());
                w.WritePropertyName("ok");
                w.WriteValue(false);
                w.WritePropertyName("error");
                w.WriteStartObject();
                w.WritePropertyName("code");
                w.WriteValue(code);
                if (hint != null)
                {
                    w.WritePropertyName("hint");
                    w.WriteValue(hint);
                }
                w.WriteEndObject();
                w.WriteEndObject();
            }
            return sw.ToString();
        }
    }
}
