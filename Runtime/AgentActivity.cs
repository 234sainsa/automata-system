namespace Monster.Balloon.AutomataSystem
{
    /// <summary>
    /// A "count from here" mark, taken when a test line starts.
    /// 「ここから数える」の印（<see cref="AgentHost.MarkActivity"/> が返す）。ゲームの試験の橋が、行を出した時に取る。
    /// <para>
    /// ⭐ 窓口が開いていない・裏の帳面が無い時は <see cref="IsSet"/> が false（数えられない）。
    /// ⭐ 世界を作り直しても使える（窓口と帳面は Play の間ずっと同じ・番号は戻らない）。
    /// </para>
    /// </summary>
    public readonly struct AgentActivityMark
    {
        internal AgentActivityMark(string playId, long surfaceSeq, long backSeq, long backCalls, int captures)
        {
            this.PlayId = playId;
            this.SurfaceSeq = surfaceSeq;
            this.BackSeq = backSeq;
            this.BackCalls = backCalls;
            this.Captures = captures;
        }

        /// <summary>
        /// True when the mark was taken in a Play.
        /// 印を取れたか（窓口が開いていて、裏の帳面がある時だけ）。
        /// </summary>
        public bool IsSet => this.PlayId != null;

        /// <summary>
        /// The ID of the Play that the mark was taken in.
        /// 印を取った Play の印（返事の instance.playId と同じ）。
        /// </summary>
        public string PlayId { get; }

        /// <summary>印を取った時の、表の帳面の最後の番号。</summary>
        internal long SurfaceSeq { get; }

        /// <summary>印を取った時の、裏の帳面の最後の番号。</summary>
        internal long BackSeq { get; }

        /// <summary>印を取った時までに、裏のトークンで来た頼みの数。</summary>
        internal long BackCalls { get; }

        /// <summary>印を取った時までに撮った枚数（撮った絵の ID の番号）。</summary>
        internal int Captures { get; }
    }

    /// <summary>
    /// What happened between a mark and now, counted by the endpoint.
    /// 印から今までに起きた事（<see cref="AgentHost.ActivitySince"/> が主スレッドで数える）。
    /// ⭐ 試験の橋が、AI の判定の記録に添える（playedWithBack・via・reflexActed）。
    ///    keptAlive はここで数えない＝合図は 1 秒に 1 行へ間引かれるので、橋が keep_alive の係の回数で見る。
    /// ⚠ どれも窓口が数えた値＝AI が書き込む値ではない。
    /// </summary>
    public sealed class AgentActivity
    {
        /// <summary>
        /// True when the counts can be trusted.
        /// 数えられたか（false＝印が無い・窓口が閉じた・Play が替わった＝ほかの数は 0 のまま）。
        /// </summary>
        public bool Known { get; internal set; }

        /// <summary>
        /// True when the ledger lost entries, so the counts may be short.
        /// 裏の帳面の輪からあふれて、数え漏れがある（数は少なめ）。
        /// </summary>
        public bool Truncated { get; internal set; }

        /// <summary>
        /// How many calls came in on the back route.
        /// 裏の道に来た頼みの数（世界を読まない係の道＝<see cref="IAgentControlRoute"/> と、表の道を裏のトークンで打った頼みは数えない）。
        /// </summary>
        public int BackCalls { get; internal set; }

        /// <summary>
        /// How many times a stop guard tripped.
        /// 止める反射が止めた回数（合図 reflex.tripped）。
        /// </summary>
        public int ReflexTripped { get; internal set; }

        /// <summary>
        /// How many times a moving reflex acted.
        /// 動く反射が動いた回数（合図 reflex.acted）。
        /// </summary>
        public int ReflexActed { get; internal set; }

        /// <summary>
        /// How many act calls ran.
        /// 終わった命令の数（合図 act.done）。
        /// </summary>
        public int Acts { get; internal set; }

        /// <summary>
        /// How many act calls pressed a virtual pad.
        /// 仮のパッドで押した命令の数（act.done の via pad）。
        /// </summary>
        public int ActsByPad { get; internal set; }

        /// <summary>
        /// How many act calls acted directly, without the pad.
        /// 直に押した命令の数（act.done の via direct）。
        /// </summary>
        public int ActsDirect { get; internal set; }
    }
}
