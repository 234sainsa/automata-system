using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Monster.Balloon.AutomataSystem.ChatGpt.Tests
{
    /// <summary>
    /// Shared helpers for the ChatGPT tests.
    /// ChatGPT のテストの共通の道具（ブラウザの代わり・SSE の字・道具の受け手・一時フォルダ）。
    /// </summary>
    internal static class ChatGptTestKit
    {
        private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

        /// <summary>
        /// async の手を、スレッドプールで回して待つ（主スレッドの同期の文脈で詰まらない）。
        /// </summary>
        public static T Run<T>(Func<Task<T>> f)
        {
            Task<T> t = Task.Run(f);
            if (!t.Wait(Limit)) Assert.Fail("30 秒で終わらなかった。");
            return t.Result;
        }

        public static void Run(Func<Task> f)
        {
            Run(async () =>
            {
                await f();
                return 0;
            });
        }

        /// <summary>手が ChatGptException を投げることを確かめ、それを返す。</summary>
        public static ChatGptException Fails(Func<Task> f)
        {
            try
            {
                Run(f);
            }
            catch (AggregateException e) when (e.InnerException is ChatGptException g)
            {
                return g;
            }

            Assert.Fail("ChatGptException が出なかった。");
            return null;
        }

        /// <summary>ブラウザの代わり＝authorize を GET し、302 の行き先（ログインの戻り）を GET する。</summary>
        public static async Task BrowseAsync(Uri authorize)
        {
            using (var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }))
            {
                HttpResponseMessage first = await http.GetAsync(authorize);
                Assert.That((int)first.StatusCode, Is.EqualTo(302));
                await http.GetAsync(first.Headers.Location);
            }
        }

        /// <summary>ログインを始め、ブラウザの代わりに戻りを踏み、済むまで待つ。</summary>
        public static async Task<ChatGptAccountState> SignInAsync(ChatGptAccount account, bool consent = false)
        {
            ChatGptSignIn signIn = await account.BeginSignInAsync(consent);
            await BrowseAsync(signIn.AuthorizeUrl);
            return await signIn.Completion;
        }

        public static string TempFolder()
        {
            string path = Path.Combine(Path.GetTempPath(), "automata-chatgpt-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        public static JObject ReadSaved(string folder)
        {
            return JObject.Parse(File.ReadAllText(Path.Combine(folder, "account.json")));
        }

        /// <summary>SSE の字を作る（出来事ごとに event: と data: の 2 行）。</summary>
        public static string Sse(params JObject[] events)
        {
            var sb = new StringBuilder();
            foreach (JObject e in events)
            {
                sb.Append("event: ").Append(e.Value<string>("type")).Append('\n');
                sb.Append("data: ").Append(e.ToString(Formatting.None)).Append("\n\n");
            }

            return sb.ToString();
        }

        public static JObject Delta(string text)
        {
            return new JObject { ["type"] = "response.output_text.delta", ["delta"] = text };
        }

        public static JObject ItemDone(JObject item)
        {
            return new JObject { ["type"] = "response.output_item.done", ["item"] = item };
        }

        public static JObject Message(string text)
        {
            return new JObject
            {
                ["type"] = "message",
                ["id"] = "msg_1",
                ["role"] = "assistant",
                ["content"] = new JArray(new JObject { ["type"] = "output_text", ["text"] = text }),
            };
        }

        public static JObject Completed()
        {
            return new JObject { ["type"] = "response.completed", ["response"] = new JObject { ["status"] = "completed" } };
        }

        /// <summary>最後の文だけを返す答え。</summary>
        public static string TextReply(string text)
        {
            return Sse(Delta(text), ItemDone(Message(text)), Completed());
        }
    }

    /// <summary>
    /// A tool host that records calls.
    /// 呼ばれた事を覚える道具の受け手（道具は "look" の 1 つ）。
    /// </summary>
    internal sealed class RecordingToolHost : IAgentToolHost
    {
        public readonly List<(string Name, JToken Args)> Calls = new List<(string, JToken)>();
        public readonly List<AgentToolReply> DeliveredReplies = new List<AgentToolReply>();

        public IReadOnlyList<AgentToolSpec> Tools { get; } = new[]
        {
            new AgentToolSpec("look", "Read the screen.", new JObject { ["type"] = "object", ["properties"] = new JObject() }),
        };

        public Task<AgentToolReply> InvokeAsync(string name, JToken arguments, CancellationToken cancel)
        {
            lock (this.Calls) this.Calls.Add((name, arguments));
            return Task.FromResult(new AgentToolReply("{\"scene\":\"Shop\"}", false));
        }

        public void Delivered(AgentToolReply reply)
        {
            lock (this.DeliveredReplies) this.DeliveredReplies.Add(reply);
        }
    }
}
