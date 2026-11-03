using System;
using System.IO;
using UnityEngine;

namespace Monster.Balloon.AutomataSystem
{
    /// <summary>
    /// Reads the flag that decides whether the endpoint opens and which abilities it opens.
    /// 窓口を開くかを決める印を読む。既定は閉じている。
    /// <para>
    /// Editor＝<c>UserSettings/AgentApi/open</c> のファイルか、起動の引数 <c>-agentApi</c> の時だけ開く。
    /// ⭐ <c>UserSettings/</c> は git にも同期にも載らない。だから、AI が使う Editor で印を置いても、人が使う別の Editor は開かない。
    /// 開発ビルド＝起動の引数だけ。製品版＝ここでは開かない（設定画面で開く）。
    /// ⭐ 印の中身は厳しく読む。空・読めない・知らない語・値の無い <c>-agentApi</c> は、全部「閉じる」。
    ///    前は全部を「操作も」と読んでいたので、狭い力だけの印でも裏の道まで開くおそれがあった。
    /// ⭐ 分けるのはビルド（開発ビルド＝引数だけ・製品版＝閉じる）。Editor では、どの Editor も同じ力を開ける
    ///    （Editor ごとには分けない＝人が使う Editor と AI が使う Editor で、開く力を変えない）。
    /// ⭐ 読める語は <see cref="AgentAbilityCatalog"/> が決める（土台の語と、ゲームが登録した力の名前）。
    ///    ⚠ ゲームの力の登録は、窓口が印を読む AfterSceneLoad より前に済ませる（Play の頭で登録簿は土台だけに戻る）。
    /// </para>
    /// </summary>
    public static class AgentOpenFlag
    {
        /// <summary>
        /// The command-line switch that opens the endpoint.
        /// 起動の引数。続けて力の組の名前を書く（例 "-agentApi look"）。名前が無ければ閉じる。
        /// </summary>
        public const string CommandLineSwitch = "-agentApi";

        /// <summary>
        /// The word that keeps the endpoint closed.
        /// 閉じる語（ほかの語と混ざっていても、閉じる方を採る）。
        /// </summary>
        public const string ClosedWord = "closed";

        /// <summary>
        /// Another word that keeps the endpoint closed.
        /// 閉じる語の別の書き方。
        /// </summary>
        public const string OffWord = "off";

        /// <summary>
        /// The path of the flag file that the Editor reads.
        /// Editor の印のファイル（中身は "operate"・"look" など。<see cref="TryParse(string, AgentAbilityCatalog, out AgentAbilities, out string)"/> を見る）。
        /// </summary>
        public static string EditorFlagPath =>
            Path.Combine(ProjectRoot, "UserSettings", "AgentApi", "open");

        private static string ProjectRoot => Path.GetDirectoryName(Application.dataPath) ?? ".";

        /// <summary>開く力の組を読む。閉じている時は <see cref="AgentAbilities.None"/>。</summary>
        /// <param name="word">読めた時の印の字（例 "operate"・"look"・"look+self"）。閉じている時は null。</param>
        internal static AgentAbilities Read(out string word)
        {
            word = null;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (TryReadCommandLine(out AgentAbilities fromArgs, out word))
            {
                return fromArgs;
            }
#endif
#if UNITY_EDITOR
            string flag = EditorFlagPath;
            if (File.Exists(flag))
            {
                if (!TryReadAllText(flag, out string text))
                {
                    Debug.LogWarning("[AgentApi] 開く印を読めないので、窓口は開かない。印＝" + flag);
                    return AgentAbilities.None;
                }

                if (!TryParse(text, out AgentAbilities abilities, out word))
                {
                    Debug.LogWarning("[AgentApi] 開く印の中身が空か、知らない語なので、窓口は開かない（読める語＝"
                        + string.Join("・", AgentAbilityCatalog.Current.Words()) + "・" + ClosedWord + "）。"
                        + "メニュー Tools/Agent API で置き直す。印＝" + flag);
                    return AgentAbilities.None;
                }

                return abilities;
            }
#endif
            return AgentAbilities.None;
        }

        /// <summary>
        /// 印の中身を、窓口の登録簿（<see cref="AgentAbilityCatalog.Current"/>）で力の組にする。
        /// </summary>
        internal static bool TryParse(string text, out AgentAbilities abilities, out string word) =>
            TryParse(text, AgentAbilityCatalog.Current, out abilities, out word);

