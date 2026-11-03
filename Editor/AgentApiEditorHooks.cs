using UnityEditor;
using UnityEngine;

namespace Monster.Balloon.AutomataSystem.Editor
{
    /// <summary>
    /// Editor の中だけの Host の手伝い。
    /// <para>
    /// ① Editor が一時停止している間（Error Pause を含む）も、この係が列を回す（ゲームのループが止まるため）。
    /// ② この係が「止まっているか」を Host に教える（返事の paused）。
    /// ③ Play を抜ける時と、スクリプトを読み直す時に、この係が窓口を閉じる（口を開けたまま残さない）。
    /// ④ Editor が止まった・動き出した時に、この係が Host に知らせる（合図 paused・resumed）。
    /// ⑤ この係が、Editor を止める・動かす口を Host に渡す（止める反射と <c>/v1/pause</c>）。
    ///    ⚠ 止めるのは <c>EditorApplication.isPaused</c> だけ。<c>Time.timeScale</c> は書かない。
    /// ⑥ この係が、止まっているかの記録（<see cref="AgentStateFile"/>）を書く。止まった・動いた・Play の出入り・スクリプトの読み直し・窓口の知らせのたびに書く。
    ///    窓口が開いていない Play でも書く（撮影のフックが、止まった Editor を見落とさないように）。
    /// ⑦ AI か反射が止めたまま Play を抜ける時は、この係が一時停止を戻す（人や Error Pause の一時停止には触らない）。
    /// ⑧ この係が、Game ビューの大きさを替える口と戻す口を Host に渡す（<c>/v1/back/screen</c>）。AI が替えたまま Play を抜ける時は戻す。
    /// </para>
    /// </summary>
    [InitializeOnLoad]
    internal static class AgentApiEditorHooks
    {
        /// <summary>書けなかった記録を書き直すまでの間（秒）。</summary>
        private const double StateRetrySeconds = 0.1;

        private static readonly int ProcessId = System.Diagnostics.Process.GetCurrentProcess().Id;
        private static bool stateUnwritten;
        private static double stateRetryAt;

        static AgentApiEditorHooks()
        {
            AgentHost.SetPauseReader(ReadPause);
            AgentHost.SetPauseWriter(WritePause);
            AgentHost.SetStateWriter(WriteState);

            // ⑧ Game ビューの大きさを替える口と戻す口（/v1/back/screen）。Play を抜ける時は、AI が替えたままなら戻す。
            AgentHost.SetScreenSizer(AgentGameViewSizer.TrySet, AgentGameViewSizer.Restore);

            EditorApplication.update -= OnUpdate;
            EditorApplication.update += OnUpdate;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            EditorApplication.pauseStateChanged -= OnPauseChanged;
            EditorApplication.pauseStateChanged += OnPauseChanged;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;

            // ⑥ スクリプトを読み込んだ時も、今の姿を書く（Play の途中の読み直しで窓口が閉じても、止まっているかはフックに見える・Play の外なら残りを消す）。
            WriteState();
        }

        /// <summary>④ 止まった・動き出したを、この係が合図（paused・resumed）にして、記録も書き直す。</summary>
        private static void OnPauseChanged(PauseState state)
        {
            AgentHost.NotePause(state == PauseState.Paused);
            WriteState();
        }

        private static string ReadPause() => EditorApplication.isPaused ? "editor" : "none";

        /// <summary>⑤ Play の間だけ止める・動かす（Play の外では何もしない）。</summary>
        private static void WritePause(bool paused)
        {
            if (EditorApplication.isPlaying)
            {
                EditorApplication.isPaused = paused;
            }
        }

        private static void OnUpdate()
        {
            // ⑥ 書けなかった記録を、少し後で書き直す（古い「動いている」を残すと、フックが止まった Editor で撮影を通す）。
            if (stateUnwritten && EditorApplication.timeSinceStartup >= stateRetryAt)
            {
                WriteState();
            }

            // ① ゲームのループが回っている間は、ゲームの側（PostLateUpdate）が列を回す。ここは止まっている間だけ。
            if (EditorApplication.isPlaying && EditorApplication.isPaused)
            {
                AgentHost.PumpWhilePaused();
            }
        }

        private static void OnPlayModeChanged(PlayModeStateChange change)
        {
            switch (change)
            {
                case PlayModeStateChange.EnteredPlayMode:
                    AgentStateFile.ResetWarnings();
                    WriteState();
                    break;
                case PlayModeStateChange.ExitingPlayMode:
                    ReleaseAgentPause();
                    AgentGameViewSizer.RestoreOnPlayExit();
                    AgentHost.Shutdown("play-exit");
                    stateUnwritten = false;
                    AgentStateFile.TryDelete(AgentStateFile.EditorPath);
                    break;
                case PlayModeStateChange.EnteredEditMode:
                    WriteState();
                    break;
            }
        }

        private static void OnBeforeAssemblyReload() => AgentHost.Shutdown("assembly-reload");

        /// <summary>
        /// ⑦ AI か反射が止めたまま Play を抜けるなら、一時停止を戻す（Unity は一時停止の印を Play の外へ持ち越し、次の Play が止まったまま始まる）。
        /// 人や Error Pause の一時停止は、人の物なので触らない。
        /// </summary>
        private static void ReleaseAgentPause()
        {
            AgentReflex reflex = AgentHost.CurrentSession?.Reflex;
            if (EditorApplication.isPaused && reflex != null && reflex.PauseOwner != null)
            {
                EditorApplication.isPaused = false;
            }
        }

        /// <summary>⑥ 止まっているかの記録を、今の姿で書き直す（Play の外なら消す）。</summary>
        private static void WriteState()
        {
            string path = AgentStateFile.EditorPath;
            if (!EditorApplication.isPlaying)
            {
                stateUnwritten = false;
                AgentStateFile.TryDelete(path);
                return;
            }

            AgentHostSession session = AgentHost.CurrentSession;
            AgentReflex reflex = session?.Reflex;
            bool written = AgentStateFile.Write(
                path, ProcessId, true, AgentHost.ReadPause(), reflex != null && reflex.Stepping, session?.PlayId, reflex?.Latch, Time.frameCount);
            stateUnwritten = !written;
            if (!written)
            {
                stateRetryAt = EditorApplication.timeSinceStartup + StateRetrySeconds;
            }
        }
    }
}
