using System;

namespace Monster.Balloon.AutomataSystem
{
    /// <summary>
    /// The part of AgentHost that changes the Game view size.
    /// Host の、画面の大きさ（Editor の Game ビューの寸法）を替える口（撮る前の縦と横の切り替え）。
    /// <para>
    /// ⭐ 書き口と戻し口は Editor の係（<c>AgentApiEditorHooks</c>）が、スクリプトを読み込んだ時に 1 回だけ差す。
    ///    ビルドでは差さない＝<c>/v1/back/screen</c> は 501 editor_only で断る（遊ぶ人の画面の大きさは、AI が替えない）。
    /// ⭐ 戻し口は、この Play で AI が初めて替える前の寸法へ戻す。AI が替えたまま Play を抜けた時も、Editor の係が戻す。
    /// </para>
    /// </summary>
    public static partial class AgentHost
    {
        private static Func<int, int, string> screenSizeWriter;
        private static Func<string> screenSizeRestorer;

        /// <summary>画面の大きさを替えられるか（Editor の係が書き口を差した時だけ）。</summary>
        internal static bool CanWriteScreenSize => screenSizeWriter != null && screenSizeRestorer != null;

        /// <summary>
        /// 画面の大きさを替える口と、元へ戻す口を差す（Editor の係が差す。ビルドでは差さない）。
        /// 書き口は (幅, 高さ) を受けて、替えられたら null・替えられなければ訳の字を返す。戻し口は、戻せたら null・替えていなければ "not_changed"。
        /// </summary>
        internal static void SetScreenSizer(Func<int, int, string> writer, Func<string> restorer)
        {
            screenSizeWriter = writer;
            screenSizeRestorer = restorer;
        }

        /// <summary>画面の大きさを替える（主スレッド）。替えられたら null。⚠ 寸法（Screen.width）は、次のフレームより後で替わる。</summary>
        internal static string WriteScreenSize(int width, int height)
        {
            Func<int, int, string> writer = screenSizeWriter;
            if (writer == null)
            {
                return "editor_only";
            }

            try
            {
                return writer(width, height);
            }
            catch (Exception e)
            {
                ReportOnce("screen-write", "[AgentApi] Game ビューの大きさを替えられなかった: " + e);
                return "size_failed";
            }
        }

        /// <summary>この Play で AI が替える前の大きさへ戻す（主スレッド）。戻せたら null・替えていなければ "not_changed"。</summary>
        internal static string RestoreScreenSize()
        {
            Func<string> restorer = screenSizeRestorer;
            if (restorer == null)
            {
                return "editor_only";
            }

            try
            {
                return restorer();
            }
            catch (Exception e)
            {
                ReportOnce("screen-restore", "[AgentApi] Game ビューの大きさを元へ戻せなかった: " + e);
                return "size_failed";
            }
        }
    }
}
