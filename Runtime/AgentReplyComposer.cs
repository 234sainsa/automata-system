using System;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace Monster.Balloon.AutomataSystem
{
    /// <summary>
    /// 主スレッドで 1 本の頼みに答える。封筒（api・ok・instance・stamp）を書き、道に中身を書かせ、最後に paused を書く。
    /// ⭐ stamp は道が動く前の姿（seq を次の wait・events に渡すと、道が書いた合図も拾える）。paused は道が動いた後の姿。
    /// </summary>
    internal static class AgentReplyComposer
    {
        /// <summary>
        /// 頼みに答える。道が答えを持ち越した時（長い待ち）は false を返す＝Host が頼みを預かり、次のフレームでまた呼ぶ。
        /// </summary>
        public static bool TryAnswer(AgentCall call, AgentHostSession session, long nowMs, out AgentReply answer)
        {
            IAgentRoute route = session.FindRoute(call.Path);
            if (route == null)
            {
                // 門を通った後に、世界が畳まれた時など。
                answer = AgentHost.WorldState == AgentWorldState.Ready
                    ? AgentReplies.Error(404, "no_route", "see_hello")
                    : AgentReplies.Error(503, "world_not_ready", "wait_world_ready");
                return true;
            }

            // 門でも見ているが、主スレッドでもう一度確かめる（表のトークンで裏の道へ入らない）。
            if (route.Face == AgentFace.Back && call.Face != AgentFace.Back)
            {
                answer = AgentReplies.Error(403, "blind", "use_surface");
                return true;
            }

            // ⭐ 預かった頼みは、答えられるようになるまで封筒も書かない（毎フレームのごみを作らない）。
            var longRoute = route as IAgentLongRoute;
            if (call.State != null && longRoute != null)
            {
                bool due;
                try
                {
                    due = longRoute.IsDue(call, nowMs);
                }
                catch (Exception e)
                {
                    answer = RouteThrew(route, e);
                    return true;
                }

                if (!due)
                {
                    answer = default;
                    return false;
                }
            }

            var sw = new StringWriter(CultureInfo.InvariantCulture);
            var w = new EnvelopeWriter(sw) { CloseOutput = false };
            var reply = new AgentReplyWriter(w);
            try
            {
                w.WriteStartObject();
                // ⭐ 裏の帳面の番号（bseq）は、裏の道だけに書く（表の道を裏のトークンで呼んだ時＝撮る 1 枚の帯消しでも、表の返事に裏の物を載せない）。
                WriteEnvelope(w, session, route.Face == AgentFace.Back ? call.Face : AgentFace.Surface);
                route.Handle(call, reply);

                // ⭐ 止まっているか・掛け金は、道が働いた後の姿で書く（ack の返事に外す前の掛け金を、pause の返事に止める前の姿を載せない）。
                if (!reply.Failed && !reply.Deferred)
                {
                    w.CloseToRoot();
                    WritePaused(w, session);
                }
            }
            catch (Exception e)
            {
                answer = RouteThrew(route, e);
                return true;
            }

            if (reply.Failed)
            {
                answer = AgentReplies.Error(reply.FailStatus, reply.FailCode, reply.FailHint);
                return true;
            }

            if (reply.Deferred)
            {
                if (longRoute == null || call.State == null)
                {
                    // 持ち越してよいのは長い待ちの道だけ（窓口は、ほかの道の答えを 5 秒しか待たない）。
                    AgentHost.ReportOnce(
                        "defer|" + route.Path,
                        "[AgentApi] 道 " + route.Path + " が答えを持ち越したが、長い待ちの道ではない（または控えを置いていない）。");
                    answer = AgentReplies.Error(500, "route_error", "bad_defer");
                    return true;
                }

                answer = default;
                return false;
            }

            // ⭐ Close は、開いたままの入れ物を全部閉じる（道が閉じ忘れても、返事は JSON の形を保つ）。
            w.Close();
            answer = new AgentReply(200, sw.ToString());
            return true;
        }

        private static AgentReply RouteThrew(IAgentRoute route, Exception e)
        {
            AgentHost.ReportOnce(
                "throw|" + AgentHost.Epoch + "|" + route.Path,
                "[AgentApi] 道 " + route.Path + " が落ちた（この世界では 1 回だけ鳴らす）: " + e);
            return AgentReplies.Error(500, "route_error", e.GetType().Name);
        }

        /// <summary>返事の封筒（どの道の返事にも入る欄）。</summary>
        private static void WriteEnvelope(JsonWriter w, AgentHostSession session, AgentFace face)
        {
            w.WritePropertyName("api");
            w.WriteValue(ApiVersion.Current.ToString());
            w.WritePropertyName("ok");
            w.WriteValue(true);

            w.WritePropertyName("instance");
            w.WriteStartObject();
            w.WritePropertyName("pid");
            w.WriteValue(session.ProcessId);
            w.WritePropertyName("playId");
            w.WriteValue(session.PlayId);
            w.WriteEndObject();

            // ⭐ seq＝この返事を書いた時の表の帳面の最後の番号。"<playId>:<seq>" を、次の wait・events の after に渡せる。
            //    裏のトークンの頼みには、裏の帳面の番号（bseq）も書く。
            w.WritePropertyName("stamp");
            w.WriteStartObject();
            w.WritePropertyName("epoch");
            w.WriteValue(AgentHost.Epoch);
            w.WritePropertyName("frame");
            w.WriteValue(Time.frameCount);
            w.WritePropertyName("seq");
            w.WriteValue(session.Signals.Surface.LastSeq);
            if (face == AgentFace.Back && session.Signals.Back != null)
            {
                w.WritePropertyName("bseq");
                w.WriteValue(session.Signals.Back.LastSeq);
            }

            w.WriteEndObject();
        }

        /// <summary>
        /// 返事の paused（止まっているか・だれが止めたか）。道が中身を書いた後に、根の入れ物へ書く。
        /// ⭐ 止める反射の掛け金が掛かっていれば、どの返事にも守りの名前を書く（AI が見落とさないように）。
        /// </summary>
        private static void WritePaused(JsonWriter w, AgentHostSession session)
        {
            w.WritePropertyName("paused");
            w.WriteStartObject();
            w.WritePropertyName("by");
            w.WriteValue(AgentHost.ReadPause());
            string latch = session.Reflex.Latch;
            if (latch != null)
            {
                w.WritePropertyName("latch");
                w.WriteValue(latch);
            }

            w.WriteEndObject();
        }

        /// <summary>
        /// 返事を書く書き手。道が開けたままにした入れ物を、根の 1 段まで閉じられる（その後に paused を根へ書く）。
        /// ⭐ 何段目にいるか（<c>Top</c>）は Newtonsoft の内側の数＝受け継いだ型からだけ読める。
        /// </summary>
        private sealed class EnvelopeWriter : JsonTextWriter
        {
            public EnvelopeWriter(TextWriter textWriter)
                : base(textWriter)
            {
            }

            /// <summary>根の入れ物（返事の {…}）の中まで戻る。</summary>
            public void CloseToRoot()
            {
                while (this.Top > 1)
                {
                    this.WriteEnd();
                }
            }
        }
    }
}
