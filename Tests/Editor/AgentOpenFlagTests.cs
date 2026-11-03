using System;
using NUnit.Framework;

namespace Monster.Balloon.AutomataSystem.Tests
{
    /// <summary>
    /// Tests for reading the open flag and for the ability register.
    /// 窓口を開く印の読み方と、力の登録簿のテスト。
    /// ⭐ 読み方が甘いと、狭い力だけの印で、A の画面や裏の道まで開くおそれがある。遊んでいても気づけないので、ここで確かめる。
    /// ⭐ 土台だけの登録簿（<c>new AgentAbilityCatalog()</c>）と、試しの力 1 つを足した登録簿で読む＝ゲームの登録に引きずられない。
    /// </summary>
    public sealed class AgentOpenFlagTests
    {
        /// <summary>試しの力（ゲームが登録する力の代わり）。専用のつなぎ先を持ち、表の合図を読める。</summary>
        private const AgentAbilities Helper = (AgentAbilities)(1 << 4);

        private const string HelperFile = "endpoint-helper.json";

        private static AgentAbilityCatalog BaseOnly() => new AgentAbilityCatalog();

        private static AgentAbilityCatalog WithHelper()
        {
            var catalog = new AgentAbilityCatalog();
            catalog.Register(new AgentAbilityDef("helper", Helper, soloEndpointFileName: HelperFile, readsSurfaceSignals: true));
            return catalog;
        }

        /// <summary>
        /// Test: empty flag, is unreadable, and opens nothing.
        /// 名前のとおりのことを確かめる。
        /// </summary>
        [TestCase(null)]
        [TestCase("")]
        [TestCase("  \r\n")]
        [TestCase(",+")]
        public void EmptyFlag_IsUnreadable_AndOpensNothing(string text)
        {
            Assert.That(AgentOpenFlag.TryParse(text, BaseOnly(), out AgentAbilities abilities, out string word), Is.False);
            Assert.That(abilities, Is.EqualTo(AgentAbilities.None));
            Assert.That(word, Is.Null);
        }

        /// <summary>
        /// Test: unknown word, is unreadable, and opens nothing.
        /// 名前のとおりのことを確かめる。
        /// </summary>
        [TestCase("1")]
        [TestCase("open")]
        [TestCase("look,lookk")]
        [TestCase("look backstage")]
        [TestCase("helper")]
        public void UnknownWord_IsUnreadable_AndOpensNothing(string text)
        {
            // 前は、知らない語も「操作も」と読んでいた。知らない語が 1 つでもあれば閉じる。
            // ⭐ 登録していない力の名前（helper）も、知らない語として閉じる。
            Assert.That(AgentOpenFlag.TryParse(text, BaseOnly(), out AgentAbilities abilities, out string word), Is.False);
            Assert.That(abilities, Is.EqualTo(AgentAbilities.None));
            Assert.That(word, Is.Null);
        }

        /// <summary>
        /// Test: operate, opens everything, including registered abilities.
        /// 名前のとおりのことを確かめる。
        /// </summary>
        [Test]
        public void Operate_OpensEverything_IncludingRegisteredAbilities()
        {
            Assert.That(AgentOpenFlag.TryParse("operate", BaseOnly(), out AgentAbilities baseAll, out string word), Is.True);
            Assert.That(baseAll, Is.EqualTo(AgentAbilities.Look | AgentAbilities.Self | AgentAbilities.Back));
            Assert.That(word, Is.EqualTo("operate"));

            // ⭐ 「全部」は、登録した力も含む（operate でゲームの力も開く）。
            AgentAbilityCatalog catalog = WithHelper();
            Assert.That(AgentOpenFlag.TryParse("operate", catalog, out AgentAbilities all, out _), Is.True);
            Assert.That(all, Is.EqualTo(catalog.All));
            Assert.That(all, Is.EqualTo(AgentAbilities.Look | AgentAbilities.Self | AgentAbilities.Back | Helper));
        }

