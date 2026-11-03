using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Monster.Balloon.AutomataSystem
{
    /// <summary>合図の返事の書き方と、頼みの読み方（events と wait で同じ形にそろえる）。</summary>
    internal static class AgentSignalJson
    {
        /// <summary>帳面の 1 行を JSON のオブジェクトで書く（<c>{"seq","epoch","frame","kind","initial"?,"data"?}</c>）。</summary>
        public static void WriteEntry(JsonWriter w, in AgentSignalEntry entry)
        {
            w.WriteStartObject();
            w.WritePropertyName("seq");
            w.WriteValue(entry.Seq);
            w.WritePropertyName("epoch");
            w.WriteValue(entry.Epoch);
            w.WritePropertyName("frame");
            w.WriteValue(entry.Frame);
            w.WritePropertyName("kind");
            w.WriteValue(entry.Kind);
            if (entry.Initial)
            {
                w.WritePropertyName("initial");
                w.WriteValue(true);
            }

            if (entry.DataJson != null)
            {
                w.WritePropertyName("data");
                w.WriteRawValue(entry.DataJson);
            }

            w.WriteEndObject();
        }

        /// <summary>しおりの読み違いを断りの形にする。読めた時と after が無い時は true。</summary>
        public static bool FailOnBadCursor(AgentCursor.Result read, AgentFace ledger, AgentReplyWriter reply)
        {
            switch (read)
            {
                case AgentCursor.Result.Malformed:
                    reply.Fail(400, "bad_cursor", ledger == AgentFace.Back ? "playId:b<seq>" : "playId:<seq>");
                    return false;
                case AgentCursor.Result.WrongLedger:
                    reply.Fail(400, "wrong_cursor", ledger == AgentFace.Back ? "use_back_cursor" : "use_surface_cursor");
                    return false;
                default:
                    return true;
            }
        }

        /// <summary>整数の欄を読む（無ければ既定の値）。範囲の外や数でない時は false。</summary>
        public static bool TryReadInt(JObject body, string name, int fallback, int min, int max, out int value)
        {
            value = fallback;
            if (body == null || !body.TryGetValue(name, out JToken token) || token.Type == JTokenType.Null)
            {
                return true;
            }

            if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)
            {
                return false;
            }

            // ⚠ (double) で読む（Value<double>() は、long に入らない大きな整数で InvalidCastException）。
            double number = (double)token;
            if (double.IsNaN(number) || number < min || number > max)
            {
                return false;
            }

            value = (int)Math.Round(number);
            return true;
        }

        /// <summary>
        /// 待つ合図の名前を読む（<c>"until":[{"kind":"choices.shown"},"conversation.*"]</c>）。無ければ空＝どの合図でもよい。
        /// ⭐ 名前の末尾の * は前方一致。
        /// </summary>
        public static bool TryReadKinds(JObject body, int maxCount, List<string> into, out string hint)
        {
            into.Clear();
            hint = null;
            if (body == null || !body.TryGetValue("until", out JToken token) || token.Type == JTokenType.Null)
            {
                return true;
            }

            if (token.Type != JTokenType.Array || ((JArray)token).Count > maxCount)
            {
                hint = "array_of_up_to_" + maxCount;
                return false;
            }

            foreach (JToken item in (JArray)token)
            {
                JToken kind = item.Type == JTokenType.Object ? ((JObject)item)["kind"] : item;
                if (kind == null || kind.Type != JTokenType.String || string.IsNullOrWhiteSpace(kind.Value<string>()))
                {
                    hint = "{\"kind\":\"name\"}";
                    return false;
                }

                into.Add(kind.Value<string>().Trim());
            }

            return true;
        }

        /// <summary>
        /// 待つ合図を、中身の絞りと一緒に読む（<c>"until":["world.*",{"kind":"reflex.tripped","guard":"hp"}]</c>）。無ければ空＝どの合図でもよい。
        /// <para>
        /// ⭐ 名前の末尾の * は前方一致。オブジェクトの kind のほかの欄は「合図の中身の同じ名前の欄が、この値と同じ」の絞り。
        /// ⭐ 絞りの値は字・数・真偽・null だけ（null は「その欄が無いか null」）。1 つの待ち物に <see cref="MaxFilterFields"/> 個まで。
        /// </para>
        /// </summary>
        public static bool TryReadUntil(JObject body, int maxCount, List<AgentWaitTarget> into, out string hint)
        {
            into.Clear();
            hint = null;
            if (body == null || !body.TryGetValue("until", out JToken token) || token.Type == JTokenType.Null)
            {
                return true;
            }

            if (token.Type != JTokenType.Array || ((JArray)token).Count > maxCount)
            {
                hint = "array_of_up_to_" + maxCount;
                return false;
            }

            foreach (JToken item in (JArray)token)
            {
                JToken kind = item;
                JObject filter = null;
                if (item.Type == JTokenType.Object)
                {
                    var obj = (JObject)item;
                    kind = obj["kind"];
                    foreach (JProperty field in obj.Properties())
                    {
                        if (field.Name == "kind")
                        {
                            continue;
                        }

                        if (!IsPlainValue(field.Value))
                        {
                            hint = "{\"kind\":\"name\",\"field\":\"value\"}";
                            return false;
                        }

                        filter = filter ?? new JObject();
                        if (filter.Count >= MaxFilterFields)
                        {
                            hint = "up_to_" + MaxFilterFields + "_fields";
                            return false;
                        }

                        filter[field.Name] = field.Value.DeepClone();
                    }
                }

                if (kind == null || kind.Type != JTokenType.String || string.IsNullOrWhiteSpace(kind.Value<string>()))
                {
                    hint = "{\"kind\":\"name\"}";
                    return false;
                }

                into.Add(new AgentWaitTarget(kind.Value<string>().Trim(), filter));
            }

            return true;
        }

        /// <summary>1 つの待ち物に書ける、中身の絞りの数。</summary>
        public const int MaxFilterFields = 8;

        private static bool IsPlainValue(JToken value)
        {
            switch (value.Type)
            {
                case JTokenType.String:
                case JTokenType.Integer:
                case JTokenType.Float:
                case JTokenType.Boolean:
                case JTokenType.Null:
                    return true;
                default:
                    return false;
            }
        }
    }

    /// <summary>wait の待ち物 1 つ（合図の名前と、中身の絞り）。</summary>
    internal readonly struct AgentWaitTarget
    {
        public AgentWaitTarget(string kind, JObject filter)
        {
            this.Kind = kind;
            this.Filter = filter;
        }

        /// <summary>合図の名前（末尾の * は前方一致・"*" だけは何でも）。</summary>
        public string Kind { get; }

        /// <summary>中身の絞り（欄の名前→値）。絞らない時は null。</summary>
        public JObject Filter { get; }
    }
}
