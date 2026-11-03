using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using static Monster.Balloon.AutomataSystem.ChatGpt.Tests.ChatGptTestKit;

namespace Monster.Balloon.AutomataSystem.ChatGpt.Tests
{
    /// <summary>
    /// Tests for Sign in with ChatGPT (register, state, nonce, scope, refresh, sign out).
    /// 「Sign in with ChatGPT」のログインのテスト。
    /// ⭐ ログインの保存（client_id・トークン）を取り違えると、遊ぶ人はログインを繰り返すか、他人の client_id で頼むことになる。
    /// 偽の OpenAI では毎回同じに再現できるので、ここで全部の分かれ道を確かめる。
    /// </summary>
    public sealed class ChatGptSignInTests
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
        /// Test: first sign-in registers with dynamic_agent_client and saves the issued client id.
        /// 初回は dynamic_agent_client で authorize し、戻りの発行済み client_id で交換して保存する。
        /// </summary>
        [Test]
        public void FirstSignIn_RegistersWithDynamicClient_AndSavesIssuedClientId()
        {
            using (ChatGptAccount account = this.NewAccount())
            {
                Assert.That(Run(() => SignInAsync(account)), Is.EqualTo(ChatGptAccountState.SignedIn));

                Dictionary<string, string> q = this.server.AuthorizeQueries[0];
                Assert.That(q["client_id"], Is.EqualTo("dynamic_agent_client"));
                Assert.That(q["agent_name_hint"], Is.EqualTo("Automata Test"));
                Assert.That(q["ext_agent_host_id"], Does.StartWith("urn:uuid:"));
                Assert.That(q["response_type"], Is.EqualTo("code"));
                Assert.That(q["scope"], Is.EqualTo(FakeOpenAi.FullScope));
                Assert.That(q["resource"], Is.EqualTo("https://api.openai.com/v1"));
                Assert.That(q["code_challenge_method"], Is.EqualTo("S256"));
                Assert.That(q["redirect_uri"], Does.Match(@"^http://127\.0\.0\.1:\d+/auth/callback$"));
                Assert.That(q.ContainsKey("prompt"), Is.False);

                Dictionary<string, string> form = this.server.TokenForms[0];
                Assert.That(form["grant_type"], Is.EqualTo("authorization_code"));
                Assert.That(form["client_id"], Is.EqualTo(FakeOpenAi.IssuedClientId));
                Assert.That(form["redirect_uri"], Is.EqualTo(q["redirect_uri"]));
                Assert.That(form["resource"], Is.EqualTo("https://api.openai.com/v1"));
                Assert.That(ChatGptWire.CodeChallenge(form["code_verifier"]), Is.EqualTo(q["code_challenge"]));

                JObject saved = ReadSaved(this.folder);
                Assert.That(saved.Value<string>("client_id"), Is.EqualTo(FakeOpenAi.IssuedClientId));
                Assert.That(saved.Value<string>("ext_agent_host_id"), Is.EqualTo(q["ext_agent_host_id"]));
                Assert.That(saved.Value<string>("plan_type"), Is.EqualTo("plus"));
                Assert.That(saved.ToString(), Does.Not.Contain("player@example.com"));
                Assert.That(saved.ToString(), Does.Not.Contain("id_token"));

                Assert.That(account.TakePlanNotice(), Is.True);
                Assert.That(account.TakePlanNotice(), Is.False);
            }
        }

