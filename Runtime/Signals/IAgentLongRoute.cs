using System;
using Newtonsoft.Json.Linq;

namespace Monster.Balloon.AutomataSystem
{
    /// <summary>
    /// 答えを持ち越せる道（長い待ち）。
    /// <para>
    /// ⭐ この道は、すぐ答えられない時に <see cref="AgentReplyWriter.Defer"/> を呼ぶ。Host はその頼みを預かり、
    ///    毎フレーム <see cref="IsDue"/> を聞いて、true になった時にもう一度 <see cref="IAgentRoute.Handle"/> を呼ぶ。
    /// ⭐ 窓口は、この道の答えを、主スレッドの待ち（5 秒）に <see cref="MaxHold"/> を足した長さまで待つ。
    /// </para>
    /// </summary>
    internal interface IAgentLongRoute
    {
        /// <summary>
        /// この頼みの答えを、主スレッドの待ちに足して待つ長さ。
        /// ⚠ 窓口のスレッドから呼ばれる。Unity の物に触らないこと。頼みの形が悪い時は 0 を返す（道が主スレッドで断る）。
        /// </summary>
        TimeSpan MaxHold(JObject body);

        /// <summary>預かった頼みに、もう答えられるか（主スレッド・毎フレーム・軽くすること）。</summary>
        bool IsDue(AgentCall call, long nowMs);
    }
}
