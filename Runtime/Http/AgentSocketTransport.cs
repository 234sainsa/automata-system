using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Scripting;

namespace Monster.Balloon.AutomataSystem.Http
{
    /// <summary>
    /// Game Agent API の窓口（同じ PC の中だけ）。<c>127.0.0.1:{port}</c> のソケットで待ち、小さな HTTP/1.1 で答える。
    /// <para>
    /// ⚠ 窓口のスレッドは Unity の物に触らない。受けた頼みを門に通し、Host の列に積んで、主スレッドの答えを返すだけ。
    /// ⚠ 待つ口は 127.0.0.1 の 1 本だけ。同じ LAN のほかの機械からは届かない。
    /// ⚠ HttpListener は使わない＝HttpListener のソケットには「子プロセスへ受け継がせない」印を付けられず、
    ///   窓口を閉じても口が残った（実測・<see cref="SocketHandleInheritance"/>）。自前のソケットなら印を付けられる。
    /// ⚠ 切れたつなぎの例外は赤にせず捨てる（AI が使う Editor は、赤 1 件で Error Pause に止まるため）。
    /// </para>
    /// </summary>
    internal sealed class AgentSocketTransport : IAgentTransport
    {
        private const int Backlog = 16;

        /// <summary>同時に答えるつなぎの上限。越えた分は 503 で断る。</summary>
        private const int MaxConnections = 16;

        /// <summary>頭と本文を読む時・返事を書く時の待ち（ミリ秒）。遅い相手に窓口のスレッドを取られ続けないため。</summary>
        private const int IoTimeoutMs = 5000;

        /// <summary>閉じる前に読み捨てる、相手の送り残しの上限（バイト）。</summary>
        private const int DrainLimitBytes = 128 * 1024;

        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        private static int inheritWarned;

        private Socket listener;
        private IAgentFrontDoor door;
        private volatile bool stopped;
        private int active;

        public int Port { get; private set; }

        public bool TryStart(IAgentFrontDoor frontDoor, int firstPort, int portCount, out string why)
        {
            this.door = frontDoor ?? throw new ArgumentNullException(nameof(frontDoor));
            why = null;
            for (int i = 0; i < portCount; i++)
            {
                int port = firstPort + i;
                Socket candidate = null;
                try
                {
                    candidate = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

                    // ⭐ ほかのプロセスに同じ口を重ねて開かせない。
                    candidate.ExclusiveAddressUse = true;
                    MarkNotInheritable(candidate);
                    candidate.Bind(new IPEndPoint(IPAddress.Loopback, port));
                    candidate.Listen(Backlog);
                }
                catch (Exception e)
                {
                    // ほかの Editor（別の作業場）が同じ口で待っていれば、次の口を試す。
                    why = e.GetType().Name + ": " + e.Message;
                    SafeClose(candidate);
                    continue;
                }

                this.listener = candidate;
                this.Port = port;
                Task.Run(() => this.AcceptLoopAsync(candidate));
                return true;
            }

            why = "no free port in " + firstPort + "-" + (firstPort + portCount - 1) + " (last: " + why + ")";
            return false;
        }

        public void Dispose()
        {
            this.stopped = true;
            Socket l = this.listener;
            this.listener = null;
            SafeClose(l);
        }

        private async Task AcceptLoopAsync(Socket l)
        {
            while (!this.stopped)
            {
                Socket client;
                try
                {
                    client = await l.AcceptAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    // 窓口を閉じた。
                    return;
                }
                catch (Exception)
                {
                    // 閉じた時の例外は、黙って終える。閉じていないのに落ちた時は、少し待って受け直す。
                    if (this.stopped)
                    {
                        return;
                    }

                    await Task.Delay(50).ConfigureAwait(false);
                    continue;
                }

                if (this.stopped)
                {
                    SafeClose(client);
                    return;
                }

                MarkNotInheritable(client);
                if (Interlocked.Increment(ref this.active) > MaxConnections)
                {
                    Interlocked.Decrement(ref this.active);
                    _ = Task.Run(() => RefuseAndClose(client, 503, AgentReplies.ErrorJson("busy", "retry")));
                    continue;
                }

                // 1 本ずつ別の仕事で答える（長い待ちが、後ろの頼みを止めないように）。
                _ = Task.Run(() => this.ServeAsync(client));
            }
        }

