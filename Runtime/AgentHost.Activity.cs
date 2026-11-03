using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Monster.Balloon.AutomataSystem
{
    /// <summary>
    /// The part of AgentHost that counts what happened between test lines.
    /// Host のうち、ゲームの試験の橋が「行の間に何が起きたか」を数える口と、AI が出した証拠を確かめる口。
    /// <para>
    /// ⭐ 数える元は、裏の帳面（表の合図も全部写っている）と、裏のトークンで来た頼みの数（<see cref="AgentHostSession.BackCalls"/>）。
    ///    どれも窓口が数えた値＝AI が書き込む値ではない。
    /// ⚠ 主スレッドで呼ぶこと（帳面は鍵で守られているが、頼みの数と撮った枚数は主スレッドだけが触る）。
    /// </para>
    /// </summary>
    public static partial class AgentHost
    {
        /// <summary>一度に読む帳面の行の数。</summary>
        private const int ActivityReadChunk = 512;

        /// <summary>証拠の ID の長さの上限（playId 6 字＋区切り＋番号で足りる）。</summary>
        private const int MaxEvidenceLength = 48;

        /// <summary>
        /// Takes a mark to count from.
        /// 今から数える印（主スレッド）。窓口が閉じている・裏の帳面が無い時は、取れなかった印（IsSet が false）。
        /// </summary>
        public static AgentActivityMark MarkActivity()
        {
            AgentHostSession s = session;
            if (s == null || s.IsClosed || s.Signals.Back == null)
            {
                return default;
            }

            return new AgentActivityMark(s.PlayId, s.Signals.Surface.LastSeq, s.Signals.Back.LastSeq, s.BackCalls, s.Captures.Count);
        }

        /// <summary>
        /// Counts what happened since the mark.
        /// 印から今までに起きた事を数える（主スレッド）。印が無い・窓口が閉じた・Play が替わった時は Known が false。
        /// ⭐ 帳面の輪（4096 行）からあふれた時は Truncated が true（数は少なめ・読む側はそう書く）。
        /// </summary>
        public static AgentActivity ActivitySince(AgentActivityMark mark)
        {
            var result = new AgentActivity();
            AgentHostSession s = session;
            if (!mark.IsSet || s == null || s.Signals.Back == null || !string.Equals(s.PlayId, mark.PlayId, StringComparison.Ordinal))
            {
                return result;
            }

            result.Known = true;
            result.BackCalls = (int)Math.Max(0L, Math.Min(int.MaxValue, s.BackCalls - mark.BackCalls));

            AgentSignalLedger back = s.Signals.Back;
            var entries = new List<AgentSignalEntry>(ActivityReadChunk);
            long after = mark.BackSeq;
            long last = back.LastSeq;
            while (after < last)
            {
                long lost = back.ReadAfter(after, ActivityReadChunk, entries, out long readTo);
                if (lost > 0)
                {
                    result.Truncated = true;
                }

                foreach (AgentSignalEntry entry in entries)
                {
                    CountActivity(entry, result);
                }

                if (readTo <= after)
                {
                    break;
                }

                after = readTo;
            }

            return result;
        }

        /// <summary>
        /// Counts the back-route calls since the mark, or null when unknown.
        /// 印から今までの「裏のトークンで来た頼み」の数だけを返す（帳面を読まない＝軽い・主スレッド）。印が使えなければ null。
        /// ⭐ 試験の橋の decide が呼ぶ（要るのはこの数だけ＝帳面を 2 回読み直していた）。
        /// </summary>
        public static int? BackCallsSince(AgentActivityMark mark)
        {
            AgentHostSession s = session;
            if (!mark.IsSet || s == null || !string.Equals(s.PlayId, mark.PlayId, StringComparison.Ordinal))
            {
                return null;
            }

            return (int)Math.Max(0L, Math.Min(int.MaxValue, s.BackCalls - mark.BackCalls));
        }

        /// <summary>
        /// Checks that an evidence ID (a capture number) is real.
        /// AI が出した証拠の ID が、この Play で印より後に出た物かを確かめる（主スレッド）。
        /// 形: <c>&lt;playId&gt;:&lt;番号&gt;</c>＝表の合図、<c>&lt;playId&gt;:b&lt;番号&gt;</c>＝裏の合図、<c>&lt;playId&gt;-&lt;番号&gt;</c>＝撮った絵。
        /// <para>
        /// ⭐ 印より前の物（前の行の合図・前の行で撮った絵）は、この行の証拠にしない。まだ無い番号も断る（作った番号を通さない）。
        /// ⭐ <paramref name="until"/>（決める段に入った時の印）があれば、それより後の物も断る＝機械の結果を見てから遊び直して集めた物は、
        ///    証拠にしない。
        /// ⭐ 番号の頭の 0 は断る（<c>:7</c> と <c>:007</c> を別の証拠にしない）。撮った絵は、絵のファイルが在る物だけ
        ///    （番号は撮り始めた時に進むので、撮れなかった番号も数の上では通ってしまう）。橋が自分で書く試験の合図も断る。
        /// ⚠ 中身までは見ない（その合図や絵が項目に合っているかは、記録を読む人が見る）。
        /// </para>
        /// </summary>
        /// <param name="why">断る訳（bad_form・no_mark・other_play・before_row・not_yet・after_decide・no_picture・not_observation）。通る時は null。</param>
        public static bool CheckEvidence(string id, AgentActivityMark since, AgentActivityMark until, out string why)
        {
            AgentHostSession s = session;
            if (!since.IsSet || s == null || !string.Equals(s.PlayId, since.PlayId, StringComparison.Ordinal))
            {
                why = "no_mark";
                return false;
            }

            if (string.IsNullOrEmpty(id) || id.Length > MaxEvidenceLength)
            {
                why = "bad_form";
                return false;
            }

            bool bounded = until.IsSet && string.Equals(until.PlayId, since.PlayId, StringComparison.Ordinal);
            int colon = id.IndexOf(':');
            if (colon > 0)
            {
                string rest = id.Substring(colon + 1);
                bool backSide = rest.StartsWith("b", StringComparison.Ordinal);
                if (backSide)
                {
                    rest = rest.Substring(1);
                }

                if (!TryReadNumber(rest, out long seq))
                {
                    why = "bad_form";
                    return false;
                }

                long from = backSide ? since.BackSeq : since.SurfaceSeq;
                long upTo = !bounded ? long.MaxValue : backSide ? until.BackSeq : until.SurfaceSeq;
                AgentSignalLedger ledger = backSide ? s.Signals.Back : s.Signals.Surface;
                if (!CheckNumber(id.Substring(0, colon), s.PlayId, seq, from, ledger?.LastSeq ?? 0, upTo, out why))
                {
                    return false;
                }

                if (backSide && IsToolSignal(s.Signals, ledger, seq))
                {
                    why = "not_observation";
                    return false;
                }

                return true;
            }

            int dash = id.LastIndexOf('-');
            if (dash > 0 && TryReadNumber(id.Substring(dash + 1), out long number) && number <= int.MaxValue)
            {
                long upTo = bounded ? until.Captures : long.MaxValue;
                if (!CheckNumber(id.Substring(0, dash), s.PlayId, number, since.Captures, s.Captures.Count, upTo, out why))
                {
                    return false;
                }

                if (!File.Exists(Path.Combine(s.Captures.Folder, id + ".png")))
                {
                    why = "no_picture";
                    return false;
                }

                return true;
            }

            why = "bad_form";
            return false;
        }

        /// <summary>番号の字（数字だけ・1 以上・頭に 0 が無い）を読む。</summary>
        private static bool TryReadNumber(string text, out long number)
        {
            number = 0;
            return !string.IsNullOrEmpty(text) && text[0] != '0'
                && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number > 0;
        }

        /// <summary>
        /// 裏の帳面のその番号が、確かめの道具が自分で書いた合図か（輪からあふれて読めなければ false）。
        /// 見分けは、合図の元が <see cref="AgentSignalKind.Observation"/> を false にして名乗った名前で決める。
        /// </summary>
        private static bool IsToolSignal(AgentSignalHub hub, AgentSignalLedger ledger, long seq)
        {
            if (ledger == null)
            {
                return false;
            }

            var one = new List<AgentSignalEntry>(1);
            ledger.ReadAfter(seq - 1, 1, one, out _);
            return one.Count == 1 && one[0].Seq == seq && one[0].Kind != null && !hub.IsObservation(one[0].Kind);
        }

        /// <summary>同じ Play の、印より後で、決める段より前で、もう出ている番号か。</summary>
        private static bool CheckNumber(string playId, string nowPlayId, long number, long from, long last, long upTo, out string why)
        {
            if (!string.Equals(playId, nowPlayId, StringComparison.Ordinal))
            {
                why = "other_play";
                return false;
            }

            if (number <= from)
            {
                why = "before_row";
                return false;
            }

            if (number > last)
            {
                why = "not_yet";
                return false;
            }

            if (number > upTo)
            {
                why = "after_decide";
                return false;
            }

            why = null;
            return true;
        }

        private static void CountActivity(AgentSignalEntry entry, AgentActivity into)
        {
            switch (entry.Kind)
            {
                case "reflex.tripped":
                    into.ReflexTripped++;
                    break;

                case "reflex.acted":
                    into.ReflexActed++;
                    break;

                case "act.done":
                    // ⭐ 反射が自分で押した命令（reflex の欄がある）は、AI の押し方に数えない（反射の数は reflex.acted で数える）。
                    if (ReadWord(entry.DataJson, "reflex") != null)
                    {
                        break;
                    }

                    into.Acts++;
                    string via = ReadWord(entry.DataJson, "via");
                    if (string.Equals(via, AgentActResult.ViaPad, StringComparison.Ordinal))
                    {
                        into.ActsByPad++;
                    }
                    else if (string.Equals(via, AgentActResult.ViaDirect, StringComparison.Ordinal))
                    {
                        into.ActsDirect++;
                    }

                    break;
            }
        }

        /// <summary>合図の中身から字の欄を 1 つ読む（無い・読めない時は null）。</summary>
        private static string ReadWord(string json, string name)
        {
            if (string.IsNullOrEmpty(json) || json.IndexOf(name, StringComparison.Ordinal) < 0)
            {
                return null;
            }

            try
            {
                JToken token = JObject.Parse(json)[name];
                return token != null && token.Type == JTokenType.String ? token.Value<string>() : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
