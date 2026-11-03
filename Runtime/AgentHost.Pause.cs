using System;

namespace Monster.Balloon.AutomataSystem
{
    /// <summary>
    /// The part of AgentHost that pauses and resumes the Editor.
    /// Host の、Editor の一時停止にかかわる口（Editor の係が差す読み口・書き口と、止まっている間の列回し）。
    /// <para>
    /// ⭐ 読み口と書き口は Editor の係（<c>AgentApiEditorHooks</c>）が、スクリプトを読み込んだ時に 1 回だけ差す。
    ///    ビルドでは差さない＝いつも "none" で、止められない（止める反射は掛け金と合図だけになる）。
    /// ⭐ 止めた・動かしたを頼んだ者（反射・AI）は、止める反射の係（<see cref="AgentReflex"/>）が覚えて、合図と返事の by に書く。
    /// ⭐ 止まっているかの記録（<c>Library/AgentApi/state.json</c>）を書くのも Editor の係（窓口から Editor の係へ移した）。
    ///    窓口は、掛け金・何コマか進める・持ち主が替わった時に <see cref="NoteStateChanged"/> で知らせるだけ。
    /// </para>
    /// </summary>
    public static partial class AgentHost
    {
        private static Func<string> pauseReader;
        private static Action<bool> pauseWriter;
        private static Action stateWriter;

        /// <summary>Editor を止めたり動かしたりできるか（Editor の係が書き口を差した時だけ）。</summary>
        internal static bool CanWritePause => pauseWriter != null;

        /// <summary>Editor が今止まっているか（だれが止めたかは問わない）。</summary>
        internal static bool IsEditorPaused => ReadRawPause() == AgentReflex.ByEditor;

        /// <summary>止まっているかを読む口を差す（Editor の係が差す。ビルドでは差さない＝いつも "none"）。</summary>
        internal static void SetPauseReader(Func<string> reader) => pauseReader = reader;

        /// <summary>Editor を止める・動かす口を差す（Editor の係が差す。ビルドでは差さない）。</summary>
        internal static void SetPauseWriter(Action<bool> writer) => pauseWriter = writer;

        /// <summary>止まっているかの記録を書き直す口を差す（Editor の係が差す。ビルドでは差さない＝記録は書かない）。</summary>
        internal static void SetStateWriter(Action writer) => stateWriter = writer;

        /// <summary>
        /// 止まっているかの記録に載る物（掛け金・何コマか進める・窓口の開け閉め）が変わった。Editor の係が記録を書き直す。
        /// ⚠ 止まった・動いたは、Editor の係が自分で見て書く（ここで知らせなくてよい）。
        /// </summary>
        internal static void NoteStateChanged()
        {
            Action writer = stateWriter;
            if (writer == null)
            {
                return;
            }

            try
            {
                writer();
            }
            catch (Exception e)
            {
                ReportOnce("state-write", "[AgentApi] 止まっているかの記録を書き直せなかった: " + e);
            }
        }

        /// <summary>Editor を止める・動かす（書き口が無ければ何もしない）。⚠ <c>Time.timeScale</c> は書かない。</summary>
        internal static void WriteEditorPause(bool paused)
        {
            Action<bool> writer = pauseWriter;
            if (writer == null)
            {
                return;
            }

            try
            {
                writer(paused);
            }
            catch (Exception e)
            {
                ReportOnce("pause-write|" + paused, "[AgentApi] Editor を" + (paused ? "止め" : "動かせ") + "られなかった: " + e);
            }
        }

        /// <summary>Editor が止まった・動き出した（Editor の係が知らせる）。合図 paused・resumed を、頼んだ者の名で書く。</summary>
        internal static void NotePause(bool paused) => session?.Reflex.OnPauseChanged(paused);

        /// <summary>
        /// Editor が一時停止している間、列を回す（ゲームのループが止まっているため）。
        /// ⭐ これで、Error Pause や止める反射で止まっている間も、使う側は答えをもらえる。
        /// </summary>
        internal static void PumpWhilePaused() => session?.Pump();

        /// <summary>
        /// 止まっているか（"none"・"editor"・"reflex"・"agent"）。返事の paused に書く。
        /// ⭐ 反射か AI が頼んで止めた時は、その名を返す（人や Error Pause が止めた時は "editor"）。
        /// </summary>
        internal static string ReadPause()
        {
            string raw = ReadRawPause();
            string owner = raw == AgentReflex.ByEditor ? session?.Reflex.PauseOwnerOrPending : null;
            return owner ?? raw;
        }

        /// <summary>Editor の係が差した読み口の答え（"none"・"editor"・読めなければ "unknown"）。</summary>
        private static string ReadRawPause()
        {
            Func<string> reader = pauseReader;
            if (reader == null)
            {
                return "none";
            }

            try
            {
                return reader() ?? "none";
            }
            catch (Exception)
            {
                return "unknown";
            }
        }
    }
}
