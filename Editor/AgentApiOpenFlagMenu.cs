using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Monster.Balloon.AutomataSystem.Editor
{
    /// <summary>
    /// Editor menu items that write the open flag.
    /// 窓口を開く印（<c>UserSettings/AgentApi/open</c>）を置く・外すメニュー。次の Play から効く。
    /// <para>
    /// ⭐ 印は git にも同期にも載らない。だから、この Editor で印を置いても、ほかの Editor は開かない。
    /// ⭐ 土台が出すのは operate・look・Close だけ。ゲームが登録した力の項は、ゲームの Editor の係が
    ///    <see cref="MenuRoot"/> の下に足し、<see cref="WriteFlag"/> で印を置く（並びは <see cref="FirstGamePriority"/> から）。
    /// </para>
    /// </summary>
    public static class AgentApiOpenFlagMenu
    {
        /// <summary>
        /// The root of the menu path.
        /// メニューの根（ゲームの項もこの下に足す）。
        /// </summary>
        public const string MenuRoot = "Tools/Agent API/";

        /// <summary>
        /// The menu priority where game-added items start.
        /// ゲームの「Open on next Play (…)」の項に使う並びの番号（土台の operate・look の次・Close の前）。
        /// </summary>
        public const int FirstGamePriority = 1002;

        /// <summary>
        /// The menu priority of the Play actions.
        /// ゲームの、Play 中だけ使う項に使う並びの番号（Close の後）。
        /// </summary>
        public const int PlayActionPriority = 1004;

        private const string OperateItem = MenuRoot + "Open on next Play (operate)";
        private const string LookItem = MenuRoot + "Open on next Play (look only)";
        private const string CloseItem = MenuRoot + "Close";

        [MenuItem(OperateItem, false, 1000)]
        private static void OpenOperate() => WriteFlag(AgentAbilityCatalog.AllWord);

        [MenuItem(LookItem, false, 1001)]
        private static void OpenLook() => WriteFlag(AgentAbilityCatalog.LookDef.Name);

        [MenuItem(CloseItem, false, 1003)]
        private static void Close()
        {
            string path = AgentOpenFlag.EditorFlagPath;
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            Debug.Log("[AgentApi] 開く印を外した。次の Play から窓口は開かない。");
        }

        [MenuItem(CloseItem, true)]
        private static bool CanClose() => File.Exists(AgentOpenFlag.EditorFlagPath);

        /// <summary>
        /// Writes a word into the open flag file.
        /// 開く印を置く（次の Play から効く）。
        /// ⚠ 今の登録簿で読めない語は置かない（例外で知らせる）＝読めない印を置くと、次の Play で黙って閉じるため。
        /// </summary>
        public static void WriteFlag(string word)
        {
            if (!AgentOpenFlag.TryParse(word, AgentAbilityCatalog.Current, out _, out _))
            {
                throw new ArgumentException(
                    "読めない語なので、開く印を置かない（読める語＝" + string.Join("・", AgentAbilityCatalog.Current.Words()) + "）: " + word,
                    nameof(word));
            }

            string path = AgentOpenFlag.EditorFlagPath;
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(path, word);
            Debug.Log("[AgentApi] 開く印を置いた（" + word + "）。次の Play から窓口が開く。印＝" + path);
        }
    }
}
