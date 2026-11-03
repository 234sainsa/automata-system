using System.Collections.Generic;

namespace Monster.Balloon.AutomataSystem.Samples.MinimalWorld
{
    /// <summary>
    /// The smallest world: one surface route (look) and nothing else.
    /// いちばん小さい世界。表の道（look）を 1 本だけ持つ。合図の元・反射の見張り・手足・撮る口は持たない（null か空）。
    /// <para>
    /// ⭐ この世界は、<see cref="MinimalAgentWorldBinder"/> が <see cref="AgentHost.AttachWorld"/> で窓口に渡す。
    /// ⭐ 手足（<see cref="IAgentHands"/>）が無いので、act と lease は 501 no_hands で断られる。撮る口が無いので、capture は 501 no_capture。
    /// </para>
    /// </summary>
    public sealed class MinimalAgentWorld : IAgentWorld
    {
        private readonly IAgentRoute[] routes = { new MinimalLookRoute() };

        /// <inheritdoc />
        public IReadOnlyList<IAgentRoute> Routes => this.routes;

        /// <inheritdoc />
        public IReadOnlyList<IAgentSignalSource> SignalSources { get; } = new IAgentSignalSource[0];

        /// <inheritdoc />
        public string PhaseName => "running";

        /// <inheritdoc />
        public IAgentReflexWatch ReflexWatch => null;

        /// <inheritdoc />
        public IAgentHands Hands => null;

        /// <inheritdoc />
        public IAgentCaptureEyes CaptureEyes => null;
    }
}