        private async Task ServeAsync(Socket client)
        {
            try
            {
                client.NoDelay = true;
                client.ReceiveTimeout = IoTimeoutMs;
                client.SendTimeout = IoTimeoutMs;
                using (var stream = new NetworkStream(client, false))
                {
                    await this.ServeOneAsync(stream).ConfigureAwait(false);
                }

                FinishAndClose(client);
            }
            catch (Exception)
            {
                // 使う側が先に切った・時間切れなど。赤にせず捨てる。
                SafeClose(client);
            }
            finally
            {
                Interlocked.Decrement(ref this.active);
            }
        }

        private async Task ServeOneAsync(NetworkStream stream)
        {
            // ── 頭を読む（空の行まで）──
            var buffer = new byte[AgentHttpWire.MaxHeadBytes];
            int filled = 0;
            int headEnd = -1;
            while (headEnd < 0)
            {
                if (filled == buffer.Length)
                {
                    Write(stream, 431, AgentReplies.ErrorJson("head_too_large", "max_16384_bytes"));
                    return;
                }

                int n = stream.Read(buffer, filled, buffer.Length - filled);
                if (n <= 0)
                {
                    return;
                }

                int searchFrom = filled - 3;
                filled += n;
                headEnd = AgentHttpWire.FindHeadEnd(buffer, searchFrom, filled);
            }

            string headText = Encoding.ASCII.GetString(buffer, 0, headEnd);
            if (!AgentHttpWire.TryParseHead(headText, out AgentHttpRequestHead head, out int badStatus, out string badCode))
            {
                Write(stream, badStatus, AgentReplies.ErrorJson(badCode, null));
                return;
            }

            // ── 門 ──
            var gateRequest = new AgentGateRequest(
                head.Method,
                head.Path,
                head.Header("Host"),
                head.Header("Origin") != null,
                head.Header("Content-Type"),
                head.ContentLength,
                head.Header("Authorization"),
                head.Headers);

            AgentGateVerdict verdict = this.door.Check(in gateRequest);
            if (!verdict.Passed)
            {
                Write(stream, verdict.Status, AgentReplies.ErrorJson(verdict.Code, verdict.Hint));
                return;
            }

            // ── 本文を読む（門が長さの上限を見た後）──
            if (head.ExpectsContinue)
            {
                stream.Write(AgentHttpWire.ContinueBytes, 0, AgentHttpWire.ContinueBytes.Length);
            }

            var body = new byte[(int)head.ContentLength];
            int bodyStart = headEnd + 4;
            int got = Math.Min(filled - bodyStart, body.Length);
            Buffer.BlockCopy(buffer, bodyStart, body, 0, got);
            while (got < body.Length)
            {
                int n = stream.Read(body, got, body.Length - got);
                if (n <= 0)
                {
                    return;
                }

                got += n;
            }

            if (!TryParseObject(body, out JObject json, out string hint))
            {
                Write(stream, 400, AgentReplies.ErrorJson("bad_json", hint));
                return;
            }

            // ── 主スレッドの答えを待つ（長い待ちの道 wait は、頼まれた待ちの分だけ長く待つ）──
            // ⭐ 待つ長さは、列に積む前に聞く（積んだ後は、主スレッドが本文を読み始めるため）。
            TimeSpan timeout = this.door.AnswerTimeoutFor(head.Path, json);
            Task<AgentReply> answer = this.door.SubmitAsync(head.Path, verdict.Face, json);
            Task finished = await Task.WhenAny(answer, Task.Delay(timeout)).ConfigureAwait(false);
            if (finished != answer)
            {
                // 主スレッドが決まった秒数さばけなかった（読み込み中の長いフレームなど）。使う側はやり直せばよい。
                Write(stream, 503, AgentReplies.ErrorJson("main_thread_busy", "retry"));
                return;
            }

            AgentReply reply = answer.Result;
            Write(stream, reply.Status, reply.Json);
        }

