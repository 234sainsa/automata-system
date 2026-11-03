using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Monster.Balloon.AutomataSystem.ChatGpt
{
    /// <summary>
    /// OpenAI のログインの口（discovery・authorize の URL・token の交換・refresh・revoke・jwks）。
    /// ⭐ client secret は無い（PKCE）。client_id は、初回だけ共通の dynamic_agent_client・以後は発行された物。
    /// ⚠ トークンを字（例外・ログ）に出さない。
    /// </summary>
    internal sealed class ChatGptOAuth
    {
        internal const string DynamicClientId = "dynamic_agent_client";
        internal const string Scope = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct";
        internal const string PlanScope = "chatgpt.tokens.use.direct";
        internal const string Resource = "https://api.openai.com/v1";

        private static readonly HashSet<string> SignInAgainCodes = new HashSet<string>(StringComparer.Ordinal)
        {
            "invalid_grant", "invalid_refresh_token", "token_expired", "refresh_token_expired",
            "refresh_token_invalidated", "refresh_token_reused",
        };

        private readonly HttpClient http;
        private readonly ChatGptSettings settings;
        private Discovery discovery;
        private JObject jwks;

        public ChatGptOAuth(HttpClient http, ChatGptSettings settings)
        {
            this.http = http;
            this.settings = settings;
        }

        /// <summary>discovery で読んだ口の一式。</summary>
        internal sealed class Discovery
        {
            public string Issuer;
            public Uri Authorize;
            public Uri Token;
            public Uri Revocation;
            public Uri Jwks;
        }

        /// <summary>token の口の返り。</summary>
        internal sealed class TokenReply
        {
            public string AccessToken;
            public string RefreshToken;
            public string IdToken;
            public string Scope;
            public long ExpiresIn;
            public long EarliestRefreshAtUnix;
        }

        /// <summary>authorize に付ける、毎回の値。</summary>
        internal sealed class AuthorizeRequest
        {
            public string ClientId;
            public Uri RedirectUri;
            public string State;
            public string Nonce;
            public string CodeChallenge;
            public string HostId;
            public string AgentNameHint;
            public bool ForceConsent;
        }

        /// <summary>discovery を読む（1 回読んだら覚える）。issuer が設定と違えば断る。</summary>
        public async Task<Discovery> GetDiscoveryAsync(CancellationToken cancel)
        {
            if (this.discovery != null) return this.discovery;
            string issuer = this.settings.Issuer.AbsoluteUri.TrimEnd('/');
            var url = new Uri(issuer + "/.well-known/openid-configuration");
            (int status, string body) = await ChatGptWire.GetAsync(this.http, url, null, this.settings.RequestTimeout, cancel).ConfigureAwait(false);
            if (status != 200) throw ChatGptApiErrors.FromHttp(status, body);

            JObject doc = ChatGptWire.ParseObject(body, "discovery");
            string said = doc.Value<string>("issuer")?.TrimEnd('/');
            if (said != issuer) throw new ChatGptException(ChatGptErrorKind.BadResponse, "discovery の issuer が設定と違う。");

            this.discovery = new Discovery
            {
                Issuer = said,
                Authorize = UriOr(doc, "authorization_endpoint", issuer + "/api/accounts/authorize"),
                Token = UriOr(doc, "token_endpoint", issuer + "/api/accounts/oauth/token"),
                Revocation = UriOr(doc, "revocation_endpoint", null),
                Jwks = UriOr(doc, "jwks_uri", null),
            };
            return this.discovery;
        }

        /// <summary>jwks を読む。reload なら読み直す（kid が見つからない時に 1 回）。</summary>
        public async Task<JObject> GetJwksAsync(bool reload, CancellationToken cancel)
        {
            if (this.jwks != null && !reload) return this.jwks;
            Discovery d = await this.GetDiscoveryAsync(cancel).ConfigureAwait(false);
            if (d.Jwks == null) throw new ChatGptException(ChatGptErrorKind.BadResponse, "discovery に jwks_uri が無い。");
            (int status, string body) = await ChatGptWire.GetAsync(this.http, d.Jwks, null, this.settings.RequestTimeout, cancel).ConfigureAwait(false);
            if (status != 200) throw ChatGptApiErrors.FromHttp(status, body);
            this.jwks = ChatGptWire.ParseObject(body, "jwks");
            return this.jwks;
        }

        /// <summary>authorize の URL を作る。agent_name_hint は初回の登録の時だけ・prompt=consent は同意を取り直す時だけ。</summary>
        public static Uri BuildAuthorizeUrl(Discovery d, AuthorizeRequest r)
        {
            var pairs = new List<KeyValuePair<string, string>>
            {
                Pair("client_id", r.ClientId),
                Pair("response_type", "code"),
                Pair("redirect_uri", r.RedirectUri.AbsoluteUri),
                Pair("scope", Scope),
                Pair("resource", Resource),
                Pair("state", r.State),
                Pair("nonce", r.Nonce),
                Pair("code_challenge_method", "S256"),
                Pair("code_challenge", r.CodeChallenge),
                Pair("ext_agent_host_id", r.HostId),
                Pair("agent_name_hint", r.AgentNameHint),
                Pair("prompt", r.ForceConsent ? "consent" : null),
            };
            var b = new UriBuilder(d.Authorize) { Query = ChatGptWire.BuildQuery(pairs) };
            return b.Uri;
        }

        /// <summary>code をトークンに替える。</summary>
        public async Task<TokenReply> ExchangeCodeAsync(string clientId, string code, string verifier, Uri redirectUri, CancellationToken cancel)
        {
            var form = new[]
            {
                Pair("grant_type", "authorization_code"),
                Pair("client_id", clientId),
                Pair("code", code),
                Pair("code_verifier", verifier),
                Pair("redirect_uri", redirectUri.AbsoluteUri),
                Pair("resource", Resource),
            };
            return await this.TokenCallAsync(form, null, cancel).ConfigureAwait(false);
        }

        /// <summary>refresh する（scope は付けない）。返りに scope が無ければ前の scope を使う。</summary>
        public async Task<TokenReply> RefreshAsync(string clientId, string refreshToken, string previousScope, CancellationToken cancel)
        {
            var form = new[]
            {
                Pair("grant_type", "refresh_token"),
                Pair("client_id", clientId),
                Pair("refresh_token", refreshToken),
                Pair("resource", Resource),
            };
            return await this.TokenCallAsync(form, previousScope, cancel).ConfigureAwait(false);
        }

        /// <summary>refresh_token を取り消す。revocation の口が無ければ何もしない。</summary>
        public async Task RevokeAsync(string clientId, string refreshToken, CancellationToken cancel)
        {
            Discovery d = await this.GetDiscoveryAsync(cancel).ConfigureAwait(false);
            if (d.Revocation == null) return;
            var form = new[]
            {
                Pair("token", refreshToken),
                Pair("token_type_hint", "refresh_token"),
                Pair("client_id", clientId),
            };
            (int status, string body) = await ChatGptWire.PostFormAsync(this.http, d.Revocation, form, this.settings.RequestTimeout, cancel).ConfigureAwait(false);
            if (status != 200) throw MapTokenError(status, body);
        }

        private async Task<TokenReply> TokenCallAsync(KeyValuePair<string, string>[] form, string previousScope, CancellationToken cancel)
        {
            Discovery d = await this.GetDiscoveryAsync(cancel).ConfigureAwait(false);
            (int status, string body) = await ChatGptWire.PostFormAsync(this.http, d.Token, form, this.settings.RequestTimeout, cancel).ConfigureAwait(false);
            if (status != 200) throw MapTokenError(status, body);

            JObject o = ChatGptWire.ParseObject(body, "token");
            var reply = new TokenReply
            {
                AccessToken = o.Value<string>("access_token"),
                RefreshToken = o.Value<string>("refresh_token"),
                IdToken = o.Value<string>("id_token"),
                Scope = o.Value<string>("scope") ?? previousScope,
                ExpiresIn = o["expires_in"]?.Type == JTokenType.Integer ? o.Value<long>("expires_in") : 3600,
                EarliestRefreshAtUnix = UnixOf(o["earliest_refresh_at"]),
            };
            if (string.IsNullOrEmpty(reply.AccessToken) || string.IsNullOrEmpty(reply.RefreshToken))
            {
                throw new ChatGptException(ChatGptErrorKind.BadResponse, "token の返りに access_token か refresh_token が無い。");
            }

            return reply;
        }

        // token の口の失敗。OAuth の形（"error":"invalid_grant"）と API の形（"error":{"code":...}）の両方を読む。
        private static ChatGptException MapTokenError(int status, string body)
        {
            string code = null;
            try
            {
                JToken e = JToken.Parse(body)["error"];
                code = e?.Type == JTokenType.String ? (string)e : e?["code"]?.ToString();
            }
            catch (Exception)
            {
                // JSON でない返り。番号で選ぶ。
            }

            if (code != null && SignInAgainCodes.Contains(code))
            {
                return new ChatGptException(ChatGptErrorKind.SignInRequired, "ログインが切れた（" + code + "）。ログインし直す。", code, httpStatus: status);
            }

            if (code == "invalid_client")
            {
                return new ChatGptException(ChatGptErrorKind.InvalidClient, "client_id が断られた（設定の誤り）。", code, httpStatus: status);
            }

            return ChatGptApiErrors.FromHttp(status, body);
        }

        private static long UnixOf(JToken t)
        {
            if (t == null) return 0;
            if (t.Type == JTokenType.Integer) return (long)t;
            if (t.Type == JTokenType.Float) return (long)(double)t;
            if (t.Type == JTokenType.Date) return new DateTimeOffset((DateTime)t).ToUnixTimeSeconds();
            if (t.Type == JTokenType.String && DateTimeOffset.TryParse((string)t, out DateTimeOffset at)) return at.ToUnixTimeSeconds();
            return 0;
        }

        private static Uri UriOr(JObject doc, string name, string fallback)
        {
            string s = doc.Value<string>(name) ?? fallback;
            return s == null ? null : new Uri(s);
        }

        private static KeyValuePair<string, string> Pair(string k, string v)
        {
            return new KeyValuePair<string, string>(k, v);
        }
    }
}
