using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Monster.Balloon.AutomataSystem
{
    /// <summary>
    /// <c>/v1/back/screen</c>＝Editor の Game ビューの大きさを替える・元へ戻す（裏のトークンだけ・Editor だけ）。
    /// <para>
    /// ⭐ 足した訳: 確かめ係が、縦（1080x2340）と横（1920x1080）の見え方を撮るたびに、Eval で Game ビューの大きさを替えていた。
    ///    橋の撮る道具の form（portrait・landscape・both）が、この道で替えてから撮り、撮り終えたら戻す。
    /// 頼み: <c>{"size":[1080,2340]}</c>（替える）・<c>{"restore":true}</c>（この Play で AI が替える前の大きさへ戻す）・<c>{}</c>（今の大きさを読むだけ）。
    ///   <c>settleSec</c>（0〜5・既定 0.6）＝寸法がそろってから答えるまで待つ秒（画面の形の受け渡し・UI の並べ直しが済むのを待つ）。
    /// 返事: <c>{"screen":{"w":1080,"h":2340,"did":"set"}}</c>。did は set・restored・not_changed（戻す物が無い）・read。
    /// ⭐ 寸法（Screen.width・height）は、選んだ次のフレームより後で替わる＝そろうまで答えを持ち越す。5 秒でそろわなければ 503 size_not_applied。
    /// ⚠ 止まっている間は替えない（409 paused・フレームが進まず、そろうのを待てない）。
    /// ⚠ ビルドでは 501 editor_only（Editor の係が口を差さない）。
    /// ⭐ 数えない道（<see cref="IAgentControlRoute"/>）＝画面の大きさは世界を読む頼みではない。
    /// </para>
    /// </summary>
    internal sealed class AgentScreenRoute : IAgentControlRoute, IAgentLongRoute
    {
        /// <summary>寸法の上下（px）。</summary>
        private const int MinSide = 160;
        private const int MaxSide = 7680;

        private const double DefaultSettleSec = 0.6;
        private const double MaxSettleSec = 5;

        /// <summary>選んでから寸法がそろうまで待つ長さ（ms）。</summary>
        private const long ApplyCapMs = 5000;

        /// <summary>戻す時（行き先の寸法を前もって知らない）に、寸法が替わるのを待つ長さ（ms）。</summary>
        private const long RestoreWaitMs = 1500;

        private static readonly TimeSpan Hold = TimeSpan.FromMilliseconds(ApplyCapMs + (MaxSettleSec * 1000) + 1000);

        public string Path => "/v1/back/screen";

        public AgentFace Face => AgentFace.Back;

        public AgentAbilities Needs => AgentAbilities.Back;

        public TimeSpan MaxHold(JObject body) => Hold;

        public bool IsDue(AgentCall call, long nowMs) =>
            !(call.State is ScreenTicket ticket) || AgentHost.CurrentSession == null || ticket.Update(nowMs);

        public void Handle(AgentCall call, AgentReplyWriter reply)
        {
            if (call.State is ScreenTicket held)
            {
                if (!held.Settled)
                {
                    reply.Fail(503, "size_not_applied", "game_view_hidden_or_closed");
                    return;
                }

                Write(reply, held.Did);
                return;
            }

            JObject body = call.Body ?? new JObject();
            if (!AgentReflexJson.TryReadBool(body, "restore", false, out bool restore))
            {
                reply.Fail(400, "bad_restore", "true_or_false");
                return;
            }

            double settleSec = DefaultSettleSec;
            if (body.TryGetValue("settleSec", out JToken settleToken) && settleToken.Type != JTokenType.Null)
            {
                if ((settleToken.Type != JTokenType.Integer && settleToken.Type != JTokenType.Float)
                    || (double)settleToken < 0 || (double)settleToken > MaxSettleSec)
                {
                    reply.Fail(400, "bad_settleSec", "0.." + MaxSettleSec);
                    return;
                }

                settleSec = (double)settleToken;
            }

            bool hasSize = body.TryGetValue("size", out JToken sizeToken) && sizeToken.Type != JTokenType.Null;
            if (hasSize && restore)
            {
                reply.Fail(400, "bad_screen", "size_or_restore");
                return;
            }

            int width = 0;
            int height = 0;
            if (hasSize && !TryReadSize(sizeToken, out width, out height))
            {
                reply.Fail(400, "bad_size", "[w,h]_" + MinSide + ".." + MaxSide);
                return;
            }

            if (!hasSize && !restore)
            {
                Write(reply, "read");
                return;
            }

            if (!AgentHost.CanWriteScreenSize)
            {
                reply.Fail(501, "editor_only", "screen_size_is_editor_only");
                return;
            }

            if (AgentHost.IsEditorPaused)
            {
                reply.Fail(409, "paused", "resume_first");
                return;
            }

            int startW = Screen.width;
            int startH = Screen.height;
            string why = restore ? AgentHost.RestoreScreenSize() : AgentHost.WriteScreenSize(width, height);
            if (why == "not_changed")
            {
                Write(reply, why);
                return;
            }

            if (why != null)
            {
                reply.Fail(why == "editor_only" ? 501 : 500, why, why == "no_game_view" ? "open_game_view" : "see_log");
                return;
            }

            call.State = restore
                ? ScreenTicket.ForRestore(startW, startH, settleSec, AgentSignalHub.NowMs())
                : ScreenTicket.ForSize(width, height, settleSec, AgentSignalHub.NowMs());
            reply.Defer();
        }

        private static bool TryReadSize(JToken token, out int width, out int height)
        {
            width = 0;
            height = 0;
            if (!(token is JArray list) || list.Count != 2
                || list[0].Type != JTokenType.Integer || list[1].Type != JTokenType.Integer)
            {
                return false;
            }

            // ⚠ (double) で読む（大きな整数で落ちない）。
            double w = (double)list[0];
            double h = (double)list[1];
            if (w < MinSide || w > MaxSide || h < MinSide || h > MaxSide)
            {
                return false;
            }

            width = (int)w;
            height = (int)h;
            return true;
        }

        private static void Write(AgentReplyWriter reply, string did)
        {
            JsonWriter w = reply.Json;
            w.WritePropertyName("screen");
            w.WriteStartObject();
            w.WritePropertyName("w");
            w.WriteValue(Screen.width);
            w.WritePropertyName("h");
            w.WriteValue(Screen.height);
            w.WritePropertyName("did");
            w.WriteValue(did);
            w.WriteEndObject();
        }

        /// <summary>替えた寸法がそろうのを待つ控え（主スレッドだけで触る）。</summary>
        private sealed class ScreenTicket
        {
            private int width;
            private int height;
            private int startW;
            private int startH;
            private bool restoring;
            private long settleMs;
            private long askedAt;
            private long matchedAt = -1;

            public string Did => this.restoring ? "restored" : "set";

            /// <summary>寸法がそろい、落ち着くまで待ち終えたか（false のまま答えた＝そろわなかった）。</summary>
            public bool Settled { get; private set; }

            public static ScreenTicket ForSize(int width, int height, double settleSec, long nowMs) => new ScreenTicket
            {
                width = width,
                height = height,
                settleMs = (long)(settleSec * 1000),
                askedAt = nowMs,
            };

            public static ScreenTicket ForRestore(int startW, int startH, double settleSec, long nowMs) => new ScreenTicket
            {
                startW = startW,
                startH = startH,
                restoring = true,
                settleMs = (long)(settleSec * 1000),
                askedAt = nowMs,
            };

            /// <summary>毎フレーム見る。答えてよければ true。</summary>
            public bool Update(long nowMs)
            {
                if (this.matchedAt < 0)
                {
                    // ⭐ 戻す時は行き先の寸法を前もって知らない（Free Aspect など）＝寸法が替わったら・少し待っても替わらなければ（同じ大きさだった）そろったとみなす。
                    bool matched = this.restoring
                        ? Screen.width != this.startW || Screen.height != this.startH || nowMs - this.askedAt >= RestoreWaitMs
                        : Screen.width == this.width && Screen.height == this.height;
                    if (matched)
                    {
                        this.matchedAt = nowMs;
                    }
                    else
                    {
                        return nowMs - this.askedAt >= ApplyCapMs;
                    }
                }

                if (nowMs - this.matchedAt < this.settleMs)
                {
                    return false;
                }

                this.Settled = true;
                return true;
            }
        }
    }
}
