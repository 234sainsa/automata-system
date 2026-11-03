using System;
using Newtonsoft.Json.Linq;

namespace Monster.Balloon.AutomataSystem.ChatGpt
{
    /// <summary>
    /// API の失敗の返り（HTTP の本文・流れの中の error）を、訳の印の付いた例外に直す。
    /// ⭐ 印の決まりは OpenAI の説明書（errors-and-recovery）の error.code。字はサーバーの message をそのまま使う（トークンは入らない）。
    /// </summary>
    internal static class ChatGptApiErrors
    {
        internal const string UsageLimitExceeded = "subscription_sharing_usage_limit_exceeded";
        internal const string UsageUnavailable = "subscription_sharing_usage_unavailable";
        internal const string UserNotEligible = "subscription_sharing_user_not_eligible";
        internal const string UnsupportedCapability = "subscription_sharing_unsupported_capability";
        internal const string RouteNotSupported = "subscription_sharing_route_not_supported";
        internal const string InvalidUser = "subscription_sharing_invalid_user";
        internal const string UserUnavailable = "subscription_sharing_user_unavailable";
        internal const string ScopeNotAuthorized = "chatpass_v2_scope_not_authorized";
        internal const string InvalidAuthorizationContext = "chatpass_v2_invalid_authorization_context";

        /// <summary>HTTP の失敗の返り（流れの前）から例外を作る。本文は JSON（error か detail）のことが多い。</summary>
        public static ChatGptException FromHttp(int status, string body)
        {
            JObject root = TryParse(body);
            JObject error = root?["error"] as JObject;
            string code = error?.Value<string>("code") ?? error?.Value<string>("type");
            string param = error?.Value<string>("param");
            string message = error?.Value<string>("message") ?? root?.Value<string>("detail");
            return Make(code, param, message, status);
        }

        /// <summary>流れの中の失敗（response.failed の response.error・error の出来事）から例外を作る。</summary>
        public static ChatGptException FromStreamError(JObject error, string fallback)
        {
            string code = error?.Value<string>("code") ?? error?.Value<string>("type");
            string param = error?.Value<string>("param");
            string message = error?.Value<string>("message") ?? fallback;
            return Make(code, param, message, 0);
        }

        /// <summary>印と HTTP の番号から、訳の印を選ぶ。印が知らない物なら番号で選ぶ。</summary>
        public static ChatGptErrorKind KindOf(string code, int status)
        {
            switch (code)
            {
                case UsageLimitExceeded: return ChatGptErrorKind.UsageLimit;
                case UsageUnavailable: return ChatGptErrorKind.Unavailable;
                case UserNotEligible: return ChatGptErrorKind.NotEligible;
                case UnsupportedCapability: return ChatGptErrorKind.Unsupported;
                case RouteNotSupported: return ChatGptErrorKind.RouteNotSupported;
                case InvalidUser: return ChatGptErrorKind.InvalidUser;
                case UserUnavailable: return ChatGptErrorKind.UserUnavailable;
                case ScopeNotAuthorized:
                case InvalidAuthorizationContext: return ChatGptErrorKind.ScopeNotAuthorized;
            }

            if (status == 401) return ChatGptErrorKind.Unauthorized;
            if (status == 429) return ChatGptErrorKind.UsageLimit;
            if (status == 502 || status == 503 || status == 504) return ChatGptErrorKind.Unavailable;
            if (status >= 400 && status < 500) return ChatGptErrorKind.Refused;
            return ChatGptErrorKind.Unknown;
        }

        private static ChatGptException Make(string code, string param, string message, int status)
        {
            ChatGptErrorKind kind = KindOf(code, status);
            string text = string.IsNullOrEmpty(message) ? kind.ToString() : message;
            if (status > 0) text = "HTTP " + status + ": " + text;
            return new ChatGptException(kind, text, code, param, status);
        }

        private static JObject TryParse(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return null;
            try
            {
                return JToken.Parse(body) as JObject;
            }
            catch (Exception)
            {
                // 本文が JSON でない時（HTML の断りの頁など）は、番号だけで選ぶ。
                return null;
            }
        }
    }
}
