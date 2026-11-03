using System;
using System.Collections;
using UnityEngine;

namespace Monster.Balloon.AutomataSystem
{
    /// <summary>
    /// Host の GameObject（DontDestroyOnLoad）に付く係。この係は、消える時に Host へ知らせる。撮る係の「フレームの終わり」も回す。
    /// <para>
    /// ⭐ Play を抜ける時、Unity はこの GameObject を消す。Host は、その知らせで窓口を閉じる
    /// （Editor の係が先に閉じていれば、Host は何もしない）。
    /// </para>
    /// </summary>
    [AddComponentMenu("")]
    internal sealed class AgentHostBehaviour : MonoBehaviour
    {
        private static readonly WaitForEndOfFrame EndOfFrame = new WaitForEndOfFrame();

        internal Action Destroyed;

        /// <summary>
        /// このフレームの終わり（描き終わった後）に 1 回だけ呼ぶ。
        /// ⭐ 画面を撮る口（ScreenCapture）は、コルーチンの WaitForEndOfFrame の後でしか正しく撮れない
        ///    （PlayerLoop の PostLateUpdate の中では、まだ描き終わっていない）。
        /// ⚠ 呼ぶ物は自分で例外を受け止めること（ここで落ちると、赤のログになる）。
        /// </summary>
        internal void AtEndOfFrame(Action action) => this.StartCoroutine(RunAtEndOfFrame(action));

        private static IEnumerator RunAtEndOfFrame(Action action)
        {
            yield return EndOfFrame;
            action();
        }

        private void OnDestroy()
        {
            Action destroyed = this.Destroyed;
            this.Destroyed = null;
            destroyed?.Invoke();
        }
    }
}