        /// <summary>
        /// Tries to read the text of the flag as an ability set.
        /// 印の中身を力の組にする。語は「,」「+」「空白」「改行」で区切って足せる（例 "look,self"）。
        /// <para>
        /// 空・知らない語が 1 つでもある＝読めない（false・閉じる）。"closed"・"off" だけ＝読めた・閉じる（true・None）。
        /// </para>
        /// </summary>
        /// <param name="catalog">読める語を決める登録簿。</param>
        /// <param name="word">読めた時の正規の字（小文字の語を "+" でつなぐ）。読めない時は null。</param>
        public static bool TryParse(string text, AgentAbilityCatalog catalog, out AgentAbilities abilities, out string word)
        {
            if (catalog == null)
            {
                throw new ArgumentNullException(nameof(catalog));
            }

            abilities = AgentAbilities.None;
            word = null;
            string[] parts = (text ?? string.Empty).Split(
                new[] { ',', '+', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                return false;
            }

            bool closed = false;
            var normalized = new string[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i].Trim().ToLowerInvariant();
                normalized[i] = part;
                if (part == ClosedWord || part == OffWord)
                {
                    closed = true;
                    continue;
                }

                if (!catalog.TryParseWord(part, out AgentAbilities one))
                {
                    abilities = AgentAbilities.None;
                    return false;
                }

                abilities |= one;
            }

            if (closed)
            {
                // 「閉じる」と一緒に力を書いた印は、閉じる方を採る（開く方へは倒さない）。
                abilities = AgentAbilities.None;
                word = ClosedWord;
                return true;
            }

            word = string.Join("+", normalized);
            return true;
        }

        private static bool TryReadCommandLine(out AgentAbilities abilities, out string word)
        {
            string[] args;
            try
            {
                args = Environment.GetCommandLineArgs();
            }
            catch (Exception)
            {
                // スマホなど、引数を読めない所では印が無いものとする。
                abilities = AgentAbilities.None;
                word = null;
                return false;
            }

            return TryReadCommandLine(args, out abilities, out word);
        }

        /// <summary>
        /// 起動の引数の並びから開く力を読む（テストは並びを渡す）。<c>-agentApi</c> が在れば true（閉じる時も）。
        /// <c>-agentApi</c> の次に読める語が無い（無い・次も <c>-</c> で始まる・知らない語）時は、閉じる（None）。
        /// </summary>
        internal static bool TryReadCommandLine(string[] args, out AgentAbilities abilities, out string word) =>
            TryReadCommandLine(args, AgentAbilityCatalog.Current, out abilities, out word);

        /// <summary>
        /// Tries to read the ability set from the command-line arguments.
        /// 起動の引数の並びから、<paramref name="catalog"/> の語で開く力を読む（<see cref="TryReadCommandLine(string[], out AgentAbilities, out string)"/> と同じ決まり）。
        /// </summary>
        public static bool TryReadCommandLine(string[] args, AgentAbilityCatalog catalog, out AgentAbilities abilities, out string word)
        {
            if (catalog == null)
            {
                throw new ArgumentNullException(nameof(catalog));
            }

            abilities = AgentAbilities.None;
            word = null;
            if (args == null)
            {
                return false;
            }

            for (int i = 0; i < args.Length; i++)
            {
                if (!string.Equals(args[i], CommandLineSwitch, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string next = i + 1 < args.Length ? args[i + 1] : null;
                if (next == null || next.StartsWith("-", StringComparison.Ordinal) || !TryParse(next, catalog, out abilities, out word))
                {
                    abilities = AgentAbilities.None;
                    word = null;
                    Debug.LogWarning("[AgentApi] 起動の引数 " + CommandLineSwitch + " に、読める力の名前が続いていないので、窓口は開かない（例 "
                        + CommandLineSwitch + " look）。");
                }

                // ⭐ 引数があれば、印のファイルより引数を採る（閉じる時も）。
                return true;
            }

            return false;
        }

        private static bool TryReadAllText(string path, out string text)
        {
            try
            {
                text = File.ReadAllText(path);
                return true;
            }
            catch (Exception)
            {
                text = null;
                return false;
            }
        }
    }
}
