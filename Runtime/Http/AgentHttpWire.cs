using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Monster.Balloon.AutomataSystem.Http
{
    /// <summary>
    /// 窓口が読み解いた、頼みの頭（HTTP の 1 行目と頭の欄）。
    /// </summary>
    internal sealed class AgentHttpRequestHead
    {
        public AgentHttpRequestHead(string method, string path, Dictionary<string, string> headers, long contentLength)
        {
            this.Method = method;
            this.Path = path;
            this.Headers = headers;
            this.ContentLength = contentLength;
        }

        public string Method { get; }

        /// <summary>道（"?" より前）。</summary>
        public string Path { get; }

        /// <summary>頭の欄（名前の大文字小文字は区別しない）。</summary>
        public Dictionary<string, string> Headers { get; }

        /// <summary>本文の長さ。長さの頭が無ければ 0。</summary>
        public long ContentLength { get; }

        public string Header(string name) => this.Headers.TryGetValue(name, out string v) ? v : null;

        /// <summary>使う側が「本文を送ってよいか」を待っているか（curl は大きな本文でこれを送る）。</summary>
        public bool ExpectsContinue =>
            string.Equals(this.Header("Expect"), "100-continue", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 窓口が話す HTTP/1.1 の、ごく小さな読み書き（1 つのつなぎで 1 本だけ答えて閉じる）。
    /// <para>
    /// ⭐ 読むのは「POST・長さの頭つきの本文」だけ。分割送り（chunked）は 411 で断る。
    /// ⚠ 長さ・Host・トークンの頭が 2 つある頼みは断る（どちらを信じるかの食い違いを作らない）。
    /// </para>
    /// </summary>
    internal static class AgentHttpWire
    {
        /// <summary>頭（1 行目と頭の欄）の上限。越えたら 431 で断る。</summary>
        public const int MaxHeadBytes = 16 * 1024;

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        private static readonly HashSet<string> SingletonHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Content-Length", "Host", "Authorization", "Transfer-Encoding",
        };

        private static readonly Dictionary<int, string> Reasons = new Dictionary<int, string>
        {
            [100] = "Continue",
            [200] = "OK",
            [400] = "Bad Request",
            [401] = "Unauthorized",
            [403] = "Forbidden",
            [404] = "Not Found",
            [405] = "Method Not Allowed",
            [411] = "Length Required",
            [413] = "Payload Too Large",
            [415] = "Unsupported Media Type",
            [429] = "Too Many Requests",
            [431] = "Request Header Fields Too Large",
            [500] = "Internal Server Error",
            [503] = "Service Unavailable",
        };

        /// <summary>「本文を送ってよい」の返事。</summary>
        public static readonly byte[] ContinueBytes = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");

        /// <summary>
        /// 頭の終わり（空の行 \r\n\r\n）を探す。見つかれば、頭の長さ（空の行の手前まで）を返す。無ければ -1。
        /// </summary>
        public static int FindHeadEnd(byte[] buffer, int from, int count)
        {
            for (int i = Math.Max(0, from); i + 3 < count; i++)
            {
                if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// 頭の字（空の行の手前まで）を読み解く。読めない時は false と、返す番号・code を返す。
        /// </summary>
        public static bool TryParseHead(string head, out AgentHttpRequestHead request, out int failStatus, out string failCode)
        {
            request = null;
            failStatus = 400;
            failCode = "bad_request";

            string[] lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] first = lines[0].Split(' ');
            if (first.Length != 3 || !first[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
            {
                return false;
            }

            string target = first[1];
            if (target.Length == 0 || target[0] != '/')
            {
                return false;
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                int colon = line.IndexOf(':');
                if (colon <= 0)
                {
                    return false;
                }

                string name = line.Substring(0, colon).Trim();
                string value = line.Substring(colon + 1).Trim();
                if (headers.ContainsKey(name))
                {
                    if (SingletonHeaders.Contains(name))
                    {
                        failCode = "duplicate_header";
                        return false;
                    }

                    continue;
                }

                headers[name] = value;
            }

            if (headers.ContainsKey("Transfer-Encoding"))
            {
                failStatus = 411;
                failCode = "length_required";
                return false;
            }

            long length = 0;
            if (headers.TryGetValue("Content-Length", out string lengthText)
                && !long.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture, out length))
            {
                failCode = "bad_length";
                return false;
            }

            int q = target.IndexOf('?');
            string path = q < 0 ? target : target.Substring(0, q);
            request = new AgentHttpRequestHead(first[0], path, headers, length);
            return true;
        }

        /// <summary>返事 1 本ぶんのバイト列（JSON・キャッシュさせない・つなぎは閉じる）を作る。</summary>
        public static byte[] FormatResponse(int status, string json)
        {
            byte[] body = Utf8NoBom.GetBytes(json ?? string.Empty);
            var head = new StringBuilder(160);
            head.Append("HTTP/1.1 ").Append(status.ToString(CultureInfo.InvariantCulture)).Append(' ')
                .Append(Reasons.TryGetValue(status, out string reason) ? reason : "Status").Append("\r\n");
            head.Append("Content-Type: application/json; charset=utf-8\r\n");
            head.Append("Content-Length: ").Append(body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            // ⚠ CORS の頭は返さない（ブラウザのページに答えを読ませない）。
            head.Append("Cache-Control: no-store\r\n");
            if (status == 405)
            {
                head.Append("Allow: POST\r\n");
            }

            head.Append("Connection: close\r\n\r\n");

            byte[] headBytes = Encoding.ASCII.GetBytes(head.ToString());
            var all = new byte[headBytes.Length + body.Length];
            Buffer.BlockCopy(headBytes, 0, all, 0, headBytes.Length);
            Buffer.BlockCopy(body, 0, all, headBytes.Length, body.Length);
            return all;
        }
    }
}
