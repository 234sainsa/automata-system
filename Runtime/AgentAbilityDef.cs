using System;
using System.IO;

namespace Monster.Balloon.AutomataSystem
{
    /// <summary>
    /// One ability: its word, its flag, and what it opens.
    /// 力 1 つの書き付け（名前・位・印の語で開く組・専用の窓口・表の合図を読めるか）。
    /// <para>
    /// ⭐ 土台は Look・Self・Back の書き付けを自分で持つ。ゲームは、自分の力をこの形で書き、<see cref="AgentAbilityCatalog.Register"/> で登録する。
    /// ⭐ 名前は 3 つの所で同じ字になる＝開く印の語・hello とつなぎ先ファイルの <c>abilities</c> の字・中の口の印の字。
    /// </para>
    /// </summary>
    public sealed class AgentAbilityDef
    {
        /// <summary>
        /// Creates an ability definition.
        /// 力の定義を作る。
        /// </summary>
        /// <param name="name">力の名前（小文字の英字と数字・例 "look"）。開く印の語にもなる。"operate"・"closed"・"off" は使えない。</param>
        /// <param name="flag">この力の位（1 つの位だけ）。</param>
        /// <param name="opens">印にこの名前を書いた時に開く組。None なら <paramref name="flag"/> だけ。必ず flag を含む。</param>
        /// <param name="soloEndpointFileName">
        /// この力だけを開いた窓口のつなぎ先のファイル名（例 "endpoint-x.json"）。null＝ふだんのつなぎ先（endpoint.json）に書く。
        /// ⭐ 名前を分けると、ふだんの橋（endpoint.json だけを読む）が、この力だけの窓口を拾わない。
        /// </param>
        /// <param name="readsSurfaceSignals">この力で、表の合図の道（events・wait）と hello の合図の名前を使えるか。</param>
        public AgentAbilityDef(
            string name,
            AgentAbilities flag,
            AgentAbilities opens = AgentAbilities.None,
            string soloEndpointFileName = null,
            bool readsSurfaceSignals = false)
        {
            if (!IsWellFormedName(name))
            {
                throw new ArgumentException("力の名前は、小文字の英字と数字だけで書く（operate・closed・off は使えない）: " + name, nameof(name));
            }

            int bits = (int)flag;
            if (bits <= 0 || (bits & (bits - 1)) != 0)
            {
                throw new ArgumentException("力の位は 1 つだけにする: " + name + "=" + bits, nameof(flag));
            }

            if (opens == AgentAbilities.None)
            {
                opens = flag;
            }

            if ((opens & flag) != flag)
            {
                throw new ArgumentException("印で開く組に、その力そのものが入っていない: " + name, nameof(opens));
            }

            if (soloEndpointFileName != null
                && (soloEndpointFileName.Length == 0
                    || soloEndpointFileName != Path.GetFileName(soloEndpointFileName)
                    || string.Equals(soloEndpointFileName, AgentEndpointFile.DefaultFileName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException(
                    "専用のつなぎ先は、ふだんの名前と違うファイル名だけで書く（道は書かない）: " + soloEndpointFileName, nameof(soloEndpointFileName));
            }

            this.Name = name;
            this.Flag = flag;
            this.Opens = opens;
            this.SoloEndpointFileName = soloEndpointFileName;
            this.ReadsSurfaceSignals = readsSurfaceSignals;
        }

        /// <summary>
        /// The word that names the ability.
        /// 力の名前（開く印の語・hello の abilities の字）。
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// The flag bit of the ability.
        /// この力の位。
        /// </summary>
        public AgentAbilities Flag { get; }

        /// <summary>
        /// The flags this ability opens (it may open more than its own bit).
        /// 印にこの名前を書いた時に開く組（例 look＝Look と Back）。
        /// </summary>
        public AgentAbilities Opens { get; }

        /// <summary>
        /// The endpoint file name used when this ability is opened alone, or null.
        /// この力だけを開いた窓口のつなぎ先のファイル名。null＝ふだんの名前。
        /// </summary>
        public string SoloEndpointFileName { get; }

        /// <summary>
        /// True when this ability reads surface signals.
        /// この力で、表の合図の道（events・wait）を使えるか。
        /// </summary>
        public bool ReadsSurfaceSignals { get; }

        /// <summary>
        /// Returns true when the other definition is exactly the same.
        /// 中身が全部同じ書き付けか（同じ物を 2 回登録した時に、黙って 1 つに数えるため）。
        /// </summary>
        public bool SameAs(AgentAbilityDef other) =>
            other != null
            && this.Name == other.Name
            && this.Flag == other.Flag
            && this.Opens == other.Opens
            && this.SoloEndpointFileName == other.SoloEndpointFileName
            && this.ReadsSurfaceSignals == other.ReadsSurfaceSignals;

        private static bool IsWellFormedName(string name)
        {
            if (string.IsNullOrEmpty(name)
                || name == AgentAbilityCatalog.AllWord
                || name == AgentOpenFlag.ClosedWord
                || name == AgentOpenFlag.OffWord)
            {
                return false;
            }

            foreach (char c in name)
            {
                if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
