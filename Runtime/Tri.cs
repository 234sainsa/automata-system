namespace Monster.Balloon.AutomataSystem
{
    /// <summary>
    /// A three-way value: yes, no, or unknown.
    /// 目の答えの 3 つの値（はい・いいえ・分からない）。
    /// <para>
    /// ⚠ 既定の値（0）は <see cref="Unknown"/> にしてある。係が欄を書き忘れても、答えは「はい」にならない。
    /// 目は、確かめられない物を「はい」と答えない（偽の OK を作らない）。
    /// </para>
    /// </summary>
    public enum Tri
    {
        Unknown = 0,
        Yes = 1,
        No = 2,
    }

    /// <summary>
    /// Helpers for Tri.
    /// <see cref="Tri"/> を窓口の字へ直す口と、bool から作る口。
    /// </summary>
    public static class TriExtensions
    {
        /// <summary>
        /// Returns the word used on the wire.
        /// 窓口の JSON に書く字（"yes"・"no"・"unknown"）を返す。
        /// </summary>
        public static string ToWire(this Tri value)
        {
            switch (value)
            {
                case Tri.Yes: return "yes";
                case Tri.No: return "no";
                default: return "unknown";
            }
        }

        /// <summary>
        /// Turns a bool into Yes or No.
        /// 確かめ終わった bool を <see cref="Tri"/> にする。
        /// ⚠ 確かめられなかった時は、この口を使わずに <see cref="Tri.Unknown"/> を書く。
        /// </summary>
        public static Tri ToTri(this bool value) => value ? Tri.Yes : Tri.No;
    }
}
