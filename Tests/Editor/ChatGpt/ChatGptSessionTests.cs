using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using static Monster.Balloon.AutomataSystem.ChatGpt.Tests.ChatGptTestKit;

namespace Monster.Balloon.AutomataSystem.ChatGpt.Tests
{
    /// <summary>
    /// Tests for a ChatGPT conversation (tools, include fallback, errors, request body).
    /// ChatGPT の会話のテスト（道具を回す・include の断り・失敗の印・頼みの本文）。
    /// ⭐ 上限や途中で切れる流れは、本物では狙って起こせない。偽の OpenAI で毎回同じに起こして確かめる。
    /// </summary>
    public sealed class ChatGptSessionTests
    {
        private static readonly string[] ForbiddenFields =
        {
            "background", "conversation", "max_output_tokens", "max_tool_calls", "metadata", "moderation", "multi_agent",
            "prompt", "prompt_cache_retention", "safety_identifier", "temperature", "top_logprobs", "top_p", "truncation", "user",
            "previous_response_id",
        };

        private FakeOpenAi server;
        private string folder;
        private ChatGptAccount account;

        [SetUp]
        public void SetUp()
        {
            this.server = new FakeOpenAi();
            this.folder = TempFolder();
            this.account = new ChatGptAccount(this.server.Settings(this.folder));
            Run(() => SignInAsync(this.account));
        }

        [TearDown]
        public void TearDown()
        {
            this.account.Dispose();
            this.server.Dispose();
            try
            {
                Directory.Delete(this.folder, true);
            }
            catch (IOException)
            {
                // 一時フォルダの片付けの失敗は、テストの結果に関係しない。
            }
        }

        /// <summary>
        /// Test: a function call runs the tool, sends function_call_output, and ends with the final text.
        /// function_call → 道具を呼ぶ → function_call_output を足して頼み直す → 最後の文。答えが届いたら Delivered。
        /// </summary>
        [Test]
        public void FunctionCall_RunsTool_ThenFinalText()
        {
            var call = new JObject { ["type"] = "function_call", ["name"] = "look", ["arguments"] = "{\"x\":1}", ["call_id"] = "call_1" };
            this.server.Responses = (i, body) => i == 0
                ? (200, Sse(ItemDone(call), Completed()))
                : (200, TextReply("You are in the shop."));

            var tools = new RecordingToolHost();
            var session = new ChatGptSession(this.account, "model-a", "Play the game.", tools);
            var events = new List<ChatGptTurnEvent>();
            ChatGptTurnResult result = Run(() => session.RunTurnAsync("Where am I?", e => { lock (events) events.Add(e); }));

            Assert.That(result.Text, Is.EqualTo("You are in the shop."));
            Assert.That(result.ToolCalls, Is.EqualTo(1));
            Assert.That(tools.Calls.Single().Name, Is.EqualTo("look"));
            Assert.That(tools.Calls.Single().Args.Value<int>("x"), Is.EqualTo(1));
            Assert.That(tools.DeliveredReplies.Count, Is.EqualTo(1));

            JArray second = (JArray)this.server.ResponseBodies[1]["input"];
            JObject output = second.OfType<JObject>().Single(o => o.Value<string>("type") == "function_call_output");
            Assert.That(output.Value<string>("call_id"), Is.EqualTo("call_1"));
            Assert.That(output.Value<string>("output"), Is.EqualTo("{\"scene\":\"Shop\"}"));
            Assert.That(second.OfType<JObject>().Any(o => o.Value<string>("type") == "function_call"), Is.True);

            Assert.That(events.Select(e => e.Kind), Is.EqualTo(new[]
            {
                ChatGptTurnEventKind.ToolCall, ChatGptTurnEventKind.ToolResult, ChatGptTurnEventKind.TextDelta, ChatGptTurnEventKind.TurnEnd,
            }));
        }

        /// <summary>
        /// Test: the body has store false, stream true, instructions, tools, and none of the refused fields.
        /// 本文に store=false・stream=true・instructions・道具があり、送らない欄と system の項目が無い。余計なヘッダーも無い。
        /// </summary>
        [Test]
        public void RequestBody_FollowsPlanUsageRules()
        {
            this.server.Responses = (i, body) => (200, TextReply("ok"));
            var session = new ChatGptSession(this.account, "model-a", "Be brief.", new RecordingToolHost()) { ReasoningEffort = "low" };
            Run(() => session.RunTurnAsync("hi"));

            JObject sent = this.server.ResponseBodies[0];
            Assert.That(sent.Value<bool>("store"), Is.False);
            Assert.That(sent.Value<bool>("stream"), Is.True);
            Assert.That(sent.Value<string>("model"), Is.EqualTo("model-a"));
            Assert.That(sent.Value<string>("instructions"), Is.EqualTo("Be brief."));
            Assert.That(sent["reasoning"].Value<string>("effort"), Is.EqualTo("low"));
            Assert.That(sent["include"].Values<string>(), Is.EqualTo(new[] { "reasoning.encrypted_content" }));
            Assert.That(sent["tools"][0].Value<string>("type"), Is.EqualTo("function"));
            Assert.That(sent["tools"][0].Value<string>("name"), Is.EqualTo("look"));
            foreach (string field in ForbiddenFields) Assert.That(sent.ContainsKey(field), Is.False, field);
            Assert.That(sent["input"].Any(o => o.Value<string>("role") == "system"), Is.False);

            var headers = this.server.ResponseHeaders[0];
            Assert.That(headers["Authorization"], Is.EqualTo("Bearer access-1"));
            Assert.That(headers["Accept"], Does.Contain("text/event-stream"));
            Assert.That(headers["OpenAI-Organization"], Is.Null);
            Assert.That(headers["OpenAI-Project"], Is.Null);
            Assert.That(headers["ChatGPT-Account-Id"], Is.Null);
        }

