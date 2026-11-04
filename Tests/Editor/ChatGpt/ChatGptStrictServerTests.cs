using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using static Monster.Balloon.AutomataSystem.ChatGpt.Tests.ChatGptTestKit;

namespace Monster.Balloon.AutomataSystem.ChatGpt.Tests
{
    /// <summary>
    /// Tests against the strict fake server: refused code exchange, re-auth callbacks, rotated and revoked refresh tokens.
    /// 決まりどおりに厳しくした偽の OpenAI で、本物でしか起きにくい分かれ道を確かめるテスト。
    /// ⭐ code の交換の断り・戻りの client_id の食い違い・使い捨ての refresh_token は、本物では狙って起こせない。
    ///    取り違えると保存のログインが消えるか、別の登録の client_id で頼むことになる。
    /// </summary>
    public sealed class ChatGptStrictServerTests
    {
        private FakeOpenAi server;
        private string folder;

        [SetUp]
        public void SetUp()
        {
            this.server = new FakeOpenAi();
            this.folder = TempFolder();
        }

        [TearDown]
        public void TearDown()
        {
            this.server.Dispose();
            try
            {
                Directory.Delete(this.folder, true);
            }
            catch (IOException)
            {
                // 一時フォルダの片付けの失敗は、テストの結果に関係しない。
            }
        }

        private ChatGptAccount NewAccount()
        {
            return new ChatGptAccount(this.server.Settings(this.folder));
        }

        /// <summary>
        /// Test: a refused code exchange keeps the issued client id, and the next sign-in re-authorizes with it.
        /// code の交換が invalid_grant で断られても、発行済みの client_id は残る。次のログインはその client_id で authorize からやり直す。
        /// </summary>
        [Test]
        public void RefusedCodeExchange_KeepsIssuedClientId_ThenReauthorizes()
        {
            this.server.CodeLifetime = TimeSpan.Zero; // 交換の時には切れている。
            using (ChatGptAccount account = this.NewAccount())
            {
                ChatGptException e = Fails(() => SignInAsync(account));
                Assert.That(e.Kind, Is.EqualTo(ChatGptErrorKind.SignInFailed));
                Assert.That(e.Code, Is.EqualTo("invalid_grant"));
                Assert.That(e.Message, Does.Contain("code の期限が切れた"), "token の口の error_description が字に入る");
                Assert.That(account.State, Is.EqualTo(ChatGptAccountState.SignedOut));
                JObject saved = ReadSaved(this.folder);
                Assert.That(saved.Value<string>("client_id"), Is.EqualTo(FakeOpenAi.IssuedClientId));
                Assert.That(saved["access_token"]?.Type ?? JTokenType.Null, Is.EqualTo(JTokenType.Null));

                this.server.CodeLifetime = TimeSpan.FromMinutes(10);
                Assert.That(Run(() => SignInAsync(account)), Is.EqualTo(ChatGptAccountState.SignedIn));
                Dictionary<string, string> again = this.server.AuthorizeQueries[1];
                Assert.That(again["client_id"], Is.EqualTo(FakeOpenAi.IssuedClientId));
                Assert.That(again.ContainsKey("agent_name_hint"), Is.False);
                Assert.That(this.server.TokenForms[1]["client_id"], Is.EqualTo(FakeOpenAi.IssuedClientId));
                Assert.That(this.server.Refusals.Count, Is.EqualTo(1));

                // 初回の案内は、登録の後に初めて済んだ時に 1 回出る。
                Assert.That(account.TakePlanNotice(), Is.True);
            }
        }

        /// <summary>
        /// Test: a re-auth callback carrying another client id is not exchanged and does not replace the saved one.
        /// 2 回目以降の戻りに別の client_id が付いたら、交換せず、保存の client_id も替えない。
        /// </summary>
        [Test]
        public void ReauthCallback_WithOtherClientId_IsRejected()
        {
            using (ChatGptAccount account = this.NewAccount())
            {
                Run(() => SignInAsync(account));
                Run(() => account.SignOutAsync());

                this.server.ReauthClientIdOverride = "oaiapp_someone_else";
                ChatGptException e = Fails(() => SignInAsync(account));
                Assert.That(e.Kind, Is.EqualTo(ChatGptErrorKind.SignInFailed));
                Assert.That(e.Code, Is.EqualTo("client_id_mismatch"));
                Assert.That(this.server.TokenForms.Count, Is.EqualTo(1));
                Assert.That(ReadSaved(this.folder).Value<string>("client_id"), Is.EqualTo(FakeOpenAi.IssuedClientId));
            }
        }

