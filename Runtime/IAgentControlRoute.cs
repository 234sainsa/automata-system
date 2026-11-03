namespace Monster.Balloon.AutomataSystem
{
    /// <summary>
    /// A route that does not read the world (hello, keep-alive, test tools).
    /// 世界を読まない係の道の印（hello・keep_alive・試験の橋）。
    /// <para>
    /// ⭐ 裏のトークンで来ても、「裏のトークンで来た頼み」の数（<see cref="AgentActivity.BackCalls"/>）に数えない
    ///    ＝試験の記録の playedWithBack（遊ぶ段で裏を読んだか）に混ざらない。
    /// ⚠ 世界の様子を返す道には付けない（付けると、遊ぶ段で裏を読んだことが記録から消える）。
    /// </para>
    /// </summary>
    public interface IAgentControlRoute : IAgentRoute
    {
    }
}