        /// <summary>
        /// Test: look, opens eyes and back, but not hands.
        /// 名前のとおりのことを確かめる。
        /// </summary>
        [Test]
        public void Look_OpensEyesAndBack_ButNotHands()
        {
            // 前からの「見るだけ」の印と同じ力（表の目と裏）。
            Assert.That(AgentOpenFlag.TryParse("look", WithHelper(), out AgentAbilities abilities, out _), Is.True);
            Assert.That(abilities, Is.EqualTo(AgentAbilities.Look | AgentAbilities.Back));
        }

        /// <summary>
        /// Test: registered word, opens that ability only.
        /// 名前のとおりのことを確かめる。
        /// </summary>
        [Test]
        public void RegisteredWord_OpensThatAbilityOnly()
        {
            Assert.That(AgentOpenFlag.TryParse(" Helper\r\n", WithHelper(), out AgentAbilities abilities, out string word), Is.True);
            Assert.That(abilities, Is.EqualTo(Helper));
            Assert.That(word, Is.EqualTo("helper"));
        }

        /// <summary>
        /// Test: words, can be joined.
        /// 名前のとおりのことを確かめる。
        /// </summary>
        [Test]
        public void Words_CanBeJoined()
        {
            Assert.That(AgentOpenFlag.TryParse("look,helper", WithHelper(), out AgentAbilities joined, out string word), Is.True);
            Assert.That(joined, Is.EqualTo(AgentAbilities.Look | AgentAbilities.Back | Helper));
            Assert.That(word, Is.EqualTo("look+helper"));

            Assert.That(AgentOpenFlag.TryParse("helper + self", WithHelper(), out AgentAbilities hands, out _), Is.True);
            Assert.That(hands, Is.EqualTo(Helper | AgentAbilities.Self));
        }

        /// <summary>
        /// Test: closed, is readable, and opens nothing.
        /// 名前のとおりのことを確かめる。
        /// </summary>
        [TestCase("closed")]
        [TestCase("OFF")]
        [TestCase("operate,closed")]
        [TestCase("helper off")]
        public void Closed_IsReadable_AndOpensNothing(string text)
        {
            // 「閉じる」と一緒に力を書いた印は、閉じる方を採る。
            Assert.That(AgentOpenFlag.TryParse(text, WithHelper(), out AgentAbilities abilities, out string word), Is.True);
            Assert.That(abilities, Is.EqualTo(AgentAbilities.None));
            Assert.That(word, Is.EqualTo("closed"));
        }

        /// <summary>
        /// Test: allows, needs any one, and no needs is always.
        /// 名前のとおりのことを確かめる。
        /// </summary>
        [Test]
        public void Allows_NeedsAnyOne_AndNoNeedsIsAlways()
        {
            AgentAbilityCatalog catalog = WithHelper();
            Assert.That(Helper.Allows(AgentAbilities.None), Is.True, "hello はいつも使える");
            Assert.That(Helper.Allows(AgentAbilities.Look), Is.False, "試しの力だけでは A の画面を読めない");
            Assert.That(Helper.Allows(catalog.SurfaceSignalReaders), Is.True, "events と wait は、表の合図を読める力でも使える");
            Assert.That(Helper.Allows(AgentAbilities.Back), Is.False, "試しの力だけでは裏を使えない");
            Assert.That(AgentAbilities.None.Allows(AgentAbilities.Look), Is.False);
            Assert.That(BaseOnly().SurfaceSignalReaders, Is.EqualTo(AgentAbilities.Look), "土台だけなら、表の合図を読めるのは look だけ");
        }

