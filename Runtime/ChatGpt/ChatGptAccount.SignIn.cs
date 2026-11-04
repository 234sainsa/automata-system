using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Monster.Balloon.AutomataSystem.ChatGpt
{
    /// <summary>
    /// ログインの流れ（authorize → ブラウザ → 待ち受け → token の交換 → ID トークンの確かめ → 保存）。
    /// <para>
    /// ⭐ 初回は共通の dynamic_agent_client で authorize し、戻りに付いた発行済みの client_id（oaiapp_...）を保存する。
    ///    dynamic_agent_client は保存しない。初回の戻りに client_id が無ければ失敗にする。
    /// ⭐ 2 回目以降は発行済みの client_id を使い、agent_name_hint を付けない。id_token_hint も付けない（トークンを URL に載せない）。
    /// </para>
    /// </summary>
    public sealed partial class ChatGptAccount
    {
        private CancellationTokenSource signInCancel;

        /// <summary>
        /// Starts a sign-in: opens the loopback listener, builds the authorize URL, and opens the browser.
        /// ログインを始める。待ち受けを開き、authorize の URL を作り、ブラウザを開く手を呼んでから返す。
        /// 済むのは返した <see cref="ChatGptSignIn.Completion"/> で待つ。前のログインが途中なら、それは取り消す。
        /// </summary>
        /// <param name="requestPlanConsent">true なら prompt=consent を付けて、プランの同意を取り直す。</param>
        /// <param name="cancel">始める所までの取り消し（済むまでの取り消しは <see cref="ChatGptSignIn.Cancel"/>）。</param>
        public async Task<ChatGptSignIn> BeginSignInAsync(bool requestPlanConsent = false, CancellationToken cancel = default)
        {
            this.CancelSignIn();
            ChatGptOAuth.Discovery discovery = await this.oauth.GetDiscoveryAsync(cancel).ConfigureAwait(false);

            // 待ち受けはブラウザを開く前に始める（戻りを取りこぼさない）。
            ChatGptLoopback loopback = ChatGptLoopback.Start(this.Settings.PreferredPort);
            var flowCancel = new CancellationTokenSource(this.Settings.SignInTimeout);
            string clientId;
            string hostId;
            lock (this.gate)
            {
                this.signInCancel = flowCancel;
                clientId = this.saved.ClientId;
                hostId = this.saved.HostId;
                this.SetStateLocked(ChatGptAccountState.SigningIn);
            }

            bool registering = string.IsNullOrEmpty(clientId);
            string verifier = ChatGptWire.RandomToken(64);
            var request = new ChatGptOAuth.AuthorizeRequest
            {
                ClientId = registering ? ChatGptOAuth.DynamicClientId : clientId,
                RedirectUri = loopback.RedirectUri,
                State = ChatGptWire.RandomToken(),
                Nonce = ChatGptWire.RandomToken(),
                CodeChallenge = ChatGptWire.CodeChallenge(verifier),
                HostId = hostId,
                AgentNameHint = registering ? this.Settings.AppName : null,
                ForceConsent = requestPlanConsent,
            };
            Uri url = ChatGptOAuth.BuildAuthorizeUrl(discovery, request);
            Task<ChatGptAccountState> completion = this.FinishSignInAsync(loopback, request, verifier, registering, flowCancel);
            var handle = new ChatGptSignIn(url, completion, flowCancel);

            if (this.Settings.OpenBrowser != null)
            {
                try
                {
                    await this.Settings.OpenBrowser(url, cancel).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    handle.Cancel();
                    throw;
                }
            }

            return handle;
        }

        /// <summary>
        /// Signs in and waits until it finishes.
        /// ログインを始めて、済むまで待つ。返り＝済んだ後の様子（SignedIn か PlanNotGranted）。
        /// </summary>
        public async Task<ChatGptAccountState> SignInAsync(bool requestPlanConsent = false, CancellationToken cancel = default)
        {
            ChatGptSignIn signIn = await this.BeginSignInAsync(requestPlanConsent, cancel).ConfigureAwait(false);
            using (cancel.Register(signIn.Cancel))
            {
                return await signIn.Completion.ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Cancels the sign-in in progress, if any.
        /// 途中のログインを取り消す（無ければ何もしない）。
        /// </summary>
        public void CancelSignIn()
        {
            CancellationTokenSource cts;
            lock (this.gate)
            {
                cts = this.signInCancel;
                this.signInCancel = null;
            }

            try
            {
                cts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // もう済んだログイン。
            }
        }

        /// <summary>
        /// True once after the first sign-in of this install: show "You're using your ChatGPT plan" one time.
        /// この端末で初めてログインした後に 1 回だけ true を返す（「You're using your ChatGPT plan」の案内を 1 回出す）。
        /// </summary>
        public bool TakePlanNotice()
        {
            lock (this.gate)
            {
                if (!this.saved.PlanNoticePending || this.state != ChatGptAccountState.SignedIn) return false;
                this.saved.PlanNoticePending = false;
                this.store.Save(this.saved);
                return true;
            }
        }

        // 戻りを待ち、code をトークンに替えて確かめ、保存する。失敗したら様子を保存の中身に戻して投げる。
        private async Task<ChatGptAccountState> FinishSignInAsync(
            ChatGptLoopback loopback, ChatGptOAuth.AuthorizeRequest request, string verifier, bool registering, CancellationTokenSource flowCancel)
        {
            await Task.Yield();
            CancellationToken cancel = flowCancel.Token;
            try
            {
                Dictionary<string, List<string>> query = await loopback.WaitAsync(request.State, cancel).ConfigureAwait(false);
                loopback.Dispose();
                string issued = ChatGptWire.Single(query, "client_id");
                if (issued == ChatGptOAuth.DynamicClientId) issued = null;
                if (registering && issued == null)
                {
                    throw new ChatGptException(ChatGptErrorKind.SignInFailed, "初回の登録で client_id が戻らなかった。", "registration_failed");
                }

                // 2 回目以降の戻りは client_id が付かないことがある（付かなければ頼んだ物のまま）。
                // 頼んだ物と違う client_id が付いた戻りは受けない（保存の client_id を他の登録で置き換えない）。
                if (!registering && issued != null && issued != request.ClientId)
                {
                    throw new ChatGptException(ChatGptErrorKind.SignInFailed, "ログインの戻りの client_id が、頼んだ物と違う（交換しない）。", "client_id_mismatch");
                }

                string clientId = issued ?? request.ClientId;
                lock (this.gate)
                {
                    // 発行された client_id は、交換の前に保存する（以後の authorize・交換・refresh・revoke はこれを使う）。
                    // 初回の案内も、ここで待ちにする（交換が断られて 2 回目の authorize で済んでも、案内は 1 回出る）。
                    this.saved.ClientId = clientId;
                    if (registering) this.saved.PlanNoticePending = true;
                    this.store.Save(this.saved);
                }

                ChatGptOAuth.TokenReply reply;
                try
                {
                    reply = await this.oauth.ExchangeCodeAsync(clientId, ChatGptWire.Single(query, "code"), verifier, request.RedirectUri, cancel).ConfigureAwait(false);
                }
                catch (ChatGptException e) when (e.Kind == ChatGptErrorKind.SignInRequired)
                {
                    // invalid_grant＝この code は捨てる。保存した client_id で authorize からやり直す（呼ぶ側がもう一度ログインを始める）。
                    throw new ChatGptException(ChatGptErrorKind.SignInFailed, "code の交換が断られた（" + e.Code + "）。ログインをやり直す。 " + e.Message, e.Code, httpStatus: e.HttpStatus, inner: e);
                }

                ChatGptIdToken.Claims claims = await this.VerifyIdTokenAsync(reply.IdToken, clientId, request.Nonce, cancel).ConfigureAwait(false);
                lock (this.gate)
                {
                    this.saved.Subject = claims.Subject;
                    this.saved.PlanType = claims.PlanType;
                    if (this.signInCancel == flowCancel) this.signInCancel = null;
                    this.ApplyTokens(reply);
                    return this.state;
                }
            }
            catch (Exception e)
            {
                lock (this.gate)
                {
                    // 後から始めた別のログインが途中なら、様子はそちらに任せる（SigningIn のままにする）。
                    if (this.signInCancel == flowCancel) this.signInCancel = null;
                    if (this.signInCancel == null && this.state == ChatGptAccountState.SigningIn) this.SetStateLocked(StateFromSaved(this.saved));
                }

                if (e is ChatGptException) throw;
                throw ChatGptWire.Translate(e, cancel, null);
            }
            finally
            {
                loopback.Dispose();
                flowCancel.Dispose();
            }
        }

        // ID トークンを確かめる。鍵が jwks に無ければ、jwks を 1 回だけ読み直す（鍵の入れ替えの直後）。
        private async Task<ChatGptIdToken.Claims> VerifyIdTokenAsync(string idToken, string clientId, string nonce, CancellationToken cancel)
        {
            if (string.IsNullOrEmpty(idToken)) throw new ChatGptException(ChatGptErrorKind.SignInFailed, "token の返りに id_token が無い。", "invalid_id_token");
            ChatGptOAuth.Discovery d = await this.oauth.GetDiscoveryAsync(cancel).ConfigureAwait(false);
            string kid = ChatGptIdToken.KeyIdOf(idToken);
            JObject key = ChatGptIdToken.FindKey(await this.oauth.GetJwksAsync(false, cancel).ConfigureAwait(false), kid);
            if (key == null) key = ChatGptIdToken.FindKey(await this.oauth.GetJwksAsync(true, cancel).ConfigureAwait(false), kid);
            return ChatGptIdToken.Verify(idToken, key, d.Issuer, clientId, nonce, DateTimeOffset.UtcNow);
        }
    }
}
