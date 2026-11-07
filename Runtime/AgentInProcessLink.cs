using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Monster.Balloon.AutomataSystem
{
    /// <summary>
    /// A link that sends requests to the endpoint inside the same process.
    /// 中の口 1 本。ゲームの中の係（製品の AI の係・Editor の試し）が、HTTP を通らずに窓口の列へ頼みを積む。
    /// <para>
    /// ⭐ 開くのは <see cref="AgentHost.OpenInProcess"/>。答えの字は HTTP の窓口と 1 字も違わない（同じ列・同じ封筒・同じ道）。
    /// ⭐ 頼みは表の顔だけ（裏の道は 403 blind で断られる）。門（トークン・回数の上限）は通らない＝ゲームの中の係だけが持つ口。
    /// ⚠ 窓口が閉じた（Play を抜けた・作り直した・最後の中の口を閉じた）後の頼みは、すぐ 503 closing で返る。この口は生き返らない＝開き直す。
    /// </para>
    /// </summary>
    public sealed class AgentInProcessLink : IDisposable
    {
        private readonly AgentHostSession session;
        private int disposed;

        internal AgentInProcessLink(AgentHostSession session)
        {
            this.session = session ?? throw new ArgumentNullException(nameof(session));
        }

        /// <summary>
        /// True until the link is disposed or the Play ends.
        /// まだ頼めるか（Dispose した・窓口が閉じた後は偽）。どのスレッドから読んでもよい。
        /// </summary>
        public bool IsAlive => Volatile.Read(ref this.disposed) == 0 && !this.session.IsClosed;

        /// <summary>
        /// The ID of the Play this link belongs to.
        /// この口の窓口の Play の印（答えの instance.playId と同じ）。
        /// </summary>
        public string PlayId => this.session.PlayId;

        /// <summary>
        /// A token that is cancelled when the endpoint closes.
        /// 窓口を閉じた時に取り消される印（待ちを止めるのに使う）。
        /// </summary>
        public CancellationToken Closed => this.session.ClosedToken;

        /// <summary>
        /// Sends one request and waits for the reply.
        /// 道へ頼みを 1 本積み、答えを待つ。どのスレッドから呼んでもよい。
        /// ⭐ 待つ長さと、待ちきれない時の答えは HTTP の窓口と同じ（ふつうの道は 5 秒・wait は頼んだ秒を足す）。
        ///    越えた時、主スレッドがまだ受け取っていなければ 503 main_thread_busy（頼みは下げた＝走らない）、
        ///    受け取った後なら 504 answer_late（頼みは走った・まだ走っている＝打ち直すと 2 回走る）。
        /// ⚠ 渡した body は、積んだ後に変えない（主スレッドが読む）。
        /// </summary>
        /// <param name="route">"/v1/" の後ろ（例 "look"・"wait"）。</param>
        /// <param name="body">頼みの本文（null は中身の無いオブジェクト）。</param>
        public Task<AgentReply> SubmitAsync(string route, JObject body)
        {
            if (!this.IsAlive)
            {
                return Task.FromResult(AgentReplies.Error(503, "closing", "reopen_after_play"));
            }

            string path = AgentGate.RoutePrefix + (route ?? string.Empty);
            return this.session.AnswerAsync(path, AgentFace.Surface, body);
        }

        /// <summary>
        /// Closes the link.
        /// 口を閉じる（何度呼んでもよい）。待ち受けの無い窓口は、最後の中の口が閉じた時に閉じる。
        /// ⚠ 主スレッドから呼ぶ（窓口を閉じる時に Host の GameObject を消すため）。
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref this.disposed, 1) != 0)
            {
                return;
            }

            AgentHost.ReleaseInProcess(this.session);
        }
    }
}
