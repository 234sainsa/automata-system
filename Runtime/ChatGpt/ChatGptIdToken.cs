using System;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Monster.Balloon.AutomataSystem.ChatGpt
{
    /// <summary>
    /// ID トークン（JWT）の確かめ。
    /// ⭐ 確かめる事＝RS256 の署名（jwks の鍵の n・e から作った RSA）・iss・aud（発行された client_id）・exp・nonce。
    /// ⭐ 取り出すのは sub とプランの種類だけ（メール・名前は読まない）。
    /// </summary>
    internal static class ChatGptIdToken
    {
        private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(2);

        /// <summary>確かめた ID トークンから取り出す物。</summary>
        internal sealed class Claims
        {
            public string Subject;
            public string PlanType;
        }

        /// <summary>JWT の頭の kid（鍵の名前）を読む。無ければ null。</summary>
        public static string KeyIdOf(string jwt)
        {
            string[] parts = Split(jwt);
            return DecodePart(parts[0]).Value<string>("kid");
        }

        /// <summary>jwks から kid の RSA の鍵を探す。無ければ null（呼ぶ側は jwks を 1 回読み直す）。</summary>
        public static JObject FindKey(JObject jwks, string kid)
        {
            if (!(jwks?["keys"] is JArray keys)) return null;
            foreach (JToken k in keys)
            {
                if (!(k is JObject key) || key.Value<string>("kty") != "RSA") continue;
                if (kid == null || key.Value<string>("kid") == kid) return key;
            }

            return null;
        }

        /// <summary>
        /// 署名と中身を確かめて、sub とプランの種類を返す。合わなければ SignInFailed で投げる。
        /// </summary>
        public static Claims Verify(string jwt, JObject key, string issuer, string clientId, string nonce, DateTimeOffset now)
        {
            if (key == null) throw Fail("ID トークンの鍵が jwks に無い。");
            string[] parts = Split(jwt);
            JObject header = DecodePart(parts[0]);
            if (header.Value<string>("alg") != "RS256") throw Fail("ID トークンの署名の形が RS256 でない。");

            byte[] signed = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
            byte[] signature = ChatGptWire.FromBase64Url(parts[2]);
            using (RSA rsa = RSA.Create())
            {
                rsa.ImportParameters(new RSAParameters
                {
                    Modulus = ChatGptWire.FromBase64Url(key.Value<string>("n") ?? string.Empty),
                    Exponent = ChatGptWire.FromBase64Url(key.Value<string>("e") ?? string.Empty),
                });
                if (!rsa.VerifyData(signed, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                {
                    throw Fail("ID トークンの署名が合わない。");
                }
            }

            JObject payload = DecodePart(parts[1]);
            if (TrimSlash(payload.Value<string>("iss")) != TrimSlash(issuer)) throw Fail("ID トークンの iss が合わない。");
            if (!AudienceHas(payload["aud"], clientId)) throw Fail("ID トークンの aud が、発行された client_id でない。");

            long exp = payload["exp"]?.Type == JTokenType.Integer ? payload.Value<long>("exp") : 0;
            if (DateTimeOffset.FromUnixTimeSeconds(exp) + ClockSkew < now) throw Fail("ID トークンの期限が切れている。");
            if (!ChatGptWire.FixedTimeEquals(payload.Value<string>("nonce"), nonce)) throw Fail("ID トークンの nonce が合わない。");

            return new Claims { Subject = payload.Value<string>("sub"), PlanType = PlanTypeOf(payload) };
        }

        // プランの種類は、OpenAI の名前空間の欄（https://api.openai.com/auth）か、上の段の chatgpt_plan_type にある。
        private static string PlanTypeOf(JObject payload)
        {
            if (payload["https://api.openai.com/auth"] is JObject auth && auth.Value<string>("chatgpt_plan_type") is string nested)
            {
                return nested;
            }

            return payload["chatgpt_plan_type"]?.Type == JTokenType.String ? payload.Value<string>("chatgpt_plan_type") : null;
        }

        private static bool AudienceHas(JToken aud, string clientId)
        {
            if (aud == null || string.IsNullOrEmpty(clientId)) return false;
            if (aud.Type == JTokenType.String) return (string)aud == clientId;
            if (aud is JArray list)
            {
                foreach (JToken a in list)
                {
                    if (a.Type == JTokenType.String && (string)a == clientId) return true;
                }
            }

            return false;
        }

        private static string[] Split(string jwt)
        {
            string[] parts = (jwt ?? string.Empty).Split('.');
            if (parts.Length != 3) throw Fail("ID トークンが JWT の形でない。");
            return parts;
        }

        private static JObject DecodePart(string part)
        {
            try
            {
                if (JToken.Parse(Encoding.UTF8.GetString(ChatGptWire.FromBase64Url(part))) is JObject obj) return obj;
            }
            catch (Exception)
            {
                // 下で投げる（中身は字に出さない）。
            }

            throw Fail("ID トークンの中身が読めない。");
        }

        private static string TrimSlash(string s)
        {
            return s?.TrimEnd('/');
        }

        private static ChatGptException Fail(string message)
        {
            return new ChatGptException(ChatGptErrorKind.SignInFailed, message, "invalid_id_token");
        }
    }
}
