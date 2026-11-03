using System;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;

namespace Monster.Balloon.AutomataSystem
{
    /// <summary>
    /// Editor が止まっているか・掛け金・何コマか進めている途中かを、<c>Library/AgentApi/state.json</c> に書く。
    /// <para>
    /// ⭐ 外の道具（Editor を外から打つ道具のフック）が、撮影の前にこれを読む。
    ///    止まった Editor で撮影を打つと、撮影は永久に返らないことがある。
    /// ⭐ 書くのは Editor の係（<c>AgentApiEditorHooks</c>）。窓口が開いていない Play（開く前・Play の途中でスクリプトを読み直して窓口が閉じた後）でも書く。
    ///    止まっているかは Editor の持ち物で、窓口の持ち物ではないため（窓口が閉じると記録も消え、フックが撮影を通した）。
    /// ⭐ フックは、pid の Unity が今も生きているかを自分で確かめる（落ちた Editor の残りで止めない）。Play を抜ける時に消す。
    /// ⚠ 書き替えは時々失敗する（書いた直後のファイルを、ほかの係がまだ開いている＝Play で 1 回起きた・だれかは分かっていない）。
    ///   失敗すると古い中身が残る＝止まった直後なら「動いている」が残り、フックが撮影を通してしまう。∴ 書いた側が 0.1 秒後に今の様子を書き直す（<see cref="Write"/> の答え）。
    /// ⚠ 書けなくても赤にしない（赤は AI が使う Editor を Error Pause で止め、赤いログの守りも働く）。続けて失敗した時だけ黄を 1 回出す。
    /// </para>
    /// </summary>
    internal static class AgentStateFile
    {
        public const string FileName = "state.json";

        /// <summary>続けてこの回数失敗したら、黄を出す（書き直しは 0.1 秒おき＝約 2 秒）。</summary>
        private const int WarnAfterFailures = 20;

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
        private static bool warned;
        private static bool noted;
        private static int failuresInRow;

        /// <summary>Editor の記録の道（<c>&lt;プロジェクト&gt;/Library/AgentApi/state.json</c>・つなぎ先ファイルの隣）。⚠ 主スレッドで読む。</summary>
        public static string EditorPath =>
            Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "Library", "AgentApi", FileName);

        /// <summary>知らせを 1 回だけにする印を戻す（Play に入るたび＝Domain Reload を切っているので、戻さないと次の Play で黙る）。</summary>
        public static void ResetWarnings()
        {
            warned = false;
            noted = false;
            failuresInRow = 0;
        }

        /// <param name="playing">Play 中か（Play の外の一時停止の印は、撮影を止めない）。</param>
        /// <param name="pausedBy">止まっているか（"none"＝動いている・"editor"・"reflex"・"agent"）。</param>
        /// <param name="stepping">止めたまま何コマか進めている途中か（動いていても、進め終えると止まる＝撮影を止める）。</param>
        /// <param name="playId">開いている窓口の playId（窓口が無ければ null）。</param>
        /// <param name="latch">掛け金を掛けた守り（無ければ null）。</param>
        /// <returns>書けたか（false なら、書いた側が少し後で書き直す）。</returns>
        public static bool Write(
            string path, int processId, bool playing, string pausedBy, bool stepping, string playId, string latch, int frame)
        {
            var sw = new StringWriter(CultureInfo.InvariantCulture);
            using (var w = new JsonTextWriter(sw) { Formatting = Formatting.Indented })
            {
                w.WriteStartObject();
                w.WritePropertyName("pid");
                w.WriteValue(processId);
                w.WritePropertyName("playing");
                w.WriteValue(playing);
                w.WritePropertyName("paused");
                w.WriteValue(pausedBy != "none" && pausedBy != "unknown");
                w.WritePropertyName("by");
                w.WriteValue(pausedBy);
                w.WritePropertyName("stepping");
                w.WriteValue(stepping);
                w.WritePropertyName("playId");
                w.WriteValue(playId);
                w.WritePropertyName("latch");
                w.WriteValue(latch);
                w.WritePropertyName("frame");
                w.WriteValue(frame);
                w.WritePropertyName("utc");
                w.WriteValue(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                w.WriteEndObject();
            }

            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                // ⭐ 書きかけを読ませない＝隣に書いてから入れ替える（フックは読めない時に「止まっていない」と読む）。
                string temp = path + ".tmp";
                File.WriteAllText(temp, sw.ToString(), Utf8NoBom);
                if (File.Exists(path))
                {
                    File.Replace(temp, path, null);
                }
                else
                {
                    File.Move(temp, path);
                }
            }
            catch (Exception e)
            {
                failuresInRow++;
                if (!noted)
                {
                    // 1 回の失敗は、少し後の書き直しで直る。どれだけ起きるかを知るために、白で 1 回だけ残す。
                    noted = true;
                    Debug.Log("[AgentApi] 止まっているかの記録（state.json）を書き替えられなかったので、少し後で書き直す（この Play では 1 回だけ言う）: " + e.Message);
                }

                if (!warned && failuresInRow >= WarnAfterFailures)
                {
                    warned = true;
                    Debug.LogWarning(
                        "[AgentApi] 止まっているかの記録（state.json）を " + failuresInRow + " 回続けて書けなかった（フックが古い中身を読む恐れ・この Play では 1 回だけ言う）: " + e.Message);
                }

                return false;
            }

            failuresInRow = 0;
            return true;
        }

        /// <summary>記録と、書きかけの隣のファイル（書き替えに失敗した時に残る）を消す。</summary>
        public static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                string temp = path + ".tmp";
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch (Exception e)
            {
                // 消せなくても、フックは書いた Unity が生きているかで見分ける。次の Play でも書き直す。
                Debug.LogWarning("[AgentApi] 止まっているかの記録（state.json）を消せなかった: " + e.Message);
            }
        }
    }
}
