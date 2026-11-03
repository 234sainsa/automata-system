using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Monster.Balloon.AutomataSystem
{
    /// <summary>
    /// 撮る頼み 1 本の控え（窓口が答えを持ち越す間、<see cref="AgentCall.State"/> に置く）。
    /// <para>
    /// ⭐ 順番: 主スレッドがフレームの終わりに撮って <see cref="NoteShot"/> を書く → 別のスレッドが絵をファイルにして
    ///    <see cref="Succeed"/> か <see cref="Fail"/> を書く。どちらも最後に印（<see cref="Shot"/>・<see cref="Finished"/>）を立てる。
    /// ⚠ 主スレッドは、印を見てから、ほかの欄を読む（印は volatile＝印より前に書いた欄が見える）。
    /// ⭐ 窓口が待ちきれずに答えた控えは「捨てた」（<see cref="Abandon"/>）＝後からフレームの終わりが来ても撮らない・書かない。
    /// </para>
    /// </summary>
    internal sealed class AgentCaptureTicket
    {
        private volatile bool shot;
        private volatile bool finished;
        private volatile bool abandoned;

        public AgentCaptureTicket(string id, JObject request, bool hideOverlays, int requestFrame, long startedMs, int epoch)
        {
            this.Id = id;
            this.Request = request ?? new JObject();
            this.HideOverlays = hideOverlays;
            this.RequestFrame = requestFrame;
            this.StartedMs = startedMs;
            this.Epoch = epoch;
        }

        /// <summary>頼みを受けた時の世界の番号（<see cref="AgentHost.Epoch"/>）。撮る時に違えば、撮らずに world_changed で答える。</summary>
        public int Epoch { get; }

        /// <summary>窓口が待ちきれずに答えた（この控えの続きは要らない）。</summary>
        public bool Abandoned => this.abandoned;

        /// <summary>古い絵を消す所で失敗した中身（絵は書けた・null なら無事）。</summary>
        public string PruneDetail { get; private set; }

        /// <summary>撮影の ID（"&lt;playId&gt;-&lt;番号&gt;"・ファイルの名前にも使う）。</summary>
        public string Id { get; }

        public JObject Request { get; }

        /// <summary>開発用の帯を隠すと頼まれた（裏のトークンだけ）。</summary>
        public bool HideOverlays { get; }

        /// <summary>頼みを受けたフレーム。</summary>
        public int RequestFrame { get; }

        public long StartedMs { get; }

        /// <summary>隠した物の名前（撮る前に主スレッドが書く）。</summary>
        public List<string> Hidden { get; } = new List<string>();

        /// <summary>フレームの終わりに撮った。</summary>
        public bool Shot => this.shot;

        /// <summary>撮ったフレーム。</summary>
        public int Frame { get; private set; }

        public int Width { get; private set; }

        public int Height { get; private set; }

        /// <summary>世界の口が書いた欄（look・measure など）。</summary>
        public JObject Body { get; private set; }

        /// <summary>絵をファイルにし終えた（か、失敗した）。</summary>
        public bool Finished => this.finished;

        /// <summary>絵のファイル（"/" で区切った絶対パス）。失敗した時は null。</summary>
        public string File { get; private set; }

        public string FailCode { get; private set; }

        public string FailHint { get; private set; }

        /// <summary>失敗の中身（例外の字・ログに書く）。</summary>
        public string FailDetail { get; private set; }

        /// <summary>撮った（主スレッド）。</summary>
        public void NoteShot(int frame, int width, int height, JObject body)
        {
            this.Frame = frame;
            this.Width = width;
            this.Height = height;
            this.Body = body ?? new JObject();
            this.shot = true;
        }

        /// <summary>絵をファイルにし終えた（別のスレッド）。</summary>
        public void Succeed(string file)
        {
            this.File = file;
            this.finished = true;
        }

        /// <summary>撮れなかった・書けなかった（どのスレッドからでもよい）。</summary>
        public void Fail(string code, string hint, string detail)
        {
            this.FailCode = code;
            this.FailHint = hint;
            this.FailDetail = detail;
            this.finished = true;
        }

        /// <summary>窓口が待ちきれずに答えた（主スレッド）。</summary>
        public void Abandon() => this.abandoned = true;

        /// <summary>古い絵を消す所で失敗した（別のスレッド・<see cref="Succeed"/> より前に書く）。</summary>
        public void NotePruneFailed(string detail) => this.PruneDetail = detail;
    }
}
