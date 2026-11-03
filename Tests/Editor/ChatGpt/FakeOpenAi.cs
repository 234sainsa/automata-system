using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Monster.Balloon.AutomataSystem.ChatGpt.Tests
{
    /// <summary>
    /// Fake OpenAI sign-in and API server for tests.
    /// テスト用の偽の OpenAI（discovery・jwks・authorize・token・revoke・models・responses）。127.0.0.1 の空いた port で待つ。
    /// <para>
    /// ⭐ authorize は、テストがブラウザの代わりに GET する。偽のサーバーは redirect_uri へ 302 で code・state・client_id を返す。
    /// ⭐ 届いた頼みは全部覚える（テストが中身を確かめる）。
    /// </para>
    /// </summary>
    internal sealed class FakeOpenAi : IDisposable
    {
        internal const string IssuedClientId = "oaiapp_test_client";
        internal const string FullScope = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct";

        private readonly HttpListener listener = new HttpListener();
        private readonly RSA rsa = RSA.Create();
        private readonly object gate = new object();
        private string lastNonce;
        private int refreshCount;

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
        public bool ReturnClientId = true;
        public string CallbackStateOverride;
        public string CallbackError;
        public string IdTokenNonceOverride;
        public string GrantedScope = FullScope;
        public long ExpiresIn = 3600;

        /// <summary>responses の答え方（何回目か・本文 → 番号・本文）。番号が 200 なら本文は SSE の字。</summary>
        public Func<int, JObject, (int Status, string Body)> Responses;

        // 届いた頼み。
        public readonly List<Dictionary<string, string>> AuthorizeQueries = new List<Dictionary<string, string>>();
        public readonly List<Dictionary<string, string>> TokenForms = new List<Dictionary<string, string>>();
        public readonly List<Dictionary<string, string>> RevokeForms = new List<Dictionary<string, string>>();
        public readonly List<JObject> ResponseBodies = new List<JObject>();
        public readonly List<NameValueCollection> ResponseHeaders = new List<NameValueCollection>();

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
                    this.Token(c, Form(body));
                    return;
                case "/api/accounts/oauth/revoke":
                    lock (this.gate) this.RevokeForms.Add(Form(body));
                    Write(c.Response, 200, "application/json", string.Empty);
                    return;
                case "/v1/models":
                    WriteJson(c.Response, 200, new JObject
                    {
                        ["models"] = new JArray(
                            new JObject { ["slug"] = "model-a", ["display_name"] = "Model A", ["visibility"] = "list" },
                            new JObject { ["slug"] = "model-hidden", ["display_name"] = "Hidden", ["visibility"] = "hide" },
                            new JObject { ["slug"] = "model-b", ["display_name"] = "Model B", ["visibility"] = "list" }),
                    });
                    return;
                case "/v1/responses":
                    int index;
                    JObject request = JObject.Parse(body);
                    lock (this.gate)
                    {
                        index = this.ResponseBodies.Count;
                        this.ResponseBodies.Add(request);
                        this.ResponseHeaders.Add(new NameValueCollection(c.Request.Headers));
                    }

                    (int status, string text) = this.Responses(index, request);
                    Write(c.Response, status, status == 200 ? "text/event-stream" : "application/json", text);
                    return;
                default:
                    Write(c.Response, 404, "text/plain", "not found");
                    return;
            }
        }

        private void Authorize(HttpListenerContext c)
        {
            var q = new Dictionary<string, string>();
            foreach (string k in c.Request.QueryString.AllKeys) q[k] = c.Request.QueryString[k];
            lock (this.gate)
            {
                this.AuthorizeQueries.Add(q);
                this.lastNonce = q["nonce"];
            }

            var back = new List<string>();
            if (this.CallbackError != null)
            {
                back.Add("error=" + Uri.EscapeDataString(this.CallbackError));
            }
            else
            {
                back.Add("code=code-" + this.AuthorizeQueries.Count);
                if (this.ReturnClientId) back.Add("client_id=" + IssuedClientId);
            }

            back.Add("state=" + Uri.EscapeDataString(this.CallbackStateOverride ?? q["state"]));
            c.Response.StatusCode = 302;
            c.Response.RedirectLocation = q["redirect_uri"] + "?" + string.Join("&", back);
            c.Response.Close();
        }

        private void Token(HttpListenerContext c, Dictionary<string, string> form)
        {
            int n;
            lock (this.gate)
            {
                this.TokenForms.Add(form);
                n = ++this.refreshCount;
            }

            var reply = new JObject
            {
                ["access_token"] = "access-" + n,
                ["refresh_token"] = "refresh-" + n,
                ["token_type"] = "Bearer",
                ["expires_in"] = this.ExpiresIn,
            };
            if (form["grant_type"] == "authorization_code")
            {
                reply["scope"] = this.GrantedScope;
                reply["id_token"] = this.IdToken(form["client_id"], this.IdTokenNonceOverride ?? this.lastNonce);
            }

            WriteJson(c.Response, 200, reply);
        }

        private string IdToken(string audience, string nonce)
        {
            var header = new JObject { ["alg"] = "RS256", ["kid"] = "k1", ["typ"] = "JWT" };
            var payload = new JObject
            {
                ["iss"] = this.Base,
                ["aud"] = audience,
                ["sub"] = "user-sub-1",
                ["email"] = "player@example.com",
                ["nonce"] = nonce,
                ["iat"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["exp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600,
                ["https://api.openai.com/auth"] = new JObject { ["chatgpt_plan_type"] = "plus" },
            };
            string signed = B64(Encoding.UTF8.GetBytes(header.ToString(Formatting.None))) + "." + B64(Encoding.UTF8.GetBytes(payload.ToString(Formatting.None)));
            byte[] sig = this.rsa.SignData(Encoding.ASCII.GetBytes(signed), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            return signed + "." + B64(sig);
        }

        private static Dictionary<string, string> Form(string body)
        {
            var result = new Dictionary<string, string>();
            foreach (string pair in body.Split('&'))
            {
                if (pair.Length == 0) continue;
                int eq = pair.IndexOf('=');
                result[Uri.UnescapeDataString(pair.Substring(0, eq).Replace('+', ' '))] = Uri.UnescapeDataString(pair.Substring(eq + 1).Replace('+', ' '));
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
