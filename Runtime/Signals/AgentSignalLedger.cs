using System;
using System.Collections.Generic;

namespace Monster.Balloon.AutomataSystem
{
    /// <summary>
    /// 合図の帳面（決まった数の輪）。Host は表と裏で 1 冊ずつ持つ（表の番号の抜けから、裏の出来事を読ませないため）。
    /// <para>
    /// ⭐ 番号は 1 から増え、Play の間は戻らない。輪からあふれた古い行は消える（読む側には、消えた数を gap で知らせる）。
    /// ⚠ 赤いログの合図は、主スレッドの外からも書かれる。だから、この帳面は読み書きを鍵で守る。
    /// </para>
    /// </summary>
    internal sealed class AgentSignalLedger
    {
        /// <summary>帳面に残す行の数（仮）。</summary>
        public const int DefaultCapacity = 4096;

        private readonly object gate = new object();
        private readonly AgentSignalEntry[] ring;
        private long last;

        public AgentSignalLedger(int capacity = DefaultCapacity)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }

            this.ring = new AgentSignalEntry[capacity];
        }

        public int Capacity => this.ring.Length;

        /// <summary>最後に書いた行の番号（まだ何も無ければ 0）。</summary>
        public long LastSeq
        {
            get
            {
                lock (this.gate)
                {
                    return this.last;
                }
            }
        }

        /// <summary>1 行書き、その番号を返す。</summary>
        public long Append(int epoch, int frame, string kind, bool initial, string dataJson)
        {
            lock (this.gate)
            {
                long seq = ++this.last;
                this.ring[this.IndexOf(seq)] = new AgentSignalEntry(seq, epoch, frame, kind, initial, dataJson);
                return seq;
            }
        }

        /// <summary>
        /// <paramref name="after"/> より後の行を、古い順に <paramref name="max"/> 行まで入れる（先に空にする）。
        /// </summary>
        /// <param name="readTo">読んだ所までの番号（次に after へ渡す値）。</param>
        /// <returns>輪からあふれて読めなかった行の数（0 なら抜けは無い）。</returns>
        public long ReadAfter(long after, int max, List<AgentSignalEntry> into, out long readTo)
        {
            into.Clear();
            lock (this.gate)
            {
                long from = this.ClampFrom(after, out long lost);
                long to = Math.Min(this.last, from + Math.Max(0, max) - 1);
                for (long seq = from; seq <= to; seq++)
                {
                    into.Add(this.ring[this.IndexOf(seq)]);
                }

                readTo = into.Count > 0 ? to : from - 1;
                return lost;
            }
        }

        /// <summary>
        /// <paramref name="after"/> より後で、<paramref name="match"/> に合う最初の行を探す。
        /// ⚠ match は鍵の中で呼ぶ。軽い判定だけにし、帳面へは書かないこと。
        /// </summary>
        /// <param name="scannedTo">見終えた所までの番号（合う行があればその行・無ければ最後の行）。</param>
        /// <param name="lost">輪からあふれて見られなかった行の数。</param>
        public bool TryFindAfter(
            long after, Func<AgentSignalEntry, bool> match, out AgentSignalEntry hit, out long scannedTo, out long lost)
        {
            lock (this.gate)
            {
                long from = this.ClampFrom(after, out lost);
                for (long seq = from; seq <= this.last; seq++)
                {
                    AgentSignalEntry entry = this.ring[this.IndexOf(seq)];
                    if (match(entry))
                    {
                        hit = entry;
                        scannedTo = seq;
                        return true;
                    }
                }

                hit = default;
                scannedTo = Math.Max(this.last, from - 1);
                return false;
            }
        }

        /// <summary>after の次から読む時に、まだ輪に残っている最初の番号を返す（消えていた数を lost に入れる）。</summary>
        private long ClampFrom(long after, out long lost)
        {
            long from = Math.Max(1, after + 1);
            long oldest = this.last < this.ring.Length ? 1 : this.last - this.ring.Length + 1;
            lost = from < oldest ? oldest - from : 0;
            return Math.Max(from, oldest);
        }

        private int IndexOf(long seq) => (int)((seq - 1) % this.ring.Length);
    }
}