        /// <summary>
        /// Test: the second sign-in uses the issued client id and sends no agent_name_hint.
        /// 2 回目は発行済みの client_id を使い、agent_name_hint を付けない。端末の印は同じ。
        /// </summary>
        [Test]
        public void SecondSignIn_UsesIssuedClientId_WithoutNameHint()
        {
            using (ChatGptAccount account = this.NewAccount())
            {
                Run(() => SignInAsync(account));
                Assert.That(account.TakePlanNotice(), Is.True);
                Run(() => account.SignOutAsync());
            }

            using (ChatGptAccount again = this.NewAccount())
            {
                Assert.That(Run(() => SignInAsync(again)), Is.EqualTo(ChatGptAccountState.SignedIn));
                Dictionary<string, string> first = this.server.AuthorizeQueries[0];
                Dictionary<string, string> second = this.server.AuthorizeQueries[1];
                Assert.That(second["client_id"], Is.EqualTo(FakeOpenAi.IssuedClientId));
                Assert.That(second.ContainsKey("agent_name_hint"), Is.False);
                Assert.That(second.ContainsKey("id_token_hint"), Is.False);
                Assert.That(second["ext_agent_host_id"], Is.EqualTo(first["ext_agent_host_id"]));
                Assert.That(again.TakePlanNotice(), Is.False);
            }
        }

        /// <summary>
        /// Test: a callback with the wrong state or an error is not exchanged.
        /// 戻りの state が食い違う・error が付いた時は、code を交換しない。
        /// </summary>
        [TestCase("state")]
        [TestCase("denied")]
        public void BadCallback_IsNotExchanged(string how)
        {
            if (how == "state") this.server.CallbackStateOverride = "not-the-state";
            else this.server.CallbackError = "access_denied";

            using (ChatGptAccount account = this.NewAccount())
            {
                ChatGptException e = Fails(() => SignInAsync(account));
                Assert.That(e.Kind, Is.EqualTo(ChatGptErrorKind.SignInFailed));
                Assert.That(e.Code, Is.EqualTo(how == "state" ? "state_mismatch" : "access_denied"));
                Assert.That(this.server.TokenForms, Is.Empty);
                Assert.That(account.State, Is.EqualTo(ChatGptAccountState.SignedOut));
            }
        }

        /// <summary>
        /// Test: a first registration without a returned client id fails.
        /// 初回の登録で client_id が戻らなければ、失敗にする（dynamic_agent_client で交換しない）。
        /// </summary>
        [Test]
        public void Registration_WithoutClientId_Fails()
        {
            this.server.ReturnClientId = false;
            using (ChatGptAccount account = this.NewAccount())
            {
                ChatGptException e = Fails(() => SignInAsync(account));
                Assert.That(e.Code, Is.EqualTo("registration_failed"));
                Assert.That(this.server.TokenForms, Is.Empty);
                Assert.That(ReadSaved(this.folder)["client_id"]?.Type ?? JTokenType.Null, Is.EqualTo(JTokenType.Null));
            }
        }

        /// <summary>
        /// Test: an id token with the wrong nonce is refused and nothing is saved.
        /// ID トークンの nonce が食い違えば断り、トークンを保存しない。
        /// </summary>
        [Test]
        public void NonceMismatch_IsRefused()
        {
            this.server.IdTokenNonceOverride = "other-nonce";
            using (ChatGptAccount account = this.NewAccount())
            {
                ChatGptException e = Fails(() => SignInAsync(account));
                Assert.That(e.Kind, Is.EqualTo(ChatGptErrorKind.SignInFailed));
                Assert.That(e.Code, Is.EqualTo("invalid_id_token"));
                Assert.That(account.State, Is.EqualTo(ChatGptAccountState.SignedOut));
                Assert.That(ReadSaved(this.folder)["access_token"]?.Type ?? JTokenType.Null, Is.EqualTo(JTokenType.Null));
            }
        }

        /// <summary>
        /// Test: without the plan scope the state is PlanNotGranted, and consent can be asked again.
        /// scope にプランが無ければ PlanNotGranted。トークンは渡さず、prompt=consent で取り直せる。
        /// </summary>
        [Test]
        public void ScopeWithoutPlan_GivesPlanNotGranted()
        {
            this.server.GrantedScope = "openid profile email offline_access";
            using (ChatGptAccount account = this.NewAccount())
            {
                Assert.That(Run(() => SignInAsync(account)), Is.EqualTo(ChatGptAccountState.PlanNotGranted));
                Assert.That(Fails(() => account.GetAccessTokenAsync()).Kind, Is.EqualTo(ChatGptErrorKind.PlanNotGranted));

                this.server.GrantedScope = FakeOpenAi.FullScope;
                Assert.That(Run(() => SignInAsync(account, consent: true)), Is.EqualTo(ChatGptAccountState.SignedIn));
                Assert.That(this.server.AuthorizeQueries[1]["prompt"], Is.EqualTo("consent"));
            }
        }