        /// <summary>
        /// Test: endpoint file, is separate, only when the solo ability is alone.
        /// 名前のとおりのことを確かめる。
        /// </summary>
        [Test]
        public void EndpointFile_IsSeparate_OnlyWhenTheSoloAbilityIsAlone()
        {
            // 専用のつなぎ先を持つ力だけの窓口は、別の名前に書く。ふだんの橋が、その窓口へつながないため。
            AgentAbilityCatalog catalog = WithHelper();
            Assert.That(catalog.EndpointFileNameFor(Helper), Is.EqualTo(HelperFile));
            Assert.That(catalog.IsSoloEndpoint(Helper), Is.True);
            Assert.That(catalog.EndpointFileNameFor(Helper | AgentAbilities.Self), Is.EqualTo(AgentEndpointFile.DefaultFileName));
            Assert.That(catalog.EndpointFileNameFor(AgentAbilities.Look | AgentAbilities.Back), Is.EqualTo(AgentEndpointFile.DefaultFileName));
            Assert.That(catalog.EndpointFileNameFor(catalog.All), Is.EqualTo(AgentEndpointFile.DefaultFileName));
            Assert.That(catalog.IsSoloEndpoint(catalog.All), Is.False);
            Assert.That(catalog.EndpointFileNames(), Is.EqualTo(new[] { AgentEndpointFile.DefaultFileName, HelperFile }));
            Assert.That(BaseOnly().EndpointFileNames(), Is.EqualTo(new[] { AgentEndpointFile.DefaultFileName }));
        }

        /// <summary>
        /// Test: wire list, is sorted by name, and skips unregistered bits.
        /// 名前のとおりのことを確かめる。
        /// </summary>
        [Test]
        public void WireList_IsSortedByName_AndSkipsUnregisteredBits()
        {
            AgentAbilityCatalog catalog = WithHelper();
            Assert.That(catalog.ToWireList(catalog.All), Is.EqualTo(new[] { "back", "helper", "look", "self" }));
            Assert.That(BaseOnly().ToWireList(AgentAbilities.Look | Helper), Is.EqualTo(new[] { "look" }));
        }

        /// <summary>
        /// Test: register, same def twice, is one, but clashes are loud.
        /// 名前のとおりのことを確かめる。
        /// </summary>
        [Test]
        public void Register_SameDefTwice_IsOne_ButClashesAreLoud()
        {
            AgentAbilityCatalog catalog = WithHelper();
            int count = catalog.Defs.Count;
            catalog.Register(new AgentAbilityDef("helper", Helper, soloEndpointFileName: HelperFile, readsSurfaceSignals: true));
            Assert.That(catalog.Defs.Count, Is.EqualTo(count), "同じ中身の登録し直しは 1 つに数える（Editor と Play の頭の両方で呼ぶため）");

            Assert.Throws<InvalidOperationException>(() => catalog.Register(new AgentAbilityDef("helper", Helper)), "同じ名前で中身が違う");
            Assert.Throws<InvalidOperationException>(() => catalog.Register(new AgentAbilityDef("other", Helper)), "位が重なる");
            Assert.Throws<InvalidOperationException>(
                () => catalog.Register(new AgentAbilityDef("other", (AgentAbilities)(1 << 5), soloEndpointFileName: HelperFile)), "専用のつなぎ先が重なる");
            Assert.Throws<ArgumentException>(() => new AgentAbilityDef("operate", (AgentAbilities)(1 << 5)), "全部を開く語は名前にできない");
            Assert.Throws<ArgumentException>(() => new AgentAbilityDef("closed", (AgentAbilities)(1 << 5)), "閉じる語は名前にできない");
            Assert.Throws<ArgumentException>(() => new AgentAbilityDef("two", (AgentAbilities)((1 << 5) | (1 << 6))), "位は 1 つだけ");
            Assert.Throws<ArgumentException>(
                () => new AgentAbilityDef("same", (AgentAbilities)(1 << 5), soloEndpointFileName: AgentEndpointFile.DefaultFileName), "ふだんの名前は専用にできない");

            catalog.ResetToBuiltIns();
            Assert.That(catalog.TryParseWord("helper", out _), Is.False, "土台だけに戻すと、登録した力は読めない");
        }

