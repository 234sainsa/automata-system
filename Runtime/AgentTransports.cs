using System;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Monster.Balloon.AutomataSystem
{
    /// <summary>
    /// 窓口のスレッドが Host に聞く口。⚠ 窓口のスレッドは Unity の物に触らないので、Host はここだけを渡す。
    /// </summary>
    internal interface IAgentFrontDoor
    {
        /// <summary>本文の上限（バイト）。</summary>
        int MaxBodyBytes { get; }

        /// <summary>主スレッドの答えを待つ長さ（ふつうの道）。</summary>
        TimeSpan MainThreadTimeout { get; }

        /// <summary>門。頼みを通すか断るかを決める。</summary>
        AgentGateVerdict Check(in AgentGateRequest request);

        /// <summary>
        /// 門を通った頼みを主スレッドの列に積み、答えを待つ。答えは主スレッドが入れる。
        /// 待つ長さ（ふつうの道は <see cref="MainThreadTimeout"/>・長い待ちの道 wait はそれに頼まれた待ちを足す）と、
        /// 待ちきれない時の答え（503 main_thread_busy＝走っていない・504 answer_late＝走った）は Host が決める。
        /// </summary>
        Task<AgentReply> AnswerAsync(string path, AgentFace face, JObject body);
    }

    /// <summary>
    /// 窓口（待ち受け）1 つ。今は自前のソケットで小さな HTTP を話す物だけ（Http の AgentSocketTransport）。
    /// ⚠ HttpListener は使わない＝Windows で待ち受けの口が子プロセスへ受け継がれ、窓口を閉じても口が残った。
    /// </summary>
    internal interface IAgentTransport : IDisposable
    {
        /// <summary>待っている口の番号。</summary>
        int Port { get; }

        /// <summary>
        /// 待ち受けを始める。firstPort から portCount 個を順に試し、最初に開けた口で待つ。
        /// </summary>
        bool TryStart(IAgentFrontDoor door, int firstPort, int portCount, out string why);
    }

    /// <summary>
    /// 窓口を作る口の置き場。芯は窓口の型を知らない＝窓口のアセンブリが読み込まれた時に、自分を作る口をここへ置く。
    /// ⭐ 窓口のアセンブリを外したビルドでは、何も置かれない＝Host は待ち受けを作らない。
    /// </summary>
    internal static class AgentTransports
    {
        private static Func<IAgentTransport> factory;

        public static bool IsRegistered => factory != null;

        public static void Register(Func<IAgentTransport> create)
        {
            factory = create ?? throw new ArgumentNullException(nameof(create));
        }

        public static IAgentTransport TryCreate() => factory?.Invoke();
    }
}
