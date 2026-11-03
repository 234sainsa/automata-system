using System.Collections.Generic;

namespace Monster.Balloon.AutomataSystem
{
    /// <summary>
    /// The hands of a world: they add and remove a virtual pad and watch human input.
    /// 世界の手足。仮のパッドを足す・外す・中立に戻す・人の本物の入力を見張る。
    /// <para>
    /// ⭐ 芯（<see cref="AgentLeaseDesk"/>）は、借りの間だけパッドを使う（借りの頭で <see cref="Begin"/>・終わりで <see cref="End"/>）。
    /// ⭐ 人の本物の入力は、借りが無い間も、芯が毎フレーム尋ねる（<see cref="TakeHumanInput"/>・人が遊び続けている間は貸さない）。
    /// ⭐ 手足は入力の設定を書き換えない（安全のため）。
    /// ⚠ 主スレッドで呼ばれる。
    /// </para>
    /// </summary>
    public interface IAgentHands
    {
        /// <summary>
        /// The commands this world can run.
        /// この世界の命令（hello に並べ、act の do で選ぶ）。
        /// </summary>
        IReadOnlyList<IAgentCommand> Commands { get; }

        /// <summary>
        /// Starts the hands for one Play.
        /// 借りが始まった。仮のパッドを足す。
        /// </summary>
        void Begin();

        /// <summary>
        /// Puts the virtual pad back to neutral.
        /// 押している物を全部離す（パッドは残す）。
        /// </summary>
        void Neutral();

        /// <summary>
        /// Ends the hands and removes the virtual pad.
        /// 借りが終わった。中立にしてパッドを外す（何度呼んでもよい）。
        /// </summary>
        void End();

        /// <summary>
        /// Returns true when a real human input was seen since the last call.
        /// 人の本物の入力が、前に聞いた時から来たか（来ていれば true を返して、印を下ろす）。
        /// ⭐ 借りが無い間も、窓口が開いて世界ができている間は毎フレーム呼ばれる（初めて呼ばれた時から聞き始め、世界と一緒にやめる）。
        /// </summary>
        bool TakeHumanInput();
    }
}
