using System;
using System.Collections.Generic;

namespace Monster.Balloon.AutomataSystem
{
    /// <summary>
    /// Flags for the sets of abilities an endpoint can open.
    /// 窓口で開く「できる事の組」。何も無い＝閉じている（Host は待ち受けを作らない）。
    /// <para>
    /// ⭐ 道は、自分に要る力を <see cref="IAgentRoute.Needs"/> で名乗る。Host は、開いた力に合う道だけを並べる。
    /// ⭐ 土台が名を付ける力は Look・Self・Back の 3 つだけ。ゲームは、空いている位（1 &lt;&lt; 2・1 &lt;&lt; 4 から上）に自分の力を置き、
    ///    <see cref="AgentAbilityCatalog.Register"/> で書き付け（<see cref="AgentAbilityDef"/>）を登録する。
    /// ⭐ 開く印の語と、力の名前の読み書きは <see cref="AgentAbilityCatalog"/> が持つ（<see cref="AgentOpenFlag"/> が読む）。
    /// </para>
    /// </summary>
    [Flags]
    public enum AgentAbilities
    {
        /// <summary>閉じている。</summary>
        None = 0,

        /// <summary>A の表の目・全部の表の合図（events・wait）。</summary>
        Look = 1 << 0,

        /// <summary>A の手（lease・act・反射・撮る）。</summary>
        Self = 1 << 1,

        /// <summary>裏のトークンと裏の道（Editor と開発ビルドだけ。製品版では、開いても外す）。</summary>
        Back = 1 << 3,
    }

    /// <summary>
    /// Helpers for the ability flags.
    /// 力の旗に使う補助の口。
    /// </summary>
    public static class AgentAbilitiesExtensions
    {
        /// <summary>
        /// Returns every ability that is registered right now.
        /// 全部の力（印 operate）＝土台の 3 つと、ゲームが登録した力の全部。
        /// ⚠ 決まった値ではない。ゲームが力を登録した後に読む（<see cref="AgentAbilityCatalog.All"/>）。
        /// </summary>
        public static AgentAbilities All => AgentAbilityCatalog.Current.All;

        /// <summary>
        /// Returns true when the set opens at least one of the abilities a route needs.
        /// この組で、<paramref name="needs"/> の道を使えるか。needs のどれか 1 つがあれば使える。needs が None なら、いつも使える（hello）。
        /// </summary>
        public static bool Allows(this AgentAbilities set, AgentAbilities needs) =>
            needs == AgentAbilities.None || (set & needs) != AgentAbilities.None;

        /// <summary>
        /// Turns the flags into the list of words that is sent over the wire.
        /// 窓口の JSON に書く字の並び（名前の順・例 ["back","look","self"]）。登録の無い位は書かない。
        /// </summary>
        public static List<string> ToWireList(this AgentAbilities set) => AgentAbilityCatalog.Current.ToWireList(set);
    }
}
