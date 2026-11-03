using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Monster.Balloon.AutomataSystem
{
    /// <summary>
    /// <c>/v1/lease</c>＝手足の借りを取る・続ける・返す。
    /// <para>
    /// 頼み: <c>{"by":"claude-e8ba"}</c>（取る・同じ名前なら続ける）・<c>{"by":"…","release":true}</c>（返す）。
    /// 返事: <c>{"lease":"L3","by":"claude-e8ba","idleSec":5}</c>。返した時は <c>{"released":"L3"}</c>。
    /// ⭐ by は、Editor のロックの持ち主の名前（2 人が同じパッドを取り合わないため）。
    /// ⚠ ほかの名前が持っていれば 409 leased・人が触った直後は 409 human_active・世界が無ければ 503 world_not_ready。
    /// </para>
    /// </summary>
    internal sealed class AgentLeaseRoute : IAgentRoute
    {
        public string Path => "/v1/lease";

        public AgentFace Face => AgentFace.Surface;

        /// <summary>手足は A の手（self）。</summary>
        public AgentAbilities Needs => AgentAbilities.Self;

        public void Handle(AgentCall call, AgentReplyWriter reply)
        {
            AgentHostSession session = AgentHost.CurrentSession;
            if (session == null)
            {
                reply.Fail(503, "closing", "reopen_after_play");
                return;
            }

            if (!AgentHandsJson.TryReadName(call.Body, out string by))
            {
                reply.Fail(400, "bad_by", "name_1_to_" + AgentLeaseDesk.MaxNameLength);
                return;
            }

            if (!AgentReflexJson.TryReadBool(call.Body, "release", false, out bool release))
            {
                reply.Fail(400, "bad_release", "true_or_false");
                return;
            }

            AgentLeaseDesk desk = session.Hands;
            if (release)
            {
                if (desk.LeaseId == null || !string.Equals(desk.Holder, by, StringComparison.Ordinal))
                {
                    reply.Fail(409, "not_leased", "nothing_to_release");
                    return;
                }

                // ⭐ 動く反射が AI の借りのまま走っている間は返させない（返すと反射の命令まで止まる）。
                //    反射を止めたい時は、腕を外す（reflex/arm の minutes:0）と、走っている反射も終わる。
                if (desk.ReflexActing != null)
                {
                    reply.Fail(409, AgentLeaseDesk.ReflexActingWhy, "wait_reflex");
                    return;
                }

                string id = desk.LeaseId;
                desk.Release("released");
                reply.Json.WritePropertyName("released");
                reply.Json.WriteValue(id);
                return;
            }

            IAgentWorld world = AgentReflexJson.ReadyWorld();
            string why = desk.TryTake(by, AgentSignalHub.NowMs(), world != null, world?.Hands);
            if (why != null)
            {
                reply.Fail(AgentHandsJson.StatusFor(why), why, AgentLeaseDesk.HintForTake(why));
                return;
            }

            AgentHandsJson.WriteLease(reply.Json, desk);
        }
    }

    /// <summary>
    /// <c>/v1/act</c>＝命令を 1 本走らせ、終わってから結果を返す。
    /// <para>
    /// 頼み: <c>{"by":"claude-e8ba","ref":"t7-1","do":"move","args":{"stick":[0,1],"sec":2}}</c>。lease（借りの印）は書いても書かなくてもよい。
    /// 返事: <c>{"act":"a17","ref":"t7-1","command":"move","outcome":"done","seenWhy":null,"via":"pad","by":null,"cursor":"8f2c1d:1851"}</c>。
    ///   cursor は命令を始めた時の帳面のしおり（wait の after に渡すと、押した後の合図を取りこぼさない）。
    ///   命令が数の束を付けた時だけ detail も書く（例 fight の <c>{"swings":7,"downed":1,"foe":"up","sec":20.1}</c>）。
    /// ⭐ 借りが無ければ、その場で by の名前で取る（人の本物の入力で切れた後は 409 lease_lost・hint human-input＝/v1/lease で取り直す）。
    /// ⭐ 同じ ref の 2 回目は、借りや掛け金より先に見て、押さずに 1 回目の結果を返す（走っていれば、終わるのを待つ）。
    /// ⭐ 裏の形の頼み（命令が <see cref="IAgentBackArgs"/> で名乗る・例 walk_to の to:{"pos":…}）は、表のトークンなら 403 back_only（知らない命令の次に見る）。
    /// ⚠ 掛け金の間は 409 reflex_latched・止まっている間は 409 paused・走っている命令があれば 409 busy。
    /// ⚠ 動く反射が走っている間は 409 reflex_acting・hint wait_reflex（反射が終われば、同じ命令で続きができる）。
    /// </para>
    /// </summary>
    internal sealed class AgentActRoute : IAgentRoute, IAgentLongRoute
    {
        /// <summary>答えを待つ長さ。⭐ 芯は命令の締め切り（60 秒まで）で必ず答える＝それより長くしておく。</summary>
        private static readonly TimeSpan ActHold = TimeSpan.FromSeconds(AgentLeaseDesk.MaxActSeconds + 5f);

        public string Path => "/v1/act";

        public AgentFace Face => AgentFace.Surface;

        public AgentAbilities Needs => AgentAbilities.Self;

        public TimeSpan MaxHold(JObject body) => ActHold;

        /// <summary>預かった act は、その控えが終わった時に答える。</summary>
        public bool IsDue(AgentCall call, long nowMs) =>
            !(call.State is AgentActTicket ticket) || ticket.Done || AgentHost.CurrentSession == null;

        public void Handle(AgentCall call, AgentReplyWriter reply)
        {
            AgentHostSession session = AgentHost.CurrentSession;
            if (session == null)
            {
                reply.Fail(503, "closing", "reopen_after_play");
                return;
            }

            if (call.State is AgentActTicket held)
            {
                AgentHandsJson.WriteAct(reply, held, session, call.Face);
                return;
            }

            JObject body = call.Body;
            if (!AgentHandsJson.TryReadName(body, out string by))
            {
                reply.Fail(400, "bad_by", "name_1_to_" + AgentLeaseDesk.MaxNameLength);
                return;
            }

            if (!AgentHandsJson.TryReadText(body, "do", 40, out string name) || name == null)
            {
                reply.Fail(400, "bad_do", "see_hello_hands");
                return;
            }

            if (!AgentHandsJson.TryReadText(body, "ref", 64, out string reference))
            {
                reply.Fail(400, "bad_ref", "text_1_to_64");
                return;
            }

            if (!AgentHandsJson.TryReadText(body, "lease", 16, out string leaseId))
            {
                reply.Fail(400, "bad_lease", "lease_from_v1_lease");
                return;
            }

            JObject args = new JObject();
            if (body.TryGetValue("args", out JToken argsToken) && argsToken.Type != JTokenType.Null)
            {
                args = argsToken as JObject;
                if (args == null)
                {
                    reply.Fail(400, "bad_args", "object");
                    return;
                }
            }

            AgentLeaseDesk desk = session.Hands;
            long now = AgentSignalHub.NowMs();

            // ⭐ 同じ ref の 2 回目は、借りや掛け金より先に見る（送り直しに、1 回目の結果を返す）。
            string refWhy = desk.FindByRef(by, reference, name, out AgentActTicket seen);
            if (refWhy != null)
            {
                reply.Fail(409, refWhy, "new_ref");
                return;
            }

            if (seen != null)
            {
                desk.Touch(by, now);
                AnswerOrHold(call, reply, seen, session);
                return;
            }

            IAgentWorld world = AgentReflexJson.ReadyWorld();
            IAgentCommand command = FindCommand(world?.Hands, name);
            if (world != null && world.Hands != null && command == null)
            {
                reply.Fail(400, "unknown_command", "see_hello_hands");
                return;
            }

            // ⭐ 裏の頼みの形（walk_to・fight の to:{"pos":…}・{"id":…}）は、裏のトークンだけが使える。
            //    表の AI は、人に見える物だけを相手にする＝撮る道の hideDebugOverlays と同じ断り。
            if (command is IAgentBackArgs backArgs && backArgs.NeedsBack(args) && call.Face != AgentFace.Back)
            {
                reply.Fail(403, "back_only", "use_back_token");
                return;
            }

            // ⭐ 掛け金と止まっているは、借りより先に見る（断る act で借りを取らない＝仮のパッドを足さない）。
            if (session.Reflex.Latch != null)
            {
                reply.Fail(409, "reflex_latched", "reflex_ack");
                return;
            }

            if (AgentHost.IsEditorPaused || session.Reflex.Stepping)
            {
                reply.Fail(409, "paused", "resume_first");
                return;
            }

            string leaseWhy = desk.ResolveForAct(by, leaseId, now, world != null, world?.Hands, out string leaseHint);
            if (leaseWhy != null)
            {
                reply.Fail(AgentHandsJson.StatusFor(leaseWhy), leaseWhy, leaseHint);
                return;
            }

            string why = desk.TryStart(by, command, args, reference, now, out AgentActTicket ticket);
            if (why != null)
            {
                reply.Fail(409, why, why == "busy" ? "wait_act" : AgentLeaseDesk.HintForTake(why));
                return;
            }

            AnswerOrHold(call, reply, ticket, session);
        }

        private static void AnswerOrHold(AgentCall call, AgentReplyWriter reply, AgentActTicket ticket, AgentHostSession session)
        {
            if (ticket.Done)
            {
                AgentHandsJson.WriteAct(reply, ticket, session, call.Face);
                return;
            }

            call.State = ticket;
            reply.Defer();
        }

        private static IAgentCommand FindCommand(IAgentHands hands, string name)
        {
            IReadOnlyList<IAgentCommand> commands = hands?.Commands;
            if (commands == null)
            {
                return null;
            }

            foreach (IAgentCommand command in commands)
            {
                if (command != null && string.Equals(command.Name, name, StringComparison.Ordinal))
                {
                    return command;
                }
            }

            return null;
        }
    }

    /// <summary>手足の道の頼みの読み方と、返事の書き方（hello・lease・act で同じ形）。</summary>
    internal static class AgentHandsJson
    {
        /// <summary>
        /// hello の <c>"hands":{"lease":"L3","by":"…","act":"a7","reflexActing":null,"commands":[{"name":"move","changesWorld":false},…]}</c> を書く。
        /// reflexActing は、動く反射が走らせている命令の反射の名前（無ければ null）。
        /// commands は、今できている世界の命令（世界が無い・手足が無ければ空）。
        /// </summary>
        public static void WriteHello(JsonWriter w, AgentLeaseDesk desk, IAgentWorld readyWorld)
        {
            w.WritePropertyName("hands");
            w.WriteStartObject();
            w.WritePropertyName("lease");
            w.WriteValue(desk.LeaseId);
            w.WritePropertyName("by");
            w.WriteValue(desk.Holder);
            w.WritePropertyName("act");
            w.WriteValue(desk.Running?.ActId);
            w.WritePropertyName("reflexActing");
            w.WriteValue(desk.ReflexActing?.Reflex);
            w.WritePropertyName("idleSec");
            w.WriteValue(AgentLeaseDesk.IdleMs / 1000);

            var commands = new List<IAgentCommand>();
            IReadOnlyList<IAgentCommand> listed = readyWorld?.Hands?.Commands;
            if (listed != null)
            {
                foreach (IAgentCommand command in listed)
                {
                    if (command != null && !string.IsNullOrEmpty(command.Name))
                    {
                        commands.Add(command);
                    }
                }
            }

            commands.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            w.WritePropertyName("commands");
            w.WriteStartArray();
            foreach (IAgentCommand command in commands)
            {
                w.WriteStartObject();
                w.WritePropertyName("name");
                w.WriteValue(command.Name);
                w.WritePropertyName("changesWorld");
                w.WriteValue(command.ChangesWorld);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        /// <summary><c>"lease":"L3","by":"…","idleSec":5</c> を書く（借りが無ければ lease と by は null）。</summary>
        public static void WriteLease(JsonWriter w, AgentLeaseDesk desk)
        {
            w.WritePropertyName("lease");
            w.WriteValue(desk.LeaseId);
            w.WritePropertyName("by");
            w.WriteValue(desk.Holder);
            w.WritePropertyName("idleSec");
            w.WriteValue(AgentLeaseDesk.IdleMs / 1000);
        }

        /// <summary>
        /// act の結果を書く。cursor は、頼んだ顔の帳面のしおり（裏のトークンなら裏の帳面）。
        /// by は邪魔した合図（<c>{"kind":"reflex.tripped","seq":1870}</c>・無ければ null）。
        /// </summary>
        public static void WriteAct(AgentReplyWriter reply, AgentActTicket ticket, AgentHostSession session, AgentFace face)
        {
            JsonWriter w = reply.Json;
            AgentActResult result = ticket.Result;
            w.WritePropertyName("act");
            w.WriteValue(ticket.ActId);
            w.WritePropertyName("ref");
            w.WriteValue(ticket.Ref);
            w.WritePropertyName("command");
            w.WriteValue(ticket.Command);
            w.WritePropertyName("outcome");
            w.WriteValue(result?.Outcome ?? AgentOutcome.Unknown);
            w.WritePropertyName("seenWhy");
            w.WriteValue(result?.SeenWhy);
            w.WritePropertyName("via");
            w.WriteValue(result?.Via);
            if (result?.Hint != null)
            {
                w.WritePropertyName("hint");
                w.WriteValue(result.Hint);
            }

            // 長い命令の短い数の束（例 fight の振った数）。命令が付けた時だけ書く。
            // ⭐ 裏のトークンなら、裏だけの束（BackDetail＝個体 ID・物の名前・座標も入る）があればそちらを書く（walk_to の stuck など）。
            JObject detail = face == AgentFace.Back && result?.BackDetail != null ? result.BackDetail : result?.Detail;
            if (detail != null)
            {
                w.WritePropertyName("detail");
                detail.WriteTo(w);
            }

            // ⭐ by.seq と cursor は、頼んだ顔の帳面の番号にそろえる（裏のトークンなら裏の帳面）。
            bool back = face == AgentFace.Back && session.Signals.Back != null;
            w.WritePropertyName("by");
            if (ticket.ByKind == null)
            {
                w.WriteNull();
            }
            else
            {
                w.WriteStartObject();
                w.WritePropertyName("kind");
                w.WriteValue(ticket.ByKind);
                w.WritePropertyName("seq");
                w.WriteValue(back ? ticket.ByBackSeq : ticket.BySeq);
                w.WriteEndObject();
            }

            w.WritePropertyName("cursor");
            w.WriteValue(AgentCursor.Format(session.PlayId, back ? AgentFace.Back : AgentFace.Surface, back ? ticket.BackSeq : ticket.SurfaceSeq));
        }

        /// <summary>断りの字を、返事の番号に直す。</summary>
        public static int StatusFor(string why)
        {
            switch (why)
            {
                case "world_not_ready":
                    return 503;
                case "no_hands":
                    return 501;
                case "hands_failed":
                    return 500;
                default:
                    return 409;
            }
        }

        /// <summary>by（借りる人の名前）を読む。英数字と <c>. _ -</c> だけで 1〜40 字。無い・形が悪ければ false。</summary>
        public static bool TryReadName(JObject body, out string name)
        {
            if (!TryReadText(body, "by", AgentLeaseDesk.MaxNameLength, out name) || name == null)
            {
                return false;
            }

            foreach (char c in name)
            {
                bool plain = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-';
                if (!plain)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>字の欄を読む（無ければ null で true）。字でない・空・長すぎれば false。</summary>
        public static bool TryReadText(JObject body, string field, int maxLength, out string text)
        {
            text = null;
            if (body == null || !body.TryGetValue(field, out JToken token) || token.Type == JTokenType.Null)
            {
                return true;
            }

            if (token.Type != JTokenType.String)
            {
                return false;
            }

            string value = token.Value<string>();
            if (string.IsNullOrEmpty(value) || value.Length > maxLength)
            {
                return false;
            }

            text = value;
            return true;
        }
    }
}
