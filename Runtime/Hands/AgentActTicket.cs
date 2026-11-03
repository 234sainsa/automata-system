namespace Monster.Balloon.AutomataSystem
{
    /// <summary>
    /// 命令 1 本の控え（act の預かった頼みが持つ）。始めた時の帳面の番号と、終わった時の結果・邪魔した合図を持つ。
    /// <para>
    /// ⭐ 動く反射が走らせた命令は <see cref="Reflex"/> に反射の名前が入る（手足の係 <see cref="AgentLeaseDesk"/> が作る）。
    /// </para>
    /// </summary>
    internal sealed class AgentActTicket
    {
        public AgentActTicket(string actId, string reference, string command, long deadlineMs, long surfaceSeq, long backSeq)
        {
            this.ActId = actId;
            this.Ref = reference;
            this.Command = command;
            this.DeadlineMs = deadlineMs;
            this.SurfaceSeq = surfaceSeq;
            this.BackSeq = backSeq;
        }

        /// <summary>命令の印（例 "a17"・Play の間で 1 つずつ増える）。</summary>
        public string ActId { get; }

        /// <summary>頼んだ人が付けた印（無ければ null）。</summary>
        public string Ref { get; }

        public string Command { get; }

        /// <summary>動く反射が走らせた命令なら、その反射の名前（AI の命令なら null）。</summary>
        public string Reflex { get; internal set; }

        /// <summary>これを過ぎたら、芯が timeout で終える。</summary>
        public long DeadlineMs { get; }

        /// <summary>始めた時の表の帳面の最後の番号（返事の cursor）。</summary>
        public long SurfaceSeq { get; }

        /// <summary>始めた時の裏の帳面の最後の番号（裏のトークンの返事の cursor）。</summary>
        public long BackSeq { get; }

        public bool Done { get; private set; }

        public AgentActResult Result { get; private set; }

        /// <summary>邪魔した合図の名前（interrupted の時だけ・無ければ null）。</summary>
        public string ByKind { get; private set; }

        /// <summary>邪魔した合図の表の帳面の番号。</summary>
        public long BySeq { get; private set; }

        /// <summary>邪魔した合図の裏の帳面の番号（裏のトークンの返事の by.seq・裏が無ければ 0）。</summary>
        public long ByBackSeq { get; private set; }

        internal void Finish(AgentActResult result, string byKind, long bySeq, long byBackSeq = 0)
        {
            this.Result = result;
            this.ByKind = byKind;
            this.BySeq = bySeq;
            this.ByBackSeq = byBackSeq;
            this.Done = true;
        }
    }
}
