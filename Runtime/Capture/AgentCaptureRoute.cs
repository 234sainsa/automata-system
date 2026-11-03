using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Monster.Balloon.AutomataSystem
{
    /// <summary>
    /// <c>/v1/capture</c>＝今の画面を 1 枚撮り、同じフレームの表の look と一緒に返す。
    /// <para>
    /// 頼み: <c>{"sections":["texts","people"],"measure":true}</c>。sections は look と同じ（省くと全部）。
    ///   measure:true の時だけ、画面全体・表の人の四角・撮る助言の四角の明るさ（0〜1）を返す（合否は出さない）。
    ///   <c>hideDebugOverlays:true</c> は裏のトークンだけ＝試験の道具の帯などを、その 1 枚だけ消す。
    /// 返事: <c>{"capture":"8f2c1d-3","file":"…/Logs/AgentApi/captures/8f2c1d-3.png","px":{"w":1920,"h":1080},"frame":5120,"epoch":1,"look":{…},"measure":[…]}</c>。
    ///   frame は撮ったフレーム（look も同じフレームで読んだ）・epoch は撮った世界の番号。記録は capture の ID で絵を指す。
    /// ⭐ 頼まれた時だけ、1 秒に 1 枚まで撮る（429 too_soon）。撮っている間の次の頼みは 409 busy。
    /// ⚠ 止まっている間は撮らない（409 paused・フレームの終わりが来ないため）。step の最中は hint wait_step（pause を外すと step が取り消される）。
    /// ⚠ フレームの終わりが 3 秒来なければ 503 no_frame（Game の画面が描かれていない）。頼んだ後に世界が作り直されたら 503 world_changed。
    /// ⭐ 鳴らすのは直すべき物だけ: capture_failed は赤・write_failed（置き場の都合）と古い絵を消せなかった時は黄（どれも 1 回だけ）。
    /// </para>
    /// </summary>
    internal sealed class AgentCaptureRoute : IAgentRoute, IAgentLongRoute
    {
        /// <summary>答えを待つ長さ。⭐ 係は、ファイルにし終えるまでの締め切り（8 秒）で必ず答える＝それより長くしておく。</summary>
        private static readonly TimeSpan CaptureHold = TimeSpan.FromMilliseconds(AgentCaptureDesk.WriteWaitMs + 2000);

        public string Path => "/v1/capture";

        public AgentFace Face => AgentFace.Surface;

        /// <summary>撮るのは A の手の力（self＝lease・act・反射・撮る）。</summary>
        public AgentAbilities Needs => AgentAbilities.Self;

        public TimeSpan MaxHold(JObject body) => CaptureHold;

        /// <summary>預かった頼みは、絵をファイルにし終えた時か、待つ長さを越えた時に答える。</summary>
        public bool IsDue(AgentCall call, long nowMs) =>
            !(call.State is AgentCaptureTicket ticket) || ticket.Finished || AgentHost.CurrentSession == null
            || AgentCaptureDesk.IsOverdue(ticket, nowMs);

        public void Handle(AgentCall call, AgentReplyWriter reply)
        {
            AgentHostSession session = AgentHost.CurrentSession;
            if (session == null)
            {
                reply.Fail(503, "closing", "reopen_after_play");
                return;
            }

            if (call.State is AgentCaptureTicket held)
            {
                Answer(reply, held, session.Captures);
                return;
            }

            JObject body = call.Body;
            if (!AgentReflexJson.TryReadBool(body, "measure", false, out _))
            {
                reply.Fail(400, "bad_measure", "true_or_false");
                return;
            }

            if (!AgentReflexJson.TryReadBool(body, "hideDebugOverlays", false, out bool hide))
            {
                reply.Fail(400, "bad_hideDebugOverlays", "true_or_false");
                return;
            }

            // ⭐ 帯を消すのは裏のトークンだけ（表の AI は、人と同じ画面を見る）。
            if (hide && call.Face != AgentFace.Back)
            {
                reply.Fail(403, "back_only", "use_back_token");
                return;
            }

            IAgentWorld world = AgentReflexJson.ReadyWorld();
            if (world == null)
            {
                reply.Fail(503, "world_not_ready", "wait_world_ready");
                return;
            }

            IAgentCaptureEyes eyes = world.CaptureEyes;
            if (eyes == null)
            {
                reply.Fail(501, "no_capture", "not_built");
                return;
            }

            string bad = eyes.CheckRequest(body, out string badHint);
            if (bad != null)
            {
                reply.Fail(400, bad, badHint);
                return;
            }

            if (AgentHost.IsEditorPaused || session.Reflex.Stepping)
            {
                // ⭐ step の最中は「待つ」（pause を外すと step が取り消される）。
                reply.Fail(409, "paused", session.Reflex.Stepping ? "wait_step" : "resume_first");
                return;
            }

            string why = session.Captures.TryStart(body, hide, eyes, session.HostObject, AgentSignalHub.NowMs(), out AgentCaptureTicket ticket);
            switch (why)
            {
                case null:
                    call.State = ticket;
                    reply.Defer();
                    return;
                case "too_soon":
                    reply.Fail(429, why, "one_per_sec");
                    return;
                case "busy":
                    reply.Fail(409, why, "wait_capture");
                    return;
                default:
                    reply.Fail(503, why, "reopen_after_play");
                    return;
            }
        }

        private static void Answer(AgentReplyWriter reply, AgentCaptureTicket ticket, AgentCaptureDesk desk)
        {
            if (!ticket.Finished)
            {
                // 待つ長さを越えた。撮れていなければ、フレームの終わりが来ていない（止まった・Game の画面が描かれていない）。
                // ⭐ 答えた控えは捨てる＝後からフレームの終わりが来ても撮らない・書かない。
                ticket.Abandon();
                if (!ticket.Shot)
                {
                    reply.Fail(503, "no_frame", AgentHost.IsEditorPaused ? "resume_first" : "game_view_not_drawn");
                }
                else
                {
                    reply.Fail(500, "write_slow", "try_again");
                }

                return;
            }

            switch (ticket.FailCode)
            {
                case null:
                    break;
                case AgentCaptureDesk.WorldChanged:
                    reply.Fail(503, ticket.FailCode, ticket.FailHint);
                    return;
                case AgentCaptureDesk.WriteFailed:
                    desk.WarnOnce(
                        "capture|" + ticket.FailCode,
                        "[AgentApi] 撮る係が絵を書けなかった（置き場の都合・この Play では 1 回だけ鳴らす）: " + ticket.FailDetail);
                    reply.Fail(500, ticket.FailCode, ticket.FailHint);
                    return;
                default:
                    AgentHost.ReportOnce(
                        "capture|" + AgentHost.Epoch + "|" + ticket.FailCode,
                        "[AgentApi] 撮る係が " + ticket.FailCode + " で失敗した（この世界では 1 回だけ鳴らす）: " + ticket.FailDetail);
                    reply.Fail(500, ticket.FailCode, ticket.FailHint);
                    return;
            }

            if (ticket.PruneDetail != null)
            {
                desk.WarnOnce(
                    "capture|prune",
                    "[AgentApi] 撮る係が古い絵を消せなかった（絵は書けた・この Play では 1 回だけ鳴らす）: " + ticket.PruneDetail);
            }

            JsonWriter w = reply.Json;
            w.WritePropertyName("capture");
            w.WriteValue(ticket.Id);
            w.WritePropertyName("file");
            w.WriteValue(ticket.File);
            w.WritePropertyName("px");
            w.WriteStartObject();
            w.WritePropertyName("w");
            w.WriteValue(ticket.Width);
            w.WritePropertyName("h");
            w.WriteValue(ticket.Height);
            w.WriteEndObject();
            w.WritePropertyName("frame");
            w.WriteValue(ticket.Frame);
            w.WritePropertyName("epoch");
            w.WriteValue(ticket.Epoch);

            // 隠した物（裏だけ）。⚠ 隠す口は、頼んだフレームと次の 1 フレームしか隠さない＝それより後に撮れた時は、隠せていない。
            if (ticket.HideOverlays)
            {
                bool inTime = ticket.Frame <= ticket.RequestFrame + 1;
                w.WritePropertyName("hidden");
                w.WriteStartArray();
                if (inTime)
                {
                    foreach (string name in ticket.Hidden)
                    {
                        w.WriteValue(name);
                    }
                }

                w.WriteEndArray();
                if (!inTime && ticket.Hidden.Count > 0)
                {
                    w.WritePropertyName("hiddenMissed");
                    w.WriteValue(true);
                }
            }

            foreach (JProperty field in ticket.Body.Properties())
            {
                w.WritePropertyName(field.Name);
                field.Value.WriteTo(w);
            }
        }
    }
}
