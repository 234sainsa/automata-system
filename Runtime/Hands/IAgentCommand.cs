using System.Threading;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Monster.Balloon.AutomataSystem
{
    /// <summary>
    /// One hands command (for example "move") that the agent can run through the virtual pad.
    /// 手足の命令 1 つ。ゲームの側が、世界ごとに DI の登録で足す。
    /// <para>
    /// ⭐ 芯（<see cref="AgentLeaseDesk"/>）は、命令を 1 本ずつ走らせる（重なった頼みは 409 busy で断る）。
    ///    締め切り・止める反射・借りが切れた時は、芯が先に結果を決め、パッドを中立に戻し、cancel を取り消す。
    /// ⭐ 命令は、押した物を、戻る前に必ず離す（押しっぱなしを残さない）。
    /// ⚠ cancel が取り消された後は、命令はパッドに触らない（芯が離した後に、次の命令が押しているかもしれない）。
    /// ⚠ 命令は主スレッドで呼ばれる。
    /// </para>
    /// </summary>
    public interface IAgentCommand
    {
        /// <summary>The command name (for example "move"). 命令の名前。⚠ 命令は決まった字を返す（hello に並べ、頼みの do と比べる）。</summary>
        string Name { get; }

        /// <summary>Whether the command changes the world (give, use, drop and so on). 世界を変える命令か。hello に並べる。</summary>
        bool ChangesWorld { get; }

        /// <summary>
        /// The longest time, in seconds, that this request may run.
        /// この頼みを、長くて何秒まで走らせるか。芯は、これを過ぎたら timeout で終える（60 秒で切る）。
        /// 頼みの形が悪い時も、命令は何か返してよい（<see cref="RunAsync"/> が refused で断る）。
        /// </summary>
        float MaxSeconds(JObject args);

        /// <summary>
        /// Runs the command on the main thread and returns its result.
        /// 命令を走らせる（主スレッド）。結果の形は <see cref="AgentActResult"/>。
        /// <para>
        /// ⭐ 戻り値は Unity の <see cref="Awaitable{T}"/>。命令は <c>async Awaitable&lt;AgentActResult&gt;</c> で書いてよい。
        ///    待たずにすぐ結果を返す命令は、<see cref="AgentActAwaitable.FromResult"/> を使う。
        /// ⚠ 芯は、返された Awaitable を 1 回だけ待つ（Awaitable は 2 回待てない）。命令の側も、同じ Awaitable を使い回さない。
        /// ⚠ 取り消された時は、命令は OperationCanceledException を投げて終えてよい（芯は、その時もう結果を決めている）。
        /// </para>
        /// </summary>
        Awaitable<AgentActResult> RunAsync(JObject args, CancellationToken cancel);
    }

    /// <summary>
    /// Helpers for commands that already know their result.
    /// 待たずに結果が決まる命令のための口。
    /// </summary>
    public static class AgentActAwaitable
    {
        /// <summary>
        /// Returns an awaitable that has already finished with the given result.
        /// もう終わった Awaitable を、渡した結果で作って返す。
        /// ⭐ この口は、呼ぶたびに新しい完了の元（AwaitableCompletionSource）を作る＝返した物は 1 回だけ待てる。
        /// </summary>
        public static Awaitable<AgentActResult> FromResult(AgentActResult result)
        {
            var source = new AwaitableCompletionSource<AgentActResult>();
            source.SetResult(result);
            return source.Awaitable;
        }
    }

    /// <summary>
    /// A command whose request may be accepted only with the back token, depending on its arguments.
    /// 頼みの形によっては、裏のトークンでしか受けない命令（例 座標や個体の ID で行き先や相手を決める頼み）。
    /// <para>
    /// ⭐ 窓口（<see cref="AgentActRoute"/>）は、<see cref="NeedsBack"/> が true の頼みを、表のトークンなら 403 back_only で断る。
    ///    表の AI は、人に見える物だけを相手にする（座標と個体の ID は、裏の道具だけの物）。撮る道の hideDebugOverlays と同じ決まり。
    /// ⭐ 命令は、窓口が通した頼みだけを受ける＝命令は RunAsync の中でトークンを見なくてよい。
    /// </para>
    /// </summary>
    public interface IAgentBackArgs
    {
        /// <summary>
        /// Whether this request (args) may be accepted only with the back token.
        /// この頼み（args）は、裏のトークンでしか受けないか。⚠ 形が悪くても、この口は例外を投げない（形は命令が後で bad_args で断る）。
        /// </summary>
        bool NeedsBack(JObject args);
    }
}
