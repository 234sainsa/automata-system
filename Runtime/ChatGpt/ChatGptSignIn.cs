using System;
using System.Threading;
using System.Threading.Tasks;

namespace Monster.Balloon.AutomataSystem.ChatGpt
{
    /// <summary>
    /// A sign-in in progress: the authorize URL and the task that finishes when the browser comes back.
    /// 途中のログイン 1 つ（authorize の URL と、ブラウザから戻って済むまでの Task）。
    /// </summary>
    public sealed class ChatGptSignIn
    {
        private readonly CancellationTokenSource cancel;

        internal ChatGptSignIn(Uri authorizeUrl, Task<ChatGptAccountState> completion, CancellationTokenSource cancel)
        {
            this.AuthorizeUrl = authorizeUrl;
            this.Completion = completion;
            this.cancel = cancel;
        }

        /// <summary>
        /// The page to open in the browser (show it as a link if the browser did not open).
        /// ブラウザで開く頁。ブラウザが開かなかった時は、この URL を遊ぶ人に見せる。
        /// </summary>
        public Uri AuthorizeUrl { get; }

        /// <summary>
        /// Finishes with SignedIn or PlanNotGranted, or fails with a ChatGptException.
        /// 済むと SignedIn か PlanNotGranted を返す。失敗は <see cref="ChatGptException"/>（取り消しは Cancelled）。
        /// </summary>
        public Task<ChatGptAccountState> Completion { get; }

        /// <summary>
        /// Stops waiting for the browser.
        /// ブラウザからの戻りを待つのをやめる。
        /// </summary>
        public void Cancel()
        {
            try
            {
                this.cancel.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // もう済んだログイン。
            }
        }
    }
}
