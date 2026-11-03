using NUnit.Framework;

namespace Monster.Balloon.AutomataSystem.Tests
{
    /// <summary>
    /// Tests for the request gate.
    /// 窓口の門のテスト。
    /// ⭐ 門が壊れると、ブラウザで開いたページから操作され、操作キャラが倒れて世界に削除の印が付くおそれがある。
    /// 遊んでいても門の穴には気づけないので、ここで全部の断り方を確かめる。
    /// </summary>
    public sealed class AgentGateTests
    {
        private const string Host = "127.0.0.1:47810";
        private const string SurfaceToken = "surface-token-0123456789abcdef";
        private const string BackToken = "back-token-0123456789abcdefghij";

        private static readonly AgentGateKeys Keys = new AgentGateKeys(Host, SurfaceToken, BackToken);

        private static AgentGateRequest Request(
            string method = "POST",
            string path = "/v1/hello",
            string host = Host,
            bool hasOrigin = false,
            string contentType = "application/json",
            long contentLength = 2,
            string authorization = "Bearer " + SurfaceToken)
        {
            return new AgentGateRequest(method, path, host, hasOrigin, contentType, contentLength, authorization);
        }

        private static AgentGateVerdict Check(in AgentGateRequest request, AgentRouteKind route = AgentRouteKind.Surface, AgentGateKeys keys = null)
        {
            return AgentGate.Check(in request, keys ?? Keys, route, new AgentRateLimiter(20), 0);
        }

        /// <summary>
        /// Test: valid request, passes as surface.
        /// 名前のとおりのことを確かめる。
        /// </summary>
        [Test]
        public void ValidRequest_PassesAsSurface()
        {
            AgentGateVerdict v = Check(Request());
            Assert.That(v.Passed, Is.True);
            Assert.That(v.Face, Is.EqualTo(AgentFace.Surface));
        }

        /// <summary>
        /// Test: origin header, is refused, even with valid back token.
        /// 名前のとおりのことを確かめる。
        /// </summary>
        [Test]
        public void OriginHeader_IsRefused_EvenWithValidBackToken()
        {
            // ブラウザのページは Origin を付ける。トークンが漏れていても、ブラウザからは通さない。
            AgentGateVerdict v = Check(Request(hasOrigin: true, authorization: "Bearer " + BackToken));
            Assert.That(v.Status, Is.EqualTo(403));
            Assert.That(v.Code, Is.EqualTo("origin"));
        }

        /// <summary>
        /// Test: host other than loopback port, is refused.
        /// 名前のとおりのことを確かめる。
        /// </summary>
        [TestCase("localhost:47810")]
        [TestCase("127.0.0.1:47811")]
        [TestCase("127.0.0.1")]
        [TestCase("evil.example:47810")]
        [TestCase("")]
        [TestCase(null)]
        public void HostOtherThanLoopbackPort_IsRefused(string host)
        {
            // 名前の付け替え（DNS rebinding）の対策。
            AgentGateVerdict v = Check(Request(host: host));
            Assert.That(v.Status, Is.EqualTo(403));
            Assert.That(v.Code, Is.EqualTo("host"));
        }

        /// <summary>
        /// Test: non post, is refused.
        /// 名前のとおりのことを確かめる。
        /// </summary>
        [TestCase("GET")]
        [TestCase("PUT")]
        [TestCase("OPTIONS")]
        [TestCase("post")]
        public void NonPost_IsRefused(string method)
        {
            Assert.That(Check(Request(method: method)).Status, Is.EqualTo(405));
        }

        /// <summary>
        /// Test: non json body, is refused.
        /// 名前のとおりのことを確かめる。
        /// </summary>
        [TestCase("application/x-www-form-urlencoded")]
        [TestCase("text/plain")]
        [TestCase("multipart/form-data")]
        [TestCase("")]
        [TestCase(null)]
        public void NonJsonBody_IsRefused(string contentType)
        {
            // ブラウザのフォームが送れる形は、どれも断る。
            Assert.That(Check(Request(contentType: contentType)).Status, Is.EqualTo(415));
        }

        /// <summary>
        /// Test: json with charset, passes.
        /// 名前のとおりのことを確かめる。
        /// </summary>
        [Test]
        public void JsonWithCharset_Passes()
        {
            Assert.That(Check(Request(contentType: "application/json; charset=utf-8")).Passed, Is.True);
        }

