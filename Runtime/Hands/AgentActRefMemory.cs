using System;
using System.Collections.Generic;

namespace Monster.Balloon.AutomataSystem
{
    /// <summary>
    /// act の ref の控え（手足の係 <see cref="AgentLeaseDesk"/> が持つ・新しい 64 個だけ覚える）。
    /// <para>
    /// ⭐ ref は頼んだ人（by）ごとに覚える＝ほかの名前が同じ字の ref を送っても、前の人の答えを返さない。
    /// ⚠ 主スレッドで使う。
    /// </para>
    /// </summary>
    internal sealed class AgentActRefMemory
    {
        private const int Capacity = 64;

        private readonly Dictionary<string, AgentActTicket> byKey = new Dictionary<string, AgentActTicket>(StringComparer.Ordinal);
        private readonly Queue<string> order = new Queue<string>();

        /// <summary>覚える鍵（頼んだ人ごと・ref が無ければ null）。⭐ by は [A-Za-z0-9._-] だけ＝"|" で区切れば混ざらない。</summary>
        public static string Key(string by, string reference) => reference == null ? null : by + "|" + reference;

        /// <summary>
        /// 同じ人の同じ ref の控えを探す。在れば <paramref name="seen"/> に返す（終わっていれば 1 回目の結果・走っていれば同じ控えを待つ）。
        /// 同じ ref で違う命令なら "ref_reused" を返す。
        /// </summary>
        public string Find(string by, string reference, string command, out AgentActTicket seen)
        {
            seen = null;
            string key = Key(by, reference);
            if (key == null || !this.byKey.TryGetValue(key, out AgentActTicket ticket))
            {
                return null;
            }

            if (!string.Equals(ticket.Command, command, StringComparison.Ordinal))
            {
                return "ref_reused";
            }

            seen = ticket;
            return null;
        }

        /// <summary>控えを覚える（鍵が null なら何もしない・古い物から忘れる）。</summary>
        public void Remember(string key, AgentActTicket ticket)
        {
            if (key == null)
            {
                return;
            }

            this.byKey[key] = ticket;
            this.order.Enqueue(key);
            while (this.order.Count > Capacity)
            {
                this.byKey.Remove(this.order.Dequeue());
            }
        }
    }
}
