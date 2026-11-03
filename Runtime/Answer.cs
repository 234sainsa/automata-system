namespace Monster.Balloon.AutomataSystem
{
    /// <summary>
    /// A value that an eye (a reader of the world) returns, with how sure it is, why it is unknown, and where it was read.
    /// 目が返す 1 つの値と、その確かさ・分からない理由・読んだ所。
    /// <para>
    /// ⭐ 目は、値を読めない時に既定の値を黙って返さない。<see cref="Sure"/> を Unknown にし、<see cref="Why"/> に理由を書く。
    /// </para>
    /// </summary>
    public readonly struct Answer<T>
    {
        private Answer(T value, Tri sure, string why, string source)
        {
            this.Value = value;
            this.Sure = sure;
            this.Why = why;
            this.Source = source;
        }

        /// <summary>
        /// The value.
        /// 読んだ値（<see cref="Sure"/> が Yes の時だけ意味がある）。
        /// </summary>
        public T Value { get; }

        /// <summary>
        /// How sure the answer is.
        /// 値が確かか（Yes＝読めた・Unknown＝読めなかった）。
        /// </summary>
        public Tri Sure { get; }

        /// <summary>
        /// Why the answer is unknown, when it is.
        /// 読めなかった理由（例 "hud-hidden"）。読めた時は null。
        /// </summary>
        public string Why { get; }

        /// <summary>
        /// Where the value came from.
        /// 値を読んだ所（例 "StatusBarPresenter"）。調べ物の手がかりにする。
        /// </summary>
        public string Source { get; }

        /// <summary>
        /// True when the answer is sure.
        /// 答えが確かな時に true。
        /// </summary>
        public bool IsKnown => this.Sure == Tri.Yes;

        /// <summary>
        /// Makes a sure answer.
        /// 確かな答えを作る。
        /// </summary>
        public static Answer<T> Known(T value, string source = null) => new Answer<T>(value, Tri.Yes, null, source);

        /// <summary>
        /// Makes an unknown answer with a reason.
        /// 分からない答えを、訳を付けて作る。
        /// </summary>
        public static Answer<T> Unknown(string why, string source = null) => new Answer<T>(default, Tri.Unknown, why, source);
    }
}