        /// <summary>
        /// Test: error=access_denied (the player pressed Cancel) is not exchanged and keeps the issued client id.
        /// 許可の画面で Cancel（error=access_denied）なら交換しない。印は SignInFailed・access_denied。訳の文があれば字に入る。
        /// </summary>
        [TestCase(false)]
        [TestCase(true)]
        public void AccessDenied_IsNotExchanged(bool registeredBefore)
        {
            using (ChatGptAccount account = this.NewAccount())
            {
                if (registeredBefore)
                {
                    Run(() => SignInAsync(account));
                    Run(() => account.SignOutAsync());
                }

                int forms = this.server.TokenForms.Count;
                this.server.CallbackError = "access_denied";
                this.server.CallbackErrorDescription = "The user denied the request.";
                ChatGptException e = Fails(() => SignInAsync(account));
                Assert.That(e.Kind, Is.EqualTo(ChatGptErrorKind.SignInFailed));
                Assert.That(e.Code, Is.EqualTo("access_denied"));
                Assert.That(e.Message, Does.Contain("The user denied the request."));
                Assert.That(this.server.TokenForms.Count, Is.EqualTo(forms));
                Assert.That(account.State, Is.EqualTo(ChatGptAccountState.SignedOut));

                JToken clientId = ReadSaved(this.folder)["client_id"];
                if (registeredBefore) Assert.That((string)clientId, Is.EqualTo(FakeOpenAi.IssuedClientId));
                else Assert.That(clientId?.Type ?? JTokenType.Null, Is.EqualTo(JTokenType.Null));
            }
        }

        /// <summary>
        /// Test: two accounts on one folder take turns refreshing without reusing a rotated refresh token.
        /// 同じフォルダを読む 2 つの口が代わる代わる refresh しても、替わった後の古い refresh_token を使わない（ログインが切れない）。
        /// </summary>
        [Test]
        public void TwoAccountsOnOneFolder_DoNotReuseRotatedRefreshToken()
        {
            this.server.ExpiresIn = 60; // 期限が近い＝使う前に毎回 refresh する。
            using (ChatGptAccount a = this.NewAccount())
            {
                Run(() => SignInAsync(a));
                using (ChatGptAccount b = this.NewAccount())
                {
                    Assert.That(Run(() => a.GetAccessTokenAsync()), Is.EqualTo("access-2"));
                    Assert.That(Run(() => b.GetAccessTokenAsync()), Is.EqualTo("access-3"));
                    Assert.That(Run(() => a.GetAccessTokenAsync()), Is.EqualTo("access-4"));

                    string[] used = this.server.TokenForms.Where(f => f["grant_type"] == "refresh_token").Select(f => f["refresh_token"]).ToArray();
                    Assert.That(used, Is.EqualTo(new[] { "refresh-1", "refresh-2", "refresh-3" }));
                    Assert.That(this.server.Refusals, Is.Empty);
                    Assert.That(b.State, Is.EqualTo(ChatGptAccountState.SignedIn));
                }
            }
        }

        /// <summary>
        /// Test: a refresh token revoked by sign-out elsewhere gives SignInRequired and the tokens are forgotten.
        /// 別の口のログアウトで取り消した refresh_token は断られる＝SignInRequired・トークンを消して SignedOut。
        /// </summary>
        [Test]
        public void RevokedRefreshToken_NeedsSignIn()
        {
            this.server.ExpiresIn = 60;
            using (ChatGptAccount a = this.NewAccount())
            {
                Run(() => SignInAsync(a));
                using (ChatGptAccount b = this.NewAccount())
                {
                    Assert.That(Run(() => a.SignOutAsync()), Is.True);
                    ChatGptException e = Fails(() => b.GetAccessTokenAsync());
                    Assert.That(e.Kind, Is.EqualTo(ChatGptErrorKind.SignInRequired));
                    Assert.That(e.Code, Is.EqualTo("invalid_grant"));
                    Assert.That(b.State, Is.EqualTo(ChatGptAccountState.SignedOut));
                    Assert.That(this.server.Refusals.Single(), Does.Contain("取り消した refresh_token"));
                }
            }
        }

        /// <summary>
        /// Test: OAuth-shaped token errors keep their code and description.
        /// token の口の OAuth の形の断り（invalid_grant 以外も）は、印と訳の文を残す。
        /// </summary>
        [Test]
        public void TokenErrors_KeepCodeAndDescription()
        {
            ChatGptException other = ChatGptOAuth.MapTokenError(400, "{\"error\":\"invalid_request\",\"error_description\":\"missing resource\"}");
            Assert.That(other.Code, Is.EqualTo("invalid_request"));
            Assert.That(other.Kind, Is.EqualTo(ChatGptErrorKind.Refused));
            Assert.That(other.Message, Does.Contain("missing resource"));

            ChatGptException grant = ChatGptOAuth.MapTokenError(400, "{\"error\":\"invalid_grant\",\"error_description\":\"code expired\"}");
            Assert.That(grant.Kind, Is.EqualTo(ChatGptErrorKind.SignInRequired));
            Assert.That(grant.Message, Does.Contain("code expired"));

            ChatGptException client = ChatGptOAuth.MapTokenError(401, "{\"error\":\"invalid_client\"}");
            Assert.That(client.Kind, Is.EqualTo(ChatGptErrorKind.InvalidClient));
        }
    }
}
