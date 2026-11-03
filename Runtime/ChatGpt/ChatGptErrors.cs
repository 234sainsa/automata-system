using System;

namespace Monster.Balloon.AutomataSystem.ChatGpt
{
    /// <summary>
    /// Why a ChatGPT call failed.
    /// ChatGPT の呼び出しが失敗した訳の印。UI はこの印で出し方を選ぶ。
    /// </summary>
    public enum ChatGptErrorKind
    {
        /// <summary>Unknown failure. 訳が分からない失敗。</summary>
        Unknown = 0,

        /// <summary>The call was cancelled. 呼ぶ側が取り消した。</summary>
        Cancelled,

        /// <summary>No connection or a timeout. つながらない・待ち時間を越えた。</summary>
        Network,

        /// <summary>The stream ended before response.completed. 答えの流れが終わりの印の前に切れた。</summary>
        Interrupted,

        /// <summary>The response ended as incomplete. 答えが「途中まで」で終わった（response.incomplete）。</summary>
        Incomplete,

        /// <summary>The server sent something that cannot be read. 読めない返りが来た。</summary>
        BadResponse,

        /// <summary>No saved sign-in, or it was lost; sign in again. ログインが無い・切れた＝ログインし直す。</summary>
        SignInRequired,

        /// <summary>The sign-in did not finish (denied, state mismatch, bad id token). ログインが済まなかった（断り・state の食い違い・ID トークンの不正）。</summary>
        SignInFailed,

        /// <summary>Signed in, but the plan usage scope was not granted. ログインは済んだが、プランを使う同意が無い（prompt=consent で取り直す）。</summary>
        PlanNotGranted,

        /// <summary>The access token was refused even after a refresh. 401＝refresh の後も断られた。</summary>
        Unauthorized,

        /// <summary>The plan usage limit was reached; show Manage usage. プランの使える量を使い切った＝「Manage usage」を出す。</summary>
        UsageLimit,

        /// <summary>Plan usage is unavailable for now; try again later. 今は使えない＝間をあけてやり直す。</summary>
        Unavailable,

        /// <summary>The user cannot use plan usage; do not loop sign-in. この人はプランを使えない＝ログインを繰り返さない。</summary>
        NotEligible,

        /// <summary>A request feature is not supported (see Param). 頼みの中の機能が使えない（どれかは Param）。</summary>
        Unsupported,

        /// <summary>The route is not supported with plan usage. この口はプランでは使えない。</summary>
        RouteNotSupported,

        /// <summary>The token or its context is not allowed. トークンの scope や文脈が許されていない。</summary>
        ScopeNotAuthorized,

        /// <summary>The user is invalid; sign in again. 利用者が不正＝ログインし直す。</summary>
        InvalidUser,

        /// <summary>The user is unavailable for now. 利用者が今は使えない。</summary>
        UserUnavailable,

        /// <summary>The client id was refused; a setup problem. client_id が断られた＝設定の誤り。</summary>
        InvalidClient,

        /// <summary>One turn hit the request limit. 1 つの番で頼む回数の上限に当たった。</summary>
        RequestLimit,

        /// <summary>The request was refused (other 4xx). そのほかの断り（4xx）。</summary>
        Refused,
    }

    /// <summary>
    /// A failed ChatGPT call with a reason kind.
    /// 訳の印の付いた ChatGPT の失敗。⚠ 字にトークンを入れない（ログに出てもよい物だけ）。
    /// </summary>
    public sealed class ChatGptException : Exception
    {
        /// <summary>
        /// Creates a failure.
        /// 失敗を作る。
        /// </summary>
        public ChatGptException(ChatGptErrorKind kind, string message, string code = null, string param = null, int httpStatus = 0, Exception inner = null)
            : base(message, inner)
        {
            this.Kind = kind;
            this.Code = code;
            this.Param = param;
            this.HttpStatus = httpStatus;
        }

        /// <summary>
        /// The reason kind.
        /// 訳の印。
        /// </summary>
        public ChatGptErrorKind Kind { get; }

        /// <summary>
        /// The server error code (for example subscription_sharing_usage_limit_exceeded), or null.
        /// サーバーの印（error.code など）。無ければ null。
        /// </summary>
        public string Code { get; }

        /// <summary>
        /// The server error param (which part of the request), or null.
        /// サーバーが名指しした頼みの欄（error.param）。無ければ null。
        /// </summary>
        public string Param { get; }

        /// <summary>
        /// The HTTP status, or 0 when the failure came inside the stream or before any reply.
        /// HTTP の番号。流れの途中の失敗・返りが無い失敗では 0。
        /// </summary>
        public int HttpStatus { get; }

        /// <summary>
        /// True when trying again later may work.
        /// 間をあけてやり直せば通るかもしれない失敗か。
        /// </summary>
        public bool IsRetryable =>
            this.Kind == ChatGptErrorKind.Unavailable || this.Kind == ChatGptErrorKind.UserUnavailable ||
            this.Kind == ChatGptErrorKind.Network || this.Kind == ChatGptErrorKind.Interrupted;

        /// <summary>
        /// True when the player should sign in again.
        /// ログインし直す失敗か。
        /// </summary>
        public bool NeedsSignIn =>
            this.Kind == ChatGptErrorKind.SignInRequired || this.Kind == ChatGptErrorKind.Unauthorized ||
            this.Kind == ChatGptErrorKind.InvalidUser;

        /// <summary>
        /// The page to show as the main action, or null (Manage usage for usage limits).
        /// UI の主な行動にする画面。使える量の上限なら「Manage usage」の URL・ほかは null。
        /// </summary>
        public string ActionUrl => this.Kind == ChatGptErrorKind.UsageLimit ? ChatGptSettings.ManageUsageUrl : null;
    }
}
