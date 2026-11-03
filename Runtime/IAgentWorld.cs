using System.Collections.Generic;

namespace Monster.Balloon.AutomataSystem
{
    /// <summary>
    /// A world that the endpoint serves; the game side implements it.
    /// 世界（DI の根）ごとの係が Host に渡す物。世界ができた時に、Host はこの係の道を並べる。
    /// </summary>
    public interface IAgentWorld
    {
        /// <summary>
        /// The routes this world adds.
        /// この世界で足す道（表と裏）。
        /// </summary>
        IReadOnlyList<IAgentRoute> Routes { get; }

        /// <summary>
        /// The signal sources of this world.
        /// この世界の合図の元（表と裏）。Host は、使う側が来ている間だけ、これらをつなぐ。
        /// </summary>
        IReadOnlyList<IAgentSignalSource> SignalSources { get; }

        /// <summary>
        /// The name of the current phase of the world.
        /// 世界の今の段の名前（例 "InitialSetup"）。hello に出す。
        /// </summary>
        string PhaseName { get; }

        /// <summary>
        /// The watch for stop guards and moving reflexes, or null.
        /// この世界の止める守りの見張り。無ければ null（止める反射は、芯が見る赤いログだけになる）。
        /// ⭐ Host は、AI が腕を掛けている間だけ、これを回す。
        /// </summary>
        IAgentReflexWatch ReflexWatch { get; }

        /// <summary>
        /// The hands of this world, or null.
        /// この世界の手足（仮のパッドと命令）。無ければ null（lease と act は 501 no_hands で断る）。
        /// ⭐ Host は、借りの間だけ、これにパッドを足させる。世界を畳むと借りは切れる。
        /// </summary>
        IAgentHands Hands { get; }

        /// <summary>
        /// The capture mouth of this world, or null.
        /// この世界の撮る口（撮ったフレームの中で、同じフレームの表の look を書く）。無ければ null（capture は 501 no_capture で断る）。
        /// </summary>
        IAgentCaptureEyes CaptureEyes { get; }
    }

    /// <summary>
    /// How ready the world is.
    /// Host から見た世界の様子。
    /// </summary>
    public enum AgentWorldState
    {
        /// <summary>世界の係が居ない（起動の途中・畳んだ後）。</summary>
        None = 0,

        /// <summary>世界の係は居るが、世界ができていない（タイトル・読み込み中）。</summary>
        Loading = 1,

        /// <summary>世界ができた。道が全部並んでいる。</summary>
        Ready = 2,

        /// <summary>世界を畳んでいる途中。</summary>
        Closing = 3,
    }

    /// <summary>
    /// Helpers for AgentWorldState.
    /// AgentWorldState に使う補助の口。
    /// </summary>
    public static class AgentWorldStateExtensions
    {
        /// <summary>
        /// Returns the word used on the wire for the state.
        /// 窓口の JSON に書く字を返す。
        /// </summary>
        public static string ToWire(this AgentWorldState state)
        {
            switch (state)
            {
                case AgentWorldState.Loading: return "loading";
                case AgentWorldState.Ready: return "ready";
                case AgentWorldState.Closing: return "closing";
                default: return "none";
            }
        }
    }
}
