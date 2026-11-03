using System;
using System.Collections.Generic;

namespace Monster.Balloon.AutomataSystem
{
    /// <summary>門に渡す頼みの形。窓口のスレッドが、HTTP の頼みから作る。</summary>
    internal readonly struct AgentGateRequest
    {
        public AgentGateRequest(
            string method,
            string path,
            string hostHeader,
            bool hasOrigin,
            string contentType,
            long contentLength,
            string authorization,
            IReadOnlyDictionary<string, string> headers = null)
        {
            this.Method = method;
            this.Path = path;
            this.HostHeader = hostHeader;
            this.HasOrigin = hasOrigin;
            this.ContentType = contentType;
            this.ContentLength = contentLength;
            this.Authorization = authorization;
            this.Headers = new AgentRequestHeaders(headers);
        }

        public string Method { get; }

        /// <summary>道（"?" より前）。例 "/v1/hello"。</summary>
        public string Path { get; }

        /// <summary>Host の頭の値。例 "127.0.0.1:47810"。</summary>
        public string HostHeader { get; }

        /// <summary>Origin の頭が付いているか（中身が空でも、付いていれば true）。</summary>
        public bool HasOrigin { get; }

        public string ContentType { get; }

        /// <summary>本文の長さ。分からない時は -1。</summary>
        public long ContentLength { get; }

        /// <summary>Authorization の頭の値。例 "Bearer 0f3a…"。</summary>
        public string Authorization { get; }

        /// <summary>頼みの頭の全部（門の後でゲームの口 <see cref="IAgentRequestGuard"/> が読む）。無ければ空。</summary>
        public AgentRequestHeaders Headers { get; }
    }

    /// <summary>道の種類。門は、これで顔（表か裏か）を確かめる。</summary>
    internal enum AgentRouteKind
    {
        /// <summary>そんな道は無い。</summary>
        Missing = 0,

        /// <summary>表の道。</summary>
        Surface = 1,

        /// <summary>裏の道。</summary>
        Back = 2,

        /// <summary>世界ができていないので、まだ道が並んでいない（世界ができれば在るかもしれない）。</summary>
        WorldPending = 3,

        /// <summary>道は在るが、この窓口で開いた力（hello の abilities）では使えない。</summary>
        NotOpen = 4,
    }

    /// <summary>門の鍵。Host が Play ごとに作り直す。⚠ 秘密なので、ログに書かない。</summary>
    internal sealed class AgentGateKeys
    {
        public AgentGateKeys(string expectedHost, string surfaceToken, string backToken)
        {
            this.ExpectedHost = expectedHost ?? throw new ArgumentNullException(nameof(expectedHost));
            this.SurfaceToken = surfaceToken ?? throw new ArgumentNullException(nameof(surfaceToken));
            this.BackToken = backToken;
        }

        /// <summary>通す Host の頭の値（"127.0.0.1:{port}"）。</summary>
        public string ExpectedHost { get; }

        public string SurfaceToken { get; }

        /// <summary>裏のトークン。null＝この版には裏が無い（製品版）。</summary>
        public string BackToken { get; }

        // ⚠ トークンを字にしない（うっかり $"{keys}" でログへ出ても漏れないように）。
        public override string ToString() => "AgentGateKeys(host=" + this.ExpectedHost + ", tokens=hidden)";
    }

    /// <summary>門の答え。<see cref="Status"/> が 0 なら通す。</summary>
    internal readonly struct AgentGateVerdict
    {
        private AgentGateVerdict(int status, string code, string hint, AgentFace face)
        {
            this.Status = status;
            this.Code = code;
            this.Hint = hint;
            this.Face = face;
        }

        /// <summary>断る時の HTTP の番号。通す時は 0。</summary>
        public int Status { get; }

        /// <summary>断った理由の短い字（例 "blind"）。</summary>
        public string Code { get; }

        /// <summary>使う側への手がかり（例 "use_surface"）。</summary>
        public string Hint { get; }

        /// <summary>通した時の顔（どちらのトークンだったか）。</summary>
        public AgentFace Face { get; }

        public bool Passed => this.Status == 0;

        public static AgentGateVerdict Pass(AgentFace face) => new AgentGateVerdict(0, null, null, face);

        public static AgentGateVerdict Refuse(int status, string code, string hint) =>
            new AgentGateVerdict(status, code, hint, AgentFace.Surface);
    }

    /// <summary>
    /// 窓口の門。頼みを 1 本ずつ見て、通すか断るかを決める（純粋な関数＝Unity の物に触らない・テストで全部回せる）。
    /// <para>
    /// ⭐ 門が壊れると、ブラウザで開いたページから操作され、操作キャラが倒れて世界に削除の印が付くおそれがある。
    /// だから、門はブラウザから来た頼み（Origin の頭・違う Host）を、トークンを見る前に断る。
    /// </para>
    /// </summary>
    internal static class AgentGate
    {
        /// <summary>本文の上限（64KB）。</summary>
        public const int MaxBodyBytes = 64 * 1024;

