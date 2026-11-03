using Newtonsoft.Json.Linq;

namespace Monster.Balloon.AutomataSystem
{
    /// <summary>
    /// The words that say how an act ended.
    /// 命令の結果の言葉（outcome の 7 つ）。
    /// </summary>
    public static class AgentOutcome
    {
        /// <summary>
        /// The command did what was asked.
        /// 押して、効いた（命令ごとの「できた」に届いた）。
        /// </summary>
        public const string Done = "done";

        /// <summary>
        /// The command ran but nothing changed.
        /// 押したが、何も変わらなかった（止められていれば seenWhy に見て分かる理由）。
        /// </summary>
        public const string NoEffect = "no-effect";

        /// <summary>
        /// Something else stopped the command.
        /// 途中で邪魔された（by に邪魔した合図）。芯だけが付ける。
        /// </summary>
        public const string Interrupted = "interrupted";

        /// <summary>
        /// The command ran out of time.
        /// 命令の「できた」に、決めた秒のうちに届かなかった。
        /// </summary>
        public const string Timeout = "timeout";

        /// <summary>
        /// The command could not press the input.
        /// パッドでは押せない操作（人へ返す不具合の候補）。
        /// </summary>
        public const string CannotPress = "cannot-press";

        /// <summary>
        /// The command was refused before it ran.
        /// 頼みの形が悪い・今は押さない（hint に理由）。
        /// </summary>
        public const string Refused = "refused";

        /// <summary>
        /// The result could not be told.
        /// 命令が落ちた・結果が分からない。芯だけが付ける。
        /// </summary>
        public const string Unknown = "unknown";
    }

    /// <summary>
    /// The result of one command.
    /// 命令 1 本の結果。
    /// <para>
    /// ⭐ 命令が返すのは done・no-effect・timeout・cannot-press・refused。interrupted と unknown は芯が付ける。
    /// ⭐ seenWhy は「見て分かる」止められた理由（例 talking・seated）。表に出るので、ゲームの中の名前（門の理由の生の字）を入れない。
    /// ⭐ via は押し方（"pad"＝仮のパッド・"direct"＝直に）。押していなければ null。
    /// </para>
    /// </summary>
    public sealed class AgentActResult
    {
        /// <summary>
        /// The command pressed a virtual pad.
        /// 仮のパッドで押した、の印。
        /// </summary>
        public const string ViaPad = "pad";
        /// <summary>
        /// The command acted directly, without the pad.
        /// パッドを通さず、直に押した、の印。
        /// </summary>
        public const string ViaDirect = "direct";

        private AgentActResult(string outcome, string via, string seenWhy, string hint, JObject detail = null, JObject backDetail = null)
        {
            this.Outcome = outcome;
            this.Via = via;
            this.SeenWhy = seenWhy;
            this.Hint = hint;
            this.Detail = detail;
            this.BackDetail = backDetail;
        }

        /// <summary>
        /// How the command ended (see AgentOutcome).
        /// <see cref="AgentOutcome"/> の言葉のどれか。
        /// </summary>
        public string Outcome { get; }

        /// <summary>
        /// How the command acted (pad or direct).
        /// 押し方（"pad"・"direct"）。押していなければ null。
        /// </summary>
        public string Via { get; }

        /// <summary>
        /// The reason, visible in the world, why the command was stopped (null when none).
        /// 見て分かる止められた理由（無ければ null）。
        /// </summary>
        public string SeenWhy { get; }

        /// <summary>
        /// A hint when the command was refused or could not press; when done, what came out.
        /// 断った・押せなかった時の手がかり。done の時は、何が出たか（短い字・無ければ null）。
        /// </summary>
        public string Hint { get; }

        /// <summary>
        /// A short bundle of numbers that a long command returns, or null.
        /// 長い命令が返す、短い数の束（例 fight の振った数・倒された数・相手の様子）。無ければ null。
        /// ⭐ act の返事の detail にだけ書く（合図 act.done には書かない）。芯が interrupted で止めた時は付かない。
        /// </summary>
        public JObject Detail { get; }

