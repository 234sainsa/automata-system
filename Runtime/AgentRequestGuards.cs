using System;
using System.Collections.Generic;

namespace Monster.Balloon.AutomataSystem
{
    /// <summary>
    /// A check that can refuse a request before any route runs.
    /// 頼みを通すか決める口（門を通った頼みを、さらにゲームの決まりで断る・例 Editor の持ち主のセッションだけを通す）。
    /// <para>
    /// ⭐ 窓口を開く時に 1 回、<see cref="AgentRequestGuards"/> に登録した作り手が、この窓口の係を作る（作らなければ全部通す）。
    /// ⚠ <see cref="TryRefuse"/> は窓口のスレッドで呼ばれる＝Unity の物に触らない。
    /// </para>
    /// </summary>
    public interface IAgentRequestGuard
    {
        /// <summary>
        /// Returns true and fills the refusal when the request must be refused.
        /// 断る時だけ true と断りを返す。通す時は false。
        /// </summary>
        /// <param name="headers">頼みの頭（名前の大文字と小文字は見ない）。</param>
        bool TryRefuse(AgentRequestHeaders headers, out AgentRefusal refusal);
    }

    /// <summary>
    /// Why a request was refused: a status, a code, and a hint.
    /// 頼みを断る答え（HTTP の番号と、断った訳の短い字・手がかり）。
    /// </summary>
    public readonly struct AgentRefusal
    {
        /// <summary>
        /// Creates a refusal.
        /// 断りを作る。
        /// </summary>
        public AgentRefusal(int status, string code, string hint)
        {
            if (status < 400 || status > 599)
            {
                throw new ArgumentOutOfRangeException(nameof(status), status, "断りの番号は 400〜599 にする。");
            }

            if (string.IsNullOrEmpty(code))
            {
                throw new ArgumentException("断りの字が空。", nameof(code));
            }

            this.Status = status;
            this.Code = code;
            this.Hint = hint;
        }

        /// <summary>
        /// The HTTP-style status code of the refusal.
        /// 断りの状態の数。
        /// </summary>
        public int Status { get; }

        /// <summary>
        /// The short code of the refusal.
        /// 断りの短い名前。
        /// </summary>
        public string Code { get; }

        /// <summary>
        /// A hint for the caller about what to do.
        /// 使う側へ、どうすればよいかの手がかり。
        /// </summary>
        public string Hint { get; }
    }

    /// <summary>
    /// The headers of a request, read by name.
    /// 頼みの頭を読むだけの口（窓口が読み解いた頭をそのまま見せる）。
    /// </summary>
    public readonly struct AgentRequestHeaders
    {
        private readonly IReadOnlyDictionary<string, string> map;

        /// <summary>
        /// Creates the headers from a dictionary.
        /// 辞書から頭の欄を作る。
        /// </summary>
        public AgentRequestHeaders(IReadOnlyDictionary<string, string> map)
        {
            this.map = map;
        }

        /// <summary>
        /// Returns the value of a header, or null.
        /// その名前の頭の値。無ければ null。
        /// </summary>
        public string Get(string name) =>
            this.map != null && name != null && this.map.TryGetValue(name, out string value) ? value : null;
    }

    /// <summary>
    /// The register of request guards that a game can add.
    /// 頼みを通すか決める口の作り手の登録簿（1 つだけ）。登録が無ければ、門を通った頼みは全部通す。
    /// <para>
    /// ⚠ Domain Reload を切っているので、Play の頭（SubsystemRegistration）で Host が空に戻す。
    ///    ゲームは、その後（AfterAssembliesLoaded か BeforeSceneLoad）に登録し直す。
    /// </para>
    /// </summary>
    public static class AgentRequestGuards
    {
        private static Func<AgentAbilities, IAgentRequestGuard> factory;

        /// <summary>
        /// Adds a function that makes a guard for an ability set.
        /// 作り手を登録する。窓口を開く時、主スレッドで開いた力の組を渡して呼ぶ。この窓口で見ない時は null を返す。
        /// ⚠ 違う作り手を 2 つ登録すると、例外で知らせる（同じ作り手を登録し直すのはよい）。
        /// </summary>
        public static void Register(Func<AgentAbilities, IAgentRequestGuard> forSession)
        {
            if (forSession == null)
            {
                throw new ArgumentNullException(nameof(forSession));
            }

            if (factory != null && !factory.Equals(forSession))
            {
                throw new InvalidOperationException("頼みを通すか決める口が、もう別に登録されている（1 つだけ）。");
            }

            factory = forSession;
        }

        /// <summary>主スレッドで呼ぶ。この窓口の係（登録が無い・見ない時は null）。</summary>
        internal static IAgentRequestGuard ForSession(AgentAbilities abilities) => factory?.Invoke(abilities);

        /// <summary>Play の頭で Host が呼ぶ。</summary>
        internal static void ResetForPlay() => factory = null;
    }
}
