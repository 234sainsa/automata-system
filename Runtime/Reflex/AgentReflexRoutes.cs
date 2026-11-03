using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Monster.Balloon.AutomataSystem
{
    /// <summary>
    /// <c>/v1/reflex/arm</c>＝反射の腕を掛ける・外す（止める守り・動く反射）。
    /// <para>
    /// 頼み: <c>{"minutes":30,"stopBefore":"18:00","modes":["flee"]}</c>。minutes は 0〜120 の整数（0＝外す・無ければ 30）。
    ///   stopBefore は「この時刻を過ぎたら止める」（無くても 23:50 では止める）。
    ///   modes は使う動く反射（hello の reflex.modeChoices から選ぶ・null＝全部・[]＝使わない）。知らない名前は 400 bad_modes。
    ///   世界ができる前に名前を書くと 503 world_not_ready（hint modes_after_world＝世界ができてから掛け直す）。
    ///   ignore は、働いても止めない守り（hello の reflex.ignoreChoices から選ぶ・null と []＝どれも止める）。知らない名前は 400 bad_ignore。
    ///   世界ができる前に名前を書くと 503 world_not_ready（hint ignore_after_world）。
    /// 返事: <c>{"reflex":{…}}</c>（hello の reflex と同じ形）。
    /// ⭐ もう掛かっていれば、長さを替える（見張りは続ける）。stopBefore・modes・ignore の欄が無ければ前の決まりを続け、null なら既定へ戻す。
    /// </para>
    /// </summary>
    internal sealed class AgentReflexArmRoute : IAgentRoute
    {
        public string Path => "/v1/reflex/arm";

        public AgentFace Face => AgentFace.Surface;

        /// <summary>反射は A の手の一部（self）。</summary>
        public AgentAbilities Needs => AgentAbilities.Self;

        public void Handle(AgentCall call, AgentReplyWriter reply)
        {
            AgentHostSession session = AgentHost.CurrentSession;
            if (session == null)
            {
                reply.Fail(503, "closing", "reopen_after_play");
                return;
            }

            // ⚠ 小数は断る（0.4 を丸めると 0＝腕を外す頼みに化ける）。
            if (!AgentReflexJson.TryReadWhole(
                    call.Body, "minutes", AgentReflex.DefaultArmMinutes, 0, AgentReflex.MaxArmMinutes, out int minutes))
            {
                reply.Fail(400, "bad_minutes", "0.." + AgentReflex.MaxArmMinutes);
                return;
            }

            if (!AgentReflexJson.TryReadClock(call.Body, "stopBefore", out int stopBefore))
            {
                reply.Fail(400, "bad_stop_before", "HH:MM");
                return;
            }

            IAgentWorld world = AgentReflexJson.ReadyWorld();
            if (!AgentReflexJson.TryReadModes(call.Body, world?.ReflexWatch, out bool modesGiven, out string[] modes, out string modesHint))
            {
                reply.Fail(400, "bad_modes", modesHint);
                return;
            }

            // ⭐ 世界ができる前は、名前を確かめられない＝名前を書いた modes は断る（黙って 200 を返さない）。
            //    null（全部）と []（使わない）は、世界が無くても通す。
            if (world == null && modes != null && modes.Length > 0)
            {
                reply.Fail(503, "world_not_ready", "modes_after_world");
                return;
            }

            // ignore も同じ決まり（名前の形・選べる名前・世界ができる前は名前を書けない）。
            if (!AgentReflexIgnore.TryRead(call.Body, world?.ReflexWatch, out bool ignoreGiven, out string[] ignore, out string ignoreHint))
            {
                reply.Fail(400, "bad_ignore", ignoreHint);
                return;
            }

            if (world == null && ignore != null && ignore.Length > 0)
            {
                reply.Fail(503, "world_not_ready", "ignore_after_world");
                return;
            }

            long now = AgentSignalHub.NowMs();

            // ⭐ 長さだけ延ばす掛け直し（stopBefore の欄が無い）は、前の決まりを続ける（黙って外さない）。
            if ((call.Body == null || !call.Body.ContainsKey("stopBefore")) && session.Reflex.IsArmed(now))
            {
                stopBefore = session.Reflex.StopBeforeMinute;
            }

            // modes も同じ（欄が無い掛け直しは、前の選びを続ける）。
            if (!modesGiven && session.Reflex.IsArmed(now))
            {
                modes = session.Reflex.Moving.CopyChosen();
            }

            if (!ignoreGiven && session.Reflex.IsArmed(now))
            {
                ignore = session.Reflex.Ignore.Copy();
            }

            session.Reflex.Arm(minutes, stopBefore, modes, ignore, now, world);
            AgentReflexJson.Write(reply.Json, session.Reflex, now);
        }
    }

    /// <summary>
    /// <c>/v1/reflex/ack</c>＝掛け金を外し、止まっている Editor を動かす（<c>{"resume":false}</c> なら止めたまま）。
    /// <para>
    /// 返事: <c>{"acked":"hp_low","reflex":{…}}</c>。掛け金が無ければ 409 not_latched。
    /// </para>
    /// </summary>
    internal sealed class AgentReflexAckRoute : IAgentRoute
    {
        public string Path => "/v1/reflex/ack";

        public AgentFace Face => AgentFace.Surface;

        public AgentAbilities Needs => AgentAbilities.Self;

        public void Handle(AgentCall call, AgentReplyWriter reply)
        {
            AgentHostSession session = AgentHost.CurrentSession;
            if (session == null)
            {
                reply.Fail(503, "closing", "reopen_after_play");
                return;
            }

            if (!AgentReflexJson.TryReadBool(call.Body, "resume", true, out bool resume))
            {
                reply.Fail(400, "bad_resume", "true_or_false");
                return;
            }

            string guard = session.Reflex.Ack(resume);
            if (guard == null)
            {
                reply.Fail(409, "not_latched", "nothing_to_ack");
                return;
            }

            JsonWriter w = reply.Json;
            w.WritePropertyName("acked");
            w.WriteValue(guard);
            AgentReflexJson.Write(w, session.Reflex, AgentSignalHub.NowMs());
        }
    }

    /// <summary>
    /// <c>/v1/pause</c>＝Editor を止める・動かす・止めたまま N コマ進める。
    /// <para>
    /// 頼み: <c>{"on":true}</c>（止める）・<c>{"on":false}</c>（動かす）・<c>{"step":N}</c>（1〜120・止まっている時だけ）。
    /// 返事: <c>{"frame":F}</c>。step は、進め終えて止まってから <c>{"stepped":N,"frame":F}</c>。
    ///   途中でだれかが止めた・<c>{"on":false}</c> で動かした・20 秒を過ぎた時は、そこまでに進んだ数で答える。
    /// ⭐ 止まっている間も、窓口は目・合図・hello に答える（Editor の係が列を回す）。
    /// ⚠ 掛け金の間は <c>{"on":false}</c> を 409 reflex_latched で断る（動かすのは reflex/ack）。step は通す。
    /// ⚠ step は 1 つずつ（進めている途中の step は 409 stepping・hint wait_step）。
    /// ⚠ ビルドには止め方が無い＝501 cannot_pause。
    /// </para>
    /// </summary>
    internal sealed class AgentPauseRoute : IAgentRoute, IAgentLongRoute
    {
        /// <summary>
        /// step の答えを待つ長さ。⭐ 芯は <see cref="AgentReflex.StepDeadlineMs"/>（20 秒）で必ず止め直して答える＝それより長くしておく。
        /// </summary>
        private static readonly TimeSpan StepHold = TimeSpan.FromSeconds(30);

        public string Path => "/v1/pause";

        public AgentFace Face => AgentFace.Surface;

        public AgentAbilities Needs => AgentAbilities.Self;

        public TimeSpan MaxHold(JObject body) =>
            body != null && body.TryGetValue("step", out JToken step) && step.Type != JTokenType.Null ? StepHold : TimeSpan.Zero;

        /// <summary>預かった step は、その控えが終わった時に答える（ほかの step の終わりでは答えない）。</summary>
        public bool IsDue(AgentCall call, long nowMs) =>
            !(call.State is AgentStepTicket ticket) || ticket.Done || AgentHost.CurrentSession == null;

        public void Handle(AgentCall call, AgentReplyWriter reply)
        {
            AgentHostSession session = AgentHost.CurrentSession;
            if (session == null)
            {
                reply.Fail(503, "closing", "reopen_after_play");
                return;
            }

            AgentReflex reflex = session.Reflex;
            JsonWriter w = reply.Json;
            if (call.State is AgentStepTicket held)
            {
                // 預かった step の答え（進め終えて止まった・だれかが止めた・動かすと頼まれた・上限の秒を過ぎた）。
                w.WritePropertyName("stepped");
                w.WriteValue(held.Stepped);
                w.WritePropertyName("frame");
                w.WriteValue(Time.frameCount);
                return;
            }

            if (!AgentHost.CanWritePause)
            {
                reply.Fail(501, "cannot_pause", "editor_only");
                return;
            }

            bool hasStep = call.Body != null && call.Body.TryGetValue("step", out JToken stepToken) && stepToken.Type != JTokenType.Null;
            JToken onToken = null;
            bool hasOn = call.Body != null && call.Body.TryGetValue("on", out onToken) && onToken.Type != JTokenType.Null;
            if (hasStep == hasOn)
            {
                reply.Fail(400, "bad_pause", "on_or_step");
                return;
            }

            if (hasStep)
            {
                if (!AgentReflexJson.TryReadWhole(call.Body, "step", 1, 1, AgentReflex.MaxStepFrames, out int frames))
                {
                    reply.Fail(400, "bad_step", "1.." + AgentReflex.MaxStepFrames);
                    return;
                }

                string why = reflex.TryStartStep(frames, AgentSignalHub.NowMs(), out AgentStepTicket ticket);
                if (why != null)
                {
                    reply.Fail(409, why, StepHint(why));
                    return;
                }

                call.State = ticket;
                reply.Defer();
                return;
            }

            if (onToken.Type != JTokenType.Boolean)
            {
                reply.Fail(400, "bad_pause", "on_true_or_false");
                return;
            }

            if (onToken.Value<bool>())
            {
                // 何コマか進める途中なら、止まった知らせで step も終わる（進んだ数で答える）。
                reflex.RequestPause(AgentReflex.ByAgent);
            }
            else
            {
                if (reflex.Latch != null)
                {
                    reply.Fail(409, "reflex_latched", "reflex_ack");
                    return;
                }

                // ⭐ 何コマか進める途中なら、そこで進めるのをやめて、動かしたままにする（止め直さない）。
                reflex.CancelStep();
                reflex.RequestResume(AgentReflex.ByAgent);
            }

            w.WritePropertyName("frame");
            w.WriteValue(Time.frameCount);
        }

        private static string StepHint(string why)
        {
            switch (why)
            {
                case "not_paused":
                    return "pause_first";
                case "stepping":
                    return "wait_step";
                case "cannot_pause":
                    return "editor_only";
                default:
                    return null;
            }
        }
    }

    /// <summary>反射の様子の書き方と、反射の道の頼みの読み方（hello・arm・ack で同じ形）。</summary>
    internal static class AgentReflexJson
    {
        /// <summary>
        /// <c>"reflex":{"armed":true,"leftSec":1800,"stopBefore":"18:00","guards":[…],"modes":[…],"modeChoices":[…],"latch":null}</c> を書く。
        /// ⭐ guards は、腕の間は今回っている見張りの守りと、芯が見る赤いログ（見張りが落ちた・世界ができる前なら red_log だけ）。
        ///    腕が無い時は、今の世界で腕を掛けたら見る守り（回っていない守りを「見ている」と言わない）。
        ///    動く反射がある世界では、芯が見る reflex_loop（同じ反射が 1 分に 3 回）も並べる。
        /// ⭐ modes は今使っている動く反射（腕が無ければ空）。modeChoices は、この世界で選べる動く反射（arm の modes に書ける名前）。
        /// ⭐ ignored は、働いても止めない守り（腕が無ければ空）。ignoreChoices は、arm の ignore に書ける名前。
        /// </summary>
        public static void Write(JsonWriter w, AgentReflex reflex, long nowMs)
        {
            bool armed = reflex.IsArmed(nowMs);
            w.WritePropertyName("reflex");
            w.WriteStartObject();
            w.WritePropertyName("armed");
            w.WriteValue(armed);
            w.WritePropertyName("leftSec");
            w.WriteValue(reflex.LeftSeconds(nowMs));
            w.WritePropertyName("stopBefore");
            w.WriteValue(reflex.StopBeforeMinute >= 0 ? AgentReflex.FormatMinute(reflex.StopBeforeMinute) : null);

            var guards = new List<string> { AgentReflex.RedLogGuard };
            IAgentReflexWatch source = armed ? reflex.Watching : ReadyWorld()?.ReflexWatch;
            IReadOnlyList<string> world = source?.Guards;
            if (world != null)
            {
                foreach (string guard in world)
                {
                    if (!string.IsNullOrEmpty(guard) && !guards.Contains(guard))
                    {
                        guards.Add(guard);
                    }
                }
            }

            var choices = new List<string>();
            IReadOnlyList<string> offered = source?.Modes;
            if (offered != null)
            {
                foreach (string mode in offered)
                {
                    if (!string.IsNullOrEmpty(mode) && !choices.Contains(mode))
                    {
                        choices.Add(mode);
                    }
                }
            }

            var on = new List<string>();
            reflex.Moving.CopyOn(armed ? source : null, on);

            // ⭐ reflex_loop は、動く反射を 1 つでも使う時だけ並べる（腕の間は選んだ反射・腕が無ければ選べる反射＝modes:[] で並べない）。
            if ((armed ? on.Count : choices.Count) > 0 && !guards.Contains(AgentMovingReflex.LoopGuard))
            {
                guards.Add(AgentMovingReflex.LoopGuard);
            }

            guards.Sort(StringComparer.Ordinal);
            WriteNames(w, "guards", guards);

            on.Sort(StringComparer.Ordinal);
            WriteNames(w, "modes", on);
            choices.Sort(StringComparer.Ordinal);
            WriteNames(w, "modeChoices", choices);

            WriteNames(w, "ignored", armed ? new List<string>(reflex.Ignore.Copy()) : new List<string>());
            WriteNames(w, "ignoreChoices", AgentReflexIgnore.Choices(source));

            w.WritePropertyName("latch");
            w.WriteValue(reflex.Latch);
            w.WriteEndObject();
        }

        /// <summary>
        /// arm の modes を読む。欄が無ければ <paramref name="given"/>＝false。null なら既定（全部＝<paramref name="modes"/> も null）。
        /// 名前の列（小文字と _ の 1〜24 字・8 個まで・重なり無し）でなければ false。世界ができていれば、見張りが持たない名前も false。
        /// </summary>
        public static bool TryReadModes(JObject body, IAgentReflexWatch watch, out bool given, out string[] modes, out string hint)
        {
            given = false;
            modes = null;
            hint = null;
            if (body == null || !body.TryGetValue("modes", out JToken token))
            {
                return true;
            }

            given = true;
            if (token.Type == JTokenType.Null)
            {
                return true;
            }

            hint = "array_of_names";
            if (!(token is JArray list) || list.Count > 8)
            {
                return false;
            }

            var names = new List<string>();
            foreach (JToken item in list)
            {
                string name = item.Type == JTokenType.String ? item.Value<string>() : null;
                if (!IsModeName(name) || names.Contains(name))
                {
                    return false;
                }

                names.Add(name);
            }

            IReadOnlyList<string> offered = watch?.Modes;
            if (offered != null)
            {
                foreach (string name in names)
                {
                    if (!Contains(offered, name))
                    {
                        hint = "see_hello_reflex_modeChoices";
                        return false;
                    }
                }
            }

            hint = null;
            modes = names.ToArray();
            return true;
        }

        /// <summary>反射の名前の形か（小文字と _ の 1〜24 字・modes と ignore で同じ）。</summary>
        internal static bool IsModeName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 24)
            {
                return false;
            }

            foreach (char c in name)
            {
                if (!((c >= 'a' && c <= 'z') || c == '_'))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool Contains(IReadOnlyList<string> list, string value)
        {
            foreach (string item in list)
            {
                if (string.Equals(item, value, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static void WriteNames(JsonWriter w, string field, List<string> names)
        {
            w.WritePropertyName(field);
            w.WriteStartArray();
            foreach (string name in names)
            {
                w.WriteValue(name);
            }

            w.WriteEndArray();
        }

        /// <summary>今できている世界（できていなければ null）。</summary>
        public static IAgentWorld ReadyWorld() =>
            AgentHost.WorldState == AgentWorldState.Ready ? AgentHost.CurrentWorld : null;

        /// <summary>
        /// 整数の欄を読む（無ければ fallback）。形が悪い・範囲の外・小数（1.0 のような整数の値は通す）なら false。
        /// ⚠ 丸めない（<c>AgentSignalJson.TryReadInt</c> は丸める＝腕の 0.4 分が 0＝外す頼みに化ける）。
        /// </summary>
        public static bool TryReadWhole(JObject body, string name, int fallback, int min, int max, out int value)
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
            if (double.IsNaN(number) || double.IsInfinity(number) || Math.Floor(number) != number || number < min || number > max)
            {
                return false;
            }

            value = (int)number;
            return true;
        }

        /// <summary>"HH:MM" の欄を分（0〜1439）で読む。欄が無い・null なら -1。形が悪ければ false。</summary>
        public static bool TryReadClock(JObject body, string name, out int minute)
        {
            minute = -1;
            if (body == null || !body.TryGetValue(name, out JToken token) || token.Type == JTokenType.Null)
            {
                return true;
            }

            if (token.Type != JTokenType.String)
            {
                return false;
            }

            string[] parts = token.Value<string>().Split(':');
            if (parts.Length != 2
                || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int hour)
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int min)
                || hour < 0 || hour > 23 || min < 0 || min > 59)
            {
                return false;
            }

            minute = (hour * 60) + min;
            return true;
        }

        /// <summary>真偽の欄を読む（無ければ fallback）。形が悪ければ false。</summary>
        public static bool TryReadBool(JObject body, string name, bool fallback, out bool value)
        {
            value = fallback;
            if (body == null || !body.TryGetValue(name, out JToken token) || token.Type == JTokenType.Null)
            {
                return true;
            }

            if (token.Type != JTokenType.Boolean)
            {
                return false;
            }

            value = token.Value<bool>();
            return true;
        }
    }
}