        /// <summary>
        /// Test: a refresh sends no scope and saves the new refresh token.
        /// refresh は scope を付けずに送り、替わった refresh_token を保存する。
        /// </summary>
        [Test]
        public void Refresh_SavesNewRefreshToken()
        {
            this.server.ExpiresIn = 60; // 期限が近い＝使う前に refresh する。
            using (ChatGptAccount account = this.NewAccount())
            {
                Run(() => SignInAsync(account));
                Assert.That(ReadSaved(this.folder).Value<string>("refresh_token"), Is.EqualTo("refresh-1"));

                string token = Run(() => account.GetAccessTokenAsync());
                Assert.That(token, Is.EqualTo("access-2"));

                Dictionary<string, string> form = this.server.TokenForms[1];
                Assert.That(form["grant_type"], Is.EqualTo("refresh_token"));
                Assert.That(form["client_id"], Is.EqualTo(FakeOpenAi.IssuedClientId));
                Assert.That(form["refresh_token"], Is.EqualTo("refresh-1"));
                Assert.That(form.ContainsKey("scope"), Is.False);

                JObject saved = ReadSaved(this.folder);
                Assert.That(saved.Value<string>("refresh_token"), Is.EqualTo("refresh-2"));
                Assert.That(saved.Value<string>("scope"), Is.EqualTo(FakeOpenAi.FullScope));
            }
        }

        /// <summary>
        /// Test: sign out revokes the refresh token and keeps the client id and host id.
        /// ログアウトは revoke を呼び、トークンを消し、client_id と端末の印を残す。
        /// </summary>
        [Test]
        public void SignOut_Revokes_AndKeepsClientIdAndHostId()
        {
            using (ChatGptAccount account = this.NewAccount())
            {
                Run(() => SignInAsync(account));
                string hostId = ReadSaved(this.folder).Value<string>("ext_agent_host_id");

                Assert.That(Run(() => account.SignOutAsync()), Is.True);
                Dictionary<string, string> form = this.server.RevokeForms[0];
                Assert.That(form["token"], Is.EqualTo("refresh-1"));
                Assert.That(form["token_type_hint"], Is.EqualTo("refresh_token"));
                Assert.That(form["client_id"], Is.EqualTo(FakeOpenAi.IssuedClientId));

                JObject saved = ReadSaved(this.folder);
                Assert.That(saved.Value<string>("client_id"), Is.EqualTo(FakeOpenAi.IssuedClientId));
                Assert.That(saved.Value<string>("ext_agent_host_id"), Is.EqualTo(hostId));
                Assert.That(saved["refresh_token"]?.Type ?? JTokenType.Null, Is.EqualTo(JTokenType.Null));
                Assert.That(saved["access_token"]?.Type ?? JTokenType.Null, Is.EqualTo(JTokenType.Null));
                Assert.That(account.State, Is.EqualTo(ChatGptAccountState.SignedOut));
            }
        }

        /// <summary>
        /// Test: the model list keeps only visibility "list".
        /// モデル一覧は visibility が "list" の物だけを並べる。
        /// </summary>
        [Test]
        public void ListModels_KeepsOnlyListedModels()
        {
            using (ChatGptAccount account = this.NewAccount())
            {
                Run(() => SignInAsync(account));
                var models = Run(() => account.ListModelsAsync());
                Assert.That(models.Count, Is.EqualTo(2));
                Assert.That(models[0].Slug, Is.EqualTo("model-a"));
                Assert.That(models[1].DisplayName, Is.EqualTo("Model B"));
            }
        }
    }
}
