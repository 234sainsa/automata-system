using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Monster.Balloon.AutomataSystem
{
    /// <summary>
    /// 腕の ignore＝働いても止めない守りの選び（反射の係の持ち物・<c>POST /v1/reflex/arm</c> の ignore）。
    /// <para>
    /// ⭐ 選べる名前は、芯が見る red_log と reflex_loop に、世界の見張りの守りを足した物（hello の reflex.ignoreChoices）。
    /// ⭐ 選んだ守りが働くと、反射の係は合図 reflex.ignored だけを書く（掛け金・一時停止・手足の中断をしない）。
    /// ⭐ 腕を外すと、選びも消える（次の腕は、どれも止める所から始まる）。
    /// </para>
    /// </summary>
    internal sealed class AgentReflexIgnore
    {
        /// <summary>止めない守りが働いた合図（表は guard と stop だけ・裏には見張りが読んだ値も＝reflex.tripped と同じ形）。</summary>
        public const string Kind = "reflex.ignored";

        /// <summary>1 回の腕で選べる名前の数の上限。</summary>
        public const int MaxNames = 12;

        private readonly HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>選んだ守りの数（選びが無ければ 0）。</summary>
        public int Count => this.names.Count;

        /// <summary>選んだ守りか（腕が無ければ、いつも false）。</summary>
        public bool Contains(string guard) => guard != null && this.names.Contains(guard);

        /// <summary>選びを替える（null・空＝どれも止める）。</summary>
        public void Choose(string[] chosen)
        {
            this.names.Clear();
            if (chosen == null)
            {
                return;
            }

            foreach (string guard in chosen)
            {
                if (!string.IsNullOrEmpty(guard))
                {
                    this.names.Add(guard);
                }
            }
        }

        public void Clear() => this.names.Clear();

        /// <summary>選んだ守りの名前を、名前の順で写す（選びが無ければ空の列）。</summary>
        public string[] Copy()
        {
            var copy = new string[this.names.Count];
            this.names.CopyTo(copy);
            Array.Sort(copy, StringComparer.Ordinal);
            return copy;
        }

        /// <summary>選べる名前（red_log・reflex_loop・見張りの守り）を名前の順で返す。見張りが無ければ芯の 2 つだけ。</summary>
        public static List<string> Choices(IAgentReflexWatch watch)
        {
            var choices = new List<string> { AgentReflex.RedLogGuard, AgentMovingReflex.LoopGuard };
            IReadOnlyList<string> guards = watch?.Guards;
            if (guards != null)
            {
                foreach (string guard in guards)
                {
                    if (!string.IsNullOrEmpty(guard) && !choices.Contains(guard))
                    {
                        choices.Add(guard);
                    }
                }
            }

            choices.Sort(StringComparer.Ordinal);
            return choices;
        }

        /// <summary>
        /// arm の ignore を読む（modes と同じ決まり）。欄が無ければ <paramref name="given"/>＝false。null なら選び無し（<paramref name="chosen"/> も null）。
        /// 名前の列（小文字と _ の 1〜24 字・12 個まで・重なり無し）でなければ false。世界ができていれば、選べない名前も false。
        /// </summary>
        public static bool TryRead(JObject body, IAgentReflexWatch watch, out bool given, out string[] chosen, out string hint)
        {
            given = false;
            chosen = null;
            hint = null;
            if (body == null || !body.TryGetValue("ignore", out JToken token))
            {
                return true;
            }

            given = true;
            if (token.Type == JTokenType.Null)
            {
                return true;
            }

            hint = "array_of_names";
            if (!(token is JArray list) || list.Count > MaxNames)
            {
                return false;
            }

            var read = new List<string>();
            foreach (JToken item in list)
            {
                string name = item.Type == JTokenType.String ? item.Value<string>() : null;
                if (!AgentReflexJson.IsModeName(name) || read.Contains(name))
                {
                    return false;
                }

                read.Add(name);
            }

            if (watch != null)
            {
                List<string> choices = Choices(watch);
                foreach (string name in read)
                {
                    if (!choices.Contains(name))
                    {
                        hint = "see_hello_reflex_ignoreChoices";
                        return false;
                    }
                }
            }

            hint = null;
            chosen = read.ToArray();
            return true;
        }
    }
}