        /// <summary>
        /// Test: body over64 kb, is refused.
        /// 名前のとおりのことを確かめる。
        /// </summary>
        [Test]
        public void BodyOver64KB_IsRefused()
        {
            Assert.That(Check(Request(contentLength: AgentGate.MaxBodyBytes)).Passed, Is.True);
            Assert.That(Check(Request(contentLength: AgentGate.MaxBodyBytes + 1)).Status, Is.EqualTo(413));
        }

        /// <summary>
        /// Test: missing or wrong token, is401.
        /// 名前のとおりのことを確かめる。
        /// </summary>
        [TestCase(null)]
        [TestCase("")]
        [TestCase("Bearer ")]
        [TestCase("Basic " + SurfaceToken)]
        [TestCase(SurfaceToken)]
        [TestCase("Bearer surface-token-0123456789abcdeX")]
        [TestCase("Bearer surface-token-0123456789abcde")]
        [TestCase("Bearer surface-token-0123456789abcdef0")]
        public void MissingOrWrongToken_Is401(string authorization)
        {
            Assert.That(Check(Request(authorization: authorization)).Status, Is.EqualTo(401));
        }

        /// <summary>
        /// Test: surface token on back route, is blind.
        /// 名前のとおりのことを確かめる。
        /// </summary>
        [Test]
        public void SurfaceTokenOnBackRoute_IsBlind()
        {
            AgentGateVerdict v = Check(Request(path: "/v1/back/look"), AgentRouteKind.Back);
            Assert.That(v.Status, Is.EqualTo(403));
            Assert.That(v.Code, Is.EqualTo("blind"));
        }

        /// <summary>
        /// Test: surface token on back prefix, is blind, even before routes exist.
        /// 名前のとおりのことを確かめる。
        /// </summary>
        [Test]
        public void SurfaceTokenOnBackPrefix_IsBlind_EvenBeforeRoutesExist()
        {
            // 世界ができる前（道がまだ並んでいない）でも、表には「裏の道が在るか」を教えない。
            Assert.That(Check(Request(path: "/v1/back/look"), AgentRouteKind.WorldPending).Code, Is.EqualTo("blind"));
            Assert.That(Check(Request(path: "/v1/back/nothing"), AgentRouteKind.Missing).Code, Is.EqualTo("blind"));
        }

        /// <summary>
        /// Test: back token, passes back and surface routes.
        /// 名前のとおりのことを確かめる。
        /// </summary>
        [Test]
        public void BackToken_PassesBackAndSurfaceRoutes()
        {
            AgentGateVerdict back = Check(Request(path: "/v1/back/look", authorization: "Bearer " + BackToken), AgentRouteKind.Back);
            Assert.That(back.Passed, Is.True);
            Assert.That(back.Face, Is.EqualTo(AgentFace.Back));

            AgentGateVerdict surface = Check(Request(authorization: "Bearer " + BackToken), AgentRouteKind.Surface);
            Assert.That(surface.Passed, Is.True);
            Assert.That(surface.Face, Is.EqualTo(AgentFace.Back));
        }

        /// <summary>
        /// Test: build without backstage, rejects any other token.
        /// 名前のとおりのことを確かめる。
        /// </summary>
        [Test]
        public void BuildWithoutBackstage_RejectsAnyOtherToken()
        {
            // 製品版（裏が無い）では、裏のトークンらしき物は、ただの違うトークンとして 401。
            var productionKeys = new AgentGateKeys(Host, SurfaceToken, null);
            Assert.That(Check(Request(authorization: "Bearer " + BackToken), AgentRouteKind.Surface, productionKeys).Status, Is.EqualTo(401));
        }

        /// <summary>
        /// Test: missing route, is404, and before world ready, is503.
        /// 名前のとおりのことを確かめる。
        /// </summary>
        [Test]
        public void MissingRoute_Is404_AndBeforeWorldReady_Is503()
        {
            Assert.That(Check(Request(path: "/v1/nothing"), AgentRouteKind.Missing).Status, Is.EqualTo(404));
            Assert.That(Check(Request(path: "/v1/look"), AgentRouteKind.WorldPending).Status, Is.EqualTo(503));
        }

