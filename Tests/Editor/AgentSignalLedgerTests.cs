using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Monster.Balloon.AutomataSystem.Tests
{
    /// <summary>
    /// Tests for the signal ledger and its cursors (bookmarks).
    /// 合図の帳面としおりのテスト。
    /// ⭐ なぜテストにするか: 帳面が輪で押し出した合図や、しおりの取り違え（前の Play・表と裏）は、
    ///    Play では 4096 行を越えるまで起きず、起きても使う側は「何も来なかった」としか見えない（合図が黙って消える）。
    /// </summary>
    public sealed class AgentSignalLedgerTests
    {
        private const string PlayId = "a1b2c3";

        /// <summary>
        /// Test: the ledger returns signals in the order they were written, with the next cursor.
        /// 名前のとおりのことを確かめる。
        /// </summary>
        [Test]
        public void 帳面は_書いた順に_続きの番号で返す()
        {
            var ledger = new AgentSignalLedger(8);
            for (int i = 0; i < 5; i++)
            {
                ledger.Append(1, 100 + i, "k" + i, false, null);
            }

            var into = new List<AgentSignalEntry>();
            long lost = ledger.ReadAfter(0, 100, into, out long readTo);
            Assert.That(lost, Is.EqualTo(0));
            Assert.That(readTo, Is.EqualTo(5));
            Assert.That(into.Count, Is.EqualTo(5));
            for (int i = 0; i < 5; i++)
            {
                Assert.That(into[i].Seq, Is.EqualTo(i + 1));
                Assert.That(into[i].Kind, Is.EqualTo("k" + i));
            }
        }

        /// <summary>
        /// Test: when the ring pushes signals out, the ledger returns how many were lost and the rest from the oldest.
        /// 名前のとおりのことを確かめる。
        /// </summary>
        [Test]
        public void 輪から押し出された合図は_数だけ返し_残りを古い順に返す()
        {
            var ledger = new AgentSignalLedger(4);
            for (int i = 1; i <= 10; i++)
            {
                ledger.Append(1, i, "k" + i, false, null);
            }

            var into = new List<AgentSignalEntry>();
            long lost = ledger.ReadAfter(2, 100, into, out long readTo);

            // 3〜6 は押し出された（4 行）。残っているのは 7〜10。
            Assert.That(lost, Is.EqualTo(4));
            Assert.That(into.ConvertAll(e => e.Seq), Is.EqualTo(new long[] { 7, 8, 9, 10 }));
            Assert.That(readTo, Is.EqualTo(10));
        }

        /// <summary>
        /// Test: when a read is cut at the limit, the cursor stops where the read ended.
        /// 名前のとおりのことを確かめる。
        /// </summary>
        [Test]
        public void 上限で切った時は_しおりが読んだ所で止まる()
        {
            var ledger = new AgentSignalLedger(16);
            for (int i = 1; i <= 10; i++)
            {
                ledger.Append(1, i, "k", false, null);
            }

            var into = new List<AgentSignalEntry>();
            ledger.ReadAfter(3, 4, into, out long readTo);
            Assert.That(into.ConvertAll(e => e.Seq), Is.EqualTo(new long[] { 4, 5, 6, 7 }));
            Assert.That(readTo, Is.EqualTo(7));

            ledger.ReadAfter(readTo, 100, into, out readTo);
            Assert.That(into.ConvertAll(e => e.Seq), Is.EqualTo(new long[] { 8, 9, 10 }));

            ledger.ReadAfter(readTo, 100, into, out long again);
            Assert.That(into, Is.Empty);
            Assert.That(again, Is.EqualTo(10));
        }

        /// <summary>
        /// Test: a search stops at the first hit after the cursor, and moves past misses up to where it read.
        /// 名前のとおりのことを確かめる。
        /// </summary>
        [Test]
        public void 探す時は_しおりの後の最初の当たりで止まり_外れは読んだ所まで進める()
        {
            var ledger = new AgentSignalLedger(16);
            ledger.Append(1, 1, "a", false, null);
            ledger.Append(1, 2, "b", false, null);
            ledger.Append(1, 3, "a", false, null);

            Assert.That(ledger.TryFindAfter(0, e => e.Kind == "a", out AgentSignalEntry hit, out long scanned, out long lost), Is.True);
            Assert.That(hit.Seq, Is.EqualTo(1));
            Assert.That(ledger.TryFindAfter(hit.Seq, e => e.Kind == "a", out hit, out scanned, out lost), Is.True);
            Assert.That(hit.Seq, Is.EqualTo(3));
            Assert.That(ledger.TryFindAfter(hit.Seq, e => e.Kind == "a", out _, out scanned, out lost), Is.False);
            Assert.That(scanned, Is.EqualTo(3));
            Assert.That(lost, Is.EqualTo(0));
        }

        /// <summary>
        /// Test: a cursor has a different shape on the surface and on the back, and can be read back.
        /// 名前のとおりのことを確かめる。
        /// </summary>
        [Test]
        public void しおりは_表と裏で形が違い_読み戻せる()
        {
            Assert.That(AgentCursor.Format(PlayId, AgentFace.Surface, 42), Is.EqualTo("a1b2c3:42"));
            Assert.That(AgentCursor.Format(PlayId, AgentFace.Back, 42), Is.EqualTo("a1b2c3:b42"));

            Assert.That(Read("a1b2c3:42", AgentFace.Surface, 50, out long seq), Is.EqualTo(AgentCursor.Result.Ok));
            Assert.That(seq, Is.EqualTo(42));
            Assert.That(Read("a1b2c3:b42", AgentFace.Back, 50, out seq), Is.EqualTo(AgentCursor.Result.Ok));
            Assert.That(seq, Is.EqualTo(42));
        }

        /// <summary>
        /// Test: a cursor of the other ledger is refused as a mix-up.
        /// 名前のとおりのことを確かめる。
        /// </summary>
        [TestCase("a1b2c3:b42", AgentFace.Surface)]
        [TestCase("a1b2c3:42", AgentFace.Back)]
        public void もう片方の帳面のしおりは_取り違えとして断る(string cursor, AgentFace ledger)
        {
            Assert.That(Read(cursor, ledger, 50, out _), Is.EqualTo(AgentCursor.Result.WrongLedger));
        }

        /// <summary>
        /// Test: a cursor of an earlier Play, or a number ahead of the ledger, is treated as stale.
        /// 名前のとおりのことを確かめる。
        /// </summary>
        [TestCase("ffffff:10")]
        [TestCase("a1b2c3:51")]
        public void 前のPlayのしおりと_先の番号は_古いしおりとみなす(string cursor)
        {
            Assert.That(Read(cursor, AgentFace.Surface, 50, out _), Is.EqualTo(AgentCursor.Result.Stale));
        }

        /// <summary>
        /// Test: a cursor with a wrong shape is refused as a shape error.
        /// 名前のとおりのことを確かめる。
        /// </summary>
        [TestCase("42")]
        [TestCase("a1b2c3:")]
        [TestCase(":42")]
        [TestCase("a1b2c3:4x")]
        [TestCase("a1b2c3:-1")]
        [TestCase("a1b2c3:b")]
        public void 形の違うしおりは_形の違いとして断る(string cursor)
        {
            Assert.That(Read(cursor, AgentFace.Surface, 50, out _), Is.EqualTo(AgentCursor.Result.Malformed));
        }

        /// <summary>
        /// Test: the case with no cursor, and the case with a number.
        /// 名前のとおりのことを確かめる。
        /// </summary>
        [Test]
        public void しおりが無い時と_数の時()
        {
            Assert.That(AgentCursor.TryRead(new JObject(), PlayId, AgentFace.Surface, 5, out _), Is.EqualTo(AgentCursor.Result.Missing));
            var number = new JObject { ["after"] = 3 };
            Assert.That(AgentCursor.TryRead(number, PlayId, AgentFace.Surface, 5, out _), Is.EqualTo(AgentCursor.Result.Malformed));
        }

        /// <summary>
        /// Test: the names of the signals to wait for are read both as plain text and with a kind.
        /// 名前のとおりのことを確かめる。
        /// </summary>
        [Test]
        public void 待つ合図の名前は_文字と_kind付きの両方を読む()
        {
            var kinds = new List<string>();
            var body = JObject.Parse("{\"until\":[{\"kind\":\"choices.shown\"},\"conversation.*\"]}");
            Assert.That(AgentSignalJson.TryReadKinds(body, 16, kinds, out _), Is.True);
            Assert.That(kinds, Is.EqualTo(new[] { "choices.shown", "conversation.*" }));

            Assert.That(AgentSignalJson.TryReadKinds(JObject.Parse("{\"until\":\"x\"}"), 16, kinds, out _), Is.False);
            Assert.That(AgentSignalJson.TryReadKinds(JObject.Parse("{\"until\":[{\"name\":\"x\"}]}"), 16, kinds, out _), Is.False);
            Assert.That(AgentSignalJson.TryReadKinds(new JObject(), 16, kinds, out _), Is.True);
            Assert.That(kinds, Is.Empty);
        }

        private static AgentCursor.Result Read(string cursor, AgentFace ledger, long lastSeq, out long seq) =>
            AgentCursor.TryRead(new JObject { ["after"] = cursor }, PlayId, ledger, lastSeq, out seq);
    }
}