        /// <summary>
        /// A bundle for the back route only, or null.
        /// 裏のトークンで頼まれた時だけ、<see cref="Detail"/> の代わりに返事の detail に書く束（無ければ null＝裏でも <see cref="Detail"/>）。
        /// 例 walk_to の stuck の blocked＝表は人か物か（と人の札）だけ、裏は個体 ID・当たった物の名前・座標も。
        /// ⭐ 表の漏れの見張りは見ない＝表に出してはいけない物（個体 ID・物の名前・座標）を書いてよい。
        /// </summary>
        public JObject BackDetail { get; }

        /// <summary>
        /// Makes a result for a command that did what was asked.
        /// 押して、効いた結果を作る。
        /// </summary>
        public static AgentActResult Done(string via) => new AgentActResult(AgentOutcome.Done, via, null, null);

        /// <summary>
        /// Makes a result for a command that did what was asked, with a hint.
        /// 押して、効いた。<paramref name="hint"/> は何が出たか（命令が決めた短い字・例 attack の "drew"＝武器を抜いただけ）。
        /// ⭐ 同じ押しでも出方が分かれる命令（抜く／振る・食べ終えた／その場で変わった）で、AI が次の手を選べるように返す。
        /// </summary>
        public static AgentActResult Done(string via, string hint) => new AgentActResult(AgentOutcome.Done, via, null, hint);

        /// <summary>
        /// Makes a result for a command that changed nothing.
        /// 押したが何も変わらなかった結果を作る。
        /// </summary>
        public static AgentActResult NoEffect(string via, string seenWhy) => new AgentActResult(AgentOutcome.NoEffect, via, seenWhy, null);

        /// <summary>
        /// Makes a result for a command that ran out of time.
        /// 時間内に届かなかった結果を作る。
        /// </summary>
        public static AgentActResult TimedOut(string via, string seenWhy) => new AgentActResult(AgentOutcome.Timeout, via, seenWhy, null);

        /// <summary>
        /// Makes a result for a command that could not press the input.
        /// パッドでは押せなかった結果を作る。
        /// </summary>
        public static AgentActResult CannotPress(string hint) => new AgentActResult(AgentOutcome.CannotPress, null, null, hint);

        /// <summary>
        /// Makes a result for a command that was refused.
        /// 断った結果を作る。
        /// </summary>
        public static AgentActResult Refused(string hint) => new AgentActResult(AgentOutcome.Refused, null, null, hint);

        internal static AgentActResult Interrupted() => new AgentActResult(AgentOutcome.Interrupted, null, null, null);

        internal static AgentActResult Unknown(string hint) => new AgentActResult(AgentOutcome.Unknown, null, null, hint);

        /// <summary>
        /// Returns a copy with a detail bundle.
        /// この結果に detail（短い数の束）を付けた写しを返す（fight が使う）。
        /// ⚠ 表の返事にも出る＝束の欄の名前は、表の漏れの見張り（SurfaceLeakCheck・agent_call.py の SURFACE_NAMES）にも足す。
        /// </summary>
        public AgentActResult WithDetail(JObject detail) => new AgentActResult(this.Outcome, this.Via, this.SeenWhy, this.Hint, detail, this.BackDetail);

        /// <summary>
        /// Returns a copy with a back-only detail bundle.
        /// この結果に、裏のトークンの返事だけに書く束（<see cref="BackDetail"/>）を付けた写しを返す。
        /// </summary>
        public AgentActResult WithBackDetail(JObject backDetail) => new AgentActResult(this.Outcome, this.Via, this.SeenWhy, this.Hint, this.Detail, backDetail);

        /// <summary>
        /// 合図 act.done の中身＝<c>{act, command, outcome, via?, seenWhy?, reflex?}</c>（hint は返事だけに書く）。
        /// ⭐ via（押し方）は、試験の橋が行の間に何で押したかを帳面から数える元（<see cref="AgentHost.ActivitySince"/>）。
        /// </summary>
        internal JObject ToSignalData(string actId, string command, string reflex)
        {
            var data = new JObject { ["act"] = actId, ["command"] = command, ["outcome"] = this.Outcome };
            if (this.Via != null)
            {
                data["via"] = this.Via;
            }

            if (this.SeenWhy != null)
            {
                data["seenWhy"] = this.SeenWhy;
            }

            if (reflex != null)
            {
                data["reflex"] = reflex;
            }

            return data;
        }
    }
}
