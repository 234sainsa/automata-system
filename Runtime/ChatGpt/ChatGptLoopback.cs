using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Monster.Balloon.AutomataSystem.ChatGpt
{
    /// <summary>
    /// ログインの戻り（http://127.0.0.1:port/auth/callback）を 1 回だけ受ける待ち受け。
    /// <para>
    /// ⭐ ブラウザを開く前に待ち受けを始める。host は必ず 127.0.0.1（localhost は使わない）・path は /auth/callback 固定。
    /// ⭐ 自前の Socket で受ける。HttpListener は Unity が起こした子プロセスに受け継がれ、閉じても口が残った（本体の窓口と同じ訳）。
    /// ⭐ 確かめ＝GET・Host・path・state（1 つだけ・定数時間で比べる）・code（1 つだけ）。違う path や Host の頼みには断りを返して待ち続ける。
    ///    state が合わない・error が付いた戻りは、ログインの失敗にする（code を交換しない）。
    /// </para>
    /// </summary>
    internal sealed class ChatGptLoopback : IDisposable
    {
        internal const string CallbackPath = "/auth/callback";
        private const int MaxHeadBytes = 16 * 1024;

        private readonly Socket socket;

        private ChatGptLoopback(Socket socket, int port)
        {
            this.socket = socket;
            this.Port = port;
            this.RedirectUri = new Uri("http://127.0.0.1:" + port + CallbackPath);
        }

        public int Port { get; }

        /// <summary>authorize と token の交換の両方で送る、同じ redirect_uri。</summary>
        public Uri RedirectUri { get; }

        /// <summary>待ち受けを始める。第 1 候補の port が塞がっていれば、OS に空いた port を選ばせる。</summary>
        public static ChatGptLoopback Start(int preferredPort)
        {
            Socket s = TryBind(preferredPort) ?? TryBind(0);
            if (s == null) throw new ChatGptException(ChatGptErrorKind.Network, "ログインの戻りを待つ口を開けない。");
            return new ChatGptLoopback(s, ((IPEndPoint)s.LocalEndPoint).Port);
        }

        /// <summary>
        /// 正しい戻りが 1 つ来るまで待つ。戻りの問いの字（名前ごとの値の列）を返す。state が合わない・error 付きなら投げる。
        /// </summary>
        public async Task<Dictionary<string, List<string>>> WaitAsync(string expectedState, CancellationToken cancel)
        {
            using (cancel.Register(() => this.Dispose()))
            {
                while (true)
                {
                    Socket client;
                    try
                    {
                        client = await this.socket.AcceptAsync().ConfigureAwait(false);
                    }
                    catch (Exception e) when (e is ObjectDisposedException || e is SocketException)
                    {
                        cancel.ThrowIfCancellationRequested();
                        throw new ChatGptException(ChatGptErrorKind.Network, "ログインの戻りを待つ口が閉じた。", inner: e);
                    }

                    using (client)
                    {
                        Dictionary<string, List<string>> query = await this.HandleAsync(client, expectedState).ConfigureAwait(false);
                        if (query != null) return query;
                    }
                }
            }
        }

        public void Dispose()
        {
            try
            {
                this.socket.Close();
            }
            catch (Exception)
            {
                // 閉じる時の失敗は気にしない（もう使わない口）。
            }
        }

        // 1 つの頼みを読む。正しい戻りなら問いの字を返す・関係の無い頼みなら null（待ち続ける）・食い違いなら投げる。
        private async Task<Dictionary<string, List<string>>> HandleAsync(Socket client, string expectedState)
        {
            string head;
            using (var stream = new NetworkStream(client, false))
            {
                head = await ReadHeadAsync(stream).ConfigureAwait(false);
                if (head == null) return null;

                string[] lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
                string[] parts = lines[0].Split(' ');
                if (parts.Length != 3)
                {
                    await RespondAsync(stream, 400, "Bad request.").ConfigureAwait(false);
                    return null;
                }

                string target = parts[1];
                int q = target.IndexOf('?');
                string path = q < 0 ? target : target.Substring(0, q);
                if (path != CallbackPath)
                {
                    await RespondAsync(stream, 404, "Not found.").ConfigureAwait(false);
                    return null;
                }

                if (parts[0] != "GET")
                {
                    await RespondAsync(stream, 405, "Method not allowed.").ConfigureAwait(false);
                    return null;
                }

                if (HostOf(lines) != "127.0.0.1:" + this.Port)
                {
                    await RespondAsync(stream, 400, "Bad host.").ConfigureAwait(false);
                    return null;
                }

                Dictionary<string, List<string>> query = ChatGptWire.ParseQuery(q < 0 ? string.Empty : target.Substring(q + 1));
                if (!ChatGptWire.FixedTimeEquals(ChatGptWire.Single(query, "state"), expectedState))
                {
                    await RespondAsync(stream, 400, "Sign-in did not complete. You can close this window and try again.").ConfigureAwait(false);
                    throw new ChatGptException(ChatGptErrorKind.SignInFailed, "ログインの戻りの state が合わない（交換しない）。", "state_mismatch");
                }

                string error = ChatGptWire.Single(query, "error");
                if (error != null || query.ContainsKey("error"))
                {
                    await RespondAsync(stream, 200, "Sign-in did not complete. You can close this window.").ConfigureAwait(false);
                    throw new ChatGptException(ChatGptErrorKind.SignInFailed, "ログインが断られた: " + (error ?? "error"), error ?? "error");
                }

                if (ChatGptWire.Single(query, "code") == null)
                {
                    await RespondAsync(stream, 400, "Sign-in did not complete. You can close this window and try again.").ConfigureAwait(false);
                    throw new ChatGptException(ChatGptErrorKind.SignInFailed, "ログインの戻りに code が 1 つだけ付いていない。", "missing_code");
                }

                await RespondAsync(stream, 200, "Signed in. You can close this window and return to the game.").ConfigureAwait(false);
                return query;
            }
        }

        private static string HostOf(string[] lines)
        {
            string host = null;
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon <= 0) continue;
                if (!lines[i].Substring(0, colon).Trim().Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
                if (host != null) return null; // Host が 2 つある頼みは受けない。
                host = lines[i].Substring(colon + 1).Trim();
            }

            return host;
        }

        // 頼みの頭（空の行まで）を読む。長すぎる・10 秒で来ない時は null。
        private static async Task<string> ReadHeadAsync(NetworkStream stream)
        {
            var buffer = new byte[MaxHeadBytes];
            int length = 0;
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                try
                {
                    while (length < buffer.Length)
                    {
                        int n = await stream.ReadAsync(buffer, length, buffer.Length - length, timeout.Token).ConfigureAwait(false);
                        if (n <= 0) return null;
                        length += n;
                        string text = Encoding.ASCII.GetString(buffer, 0, length);
                        int end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                        if (end >= 0) return text.Substring(0, end);
                    }
                }
                catch (Exception)
                {
                    return null;
                }
            }

            return null;
        }

        private static async Task RespondAsync(NetworkStream stream, int status, string message)
        {
            string html = "<!doctype html><html><head><meta charset=\"utf-8\"><title>ChatGPT sign-in</title></head>" +
                          "<body style=\"font-family:sans-serif;margin:3em\"><p>" + WebUtility.HtmlEncode(message) + "</p></body></html>";
            byte[] body = Encoding.UTF8.GetBytes(html);
            string reason = status == 200 ? "OK" : status == 404 ? "Not Found" : status == 405 ? "Method Not Allowed" : "Bad Request";
            string head = "HTTP/1.1 " + status + " " + reason + "\r\n" +
                          "Content-Type: text/html; charset=utf-8\r\n" +
                          "Content-Length: " + body.Length + "\r\n" +
                          "Cache-Control: no-store\r\n" +
                          "Connection: close\r\n\r\n";
            try
            {
                byte[] h = Encoding.ASCII.GetBytes(head);
                await stream.WriteAsync(h, 0, h.Length).ConfigureAwait(false);
                await stream.WriteAsync(body, 0, body.Length).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // ブラウザが先に閉じても、ログインの結果には関係しない。
            }
        }

        private static Socket TryBind(int port)
        {
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                s.ExclusiveAddressUse = true;
                DisableInheritance(s);
                s.Bind(new IPEndPoint(IPAddress.Loopback, port));
                s.Listen(8);
                return s;
            }
            catch (SocketException)
            {
                s.Close();
                return null;
            }
        }

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);

        // Windows では、Unity の子プロセスにソケットを受け継がせない印を付ける（付けられなくても待ち受けは使える）。
        private static void DisableInheritance(Socket s)
        {
            SetHandleInformation(s.Handle, 0x00000001, 0);
        }
#else
        private static void DisableInheritance(Socket s)
        {
        }
#endif
    }
}