        /// <summary>
        /// Test: command line, reads the word after the switch.
        /// 名前のとおりのことを確かめる。
        /// </summary>
        [Test]
        public void CommandLine_ReadsTheWordAfterTheSwitch()
        {
            AgentAbilityCatalog catalog = WithHelper();
            Assert.That(AgentOpenFlag.TryReadCommandLine(new[] { "Unity.exe", "-agentApi", "look" }, catalog, out AgentAbilities look, out string word), Is.True);
            Assert.That(look, Is.EqualTo(AgentAbilities.Look | AgentAbilities.Back));
            Assert.That(word, Is.EqualTo("look"));

            Assert.That(AgentOpenFlag.TryReadCommandLine(new[] { "game.exe", "-AGENTAPI", "helper" }, catalog, out AgentAbilities helper, out _), Is.True);
            Assert.That(helper, Is.EqualTo(Helper));

            Assert.That(AgentOpenFlag.TryReadCommandLine(new[] { "game.exe", "-agentApi", "closed" }, catalog, out AgentAbilities closed, out string closedWord), Is.True);
            Assert.That(closed, Is.EqualTo(AgentAbilities.None));
            Assert.That(closedWord, Is.EqualTo("closed"));
        }

        /// <summary>
        /// Test: command line, switch without a readable word, closes.
        /// 名前のとおりのことを確かめる。
        /// </summary>
        [Test]
        public void CommandLine_SwitchWithoutAReadableWord_Closes()
        {
            // 引数が在れば印のファイルより引数を採る（true）。ただし読める語が続かなければ閉じる。
            // ⚠ 配列を TestCase に渡すと、NUnit が中身を引数の並びと読むので、ここで回す。
            string[][] cases =
            {
                new[] { "game.exe", "-agentApi" },
                new[] { "game.exe", "-agentApi", "-batchmode" },
                new[] { "game.exe", "-agentApi", "bogus" },
                new[] { "game.exe", "-agentApi", "helper" },
            };
            foreach (string[] args in cases)
            {
                string label = string.Join(" ", args);
                Assert.That(AgentOpenFlag.TryReadCommandLine(args, BaseOnly(), out AgentAbilities abilities, out string word), Is.True, label);
                Assert.That(abilities, Is.EqualTo(AgentAbilities.None), label);
                Assert.That(word, Is.Null, label);
            }
        }

        /// <summary>
        /// Test: command line, without the switch, leaves it to the flag file.
        /// 名前のとおりのことを確かめる。
        /// </summary>
        [Test]
        public void CommandLine_WithoutTheSwitch_LeavesItToTheFlagFile()
        {
            Assert.That(AgentOpenFlag.TryReadCommandLine(new[] { "game.exe", "-batchmode" }, BaseOnly(), out AgentAbilities abilities, out _), Is.False);
            Assert.That(abilities, Is.EqualTo(AgentAbilities.None));
            Assert.That(AgentOpenFlag.TryReadCommandLine(null, BaseOnly(), out _, out _), Is.False);
        }

        /// <summary>
        /// Test: tags, stay for the same world, and go stale in the next.
        /// 名前のとおりのことを確かめる。
        /// </summary>
        [Test]
        public void Tags_StayForTheSameWorld_AndGoStaleInTheNext()
        {
            // 札は同じ世界の間は同じ字。世界を作り直すと前の札は戻せない（別の D を指さないため）。
            var table = new AgentTagTable(new byte[] { 1, 2, 3, 4 });
            string tag = table.TagFor("npc-01", 1);
            Assert.That(tag, Does.Match("^[abcdefghijkmnpqrstuvwxyz23456789]{6}$"));
            Assert.That(table.TagFor("npc-01", 1), Is.EqualTo(tag));
            Assert.That(table.TryResolve(" " + tag.ToUpperInvariant() + " ", 1, out string id), Is.True);
            Assert.That(id, Is.EqualTo("npc-01"));

            Assert.That(table.TryResolve(tag, 2, out _), Is.False, "世界が替わったら、前の札は戻せない");
            Assert.That(table.TryResolve("aaaaaa", 2, out _), Is.False, "出していない札は、形が合っていても戻さない");
            Assert.That(table.TagFor(null, 2), Is.Null);
        }
    }
}
