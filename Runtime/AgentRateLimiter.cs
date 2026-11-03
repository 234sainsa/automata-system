namespace Monster.Balloon.AutomataSystem
{
    /// <summary>
    /// 1 秒あたりの頼みの数を数える（窓口のスレッドから呼ばれる）。
    /// <para>
    /// ⭐ この係は、直近の N 本の時刻を輪で持つ。輪でいちばん古い時刻が 1 秒より新しければ、1 秒に N 本を超えたことになる。
    /// </para>
    /// </summary>
    internal sealed class AgentRateLimiter
    {
        private const long Empty = long.MinValue;

        private readonly long[] stamps;
        private readonly object sync = new object();
        private int next;

        public AgentRateLimiter(int perSecond)
        {
            this.stamps = new long[perSecond < 1 ? 1 : perSecond];
            for (int i = 0; i < this.stamps.Length; i++)
            {
                this.stamps[i] = Empty;
            }
        }

        /// <summary>1 本ぶんの枠を取る。枠が無ければ false を返す。</summary>
        /// <param name="nowMs">単調に増えるミリ秒（<see cref="System.Diagnostics.Stopwatch"/> から作る）。</param>
        public bool TryTake(long nowMs)
        {
            lock (this.sync)
            {
                long oldest = this.stamps[this.next];
                if (oldest != Empty && nowMs - oldest < 1000)
                {
                    return false;
                }

                this.stamps[this.next] = nowMs;
                this.next = (this.next + 1) % this.stamps.Length;
                return true;
            }
        }
    }
}