        /// <summary>
        /// Test: when include is refused, it is dropped with the reasoning items and sent again once.
        /// include が断られたら、include と考えの項目を落として 1 回だけ送り直す。
        /// </summary>
        [Test]
        public void IncludeRefused_RetriesOnceWithoutInclude()
        {
            var reasoning = new JObject { ["type"] = "reasoning", ["id"] = "rs_1", ["encrypted_content"] = "enc", ["summary"] = new JArray() };
            const string refused = "{\"error\":{\"code\":\"subscription_sharing_unsupported_capability\",\"param\":\"include\",\"message\":\"no\"}}";
            this.server.Responses = (i, body) =>
            {
                if (i == 0) return (200, Sse(ItemDone(reasoning), ItemDone(Message("first")), Completed()));
                if (i == 1) return (400, refused);
                return (200, TextReply("second"));
            };

            var session = new ChatGptSession(this.account, "model-a");
            Run(() => session.RunTurnAsync("one"));
            Assert.That(this.server.ResponseBodies[0].ContainsKey("include"), Is.True);

            ChatGptTurnResult result = Run(() => session.RunTurnAsync("two"));
            Assert.That(result.Text, Is.EqualTo("second"));
            Assert.That(result.Requests, Is.EqualTo(2));
            Assert.That(session.CarryReasoning, Is.False);
            Assert.That(this.server.ResponseBodies[1]["input"].Any(o => o.Value<string>("type") == "reasoning"), Is.True);

            JObject retried = this.server.ResponseBodies[2];
            Assert.That(retried.ContainsKey("include"), Is.False);
            Assert.That(retried["input"].Any(o => o.Value<string>("type") == "reasoning"), Is.False);
            Assert.That(this.server.ResponseBodies.Count, Is.EqualTo(3));
        }

        /// <summary>
        /// Test: a 429 usage limit gives UsageLimit with Manage usage, and the turn is not kept.
        /// 429 の上限は UsageLimit の印と「Manage usage」の行き先。失敗した番は話に残らない。
        /// </summary>
        [Test]
        public void UsageLimit429_GivesUsageLimit()
        {
            const string limit = "{\"error\":{\"code\":\"subscription_sharing_usage_limit_exceeded\",\"message\":\"limit\"}}";
            this.server.Responses = (i, body) => (429, limit);
            var session = new ChatGptSession(this.account, "model-a");

            ChatGptException e = Fails(() => session.RunTurnAsync("hi"));
            Assert.That(e.Kind, Is.EqualTo(ChatGptErrorKind.UsageLimit));
            Assert.That(e.HttpStatus, Is.EqualTo(429));
            Assert.That(e.ActionUrl, Is.EqualTo("https://chatgpt.com/settings/usage"));
            Assert.That(session.History, Is.Empty);
        }

        /// <summary>
        /// Test: a usage limit inside the stream (response.failed) also gives UsageLimit.
        /// 流れの途中の response.failed で来た上限も UsageLimit の印になる。
        /// </summary>
        [Test]
        public void UsageLimitInStream_GivesUsageLimit()
        {
            var failed = new JObject
            {
                ["type"] = "response.failed",
                ["response"] = new JObject { ["error"] = new JObject { ["code"] = "subscription_sharing_usage_limit_exceeded", ["message"] = "limit" } },
            };
            this.server.Responses = (i, body) => (200, Sse(Delta("par"), failed));
            var session = new ChatGptSession(this.account, "model-a");

            ChatGptException e = Fails(() => session.RunTurnAsync("hi"));
            Assert.That(e.Kind, Is.EqualTo(ChatGptErrorKind.UsageLimit));
            Assert.That(e.HttpStatus, Is.EqualTo(0));
        }

        /// <summary>
        /// Test: a stream that ends before response.completed is Interrupted.
        /// response.completed の前に流れが終わったら「途中で切れた」。
        /// </summary>
        [Test]
        public void StreamEndsBeforeCompleted_IsInterrupted()
        {
            this.server.Responses = (i, body) => (200, Sse(Delta("half"), ItemDone(Message("half"))));
            var session = new ChatGptSession(this.account, "model-a");

            ChatGptException e = Fails(() => session.RunTurnAsync("hi"));
            Assert.That(e.Kind, Is.EqualTo(ChatGptErrorKind.Interrupted));
            Assert.That(session.History, Is.Empty);
        }

        /// <summary>
        /// Test: a 401 refreshes once and the request is sent again.
        /// 401 なら 1 回だけ refresh して、新しいトークンで送り直す。
        /// </summary>
        [Test]
        public void Unauthorized401_RefreshesOnceAndRetries()
        {
            this.server.Responses = (i, body) => i == 0 ? (401, "{\"detail\":\"expired\"}") : (200, TextReply("ok"));
            var session = new ChatGptSession(this.account, "model-a");

            Assert.That(Run(() => session.RunTurnAsync("hi")).Text, Is.EqualTo("ok"));
            Assert.That(this.server.TokenForms.Count(f => f["grant_type"] == "refresh_token"), Is.EqualTo(1));
            Assert.That(this.server.ResponseHeaders[1]["Authorization"], Is.EqualTo("Bearer access-2"));
        }
    }
}
