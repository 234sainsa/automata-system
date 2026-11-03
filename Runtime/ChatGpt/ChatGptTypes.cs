using Newtonsoft.Json.Linq;

namespace Monster.Balloon.AutomataSystem.ChatGpt
{
    /// <summary>
    /// The sign-in state of a ChatGPT account.
    /// ChatGPT のアカウントの様子。
    /// </summary>
    public enum ChatGptAccountState
    {
        /// <summary>No usable sign-in. ログインしていない（ボタン「Continue with ChatGPT」を出す）。</summary>
        SignedOut = 0,

        /// <summary>Waiting for the player in the browser. ブラウザでのログインを待っている。</summary>
        SigningIn,

        /// <summary>Signed in with plan usage. ログイン済みでプランを使える（「Using ChatGPT plan」を出す）。</summary>
        SignedIn,

        /// <summary>Signed in, but plan usage was not granted. ログインは済んだが、プランを使う同意が無い（同意を取り直す）。</summary>
        PlanNotGranted,
    }

    /// <summary>
    /// One model the signed-in player may use.
    /// 遊ぶ人が使えるモデル 1 つ（一覧の visibility が "list" の物だけ）。
    /// </summary>
    public sealed class ChatGptModelInfo
    {
        internal ChatGptModelInfo(string slug, string displayName, JObject raw)
        {
            this.Slug = slug;
            this.DisplayName = string.IsNullOrEmpty(displayName) ? slug : displayName;
            this.Raw = raw;
        }

        /// <summary>
        /// The id to send as model.
        /// 頼みの model に入れる名前。
        /// </summary>
        public string Slug { get; }

        /// <summary>
        /// The name to show to the player.
        /// 遊ぶ人に見せる名前。
        /// </summary>
        public string DisplayName { get; }

        /// <summary>
        /// The whole entry from the server (read only).
        /// サーバーの返りの 1 項目まるごと（⚠ 書き換えない）。
        /// </summary>
        public JObject Raw { get; }
    }
}
