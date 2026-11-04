using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Monster.Balloon.AutomataSystem.ChatGpt
{
    /// <summary>
    /// One player's ChatGPT sign-in: sign in, sign out, fresh access tokens, and the model list.
    /// 遊ぶ人 1 人の ChatGPT のログイン（ログイン・ログアウト・使えるトークン・モデル一覧）。
    /// <para>
    /// ⭐ どのスレッドから呼んでもよい。通信は HttpClient の async で、主スレッドを止めない。
    /// ⭐ <see cref="StateChanged"/> は、様子を変えたスレッドで呼ばれる（主スレッドとは限らない）。
    /// ⭐ refresh は 1 本ずつに並べる。refresh のたびに refresh_token が替わるので、替わった物を必ず保存する。
    ///    refresh の前に保存のファイルを読み直し、同じフォルダを読む別の口が替えた物があれば、そちらを使う。
    /// ⚠ 保存の場所を設定で決めていなければ、主スレッドで作る（persistentDataPath を読むため）。
    /// </para>
    /// </summary>
    public sealed partial class ChatGptAccount : IDisposable
    {
        // 期限のこれだけ前になったら、使う前に refresh する。
        private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(2);

        private readonly object gate = new object();
        private readonly SemaphoreSlim refreshGate = new SemaphoreSlim(1, 1);
        private readonly HttpClient http;
        private readonly ChatGptStore store;
        private readonly ChatGptOAuth oauth;
        private readonly ChatGptSaved saved;
        private ChatGptAccountState state;
        private bool disposed;

        /// <summary>
        /// Creates the account and reads the saved sign-in, if any.
        /// アカウントを作り、保存があれば読む（端末の印が無ければここで作って保存する）。
        /// </summary>
        public ChatGptAccount(ChatGptSettings settings)
        {
            this.Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            string folder = settings.StorageDirectory ?? ChatGptSettings.DefaultStorageDirectory();
            this.store = new ChatGptStore(folder);
            this.saved = this.store.LoadOrCreate();
            this.http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
            this.oauth = new ChatGptOAuth(this.http, settings);
            this.state = StateFromSaved(this.saved);
        }

        /// <summary>
        /// Raised when the state changes (on any thread).
        /// 様子が変わった時（どのスレッドかは決まっていない）。
        /// </summary>
        public event Action<ChatGptAccountState> StateChanged;

        /// <summary>
        /// The settings given at creation.
        /// 作る時に渡した設定。
        /// </summary>
        public ChatGptSettings Settings { get; }

        /// <summary>
        /// The current state.
        /// 今の様子。
        /// </summary>
        public ChatGptAccountState State
        {
            get
            {
                lock (this.gate) return this.state;
            }
        }

        /// <summary>
        /// The plan type from the id token (for example "plus"), or null.
        /// ID トークンにあったプランの種類。無ければ null。
        /// </summary>
        public string PlanType
        {
            get
            {
                lock (this.gate) return this.saved.PlanType;
            }
        }

        /// <summary>
        /// The folder of the saved sign-in.
        /// ログインを保存しているフォルダ。
        /// </summary>
        public string StorageDirectory => this.store.Directory;

        /// <summary>
        /// Returns a usable access token, refreshing it first when it is about to expire.
        /// 使えるアクセストークンを返す。期限が近ければ先に refresh する。
        /// ログインが無ければ SignInRequired・プランの同意が無ければ PlanNotGranted で投げる。
        /// </summary>
        public async Task<string> GetAccessTokenAsync(CancellationToken cancel = default)
        {
            string current;
            lock (this.gate)
            {
                this.ThrowIfUnusable();
                current = this.saved.AccessToken;
                if (!this.RefreshIsDue(DateTimeOffset.UtcNow)) return current;
            }

            return await this.RefreshCoreAsync(current, cancel).ConfigureAwait(false);
        }

        /// <summary>
        /// Lists the models the player may use (visibility "list" only).
        /// 遊ぶ人が使えるモデルを並べる（visibility が "list" の物だけ）。決め打ちのモデル名は持たない。
        /// </summary>
        public Task<IReadOnlyList<ChatGptModelInfo>> ListModelsAsync(CancellationToken cancel = default)
        {
            return this.WithTokenAsync(async token =>
            {
                (int status, string body) = await ChatGptWire.GetAsync(this.http, this.ApiUrl("models"), token, this.Settings.RequestTimeout, cancel).ConfigureAwait(false);
                if (status != 200) throw ChatGptApiErrors.FromHttp(status, body);
                var list = new List<ChatGptModelInfo>();
                if (ChatGptWire.ParseObject(body, "models")["models"] is JArray models)
                {
                    foreach (JToken m in models)
                    {
                        if (!(m is JObject o) || o.Value<string>("visibility") != "list") continue;
                        string slug = o.Value<string>("slug");
                        if (!string.IsNullOrEmpty(slug)) list.Add(new ChatGptModelInfo(slug, o.Value<string>("display_name"), o));
                    }
                }

                return (IReadOnlyList<ChatGptModelInfo>)list;
            }, cancel);
        }

        /// <summary>
        /// Signs out: revokes the refresh token, then forgets the tokens (the client id and host id stay).
        /// ログアウト＝refresh_token を取り消してから、トークンを消す（client_id と端末の印は残す）。
        /// 取り消しが通らなくても、トークンは消す。返り＝サーバーが取り消しを受けたか。
        /// </summary>
        public async Task<bool> SignOutAsync(CancellationToken cancel = default)
        {
            this.CancelSignIn();
            string clientId;
            string refresh;
            lock (this.gate)
            {
                clientId = this.saved.ClientId;
                refresh = this.saved.RefreshToken;
            }

            bool revoked = false;
            if (clientId != null && refresh != null)
            {
                try
                {
                    await this.oauth.RevokeAsync(clientId, refresh, cancel).ConfigureAwait(false);
                    revoked = true;
                }
                catch (ChatGptException e) when (e.Kind != ChatGptErrorKind.Cancelled)
                {
                    // 取り消しが通らなくても、この端末のトークンは消す（遊ぶ人はログアウトしたい）。
                }
            }

            this.ForgetTokens();
            return revoked;
        }

        /// <summary>
        /// Stops any sign-in in progress and closes the HTTP client.
        /// 途中のログインを止めて、通信の道具を閉じる。
        /// </summary>
        public void Dispose()
        {
            lock (this.gate)
            {
                if (this.disposed) return;
                this.disposed = true;
            }

            this.CancelSignIn();
            this.http.Dispose();
        }

        internal HttpClient Http => this.http;

        /// <summary>API の基に、相手の道を足した URL。</summary>
        internal Uri ApiUrl(string relative)
        {
            string b = this.Settings.ApiBase.AbsoluteUri;
            return new Uri(new Uri(b.EndsWith("/") ? b : b + "/"), relative);
        }

        /// <summary>
        /// トークンを付けて 1 回呼ぶ。401 なら 1 回だけ refresh して呼び直す。それでも 401 なら、トークンを消して投げる。
        /// </summary>
        internal async Task<T> WithTokenAsync<T>(Func<string, Task<T>> call, CancellationToken cancel)
        {
            string token = await this.GetAccessTokenAsync(cancel).ConfigureAwait(false);
            try
            {
                return await call(token).ConfigureAwait(false);
            }
            catch (ChatGptException e) when (e.HttpStatus == 401)
            {
                // 下で refresh してから呼び直す。
            }

            token = await this.RefreshCoreAsync(token, cancel).ConfigureAwait(false);
            try
            {
                return await call(token).ConfigureAwait(false);
            }
            catch (ChatGptException e) when (e.HttpStatus == 401)
            {
                this.ForgetTokens();
                throw new ChatGptException(ChatGptErrorKind.Unauthorized, "refresh の後もトークンが断られた。ログインし直す。", e.Code, e.Param, 401, e);
            }
        }

        // 中身を見て、使えない時は投げる（gate の中で呼ぶ）。
        private void ThrowIfUnusable()
        {
            if (!this.saved.HasTokens) throw new ChatGptException(ChatGptErrorKind.SignInRequired, "ログインしていない。");
            if (!HasPlanScope(this.saved.Scope))
            {
                throw new ChatGptException(ChatGptErrorKind.PlanNotGranted, "ChatGPT のプランを使う同意が無い（同意を取り直す）。", ChatGptOAuth.PlanScope);
            }
        }

        // 期限が近いか（gate の中で呼ぶ）。earliest_refresh_at より前で、期限もまだなら refresh しない。
        private bool RefreshIsDue(DateTimeOffset now)
        {
            long nowUnix = now.ToUnixTimeSeconds();
            if (nowUnix >= this.saved.ExpiresAtUnix) return true;
            if (this.saved.EarliestRefreshAtUnix > nowUnix) return false;
            return nowUnix >= this.saved.ExpiresAtUnix - (long)RefreshMargin.TotalSeconds;
        }

        /// <summary>
        /// refresh を 1 本ずつに並べて行う。stale は呼ぶ側が持っていた古いトークン＝並んでいる間に他が替えていれば、それを返す。
        /// </summary>
        private async Task<string> RefreshCoreAsync(string stale, CancellationToken cancel)
        {
            await this.refreshGate.WaitAsync(cancel).ConfigureAwait(false);
            try
            {
                string clientId;
                string refresh;
                string scope;
                lock (this.gate)
                {
                    if (!this.saved.HasTokens) throw new ChatGptException(ChatGptErrorKind.SignInRequired, "ログインしていない。");
                    this.AdoptNewerTokensLocked();
                    if (this.saved.AccessToken != stale && !this.RefreshIsDue(DateTimeOffset.UtcNow)) return this.saved.AccessToken;
                    clientId = this.saved.ClientId;
                    refresh = this.saved.RefreshToken;
                    scope = this.saved.Scope;
                }

                ChatGptOAuth.TokenReply reply;
                try
                {
                    reply = await this.oauth.RefreshAsync(clientId, refresh, scope, cancel).ConfigureAwait(false);
                }
                catch (ChatGptException e) when (e.Kind == ChatGptErrorKind.SignInRequired)
                {
                    this.ForgetTokens();
                    throw;
                }

                lock (this.gate)
                {
                    this.ApplyTokens(reply);
                    this.ThrowIfUnusable();
                    return this.saved.AccessToken;
                }
            }
            finally
            {
                this.refreshGate.Release();
            }
        }

        // 同じ保存のファイルを読む別の口（同じフォルダの ChatGptAccount）が先に refresh していれば、その替わった
        // トークンを使う（gate の中で呼ぶ）。refresh_token は使い捨て＝古い物で refresh すると断られ、ログインが切れる。
        private void AdoptNewerTokensLocked()
        {
            ChatGptSaved disk;
            try
            {
                disk = this.store.TryLoad();
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return; // 読めない時は手元の物で refresh する。
            }

            if (disk == null || !disk.HasTokens || disk.ClientId != this.saved.ClientId || disk.RefreshToken == this.saved.RefreshToken) return;
            this.saved.AccessToken = disk.AccessToken;
            this.saved.RefreshToken = disk.RefreshToken;
            this.saved.Scope = disk.Scope;
            this.saved.ExpiresAtUnix = disk.ExpiresAtUnix;
            this.saved.EarliestRefreshAtUnix = disk.EarliestRefreshAtUnix;
            this.saved.Subject = disk.Subject;
            this.saved.PlanType = disk.PlanType;
            this.SetStateLocked(StateFromSaved(this.saved));
        }

        // トークンの返りを中身へ入れて保存し、様子を合わせる（gate の中で呼ぶ）。
        private void ApplyTokens(ChatGptOAuth.TokenReply reply)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            this.saved.AccessToken = reply.AccessToken;
            this.saved.RefreshToken = reply.RefreshToken;
            this.saved.Scope = reply.Scope;
            this.saved.ExpiresAtUnix = now + Math.Max(60, reply.ExpiresIn);
            this.saved.EarliestRefreshAtUnix = reply.EarliestRefreshAtUnix;
            this.store.Save(this.saved);
            this.SetStateLocked(StateFromSaved(this.saved));
        }

        /// <summary>トークンを消して保存し、ログアウトの様子にする（client_id と端末の印は残す）。</summary>
        internal void ForgetTokens()
        {
            lock (this.gate)
            {
                this.saved.ForgetTokens();
                this.store.Save(this.saved);
                if (this.state != ChatGptAccountState.SigningIn) this.SetStateLocked(ChatGptAccountState.SignedOut);
            }
        }

        // 様子を変える（gate の中で呼ぶ）。知らせは gate の外へ出してから呼ぶ。
        private void SetStateLocked(ChatGptAccountState next)
        {
            if (this.state == next) return;
            this.state = next;
            Action<ChatGptAccountState> handler = this.StateChanged;
            if (handler != null) ThreadPool.QueueUserWorkItem(_ => handler(next));
        }

        private static ChatGptAccountState StateFromSaved(ChatGptSaved s)
        {
            if (!s.HasTokens) return ChatGptAccountState.SignedOut;
            return HasPlanScope(s.Scope) ? ChatGptAccountState.SignedIn : ChatGptAccountState.PlanNotGranted;
        }

        internal static bool HasPlanScope(string scope)
        {
            if (string.IsNullOrEmpty(scope)) return false;
            foreach (string s in scope.Split(' '))
            {
                if (s == ChatGptOAuth.PlanScope) return true;
            }

            return false;
        }
    }
}
