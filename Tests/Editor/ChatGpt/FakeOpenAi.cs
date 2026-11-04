using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Monster.Balloon.AutomataSystem.ChatGpt.Tests
{
    /// <summary>
    /// Fake OpenAI sign-in and API server for tests, strict about the published rules.
    /// テスト用の偽の OpenAI（discovery・jwks・authorize・token・revoke・models・responses）。127.0.0.1 の空いた port で待つ。
    /// <para>
    /// ⭐ 決まりは OpenAI の説明書（developers.openai.com/siwc/token-sharing-open-source の sign-in・profiles-and-sessions・
    ///    token-reference・models-and-inference・errors-and-recovery・preview-limitations）に書いてある事だけを厳しく見る。
    ///    説明書に無い所は OAuth の決まり（RFC 6749・7636・7009・8707）の形にそろえる（例＝token の口の断りの JSON）。
    /// ⭐ authorize は、テストがブラウザの代わりに GET する。決まりに合えば redirect_uri へ 302 で戻す。
    ///    合わない頼みは 400 の頁を返して戻さない（本物の画面の出し方は説明書に無い＝テストが 302 でない事で気づく）。
    /// ⭐ 断った訳は <see cref="Refusals"/> に「口: 印: 訳」で残す（テストが中身を確かめる）。届いた頼みも全部覚える。
    /// </para>
    /// </summary>
    internal sealed partial class FakeOpenAi : IDisposable
    {
        internal const string DynamicClientId = "dynamic_agent_client";
        internal const string IssuedClientId = "oaiapp_test_client";
        internal const string FullScope = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct";
        internal const string PlanScope = "chatgpt.tokens.use.direct";
        internal const string Resource = "https://api.openai.com/v1";

        private static readonly Regex CodeChallengeShape = new Regex("^[A-Za-z0-9_-]{43}$");

        private readonly HttpListener listener = new HttpListener();
        private readonly RSA rsa = RSA.Create();
        private readonly object gate = new object();
        private bool registered;

        public FakeOpenAi()
        {
            this.rsa.KeySize = 2048;
            this.Port = FreePort();
            this.Base = "http://127.0.0.1:" + this.Port;
            this.listener.Prefixes.Add(this.Base + "/");
            this.listener.Start();
            Task.Run(this.LoopAsync);
        }

        public int Port { get; }

        public string Base { get; }

        // 戻りの作り方を変えるつまみ。

        /// <summary>初回の登録の戻りに、発行した client_id を付けるか（付けないと登録は済まない）。</summary>
        public bool ReturnClientId = true;

        /// <summary>2 回目以降（発行済みの client_id）の戻りにも client_id を付けるか。説明書は「付かないことがある」＝既定は付けない。</summary>
        public bool ReauthReturnsClientId;

        /// <summary>2 回目以降の戻りに付ける、別の client_id（受け取る側が断るかを見る）。</summary>
        public string ReauthClientIdOverride;

        public string CallbackStateOverride;
        public string CallbackError;
        public string CallbackErrorDescription;
        public string IdTokenNonceOverride;
        public string GrantedScope = FullScope;
        public long ExpiresIn = 3600;

        /// <summary>code の期限（説明書に値は無い＝OAuth の勧めの 10 分）。0 以下なら発行した時から切れている。</summary>
        public TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);

        /// <summary>false なら /v1/responses が 403 subscription_sharing_user_not_eligible を返す。</summary>
        public bool PlanEligible = true;

        /// <summary>true なら /v1/responses が 429 subscription_sharing_usage_limit_exceeded を返す。</summary>
        public bool UsageLimitReached;

        /// <summary>responses の答え方（何回目か・本文 → 番号・本文）。番号が 200 なら本文は SSE の字。決まりの確かめを通った頼みだけが来る。</summary>
        public Func<int, JObject, (int Status, string Body)> Responses;

        // 届いた頼み。
        public readonly List<Dictionary<string, string>> AuthorizeQueries = new List<Dictionary<string, string>>();
        public readonly List<Dictionary<string, string>> TokenForms = new List<Dictionary<string, string>>();
        public readonly List<Dictionary<string, string>> RevokeForms = new List<Dictionary<string, string>>();
        public readonly List<JObject> ResponseBodies = new List<JObject>();
        public readonly List<NameValueCollection> ResponseHeaders = new List<NameValueCollection>();

        /// <summary>断った頼み（「口: 印: 訳」）。</summary>
        public readonly List<string> Refusals = new List<string>();

        public ChatGptSettings Settings(string storage)
        {
            return new ChatGptSettings("Automata Test")
            {
                StorageDirectory = storage,
                Issuer = new Uri(this.Base),
                ApiBase = new Uri(this.Base + "/v1/"),
                OpenBrowser = null,
                PreferredPort = FreePort(),
                RequestTimeout = TimeSpan.FromSeconds(10),
                StreamIdleTimeout = TimeSpan.FromSeconds(10),
                SignInTimeout = TimeSpan.FromSeconds(20),
            };
        }

        public void Dispose()
        {
            try
            {
                this.listener.Stop();
                this.listener.Close();
            }
            catch (Exception)
            {
                // テストの片付け。
            }

            this.rsa.Dispose();
        }

        internal static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        private async Task LoopAsync()
        {
            while (this.listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await this.listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    return;
                }

                try
                {
                    this.Handle(context);
                }
                catch (Exception e)
                {
                    Write(context.Response, 500, "text/plain", e.GetType().Name);
                }
            }
        }

        private void Handle(HttpListenerContext c)
        {
            string path = c.Request.Url.AbsolutePath;
            string body = new StreamReader(c.Request.InputStream, Encoding.UTF8).ReadToEnd();
            switch (path)
            {
                case "/.well-known/openid-configuration":
                    WriteJson(c.Response, 200, new JObject
                    {
                        ["issuer"] = this.Base,
                        ["authorization_endpoint"] = this.Base + "/api/accounts/authorize",
                        ["token_endpoint"] = this.Base + "/api/accounts/oauth/token",
                        ["revocation_endpoint"] = this.Base + "/api/accounts/oauth/revoke",
                        ["jwks_uri"] = this.Base + "/jwks",
                    });
                    return;
                case "/jwks":
                    RSAParameters p = this.rsa.ExportParameters(false);
                    var key = new JObject { ["kty"] = "RSA", ["kid"] = "k1", ["alg"] = "RS256", ["use"] = "sig", ["n"] = B64(p.Modulus), ["e"] = B64(p.Exponent) };
                    WriteJson(c.Response, 200, new JObject { ["keys"] = new JArray(key) });
                    return;
                case "/api/accounts/authorize":
                    this.Authorize(c);
                    return;
                case "/api/accounts/oauth/token":
                    this.Token(c, body);
                    return;
                case "/api/accounts/oauth/revoke":
                    this.Revoke(c, body);
                    return;
                case "/v1/models":
                    this.Models(c);
                    return;
                case "/v1/responses":
                    this.ResponsesCall(c, body);
                    return;
                default:
                    Write(c.Response, 404, "text/plain", "not found");
                    return;
            }
        }

        // authorize の決まり（sign-in の「Start authorization」）。合えば redirect_uri へ 302 で戻す。
        private void Authorize(HttpListenerContext c)
        {
            NameValueCollection raw = c.Request.QueryString;
            var q = new Dictionary<string, string>();
            foreach (string k in raw.AllKeys)
            {
                if (k != null) q[k] = raw[k];
            }

            lock (this.gate) this.AuthorizeQueries.Add(q);

            string why = this.AuthorizeProblem(c.Request.HttpMethod, raw, q);
            if (why != null)
            {
                this.Refuse("authorize", "invalid_request", why);
                Write(c.Response, 400, "text/plain", "invalid_request: " + why);
                return;
            }

            bool registering = q["client_id"] == DynamicClientId;
            var back = new List<string>();
            if (this.CallbackError != null)
            {
                back.Add("error=" + Uri.EscapeDataString(this.CallbackError));
                if (this.CallbackErrorDescription != null) back.Add("error_description=" + Uri.EscapeDataString(this.CallbackErrorDescription));
            }
            else
            {
                string code = "code-" + ChatGptWire.RandomToken(16);
                lock (this.gate)
                {
                    if (registering && this.ReturnClientId) this.registered = true;
                    this.codes[code] = new CodeGrant
                    {
                        ClientId = IssuedClientId,
                        RedirectUri = q["redirect_uri"],
                        Challenge = q["code_challenge"],
                        Nonce = q["nonce"],
                        Resource = q["resource"],
                        Scope = this.GrantedScope,
                        ExpiresAt = DateTimeOffset.UtcNow + this.CodeLifetime,
                    };
                }

                back.Add("code=" + Uri.EscapeDataString(code));
                back.Add("scope=" + Uri.EscapeDataString(this.GrantedScope));
                string issued = registering
                    ? (this.ReturnClientId ? IssuedClientId : null)
                    : this.ReauthClientIdOverride ?? (this.ReauthReturnsClientId ? IssuedClientId : null);
                if (issued != null) back.Add("client_id=" + Uri.EscapeDataString(issued));
            }

            back.Add("state=" + Uri.EscapeDataString(this.CallbackStateOverride ?? q["state"]));
            c.Response.StatusCode = 302;
            c.Response.RedirectLocation = q["redirect_uri"] + "?" + string.Join("&", back);
            c.Response.Close();
        }

        // authorize の頼みが決まりに合わない訳。合えば null。
        private string AuthorizeProblem(string method, NameValueCollection raw, Dictionary<string, string> q)
        {
            if (method != "GET") return "authorize は GET";
            string[] required =
            {
                "client_id", "response_type", "redirect_uri", "scope", "resource", "state", "nonce",
                "code_challenge_method", "code_challenge", "ext_agent_host_id",
            };
            foreach (string name in required)
            {
                if (!q.TryGetValue(name, out string v) || string.IsNullOrEmpty(v)) return name + " が無い";
            }

            foreach (string k in raw.AllKeys)
            {
                if (k != null && raw.GetValues(k)?.Length > 1) return k + " が 2 つある";
            }

            bool registering = q["client_id"] == DynamicClientId;
            bool known;
            lock (this.gate) known = this.registered && q["client_id"] == IssuedClientId;
            if (!registering && !known) return "client_id が dynamic_agent_client でも発行済みの物でもない";
            if (registering && !q.ContainsKey("agent_name_hint")) return "初回の登録に agent_name_hint が無い";
            if (!registering && q.ContainsKey("agent_name_hint")) return "発行済みの client_id で agent_name_hint を付けた";
            if (q["response_type"] != "code") return "response_type が code でない";
            if (q["code_challenge_method"] != "S256") return "code_challenge_method が S256 でない";
            if (!CodeChallengeShape.IsMatch(q["code_challenge"])) return "code_challenge が base64url（= 無し）の SHA-256 でない";
            if (q["resource"] != Resource) return "resource が違う";
            if (!SameScopes(q["scope"], FullScope)) return "scope が全部そろっていない";
            if (!LoopbackRedirect(q["redirect_uri"])) return "redirect_uri が http://127.0.0.1:<port>/auth/callback でない";
            string host = q["ext_agent_host_id"];
            if (!host.StartsWith("urn:uuid:", StringComparison.Ordinal) && !host.StartsWith("urn:ietf:params:oauth:jwk-thumbprint:", StringComparison.Ordinal))
            {
                return "ext_agent_host_id の形が違う";
            }

            return null;
        }

        // ループバックの戻り先か（http・127.0.0.1・port は自由・path は /auth/callback・問いの字は無し）。
        private static bool LoopbackRedirect(string s)
        {
            if (!Uri.TryCreate(s, UriKind.Absolute, out Uri u)) return false;
            return u.Scheme == "http" && s.StartsWith("http://127.0.0.1:", StringComparison.Ordinal) && !u.IsDefaultPort &&
                   u.AbsolutePath == "/auth/callback" && string.IsNullOrEmpty(u.Query) && string.IsNullOrEmpty(u.Fragment);
        }

        private static bool SameScopes(string a, string b)
        {
            var x = new HashSet<string>((a ?? string.Empty).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
            var y = new HashSet<string>((b ?? string.Empty).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
            return x.SetEquals(y);
        }

        private void Refuse(string where, string code, string why)
        {
            lock (this.gate) this.Refusals.Add(where + ": " + code + ": " + why);
        }

        // 頼みの本文（form）を読む。同じ名前が 2 つあれば null の値で印す（呼ぶ側が断る）。
        private static Dictionary<string, string> Form(string body, out string duplicate)
        {
            duplicate = null;
            var result = new Dictionary<string, string>();
            foreach (string pair in body.Split('&'))
            {
                if (pair.Length == 0) continue;
                int eq = pair.IndexOf('=');
                string name = Uri.UnescapeDataString((eq < 0 ? pair : pair.Substring(0, eq)).Replace('+', ' '));
                string value = eq < 0 ? string.Empty : Uri.UnescapeDataString(pair.Substring(eq + 1).Replace('+', ' '));
                if (result.ContainsKey(name)) duplicate = name;
                result[name] = value;
            }

            return result;
        }

        private static string B64(byte[] data)
        {
            return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static void WriteJson(HttpListenerResponse r, int status, JObject body)
        {
            Write(r, status, "application/json", body.ToString(Formatting.None));
        }

        private static void Write(HttpListenerResponse r, int status, string type, string text)
        {
            byte[] data = Encoding.UTF8.GetBytes(text ?? string.Empty);
            r.StatusCode = status;
            r.ContentType = type;
            r.ContentLength64 = data.Length;
            r.OutputStream.Write(data, 0, data.Length);
            r.Close();
        }
    }
}
