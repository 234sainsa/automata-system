using System.Collections.Generic;

namespace Monster.Balloon.AutomataSystem
{
    /// <summary>
    /// Optional: the moving reflexes of a watch that run again and again as part of normal play.
    /// 見張りが持つ動く反射のうち、ふつうの遊びの中で何度も動く物（例＝会話の行を送る "talk"）の名前。
    /// <para>
    /// ⭐ 見張り（<see cref="IAgentReflexWatch"/>）がこれも持てば、Host は名前の並んだ反射を次のように扱う。
    ///   1) 同じ反射が 1 分に 3 回の守り（reflex_loop）で数えない（会話の行は 1 分に何行も送るのが当たり前）。
    ///   2) 腕の modes で名指しした時だけ動く（modes を省いた腕では動かない＝前からの腕の掛け方で、勝手に会話が進まない）。
    /// ⚠ 決まった値を返すこと。<see cref="IAgentReflexWatch.Modes"/> に無い名前は見ない。
    /// </para>
    /// </summary>
    public interface IAgentRoutineReflexes
    {
        /// <summary>
        /// The names of the routine moving reflexes (a subset of <see cref="IAgentReflexWatch.Modes"/>).
        /// ふつうの遊びの中で何度も動く反射の名前（<see cref="IAgentReflexWatch.Modes"/> の一部）。
        /// </summary>
        IReadOnlyList<string> RoutineModes { get; }
    }
}
