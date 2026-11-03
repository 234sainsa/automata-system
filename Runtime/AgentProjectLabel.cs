using System;

namespace Monster.Balloon.AutomataSystem
{
    /// <summary>
    /// The short name of this Editor that hello shows as the project.
    /// hello の <c>project</c> に出す、この Editor の短い名乗りの口（例 1 台の PC で Editor を 2 つ開く時の見分け）。
    /// <para>
    /// ⭐ 名乗りの中身はゲームが登録する。登録が無い・null を返す時、hello は <c>project</c> を出さない（ビルドも出さない形にする）。
    /// ⚠ フォルダの道そのものは出さない（Windows の名前が入るため）。短い語だけを返す。
    /// ⚠ Domain Reload を切っているので、Play の頭（SubsystemRegistration）で Host が空に戻す。
    ///    ゲームは、その後（AfterAssembliesLoaded か BeforeSceneLoad）に登録し直す。
    /// </para>
    /// </summary>
    public static class AgentProjectLabel
    {
        private static Func<string> reader;

        /// <summary>
        /// Sets the function that reads the project name.
        /// 名乗りを読む口を登録する（hello のたびに主スレッドで呼ぶ）。
        /// ⚠ 違う口を 2 つ登録すると、例外で知らせる（同じ口を登録し直すのはよい）。
        /// </summary>
        public static void Register(Func<string> read)
        {
            if (read == null)
            {
                throw new ArgumentNullException(nameof(read));
            }

            if (reader != null && !reader.Equals(read))
            {
                throw new InvalidOperationException("Editor の名乗りの口が、もう別に登録されている（1 つだけ）。");
            }

            reader = read;
        }

        /// <summary>今の名乗り（登録が無ければ null＝hello に出さない）。</summary>
        internal static string Read() => reader?.Invoke();

        /// <summary>Play の頭で Host が呼ぶ。</summary>
        internal static void ResetForPlay() => reader = null;
    }
}
