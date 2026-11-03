using System;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;

namespace Monster.Balloon.AutomataSystem
{
    /// <summary>
    /// PostLateUpdate で列を回す係。Unity の PlayerLoop の PostLateUpdate の頭に、自前の system を 1 つ差し込む。
    /// <para>
    /// ⭐ この係は、差し込んだ system で、毎コマ 1 回、今の窓口（<see cref="AgentHost.CurrentSession"/>）の列を回す。
    ///    古い Play の窓口・閉じた窓口は、今の窓口ではないので回さない（前の作りで「列から外れる」だった物と同じ働き）。
    /// ⭐ この係は、差し込む前に、今の PlayerLoop に自分の system が在るかを型で調べる＝Domain Reload を切って Play を何回入っても、二重に入らない。
    /// ⭐ この係は、Play を抜けても system を外さない。窓口が無い間の system は、何もしないで戻る。
    /// ⚠ この係は、ほかのパッケージが入れた system を消さない（今の PlayerLoop を取って、自分の 1 つを足して戻すだけ）。
    /// </para>
    /// </summary>
    internal static class AgentHostPump
    {
        /// <summary>列を回す途中で例外を投げた窓口。この窓口は、もう回さない（前の作りで、落ちた係が列から外れたのと同じ）。</summary>
        private static AgentHostSession broken;

        /// <summary>
        /// 列を回す system を、まだ無ければ PostLateUpdate の頭に差し込む（窓口を開く時に呼ぶ）。
        /// ⚠ 主スレッドから呼ぶ。PlayerLoop を回している途中（system の中）からは呼ばない。
        /// </summary>
        internal static void EnsureInstalled()
        {
            broken = null;

            PlayerLoopSystem root = PlayerLoop.GetCurrentPlayerLoop();
            PlayerLoopSystem[] phases = root.subSystemList;
            if (phases == null)
            {
                AgentHost.ReportOnce("pump-install", "[AgentApi] PlayerLoop に段が無いので、列を回す係を差し込めない（窓口は答えない）。");
                return;
            }

            for (int i = 0; i < phases.Length; i++)
            {
                if (phases[i].type != typeof(PostLateUpdate))
                {
                    continue;
                }

                PlayerLoopSystem[] inner = phases[i].subSystemList ?? Array.Empty<PlayerLoopSystem>();
                foreach (PlayerLoopSystem system in inner)
                {
                    if (system.type == typeof(AgentHostPump))
                    {
                        return;
                    }
                }

                // ⭐ 頭に足す＝そのコマの LateUpdate が全部済み、値がそろった後で、描く前に回す。
                var added = new PlayerLoopSystem[inner.Length + 1];
                added[0] = new PlayerLoopSystem
                {
                    type = typeof(AgentHostPump),
                    updateDelegate = Run,
                };
                Array.Copy(inner, 0, added, 1, inner.Length);
                phases[i].subSystemList = added;
                root.subSystemList = phases;
                PlayerLoop.SetPlayerLoop(root);
                return;
            }

            AgentHost.ReportOnce("pump-install", "[AgentApi] PlayerLoop に PostLateUpdate が無いので、列を回す係を差し込めない（窓口は答えない）。");
        }

        /// <summary>毎コマ 1 回、PostLateUpdate の頭で呼ばれる。今の窓口が開いていれば、列を回す。</summary>
        private static void Run()
        {
            AgentHostSession s = AgentHost.CurrentSession;
            if (s == null || s.IsClosed || ReferenceEquals(s, broken))
            {
                return;
            }

            try
            {
                s.Pump();
            }
            catch (Exception e)
            {
                // ⚠ 落ちた窓口は、もう回さない。例外は赤のログに残す（握り潰さない）。
                broken = s;
                Debug.LogException(e);
            }
        }
    }
}
