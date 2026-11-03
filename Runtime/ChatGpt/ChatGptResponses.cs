using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Monster.Balloon.AutomataSystem.ChatGpt
{
    /// <summary>
    /// /v1/responses への頼み 1 回（流れ＝SSE を読み切るまで）。
    /// <para>
    /// ⭐ 送るヘッダーは Authorization・Content-Type・Accept の 3 つだけ（OpenAI-Organization・Project・account id は送らない）。
    /// ⭐ 成功は response.completed が来た時だけ。その前に流れが切れたら Interrupted。
    /// ⭐ 流れの前の失敗は HTTP の番号付きで投げる（401 なら呼ぶ側が refresh して 1 回だけ送り直す）。
    /// </para>
    /// </summary>
    internal static class ChatGptResponses
    {
        /// <summary>1 回の頼みの返り＝出力の項目の列。</summary>
        internal sealed class Result
        {
            public readonly List<JObject> Output = new List<JObject>();
        }

        public static async Task<Result> SendAsync(
            ChatGptAccount account, string token, JObject body, Action<string> onTextDelta, CancellationToken cancel)
        {
            ChatGptSettings settings = account.Settings;
            Uri url = account.ApiUrl("responses");
            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            using (var headerLimit = CancellationTokenSource.CreateLinkedTokenSource(cancel))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.Accept.ParseAdd("text/event-stream");
                request.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

                HttpResponseMessage response;
                headerLimit.CancelAfter(settings.RequestTimeout);
                try
                {
                    response = await account.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerLimit.Token).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    throw ChatGptWire.Translate(e, cancel, url);
                }

                using (response)
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        string text = await ReadAllAsync(response, cancel, url).ConfigureAwait(false);
                        throw ChatGptApiErrors.FromHttp((int)response.StatusCode, text);
                    }

                    Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    using (stream)
                    using (cancel.Register(() => stream.Dispose()))
                    {
                        return await ReadStreamAsync(new ChatGptSseReader(stream), settings.StreamIdleTimeout, onTextDelta, cancel).ConfigureAwait(false);
                    }
                }
            }
        }

        private static async Task<Result> ReadStreamAsync(ChatGptSseReader reader, TimeSpan idle, Action<string> onTextDelta, CancellationToken cancel)
        {
            var result = new Result();
            while (true)
            {
                JObject e;
                try
                {
                    e = await reader.NextAsync(idle, cancel).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is TimeoutException || ex is HttpRequestException || ex is OperationCanceledException)
                {
                    if (cancel.IsCancellationRequested) throw new ChatGptException(ChatGptErrorKind.Cancelled, "取り消した。", inner: ex);
                    throw new ChatGptException(ChatGptErrorKind.Interrupted, "答えの流れが終わりの印の前に切れた（" + ex.GetType().Name + "）。", inner: ex);
                }

                if (e == null) throw new ChatGptException(ChatGptErrorKind.Interrupted, "答えの流れが response.completed の前に終わった。");

                switch (e.Value<string>("type"))
                {
                    case "response.output_text.delta":
                        string delta = e.Value<string>("delta");
                        if (!string.IsNullOrEmpty(delta)) onTextDelta?.Invoke(delta);
                        break;

                    case "response.output_item.done":
                        if (e["item"] is JObject item) result.Output.Add(item);
                        break;

                    case "response.completed":
                        // 項目ごとの知らせが来なかった時だけ、終わりの知らせの output を使う（2 重に足さない）。
                        if (result.Output.Count == 0 && e["response"]?["output"] is JArray output)
                        {
                            foreach (JToken t in output)
                            {
                                if (t is JObject o) result.Output.Add(o);
                            }
                        }

                        return result;

                    case "response.failed":
                        throw ChatGptApiErrors.FromStreamError(e["response"]?["error"] as JObject, "答えが失敗した（response.failed）。");

                    case "response.incomplete":
                        string reason = e["response"]?["incomplete_details"]?.Value<string>("reason") ?? "unknown";
                        throw new ChatGptException(ChatGptErrorKind.Incomplete, "答えが途中で終わった（" + reason + "）。", reason);

                    case "error":
                        throw ChatGptApiErrors.FromStreamError(e["error"] as JObject ?? e, "答えの流れに error が来た。");
                }
            }
        }

        private static async Task<string> ReadAllAsync(HttpResponseMessage response, CancellationToken cancel, Uri url)
        {
            try
            {
                return response.Content == null ? string.Empty : await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                throw ChatGptWire.Translate(e, cancel, url);
            }
        }
    }
}
