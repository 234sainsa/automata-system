namespace Monster.Balloon.AutomataSystem
{
    /// <summary>
    /// One signal in the ledger.
    /// 帳面の 1 行（合図 1 つ）。帳面に書いた後は変えない。
    /// </summary>
    public readonly struct AgentSignalEntry
    {
        /// <summary>
        /// Creates a signal entry.
        /// 合図 1 行を作る。
        /// </summary>
        public AgentSignalEntry(long seq, int epoch, int frame, string kind, bool initial, string dataJson)
        {
            this.Seq = seq;
            this.Epoch = epoch;
            this.Frame = frame;
            this.Kind = kind;
            this.Initial = initial;
            this.DataJson = dataJson;
        }

        /// <summary>
        /// The sequence number.
        /// 帳面の通し番号（1 から。Play の間ずっと増え、世界を作り直しても戻らない）。
        /// </summary>
        public long Seq { get; }

        /// <summary>
        /// The world epoch at the time the signal was written.
        /// 書いた時の世界の回（<see cref="AgentHost.Epoch"/>）。
        /// </summary>
        public int Epoch { get; }

        /// <summary>
        /// The frame the signal was written in.
        /// 書いた時のフレーム（主スレッドの外で書いた時は、Host が最後に列を回したフレーム）。
        /// </summary>
        public int Frame { get; }

        /// <summary>
        /// The kind name of the signal.
        /// 合図の名前（例 "choices.shown"）。
        /// </summary>
        public string Kind { get; }

        /// <summary>
        /// True when it was written as the current state at the start of watching, not as a change.
        /// 変わった時でなく「見始めた時の今の姿」として書いた合図か。
        /// ⭐ 合図の元は、見始めた時に今の姿を initial で 1 回だけ書く
        ///   （購読した時に今の値が来る event と、来ない event の違いを、使う側に見せないため）。
        /// </summary>
        public bool Initial { get; }

        /// <summary>
        /// The data of the signal as a JSON text.
        /// 中身（JSON のオブジェクトの字）。中身の無い合図では null。
        /// </summary>
        public string DataJson { get; }
    }
}
