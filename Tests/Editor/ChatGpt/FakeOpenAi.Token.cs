using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Monster.Balloon.AutomataSystem.ChatGpt.Tests
{
    /// <summary>
    /// 偽の OpenAI の token と revoke の口（code の交換・refresh・取り消し・ID トークン）。
    /// <para>
    /// ⭐ code は 1 回だけ・期限付き。client_id（発行済み）・redirect_uri・PKCE の verifier・resource を、authorize の時の値と比べる。
    ///    出した code は、合わずに断った時も使った事にする（説明書＝invalid_grant なら、その code は捨てて authorize からやり直す）。
    /// ⭐ refresh_token は使い捨て。使うたびに新しい物を返し、使った古い物は invalid_grant。scope を付けた refresh は断る。
    ///    refresh の返りには scope を付けない（前の scope のまま＝受け取る側が前の scope を使うかを見る）。
    /// ⭐ 断りの形は OAuth の決まり（400・invalid_client だけ 401・{"error","error_description"}）。
    /// </para>
    /// </summary>
    internal sealed partial class FakeOpenAi
    {
        private static readonly Regex VerifierShape = new Regex("^[A-Za-z0-9._~-]{43,128}$");

        private readonly Dictionary<string, CodeGrant> codes = new Dictionary<string, CodeGrant>();
        private readonly Dictionary<string, RefreshGrant> refreshTokens = new Dictionary<string, RefreshGrant>();
        private readonly Dictionary<string, AccessGrant> accessTokens = new Dictionary<string, AccessGrant>();
        private int tokenCount;

        /// <summary>authorize で出した code の、交換の時に比べる値。</summary>
        private sealed class CodeGrant
        {
            public string ClientId;
            public string RedirectUri;
            public string Challenge;
            public string Nonce;
            public string Resource;
            public string Scope;
            public DateTimeOffset ExpiresAt;
            public bool Used;
        }

        private sealed class RefreshGrant
        {
            public string ClientId;
            public string Scope;
            public bool Used;
            public bool Revoked;
        }

        private sealed class AccessGrant
        {
            public string ClientId;
            public string Scope;
            public DateTimeOffset ExpiresAt;
        }

        private void Token(HttpListenerContext c, string body)
        {
            Dictionary<string, string> form = Form(body, out string duplicate);
            lock (this.gate) this.TokenForms.Add(form);

            if (c.Request.HttpMethod != "POST")
            {
                this.TokenError(c, 400, "invalid_request", "token の口は POST");
                return;
            }

            if (!IsForm(c.Request.ContentType))
            {
                this.TokenError(c, 400, "invalid_request", "本文が application/x-www-form-urlencoded でない");
                return;
            }

            if (duplicate != null)
            {
                this.TokenError(c, 400, "invalid_request", duplicate + " が 2 つある");
                return;
            }

            form.TryGetValue("grant_type", out string grant);
            if (grant == "authorization_code")
            {
                this.ExchangeCode(c, form);
            }
            else if (grant == "refresh_token")
            {
                this.Refresh(c, form);
            }
            else
            {
                this.TokenError(c, 400, "unsupported_grant_type", "grant_type が authorization_code でも refresh_token でもない");
            }
        }

        private void ExchangeCode(HttpListenerContext c, Dictionary<string, string> form)
        {
            string missing = Missing(form, "client_id", "code", "code_verifier", "redirect_uri", "resource");
            if (missing != null)
            {
                this.TokenError(c, 400, "invalid_request", missing + " が無い");
                return;
            }

            if (!this.KnownClient(form["client_id"]))
            {
                this.TokenError(c, 401, "invalid_client", "知らない client_id");
                return;
            }

            CodeGrant g;
            bool used;
            lock (this.gate)
            {
                this.codes.TryGetValue(form["code"], out g);
                used = g != null && g.Used;
                if (g != null) g.Used = true;
            }

            string why =
                g == null ? "知らない code" :
                used ? "code はもう使った" :
                DateTimeOffset.UtcNow >= g.ExpiresAt ? "code の期限が切れた" :
                form["client_id"] != g.ClientId ? "code を出した client_id と違う（発行済みの client_id で交換する）" :
                form["redirect_uri"] != g.RedirectUri ? "redirect_uri が authorize の時と違う" :
                !VerifierShape.IsMatch(form["code_verifier"]) ? "code_verifier の形が違う" :
                ChatGptWire.CodeChallenge(form["code_verifier"]) != g.Challenge ? "code_verifier が code_challenge に合わない" :
                null;
            if (why != null)
            {
                this.TokenError(c, 400, "invalid_grant", why);
                return;
            }

            if (form["resource"] != g.Resource)
            {
                this.TokenError(c, 400, "invalid_target", "resource が authorize の時と違う");
                return;
            }

            JObject reply = this.IssueTokens(g.ClientId, g.Scope);
            reply["scope"] = g.Scope;
            reply["id_token"] = this.IdToken(g.ClientId, this.IdTokenNonceOverride ?? g.Nonce);
            WriteJson(c.Response, 200, reply);
        }

        private void Refresh(HttpListenerContext c, Dictionary<string, string> form)
        {
            string missing = Missing(form, "client_id", "refresh_token", "resource");
            if (missing != null)
            {
                this.TokenError(c, 400, "invalid_request", missing + " が無い");
                return;
            }

            if (form.ContainsKey("scope"))
            {
                this.TokenError(c, 400, "invalid_request", "refresh に scope を付けた（付けないで前の許しを保つ）");
                return;
            }

            if (!this.KnownClient(form["client_id"]) || form["client_id"] == DynamicClientId)
            {
                this.TokenError(c, 401, "invalid_client", "refresh の client_id が発行済みの物でない");
                return;
            }

            RefreshGrant g;
            bool used;
            bool revoked;
            lock (this.gate)
            {
                this.refreshTokens.TryGetValue(form["refresh_token"], out g);
                used = g != null && g.Used;
                revoked = g != null && g.Revoked;
                if (g != null) g.Used = true;
            }

            string why =
                g == null ? "知らない refresh_token" :
                revoked ? "取り消した refresh_token" :
                used ? "もう使った refresh_token（替わった新しい物を使う）" :
                form["client_id"] != g.ClientId ? "refresh_token を出した client_id と違う" :
                null;
            if (why != null)
            {
                this.TokenError(c, 400, "invalid_grant", why);
                return;
            }

            if (form["resource"] != Resource)
            {
                this.TokenError(c, 400, "invalid_target", "resource が違う");
                return;
            }

            WriteJson(c.Response, 200, this.IssueTokens(g.ClientId, g.Scope));
        }

        // 取り消しの口（profiles-and-sessions）。トークンが既に使えなくても、空の 200 を返す。
        private void Revoke(HttpListenerContext c, string body)
        {
            Dictionary<string, string> form = Form(body, out string duplicate);
            lock (this.gate) this.RevokeForms.Add(form);

            string why =
                c.Request.HttpMethod != "POST" ? "revoke の口は POST" :
                !IsForm(c.Request.ContentType) ? "本文が application/x-www-form-urlencoded でない" :
                duplicate != null ? duplicate + " が 2 つある" :
                Missing(form, "token", "token_type_hint", "client_id") is string missing ? missing + " が無い" :
                form["token_type_hint"] != "refresh_token" ? "token_type_hint が refresh_token でない" :
                null;
            if (why != null)
            {
                this.TokenError(c, 400, "invalid_request", why);
                return;
            }

            if (!this.KnownClient(form["client_id"]) || form["client_id"] == DynamicClientId)
            {
                this.TokenError(c, 401, "invalid_client", "revoke の client_id が発行済みの物でない");
                return;
            }

            lock (this.gate)
            {
                if (this.refreshTokens.TryGetValue(form["token"], out RefreshGrant g) && g.ClientId == form["client_id"]) g.Revoked = true;
            }

            Write(c.Response, 200, "application/json", string.Empty);
        }

        // 新しい access_token と refresh_token を出す（名前は数の順＝テストが名指しで確かめる）。
        private JObject IssueTokens(string clientId, string scope)
        {
            int n;
            lock (this.gate)
            {
                n = ++this.tokenCount;
                this.accessTokens["access-" + n] = new AccessGrant { ClientId = clientId, Scope = scope, ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(this.ExpiresIn) };
                this.refreshTokens["refresh-" + n] = new RefreshGrant { ClientId = clientId, Scope = scope };
            }

            return new JObject
            {
                ["access_token"] = "access-" + n,
                ["refresh_token"] = "refresh-" + n,
                ["token_type"] = "Bearer",
                ["expires_in"] = this.ExpiresIn,
            };
        }

        // Bearer のトークンを読む。使えなければ null（期限切れも null）。
        private AccessGrant Bearer(HttpListenerRequest r)
        {
            string header = r.Headers["Authorization"];
            if (header == null || !header.StartsWith("Bearer ", StringComparison.Ordinal)) return null;
            lock (this.gate)
            {
                if (!this.accessTokens.TryGetValue(header.Substring(7), out AccessGrant g)) return null;
                return DateTimeOffset.UtcNow < g.ExpiresAt ? g : null;
            }
        }

        private bool KnownClient(string clientId)
        {
            if (clientId == DynamicClientId) return true;
            lock (this.gate) return this.registered && clientId == IssuedClientId;
        }

        private void TokenError(HttpListenerContext c, int status, string code, string why)
        {
            this.Refuse(c.Request.Url.AbsolutePath, code, why);
            WriteJson(c.Response, status, new JObject { ["error"] = code, ["error_description"] = why });
        }

        private static bool IsForm(string contentType)
        {
            return contentType != null && contentType.Split(';')[0].Trim().Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase);
        }

        private static string Missing(Dictionary<string, string> form, params string[] names)
        {
            foreach (string name in names)
            {
                if (!form.TryGetValue(name, out string v) || string.IsNullOrEmpty(v)) return name;
            }

            return null;
        }

        // ID トークン（RS256・iss＝この偽の issuer・aud＝code を出した client_id・nonce＝authorize の時の値）。
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
    }
}
