namespace Monster.Balloon.AutomataSystem
{
    /// <summary>
    /// The version of the wire format.
    /// 公開 API の版（major.minor）。窓口の道は major ごとに分ける（今は /v1/）。
    /// <para>
    /// 係は、欄や道を足す時に minor を上げ、変える・消す時に major を上げる。使う側は、知らない欄を読み飛ばす。
    /// </para>
    /// </summary>
    public readonly struct ApiVersion
    {
        /// <summary>
        /// Creates a version from a major and a minor number.
        /// 大きい番号と小さい番号から版を作る。
        /// </summary>
        public ApiVersion(int major, int minor)
        {
            this.Major = major;
            this.Minor = minor;
        }

        /// <summary>
        /// The version that this code speaks.
        /// 今の版。
        /// </summary>
        public static ApiVersion Current => new ApiVersion(1, 0);

        /// <summary>
        /// The major number.
        /// 大きい番号。
        /// </summary>
        public int Major { get; }

        /// <summary>
        /// The minor number.
        /// 小さい番号。
        /// </summary>
        public int Minor { get; }

        /// <summary>
        /// Returns the version as "major.minor".
        /// "大.小" の字にして返す。
        /// </summary>
        public override string ToString() => this.Major + "." + this.Minor;
    }
}
