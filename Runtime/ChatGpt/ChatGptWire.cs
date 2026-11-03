using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Monster.Balloon.AutomataSystem.ChatGpt
{
    /// <summary>
    /// ログインと API の通信で使う小さな部品（乱数・base64url・PKCE・form の POST・JSON の GET・問いの字の読み分け）。
    /// </summary>
    internal static class ChatGptWire
    {
        /// <summary>暗号の乱数で、base64url（= 無し）の字を作る。</summary>
        public static string RandomToken(int bytes = 32)
        {
            byte[] buffer = new byte[bytes];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(buffer);
            }

            return Base64Url(buffer);
        }

        /// <summary>PKCE の code_challenge（verifier の SHA-256 を base64url・= 無し）。</summary>
        public static string CodeChallenge(string verifier)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return Base64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
            }
        }

        public static string Base64Url(byte[] data)
        {
            return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        public static byte[] FromBase64Url(string text)
        {
            string s = text.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "="; break;
            }

            return Convert.FromBase64String(s);
        }

        /// <summary>2 つの字を、長さが同じなら中身に関係なく同じ時間で比べる（state の比べ）。</summary>
        public static bool FixedTimeEquals(string a, string b)
        {
            if (a == null || b == null) return false;
            byte[] x = Encoding.UTF8.GetBytes(a);
            byte[] y = Encoding.UTF8.GetBytes(b);
            if (x.Length != y.Length) return false;
            int diff = 0;
            for (int i = 0; i < x.Length; i++) diff |= x[i] ^ y[i];
            return diff == 0;
        }

        /// <summary>URL の問いの字（?a=1&b=2）を名前ごとの値の列に分ける。同じ名前が 2 つ以上あれば列に並ぶ。</summary>
        public static Dictionary<string, List<string>> ParseQuery(string query)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(query)) return result;
            string q = query[0] == '?' ? query.Substring(1) : query;
            foreach (string pair in q.Split('&'))
            {
                if (pair.Length == 0) continue;
                int eq = pair.IndexOf('=');
                string name = Decode(eq < 0 ? pair : pair.Substring(0, eq));
                string value = eq < 0 ? string.Empty : Decode(pair.Substring(eq + 1));
                if (!result.TryGetValue(name, out List<string> list)) result[name] = list = new List<string>();
                list.Add(value);
            }

            return result;
        }

        /// <summary>問いの字の値を 1 つだけ取る。無い・2 つ以上ある時は null。</summary>
        public static string Single(Dictionary<string, List<string>> query, string name)
        {
            return query.TryGetValue(name, out List<string> list) && list.Count == 1 ? list[0] : null;
        }

        public static string BuildQuery(IEnumerable<KeyValuePair<string, string>> pairs)
        {
            var sb = new StringBuilder();
            foreach (KeyValuePair<string, string> p in pairs)
            {
                if (p.Value == null) continue;
                if (sb.Length > 0) sb.Append('&');
                sb.Append(Uri.EscapeDataString(p.Key)).Append('=').Append(Uri.EscapeDataString(p.Value));
            }

            return sb.ToString();
        }

        /// <summary>form の POST を送り、HTTP の番号と本文を返す（失敗の番号でも投げない・つながらない時だけ投げる）。</summary>
        public static async Task<(int Status, string Body)> PostFormAsync(
            HttpClient http, Uri url, IEnumerable<KeyValuePair<string, string>> form, TimeSpan timeout, CancellationToken cancel)
        {
            using (var content = new StringContent(BuildQuery(form), Encoding.UTF8, "application/x-www-form-urlencoded"))
            {
                content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
                using (var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content })
                {
                    request.Headers.Accept.ParseAdd("application/json");
                    return await SendAsync(http, request, timeout, cancel).ConfigureAwait(false);
                }
            }
        }

        /// <summary>GET を送り、番号と本文を返す。bearer があれば Authorization に付ける。</summary>
        public static async Task<(int Status, string Body)> GetAsync(
            HttpClient http, Uri url, string bearer, TimeSpan timeout, CancellationToken cancel)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                request.Headers.Accept.ParseAdd("application/json");
                if (bearer != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                return await SendAsync(http, request, timeout, cancel).ConfigureAwait(false);
            }
        }

        /// <summary>JSON のオブジェクトとして読む。読めなければ BadResponse で投げる（本文は字に入れない）。</summary>
        public static JObject ParseObject(string body, string what)
        {
            try
            {
                if (JToken.Parse(body) is JObject obj) return obj;
            }
            catch (Exception)
            {
                // 下で投げる。
            }

            throw new ChatGptException(ChatGptErrorKind.BadResponse, what + " の返りが JSON のオブジェクトでない。");
        }

        /// <summary>
        /// 1 回の頼みを送る。待ち時間を越えたら Network、呼ぶ側の取り消しなら Cancelled で投げる。
        /// </summary>
        private static async Task<(int, string)> SendAsync(HttpClient http, HttpRequestMessage request, TimeSpan timeout, CancellationToken cancel)
        {
            using (var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel))
            {
                limit.CancelAfter(timeout);
                try
                {
                    using (HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, limit.Token).ConfigureAwait(false))
                    {
                        string body = response.Content == null ? string.Empty : await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        return ((int)response.StatusCode, body);
                    }
                }
                catch (Exception e) when (!(e is ChatGptException))
                {
                    throw Translate(e, cancel, request.RequestUri);
                }
            }
        }

        /// <summary>通信の例外を、訳の印の付いた例外に直す（URL の問いの字は出さない）。</summary>
        public static ChatGptException Translate(Exception e, CancellationToken cancel, Uri url)
        {
            string where = url == null ? "?" : url.GetLeftPart(UriPartial.Path);
            if (cancel.IsCancellationRequested) return new ChatGptException(ChatGptErrorKind.Cancelled, "取り消した: " + where, inner: e);
            if (e is OperationCanceledException) return new ChatGptException(ChatGptErrorKind.Network, "待ち時間を越えた: " + where, inner: e);
            return new ChatGptException(ChatGptErrorKind.Network, "つながらない: " + where + " (" + e.GetType().Name + ")", inner: e);
        }

        private static string Decode(string s)
        {
            return Uri.UnescapeDataString(s.Replace('+', ' '));
        }
    }
}
