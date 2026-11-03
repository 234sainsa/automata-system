using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Monster.Balloon.AutomataSystem.ChatGpt
{
    /// <summary>
    /// A conversation with ChatGPT plan usage that can call the tools of an <see cref="IAgentToolHost"/>.
    /// ChatGPT のプランで話す 1 つの会話。道具の受け手（<see cref="IAgentToolHost"/>）の道具を AI に使わせる。
    /// <para>
    /// ⭐ 話の全部を覚え、毎回全部を送る（store=false なので previous_response_id は使わない）。
    /// ⭐ 1 つの番＝言葉を 1 つ足し、AI が呼んだ道具を回し、最後の文が出るまで頼む。番は 1 つずつ（重ねて呼ぶと断る）。
    /// ⭐ 失敗した番は話に残さない（番の前の話に戻す＝同じ言葉で呼び直せる）。
    /// ⭐ 番の途中の出来事は onEvent で渡す。呼ぶスレッドは決まっていない（主スレッドとは限らない）。
    /// </para>
    /// </summary>
    public sealed class ChatGptSession
    {
        internal const string IncludeReasoning = "reasoning.encrypted_content";

        private readonly ChatGptAccount account;
        private readonly List<JObject> history = new List<JObject>();
        private int running;

        /// <summary>
        /// Creates a conversation.
        /// 会話を作る。model は <see cref="ChatGptAccount.ListModelsAsync"/> の Slug を入れる。
        /// </summary>
        public ChatGptSession(ChatGptAccount account, string model, string instructions = null, IAgentToolHost tools = null)
        {
            this.account = account ?? throw new ArgumentNullException(nameof(account));
            this.Model = model;
            this.Instructions = instructions;
            this.Tools = tools;
        }

        /// <summary>The model slug. 使うモデル（一覧の Slug）。</summary>
        public string Model { get; set; }

        /// <summary>The instructions (sent as instructions, not as a system item). 手引き（instructions に入れる）。</summary>
        public string Instructions { get; set; }

        /// <summary>The tool host, or null for no tools. 道具の受け手（null なら道具なし）。</summary>
        public IAgentToolHost Tools { get; set; }

        /// <summary>The reasoning effort ("low", "medium", "high" ...), or null to leave it out. 考える深さ（null なら送らない）。</summary>
        public string ReasoningEffort { get; set; }

        /// <summary>
        /// Carries reasoning items to the next request (turned off when the server refuses it).
        /// 考えの項目を次の頼みへ持ち越すか。サーバーが断ったら、自分で false にする。
        /// </summary>
        public bool CarryReasoning { get; set; } = true;

        /// <summary>
        /// A copy of the conversation items sent each time.
        /// 毎回送る話の項目の写し。
        /// </summary>
        public IReadOnlyList<JObject> History
        {
            get
            {
                lock (this.history) return this.history.ConvertAll(o => (JObject)o.DeepClone());
            }
        }

        /// <summary>
        /// Forgets the conversation.
        /// 話を忘れる（番の途中では呼ばない）。
        /// </summary>
        public void Clear()
        {
            lock (this.history) this.history.Clear();
        }

        /// <summary>
        /// Adds the player's words, runs tools as the model asks, and returns the final text.
        /// 言葉を 1 つ足して番を回す。AI が呼んだ道具を回し、最後の文が出たら返す。失敗は <see cref="ChatGptException"/>。
        /// </summary>
        public async Task<ChatGptTurnResult> RunTurnAsync(string userText, Action<ChatGptTurnEvent> onEvent = null, CancellationToken cancel = default)
        {
            if (string.IsNullOrEmpty(this.Model)) throw new InvalidOperationException("モデルが決まっていない（ListModelsAsync の Slug を入れる）。");
            if (Interlocked.Exchange(ref this.running, 1) == 1) throw new InvalidOperationException("前の番がまだ終わっていない。");

            int mark;
            lock (this.history)
            {
                mark = this.history.Count;
                this.history.Add(UserMessage(userText ?? string.Empty));
            }

            try
            {
                return await this.LoopAsync(onEvent, cancel).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                lock (this.history)
                {
                    if (this.history.Count > mark) this.history.RemoveRange(mark, this.history.Count - mark);
                }

                if (e is ChatGptException) throw;
                if (e is OperationCanceledException && cancel.IsCancellationRequested) throw new ChatGptException(ChatGptErrorKind.Cancelled, "取り消した。", inner: e);
                throw;
            }
            finally
            {
                Volatile.Write(ref this.running, 0);
            }
        }

        private async Task<ChatGptTurnResult> LoopAsync(Action<ChatGptTurnEvent> onEvent, CancellationToken cancel)
        {
            var pending = new List<AgentToolReply>();
            int requests = 0;
            int toolCalls = 0;
            while (true)
            {
                int limit = this.account.Settings.MaxRequestsPerTurn;
                if (requests >= limit) throw new ChatGptException(ChatGptErrorKind.RequestLimit, "1 つの番で頼む回数の上限（" + limit + "）に当たった。");

                (ChatGptResponses.Result result, int sent) = await this.SendAsync(onEvent, cancel).ConfigureAwait(false);
                requests += sent;

                // 道具の答えを載せた頼みが通った＝AI へ渡し終えた。
                foreach (AgentToolReply reply in pending) this.Tools?.Delivered(reply);
                pending.Clear();

                var calls = new List<JObject>();
                var text = new StringBuilder();
                lock (this.history)
                {
                    foreach (JObject item in result.Output)
                    {
                        string type = item.Value<string>("type");
                        if (type == "function_call") calls.Add(item);
                        if (type == "message") AppendText(item, text);
                        if (type == "function_call" || type == "message" || (type == "reasoning" && this.CarryReasoning)) this.history.Add(item);
                    }
                }

                if (calls.Count == 0)
                {
                    onEvent?.Invoke(new ChatGptTurnEvent(ChatGptTurnEventKind.TurnEnd, text.ToString()));
                    return new ChatGptTurnResult(text.ToString(), toolCalls, requests);
                }

                foreach (JObject call in calls)
                {
                    toolCalls++;
                    string name = call.Value<string>("name");
                    string args = call.Value<string>("arguments");
                    string callId = call.Value<string>("call_id");
                    onEvent?.Invoke(new ChatGptTurnEvent(ChatGptTurnEventKind.ToolCall, null, name, args, callId));

                    (string output, bool isError, AgentToolReply reply) = await this.RunToolAsync(name, args, cancel).ConfigureAwait(false);
                    cancel.ThrowIfCancellationRequested();
                    lock (this.history)
                    {
                        this.history.Add(new JObject { ["type"] = "function_call_output", ["call_id"] = callId, ["output"] = output });
                    }

                    if (reply != null) pending.Add(reply);
                    onEvent?.Invoke(new ChatGptTurnEvent(ChatGptTurnEventKind.ToolResult, output, name, args, callId, isError));
                }
            }
        }

        // 1 回頼む。include が断られたら、include を外し、考えの項目を落として 1 回だけ送り直す。返り＝結果と、送った回数。
        private async Task<(ChatGptResponses.Result, int)> SendAsync(Action<ChatGptTurnEvent> onEvent, CancellationToken cancel)
        {
            Action<string> delta = onEvent == null ? (Action<string>)null : d => onEvent(new ChatGptTurnEvent(ChatGptTurnEventKind.TextDelta, d));
            try
            {
                return (await this.SendOnceAsync(delta, cancel).ConfigureAwait(false), 1);
            }
            catch (ChatGptException e) when (this.CarryReasoning && e.Code == ChatGptApiErrors.UnsupportedCapability && (e.Param ?? string.Empty).Contains("include"))
            {
                this.CarryReasoning = false;
                lock (this.history) this.history.RemoveAll(o => o.Value<string>("type") == "reasoning");
                return (await this.SendOnceAsync(delta, cancel).ConfigureAwait(false), 2);
            }
        }

        private Task<ChatGptResponses.Result> SendOnceAsync(Action<string> delta, CancellationToken cancel)
        {
            JObject body = this.BuildBody();
            return this.account.WithTokenAsync(token => ChatGptResponses.SendAsync(this.account, token, body, delta, cancel), cancel);
        }

        /// <summary>
        /// 頼みの本文を作る。⚠ 送らない欄（temperature・max_output_tokens・metadata・user など）はここに足さない。
        /// </summary>
        internal JObject BuildBody()
        {
            var input = new JArray();
            lock (this.history)
            {
                foreach (JObject item in this.history) input.Add(item.DeepClone());
            }

            var body = new JObject
            {
                ["model"] = this.Model,
                ["input"] = input,
                ["store"] = false,
                ["stream"] = true,
            };
            if (!string.IsNullOrEmpty(this.Instructions)) body["instructions"] = this.Instructions;

            JArray tools = this.ToolsJson();
            if (tools.Count > 0) body["tools"] = tools;
            if (!string.IsNullOrEmpty(this.ReasoningEffort)) body["reasoning"] = new JObject { ["effort"] = this.ReasoningEffort };
            if (this.CarryReasoning) body["include"] = new JArray(IncludeReasoning);
            return body;
        }

        // 道具の表を function tools の形にする。strict は false（ゲームの道具の形は strict の決まりを満たすとは限らない）。
        private JArray ToolsJson()
        {
            var list = new JArray();
            IAgentToolHost host = this.Tools;
            if (host?.Tools == null) return list;
            foreach (AgentToolSpec spec in host.Tools)
            {
                list.Add(new JObject
                {
                    ["type"] = "function",
                    ["name"] = spec.Name,
                    ["description"] = spec.Description,
                    ["parameters"] = spec.InputSchema.DeepClone(),
                    ["strict"] = false,
                });
            }

            return list;
        }

        // 道具を 1 回呼ぶ。引数が JSON として読めなければ、道具を呼ばずに断りを AI へ返す。
        private async Task<(string, bool, AgentToolReply)> RunToolAsync(string name, string arguments, CancellationToken cancel)
        {
            IAgentToolHost host = this.Tools;
            if (host == null) return ("ERROR: no tools are available.", true, null);

            JToken args;
            try
            {
                args = string.IsNullOrWhiteSpace(arguments) ? new JObject() : JToken.Parse(arguments);
            }
            catch (Exception)
            {
                return ("ERROR: the arguments are not valid JSON.", true, null);
            }

            AgentToolReply reply = await host.InvokeAsync(name, args, cancel).ConfigureAwait(false);
            if (reply == null) return ("ERROR: the tool returned nothing.", true, null);
            return (reply.IsError ? "ERROR: " + reply.Text : reply.Text, reply.IsError, reply);
        }

        private static void AppendText(JObject message, StringBuilder text)
        {
            if (!(message["content"] is JArray parts)) return;
            foreach (JToken part in parts)
            {
                if (part.Value<string>("type") == "output_text") text.Append(part.Value<string>("text"));
            }
        }

        private static JObject UserMessage(string text)
        {
            return new JObject
            {
                ["type"] = "message",
                ["role"] = "user",
                ["content"] = new JArray(new JObject { ["type"] = "input_text", ["text"] = text }),
            };
        }
    }
}
