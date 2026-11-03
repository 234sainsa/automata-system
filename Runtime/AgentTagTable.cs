using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Monster.Balloon.AutomataSystem
{
    /// <summary>
    /// 表の札（6 字・例 "p3k9x2"）の表。個体の ID を、表で見せてよい短い札に替え、札から ID へ戻す。
    /// <para>
    /// ⭐ 札は、Play ごとの秘密の塩と世界の回（epoch）から作る。同じ世界の間は、同じ人に同じ札が付く。
    ///    世界を作り直すと札は全部替わり、前の札は戻せなくなる（使う側へは stale-tag で断る）。
    /// ⭐ 戻せるのは、今の世界でこの表が出した札だけ（出していない札は、形が合っていても戻さない）。
    /// ⭐ 字は 0・1・l・o を除いた 32 字。漏れ探しの形（UUID・32 字の 16 進・キーの形）に当たらない。
    /// ⚠ 主スレッドだけで使う。
    /// </para>
    /// </summary>
    internal sealed class AgentTagTable
    {
        public const int TagLength = 6;

        private const string Alphabet = "abcdefghijkmnpqrstuvwxyz23456789";
        private const int MaxAttempts = 16;

        private readonly byte[] salt;
        private readonly Dictionary<string, string> tagById = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> idByTag = new Dictionary<string, string>(StringComparer.Ordinal);
        private int epoch = int.MinValue;

        public AgentTagTable()
            : this(Encoding.ASCII.GetBytes(AgentTokens.NewHex(16)))
        {
        }

        /// <param name="salt">札の元にする秘密（テストは決まった値を渡す）。</param>
        internal AgentTagTable(byte[] salt)
        {
            this.salt = salt ?? throw new ArgumentNullException(nameof(salt));
        }

        /// <summary>この ID の札（今の世界で初めてなら作って覚える）。ID が空なら null。</summary>
        public string TagFor(string instanceId, int currentEpoch)
        {
            if (string.IsNullOrEmpty(instanceId))
            {
                return null;
            }

            this.Roll(currentEpoch);
            if (this.tagById.TryGetValue(instanceId, out string known))
            {
                return known;
            }

            using (var hmac = new HMACSHA256(this.salt))
            {
                for (int attempt = 0; attempt < MaxAttempts; attempt++)
                {
                    string seed = currentEpoch.ToString(CultureInfo.InvariantCulture) + "\n" + instanceId + "\n"
                        + attempt.ToString(CultureInfo.InvariantCulture);
                    byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(seed));
                    var chars = new char[TagLength];
                    for (int i = 0; i < TagLength; i++)
                    {
                        chars[i] = Alphabet[hash[i] & 31];
                    }

                    // ⭐ ほかの人の札と重なったら、次の回で作り直す（32 の 6 乗＝約 10 億通りなので、まず起きない）。
                    string tag = new string(chars);
                    if (!this.idByTag.ContainsKey(tag))
                    {
                        this.tagById[instanceId] = tag;
                        this.idByTag[tag] = instanceId;
                        return tag;
                    }
                }
            }

            return null;
        }

        /// <summary>札を ID に戻す。今の世界でこの表が出した札でなければ false。大文字と前後の空白は見ない。</summary>
        public bool TryResolve(string tag, int currentEpoch, out string instanceId)
        {
            instanceId = null;
            if (string.IsNullOrWhiteSpace(tag))
            {
                return false;
            }

            this.Roll(currentEpoch);
            return this.idByTag.TryGetValue(tag.Trim().ToLowerInvariant(), out instanceId);
        }

        /// <summary>世界が替わっていたら、前の世界の札を全部忘れる。</summary>
        private void Roll(int currentEpoch)
        {
            if (this.epoch == currentEpoch)
            {
                return;
            }

            this.epoch = currentEpoch;
            this.tagById.Clear();
            this.idByTag.Clear();
        }
    }
}