        /// <summary>
        /// Test: route outside opened abilities, is403 not open.
        /// 名前のとおりのことを確かめる。
        /// </summary>
        [Test]
        public void RouteOutsideOpenedAbilities_Is403NotOpen()
        {
            // 窓口を開いた力に入らない道（例 look を開いていない窓口の look）は、「開いていない」と答える。
            AgentGateVerdict surface = Check(Request(path: "/v1/look"), AgentRouteKind.NotOpen);
            Assert.That(surface.Status, Is.EqualTo(403));
            Assert.That(surface.Code, Is.EqualTo("not_open"));

            AgentGateVerdict back = Check(Request(path: "/v1/look", authorization: "Bearer " + BackToken), AgentRouteKind.NotOpen);
            Assert.That(back.Code, Is.EqualTo("not_open"));
        }

        /// <summary>
        /// Test: surface token on back prefix, is blind, even when not open.
        /// 名前のとおりのことを確かめる。
        /// </summary>
        [Test]
        public void SurfaceTokenOnBackPrefix_IsBlind_EvenWhenNotOpen()
        {
            // 表のトークンには、裏の道が「開いていない」ことも教えない（裏の道が在るかを漏らさない）。
            Assert.That(Check(Request(path: "/v1/back/look"), AgentRouteKind.NotOpen).Code, Is.EqualTo("blind"));
        }

        /// <summary>
        /// Test: before keys are set, is503 starting.
        /// 名前のとおりのことを確かめる。
        /// </summary>
        [Test]
        public void BeforeKeysAreSet_Is503Starting()
        {
            AgentGateVerdict v = AgentGate.Check(Request(), null, AgentRouteKind.Surface, new AgentRateLimiter(20), 0);
            Assert.That(v.Status, Is.EqualTo(503));
            Assert.That(v.Code, Is.EqualTo("starting"));
        }

        /// <summary>
        /// Test: twenty first request within one second, is429, then recovers.
        /// 名前のとおりのことを確かめる。
        /// </summary>
        [Test]
        public void TwentyFirstRequestWithinOneSecond_Is429_ThenRecovers()
        {
            var limiter = new AgentRateLimiter(20);
            AgentGateRequest request = Request();
            for (int i = 0; i < 20; i++)
            {
                Assert.That(AgentGate.Check(in request, Keys, AgentRouteKind.Surface, limiter, 1000 + i).Passed, Is.True, "request " + (i + 1));
            }

            Assert.That(AgentGate.Check(in request, Keys, AgentRouteKind.Surface, limiter, 1500).Status, Is.EqualTo(429));
            Assert.That(AgentGate.Check(in request, Keys, AgentRouteKind.Surface, limiter, 2000).Passed, Is.True);
        }

        /// <summary>
        /// Test: fixed time equals, is false on any difference.
        /// 名前のとおりのことを確かめる。
        /// </summary>
        [Test]
        public void FixedTimeEquals_IsFalseOnAnyDifference()
        {
            Assert.That(AgentTokens.FixedTimeEquals("abc", "abc"), Is.True);
            Assert.That(AgentTokens.FixedTimeEquals("abc", "abd"), Is.False);
            Assert.That(AgentTokens.FixedTimeEquals("abc", "ab"), Is.False);
            Assert.That(AgentTokens.FixedTimeEquals(null, "abc"), Is.False);
            Assert.That(AgentTokens.FixedTimeEquals(null, null), Is.False);
        }

        /// <summary>
        /// Test: new token, is64 hex chars, and differs each time.
        /// 名前のとおりのことを確かめる。
        /// </summary>
        [Test]
        public void NewToken_Is64HexChars_AndDiffersEachTime()
        {
            string a = AgentTokens.NewToken();
            string b = AgentTokens.NewToken();
            Assert.That(a.Length, Is.EqualTo(64));
            Assert.That(a, Is.Not.EqualTo(b));
        }

        /// <summary>
        /// Test: keys to string, does not leak tokens.
        /// 名前のとおりのことを確かめる。
        /// </summary>
        [Test]
        public void KeysToString_DoesNotLeakTokens()
        {
            string text = Keys.ToString();
            Assert.That(text, Does.Not.Contain(SurfaceToken));
            Assert.That(text, Does.Not.Contain(BackToken));
        }
    }
}