        /// <summary>道の頭。道は全部これで始まる。</summary>
        public const string RoutePrefix = "/v1/";

        /// <summary>裏の道の頭。この頭の道は、裏のトークンでしか通らない（道が並んでいなくても）。</summary>
        public const string BackPrefix = "/v1/back/";

        public static AgentGateVerdict Check(
            in AgentGateRequest request,
            AgentGateKeys keys,
            AgentRouteKind route,
            AgentRateLimiter limiter,
            long nowMs)
        {
            if (keys == null)
            {
                // Host が口を開けた直後で、鍵をまだ渡していない一瞬。使う側はやり直せばよい。
                return AgentGateVerdict.Refuse(503, "starting", "retry");
            }

            // ① ブラウザのページから来た頼みを断る。ブラウザは、ほかのページからの POST に必ず Origin の頭を付ける。
            if (request.HasOrigin)
            {
                return AgentGateVerdict.Refuse(403, "origin", "no_browser");
            }

            // ② 名前の付け替え（DNS rebinding）を断る。Host の頭が 127.0.0.1:{port} と同じ時だけ通す。
            if (!string.Equals(request.HostHeader, keys.ExpectedHost, StringComparison.OrdinalIgnoreCase))
            {
                return AgentGateVerdict.Refuse(403, "host", "use_127.0.0.1");
            }

            // ③ POST だけを受ける。
            if (!string.Equals(request.Method, "POST", StringComparison.Ordinal))
            {
                return AgentGateVerdict.Refuse(405, "method", "post_only");
            }

            // ④ 1 秒に決まった本数まで（外の台本の暴走で、主スレッドが詰まらないように）。
            if (limiter != null && !limiter.TryTake(nowMs))
            {
                return AgentGateVerdict.Refuse(429, "rate", "slow_down");
            }

            // ⑤ JSON だけを受ける。ブラウザのフォームは JSON を送れない（送るには事前の問い合わせ＝CORS が要り、窓口は答えない）。
            if (!IsJson(request.ContentType))
            {
                return AgentGateVerdict.Refuse(415, "content_type", "application/json");
            }

            // ⑥ 本文は 64KB まで（長さの頭が無い時は、窓口が読みながら数える）。
            if (request.ContentLength > MaxBodyBytes)
            {
                return AgentGateVerdict.Refuse(413, "too_large", "max_65536_bytes");
            }

            // ⑦ トークン。裏のトークンは表の道も通る。表のトークンは裏の道を通らない。
            if (!TryReadBearer(request.Authorization, out string token))
            {
                return AgentGateVerdict.Refuse(401, "unauthorized", "bearer_token");
            }

            bool isBack = keys.BackToken != null && AgentTokens.FixedTimeEquals(token, keys.BackToken);
            bool isSurface = AgentTokens.FixedTimeEquals(token, keys.SurfaceToken);
            if (!isBack && !isSurface)
            {
                return AgentGateVerdict.Refuse(401, "unauthorized", "bearer_token");
            }

            AgentFace face = isBack ? AgentFace.Back : AgentFace.Surface;
            string path = request.Path ?? string.Empty;

            // ⑧ 目隠し。表のトークンでは、裏の頭の道を名前だけで断る（道が並んでいるかどうかを、表に教えない）。
            if (face != AgentFace.Back && path.StartsWith(BackPrefix, StringComparison.Ordinal))
            {
                return AgentGateVerdict.Refuse(403, "blind", "use_surface");
            }

            // ⑨ 道。
            if (route == AgentRouteKind.WorldPending)
            {
                return AgentGateVerdict.Refuse(503, "world_not_ready", "wait_world_ready");
            }

            if (route == AgentRouteKind.Missing)
            {
                return AgentGateVerdict.Refuse(404, "no_route", "see_hello");
            }

            if (route == AgentRouteKind.Back && face != AgentFace.Back)
            {
                return AgentGateVerdict.Refuse(403, "blind", "use_surface");
            }

            // ⑩ 開いていない力の道（例 look を開いていない窓口で /v1/look）。⭐ 裏の頭の道は、⑧ の目隠しが先に断る。
            if (route == AgentRouteKind.NotOpen)
            {
                return AgentGateVerdict.Refuse(403, "not_open", "see_hello_abilities");
            }

            return AgentGateVerdict.Pass(face);
        }

        private static bool IsJson(string contentType)
        {
            if (string.IsNullOrEmpty(contentType))
            {
                return false;
            }

            return contentType.TrimStart().StartsWith("application/json", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryReadBearer(string authorization, out string token)
        {
            token = null;
            if (string.IsNullOrEmpty(authorization))
            {
                return false;
            }

            const string Scheme = "Bearer ";
            string text = authorization.Trim();
            if (text.Length <= Scheme.Length || !text.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            token = text.Substring(Scheme.Length).Trim();
            return token.Length > 0;
        }
    }
}