        private static bool TryParseObject(byte[] body, out JObject json, out string hint)
        {
            hint = null;
            string text;
            try
            {
                text = StrictUtf8.GetString(body);
            }
            catch (DecoderFallbackException)
            {
                json = null;
                hint = "not_utf8";
                return false;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                json = new JObject();
                return true;
            }

            try
            {
                using (var reader = new JsonTextReader(new StringReader(text)))
                {
                    reader.DateParseHandling = DateParseHandling.None;
                    reader.MaxDepth = 32;
                    json = JToken.ReadFrom(reader) as JObject;
                }
            }
            catch (JsonException)
            {
                json = null;
                hint = "parse_error";
                return false;
            }

            if (json == null)
            {
                hint = "object_expected";
                return false;
            }

            return true;
        }

        private static void Write(NetworkStream stream, int status, string json)
        {
            byte[] bytes = AgentHttpWire.FormatResponse(status, json);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }

        private static void RefuseAndClose(Socket client, int status, string json)
        {
            try
            {
                client.SendTimeout = IoTimeoutMs;
                byte[] bytes = AgentHttpWire.FormatResponse(status, json);
                client.Send(bytes);
                FinishAndClose(client);
            }
            catch (Exception)
            {
                SafeClose(client);
            }
        }

        /// <summary>
        /// 送り終えたことを相手に知らせてから閉じる。
        /// ⚠ 読み残しがあるまま閉じると、Windows は相手に RST を送り、相手が返事を読む前に切れることがある。
        ///   だから送る側を閉じた後、相手が送り残した分を少しだけ読み捨ててから閉じる。
        /// </summary>
        private static void FinishAndClose(Socket client)
        {
            try
            {
                client.Shutdown(SocketShutdown.Send);
                client.ReceiveTimeout = 500;
                var sink = new byte[4096];
                int total = 0;
                while (total < DrainLimitBytes)
                {
                    int n = client.Receive(sink);
                    if (n <= 0)
                    {
                        break;
                    }

                    total += n;
                }
            }
            catch (Exception)
            {
                // 相手が先に閉じた・時間切れ。どちらでも閉じてよい。
            }

            SafeClose(client);
        }

        private static void MarkNotInheritable(Socket socket)
        {
            if (!SocketHandleInheritance.TryDisable(socket, out string why)
                && Interlocked.Exchange(ref inheritWarned, 1) == 0)
            {
                Debug.LogWarning("[AgentApi] ソケットに「子プロセスへ受け継がせない」印を付けられなかった（" + why
                    + "）。窓口を閉じても、子プロセスが終わるまで口が残ることがある。");
            }
        }

        private static void SafeClose(Socket s)
        {
            if (s == null)
            {
                return;
            }

            try
            {
                s.Close();
            }
            catch (Exception)
            {
                // もう閉じている。
            }
        }
    }

    /// <summary>
    /// 窓口を作る口を、芯へ渡す係。
    /// ⭐ 芯は窓口の型を知らない。窓口のアセンブリは、読み込まれた時にここで自分を作る口を芯へ置く。
    /// ⚠ Host が窓口を作るのは AfterSceneLoad。AfterAssembliesLoaded は、それより前に必ず来る。
    /// </summary>
    [Preserve]
    internal static class AgentSocketTransportRegistration
    {
        [Preserve]
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        private static void Register() => AgentTransports.Register(() => new AgentSocketTransport());
    }
}
