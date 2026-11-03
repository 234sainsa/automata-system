using System.Security.Cryptography;
using System.Text;

namespace Monster.Balloon.AutomataSystem
{
    /// <summary>
    /// 窓口のトークンと、Play ごとの印（playId）を作る口。トークンを比べる口。
    /// <para>
    /// ⚠ 係は、トークンをログにも返事にも書かない。トークンを書く所は、つなぎ先ファイル 1 つだけ。
    /// </para>
    /// </summary>
    internal static class AgentTokens
    {
        /// <summary>トークンの乱数の長さ（32 バイト＝16 進で 64 字）。</summary>
        public const int TokenBytes = 32;

        /// <summary>新しいトークンを作る（暗号の乱数・16 進の小文字）。</summary>
        public static string NewToken() => NewHex(TokenBytes);

        /// <summary>短い印を作る（playId など・秘密ではない）。</summary>
        public static string NewHex(int byteCount)
        {
            var bytes = new byte[byteCount];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }

            var sb = new StringBuilder(byteCount * 2);
            for (int i = 0; i < bytes.Length; i++)
            {
                sb.Append(bytes[i].ToString("x2"));
            }
            return sb.ToString();
        }

        /// <summary>
        /// 2 つの字を、中身によって時間が変わらない比べ方で比べる。
        /// <para>
        /// ⭐ 先頭から何字あっているかで答えの時間が変わると、外の者が 1 字ずつ当てられる。だから、この口は最後まで比べてから答える。
        /// 長さの違いは隠さない（トークンの長さは決まっていて、秘密ではない）。
        /// </para>
        /// </summary>
        public static bool FixedTimeEquals(string a, string b)
        {
            if (a == null || b == null)
            {
                return false;
            }

            if (a.Length != b.Length)
            {
                return false;
            }

            int diff = 0;
            for (int i = 0; i < a.Length; i++)
            {
                diff |= a[i] ^ b[i];
            }
            return diff == 0;
        }
    }
}
