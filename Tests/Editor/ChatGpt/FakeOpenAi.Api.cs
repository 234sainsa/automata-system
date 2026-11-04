using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Net;
using Newtonsoft.Json.Linq;

namespace Monster.Balloon.AutomataSystem.ChatGpt.Tests
{
    /// <summary>
    /// 偽の OpenAI の API の口（/v1/models・/v1/responses）。
    /// <para>
    /// ⭐ 入口（流れの前）の断りは {"detail": "..."}（401＝トークンが使えない）。errors-and-recovery の「admission」の形。
    /// ⭐ 頼みの中身の断りは {"error": {"code", "param", "message"}}。説明書の preview-limitations に合わない欄は
    ///    subscription_sharing_unsupported_capability（400・param＝その欄）で断る。
    /// </para>
    /// </summary>
    internal sealed partial class FakeOpenAi
    {
        /// <summary>preview-limitations で「送らない」とある欄。</summary>
        internal static readonly string[] UnsupportedFields =
        {
            "background", "conversation", "max_output_tokens", "max_tool_calls", "metadata", "moderation", "multi_agent",
            "prompt", "prompt_cache_retention", "safety_identifier", "temperature", "top_logprobs", "top_p", "truncation", "user",
            "previous_response_id",
        };

        private static readonly JObject[] ModelList =
        {
            new JObject { ["slug"] = "model-a", ["display_name"] = "Model A", ["visibility"] = "list" },
            new JObject { ["slug"] = "model-hidden", ["display_name"] = "Hidden", ["visibility"] = "hide" },
            new JObject { ["slug"] = "model-b", ["display_name"] = "Model B", ["visibility"] = "list" },
        };

        private void Models(HttpListenerContext c)
        {
            if (c.Request.HttpMethod != "GET")
            {
                this.ApiError(c, 403, "subscription_sharing_route_not_supported", null, "models は GET");
                return;
            }

            if (this.Bearer(c.Request) == null)
            {
                this.Admission(c, 401, "トークンが使えない");
                return;
            }

            var list = new JArray();
            foreach (JObject m in ModelList) list.Add(m.DeepClone());
            WriteJson(c.Response, 200, new JObject { ["models"] = list });
        }

        private void ResponsesCall(HttpListenerContext c, string body)
        {
            if (c.Request.HttpMethod != "POST")
            {
                this.ApiError(c, 403, "subscription_sharing_route_not_supported", null, "responses は POST");
                return;
            }

            AccessGrant token = this.Bearer(c.Request);
            if (token == null)
            {
                this.Admission(c, 401, "トークンが使えない");
                return;
            }

            if (!ChatGptAccount.HasPlanScope(token.Scope))
            {
                this.ApiError(c, 403, "chatpass_v2_scope_not_authorized", null, "chatgpt.tokens.use.direct の許しが無い");
                return;
            }

            JObject request;
            try
            {
                request = JObject.Parse(body);
            }
            catch (Exception)
            {
                this.ApiError(c, 400, "invalid_request_error", null, "本文が JSON でない");
                return;
            }

            int index;
            lock (this.gate)
            {
                index = this.ResponseBodies.Count;
                this.ResponseBodies.Add(request);
                this.ResponseHeaders.Add(new NameValueCollection(c.Request.Headers));
            }

            (string param, string why) = BodyProblem(request);
            if (param != null)
            {
                this.ApiError(c, 400, "subscription_sharing_unsupported_capability", param, why);
                return;
            }

            if (!this.PlanEligible)
            {
                this.ApiError(c, 403, "subscription_sharing_user_not_eligible", null, "Plan usage is not available for this user.");
                return;
            }

            if (this.UsageLimitReached)
            {
                this.ApiError(c, 429, "subscription_sharing_usage_limit_exceeded", null, "Usage limit reached.");
                return;
            }

            if (this.Responses == null)
            {
                Write(c.Response, 500, "text/plain", "no responder");
                return;
            }

            (int status, string text) = this.Responses(index, request);
            Write(c.Response, status, status == 200 ? "text/event-stream" : "application/json", text);
        }

        // 頼みの本文が決まり（models-and-inference・preview-limitations）に合わない時の欄と訳。合えば (null, null)。
        private static (string, string) BodyProblem(JObject r)
        {
            if (r["store"]?.Type != JTokenType.Boolean || (bool)r["store"]) return ("store", "store は false");
            if (r["stream"]?.Type != JTokenType.Boolean || !(bool)r["stream"]) return ("stream", "stream は true");
            foreach (string field in UnsupportedFields)
            {
                if (r.ContainsKey(field)) return (field, field + " は送らない");
            }

            string model = r.Value<string>("model");
            bool known = false;
            foreach (JObject m in ModelList) known |= m.Value<string>("slug") == model;
            if (!known) return ("model", "この人が使えるモデルでない");

            if (!(r["input"] is JArray input)) return ("input", "input は配列");
            foreach (JToken item in input)
            {
                if (item.Value<string>("type") == "message" && item.Value<string>("role") == "system") return ("input", "system の項目は送らない（instructions を使う）");
            }

            if (r["instructions"] != null && r["instructions"].Type != JTokenType.String) return ("instructions", "instructions は字");
            if (r["tools"] != null)
            {
                if (!(r["tools"] is JArray tools)) return ("tools", "tools は配列");
                foreach (JToken t in tools)
                {
                    if (t.Value<string>("type") != "function") return ("tools", "使える道具は function だけ");
                    if (string.IsNullOrEmpty(t.Value<string>("name")) || !(t["parameters"] is JObject)) return ("tools", "function に name と parameters が無い");
                }
            }

            if (r["include"] != null)
            {
                if (!(r["include"] is JArray include)) return ("include", "include は配列");
                foreach (JToken v in include)
                {
                    if (v.Type != JTokenType.String) return ("include", "include の中身は字");
                }
            }

            return (null, null);
        }

        private void Admission(HttpListenerContext c, int status, string why)
        {
            this.Refuse(c.Request.Url.AbsolutePath, status.ToString(), why);
            WriteJson(c.Response, status, new JObject { ["detail"] = why });
        }

        private void ApiError(HttpListenerContext c, int status, string code, string param, string message)
        {
            this.Refuse(c.Request.Url.AbsolutePath, code, (param == null ? string.Empty : param + ": ") + message);
            var error = new JObject { ["code"] = code, ["message"] = message, ["param"] = param };
            WriteJson(c.Response, status, new JObject { ["error"] = error });
        }
    }
}
