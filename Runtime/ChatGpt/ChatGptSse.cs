using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Monster.Balloon.AutomataSystem.ChatGpt
{
    /// <summary>
    /// SSE（text/event-stream）の読み手。出来事 1 つを JSON のオブジェクトで返す。
    /// ⭐ 出来事の名前は JSON の "type" を使う（無ければ event: の行の名前を "type" に入れる）。
    /// ⭐ 新しい字が来ないまま idle を越えたら投げる（呼ぶ側が流れを閉じる＝読みかけの手も止まる）。
    /// </summary>
    internal sealed class ChatGptSseReader
    {
        private readonly StreamReader reader;

        public ChatGptSseReader(Stream stream)
        {
            this.reader = new StreamReader(stream, Encoding.UTF8);
        }

        /// <summary>次の出来事を返す。流れが終われば null。</summary>
        public async Task<JObject> NextAsync(TimeSpan idle, CancellationToken cancel)
        {
            string eventName = null;
            var data = new StringBuilder();
            while (true)
            {
                string line = await this.ReadLineAsync(idle, cancel).ConfigureAwait(false);
                if (line == null) return data.Length > 0 ? Parse(eventName, data.ToString()) : null;

                if (line.Length == 0)
                {
                    if (data.Length == 0)
                    {
                        eventName = null;
                        continue;
                    }

                    JObject parsed = Parse(eventName, data.ToString());
                    if (parsed != null) return parsed;
                    eventName = null;
                    data.Clear();
                    continue;
                }

                if (line[0] == ':') continue; // 心拍などの注釈の行。
                int colon = line.IndexOf(':');
                string field = colon < 0 ? line : line.Substring(0, colon);
                string value = colon < 0 ? string.Empty : line.Substring(colon + 1);
                if (value.StartsWith(" ")) value = value.Substring(1);
                if (field == "event") eventName = value;
                else if (field == "data") data.Append(data.Length > 0 ? "\n" : string.Empty).Append(value);
            }
        }

        private async Task<string> ReadLineAsync(TimeSpan idle, CancellationToken cancel)
        {
            Task<string> read = this.reader.ReadLineAsync();
            using (var stop = CancellationTokenSource.CreateLinkedTokenSource(cancel))
            {
                Task done = await Task.WhenAny(read, Task.Delay(idle, stop.Token)).ConfigureAwait(false);

                // 待ちの時計は毎行止める（止めないと、行ごとに時計が残る）。
                stop.Cancel();
                if (done == read) return await read.ConfigureAwait(false);
            }

            // 読みかけの手は、呼ぶ側が流れを閉じた時に終わる。その失敗は誰も待たないので、ここで拾っておく。
            _ = read.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
            cancel.ThrowIfCancellationRequested();
            throw new TimeoutException("流れに新しい字が来ない。");
        }

        // data を JSON として読む。[DONE] や読めない data は null（飛ばす）。
        private static JObject Parse(string eventName, string data)
        {
            if (data == "[DONE]") return null;
            JObject obj;
            try
            {
                obj = JToken.Parse(data) as JObject;
            }
            catch (Exception)
            {
                return null;
            }

            if (obj != null && obj["type"] == null && eventName != null) obj["type"] = eventName;
            return obj;
        }
    }
}
