using System;
using UnityEngine;

namespace Monster.Balloon.AutomataSystem
{
    /// <summary>
    /// The part of AgentHost that lets code in the same process call the API without HTTP.
    /// 中の口の開け閉め。製品の AI の係と Editor の試しは、HTTP を通らずに窓口の列へ頼みを積む（<see cref="AgentInProcessLink"/>）。
    /// </summary>
    public static partial class AgentHost
    {
        /// <summary>
        /// True while the endpoint has an HTTP listener.
        /// 窓口が HTTP の待ち受けを持っているか（Editor の印で開いた窓口＝開発の道）。待ち受けの無い窓口（製品の AI の口）と、閉じている時は偽。
        /// ⭐ ゲームが足す力のキー（例: AI に仲間を任せるキー）を、開発の道（HTTP で来る外の AI）と製品の道（ゲームの中の AI）のどちらが受け持つかを、これで分ける。
        /// ⚠ 主スレッドから読む。
        /// </summary>
        public static bool IsListening => session != null && session.Transport != null;

        /// <summary>
        /// Opens a link that calls the API in the same process.
        /// 中の口を開く。HTTP の待ち受けは作らない。
        /// <para>
        /// ⭐ 窓口がもう開いていて、要る力が開いていれば、その窓口を使う（Editor の印で開いた窓口＝外の橋と同じ列・同じ持ち主の決まり）。
        ///    閉じていれば、待ち受けの無い窓口を、要る力だけで開く（製品はいつもこちら＝外のプログラムが D を動かせる口を増やさない）。
        /// ⭐ 待ち受けの無い窓口は、トークンもつなぎ先のファイルも外へ出さない。最後の中の口を閉じた時に閉じる
        ///    （預かっていた D は、預かりの係が黙って手放す）。
        /// ⚠ 主スレッドから呼ぶ。開けない時は null を返し、why に訳を書く（not-open＝開いている窓口に要る力が無い・
        ///    not-playing＝Play の外。Edit モードで開くと、窓口を閉じる見回り（Play を抜ける時）が来ないまま残る）。
        /// </para>
        /// </summary>
        /// <param name="needs">要る力（例 ゲームが登録した力）。裏（Back）は外す＝中の口は表の顔だけ。</param>
        /// <param name="word">窓口を新しく開く時の印の字（hello の open に出る・例 その力の名前）。</param>
        /// <param name="why">開けなかった訳（開けた時は null）。</param>
        public static AgentInProcessLink OpenInProcess(AgentAbilities needs, string word, out string why)
        {
            needs &= ~AgentAbilities.Back;
            if (needs == AgentAbilities.None)
            {
                throw new ArgumentException("中の口に要る力が無い（裏だけでは開けない）。", nameof(needs));
            }

            if (!Application.isPlaying)
            {
                why = "not-playing";
                return null;
            }

            why = null;
            AgentHostSession s = session;
            if (s == null)
            {
                s = new AgentHostSession(needs, string.IsNullOrEmpty(word) ? "in-process" : word);
                session = s;
                RebuildRoutes(worldState == AgentWorldState.Ready ? world : null);
                StartSession(s);
                Debug.Log("[AgentApi] 窓口を中の口だけで開いた（待ち受け無し・" + s.OpenWord + "＝"
                    + string.Join(",", s.Abilities.ToWireList()) + "・playId=" + s.PlayId + "）");
            }
            else if ((s.Abilities & needs) != needs)
            {
                why = "not-open";
                return null;
            }

            s.InProcessLinks++;
            return new AgentInProcessLink(s);
        }

        /// <summary>
        /// 中の口を 1 本閉じた（<see cref="AgentInProcessLink.Dispose"/> が呼ぶ）。
        /// ⭐ 待ち受けの無い窓口は、最後の中の口が閉じた時に閉じる。HTTP の窓口（Editor の印で開いた物）は閉じない（Play を抜ける時に閉じる）。
        /// ⚠ 主スレッドから呼ぶ。
        /// </summary>
        internal static void ReleaseInProcess(AgentHostSession s)
        {
            if (s == null)
            {
                return;
            }

            s.InProcessLinks = Math.Max(0, s.InProcessLinks - 1);
            if (s.InProcessLinks == 0 && s.Transport == null && ReferenceEquals(session, s))
            {
                Shutdown("in-process-closed");
            }
        }
    }
}
