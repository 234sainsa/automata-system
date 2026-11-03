namespace Monster.Balloon.AutomataSystem
{
    /// <summary>
    /// 「何コマか進める」1 回ぶんの控え（<c>pause {step:N}</c> の預かった頼みが持つ）。
    /// ⭐ 進め終えた数は、この控えに入る＝後から来た step の数と混ざらない。
    /// </summary>
    internal sealed class AgentStepTicket
    {
        public AgentStepTicket(int frames, long deadlineMs)
        {
            this.Frames = frames;
            this.DeadlineMs = deadlineMs;
        }

        /// <summary>頼まれたコマの数。</summary>
        public int Frames { get; }

        /// <summary>これを過ぎたら、進んだ所で止め直して答える（<see cref="AgentReflex.StepDeadlineMs"/>）。</summary>
        public long DeadlineMs { get; }

        /// <summary>進め終えたか（だれかが止めた・動かすと頼まれた・上限の秒を過ぎた時も）。</summary>
        public bool Done { get; private set; }

        /// <summary>実際に進んだコマの数（<see cref="Done"/> の後だけ意味がある）。</summary>
        public int Stepped { get; private set; }

        internal void Finish(int stepped)
        {
            this.Done = true;
            this.Stepped = stepped;
        }
    }
}
