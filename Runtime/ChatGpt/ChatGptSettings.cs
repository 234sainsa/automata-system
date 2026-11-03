using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Monster.Balloon.AutomataSystem.ChatGpt
{
    /// <summary>
    /// Settings for Sign in with ChatGPT.
    /// 「Sign in with ChatGPT」の設定。アカウント（<see cref="ChatGptAccount"/>）を作る時に 1 回渡す。
    /// <para>
    /// ⭐ 呼ぶ側が変えてよいのは、アプリの名前・保存の場所・待ち受けの port・URL の基（テストで偽のサーバーへ向けるため）・
    ///    ブラウザを開く手・待ち時間。OpenAI の決まった値（scope・resource・redirect の形）は、ここでは変えられない。
    /// </para>
    /// </summary>
    public sealed class ChatGptSettings
    {
        /// <summary>
        /// The issuer of the official OpenAI sign-in.
        /// OpenAI のログインの発行元（本物）。
        /// </summary>
        public static readonly Uri OfficialIssuer = new Uri("https://auth.openai.com");

        /// <summary>
        /// The base of the official OpenAI API.
        /// OpenAI の API の基（本物）。
        /// </summary>
        public static readonly Uri OfficialApiBase = new Uri("https://api.openai.com/v1/");

        /// <summary>
        /// The page where a player sees and manages their ChatGPT plan usage.
        /// 遊ぶ人が「使える量」を見る画面（UI の「Manage usage」の行き先）。
        /// </summary>
        public const string ManageUsageUrl = "https://chatgpt.com/settings/usage";

        /// <summary>
        /// Creates settings with the name shown to the player on the consent page.
        /// 設定を作る。名前は、初回の登録で OpenAI の同意の画面に出る（agent_name_hint）。
        /// </summary>
        public ChatGptSettings(string appName)
        {
            this.AppName = string.IsNullOrWhiteSpace(appName) ? throw new ArgumentException("アプリの名前が空。", nameof(appName)) : appName.Trim();
        }

        /// <summary>
        /// The app name sent once at the first registration.
        /// アプリの名前（初回の登録の時だけ送る）。
        /// </summary>
        public string AppName { get; }

        /// <summary>
        /// The folder for the saved sign-in. Null means persistentDataPath/AutomataSystem/ChatGpt.
        /// ログインを保存するフォルダ。null なら <c>Application.persistentDataPath/AutomataSystem/ChatGpt/</c>。
        /// ⚠ null のままなら、アカウントは主スレッドで作る（Unity の persistentDataPath は主スレッドでしか読めない）。
        /// </summary>
        public string StorageDirectory { get; set; }

        /// <summary>
        /// The preferred loopback port for the sign-in callback (another free port is used when busy).
        /// ログインの戻りを待つ port の第 1 候補。塞がっていれば空いた port を使う。
        /// </summary>
        public int PreferredPort { get; set; } = 1455;

        /// <summary>
        /// The sign-in issuer. Change it only to point tests at a fake server.
        /// ログインの発行元。テストで偽のサーバーへ向ける時だけ変える。
        /// </summary>
        public Uri Issuer { get; set; } = OfficialIssuer;

        /// <summary>
        /// The API base (models and responses). Change it only to point tests at a fake server.
        /// API の基（モデル一覧と推論）。テストで偽のサーバーへ向ける時だけ変える。
        /// </summary>
        public Uri ApiBase { get; set; } = OfficialApiBase;

        /// <summary>
        /// Opens the sign-in page. Default: Application.OpenURL on the main thread. Null: the caller opens the URL itself.
        /// ログインの画面を開く手。既定は主スレッドで <c>Application.OpenURL</c>。null なら開かない（呼ぶ側が URL を自分で開く）。
        /// </summary>
        public Func<Uri, CancellationToken, Task> OpenBrowser { get; set; } = OpenWithUnityAsync;

        /// <summary>
        /// Time limit for one non-streaming HTTP call, and for the first bytes of a streaming call.
        /// HTTP の 1 回の待ち時間（流れの頼みでは、返りの頭が来るまで）。
        /// </summary>
        public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Time limit with no new bytes while a response streams.
        /// 流れの途中で、新しい字が来ないまま待つ上限。越えたら「途中で切れた」。
        /// </summary>
        public TimeSpan StreamIdleTimeout { get; set; } = TimeSpan.FromSeconds(180);

        /// <summary>
        /// Time limit for the player to finish signing in in the browser.
        /// 遊ぶ人がブラウザでログインを済ませるまで待つ上限。
        /// </summary>
        public TimeSpan SignInTimeout { get; set; } = TimeSpan.FromMinutes(10);

        /// <summary>
        /// How many requests one turn may send (tool rounds included).
        /// 1 つの番で頼む回数の上限（道具を回すたびに 1 回）。
        /// </summary>
        public int MaxRequestsPerTurn { get; set; } = 24;

        /// <summary>
        /// The default storage folder (main thread only).
        /// 既定の保存の場所を返す（⚠ 主スレッドでだけ呼べる）。
        /// </summary>
        public static string DefaultStorageDirectory()
        {
            return Path.Combine(Application.persistentDataPath, "AutomataSystem", "ChatGpt");
        }

        // 既定のブラウザを開く手。どのスレッドから呼ばれても、主スレッドへ移ってから開く。
        private static async Task OpenWithUnityAsync(Uri url, CancellationToken cancel)
        {
            await Awaitable.MainThreadAsync();
            cancel.ThrowIfCancellationRequested();
            Application.OpenURL(url.AbsoluteUri);
        }
    }
}
