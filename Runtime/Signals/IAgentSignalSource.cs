using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Monster.Balloon.AutomataSystem
{
    /// <summary>
    /// Where a signal source writes its signals.
    /// 合図の元が合図を書く口（Host の帳面）。
    /// </summary>
    public interface IAgentSignalSink
    {
        /// <summary>
        /// Writes one signal.
        /// 合図を 1 つ帳面に書く。
        /// <para>
        /// face が Surface なら表と裏の両方の帳面へ、Back なら裏の帳面だけへ書く。
        /// ⚠ 表の合図の中身には、画面に描かれている物だけを入れる（キー・ID・座標・本当の HP を入れない）。
        /// </para>
        /// </summary>
        void Write(AgentFace face, string kind, bool initial, JObject data = null);
    }

    /// <summary>
    /// The description of one kind of signal.
    /// 合図の名前と顔（hello で並べる）。
    /// </summary>
    public readonly struct AgentSignalKind
    {
        /// <summary>
        /// Creates a signal kind.
        /// 合図の種類を作る。
        /// </summary>
        /// <param name="observation">
        /// この合図が、世界で起きた事を見て書いた物か（既定 true）。
        /// false＝確かめの道具が自分の進み具合を書いた物（例: ゲームの確かめの橋の「行を出した」）。
        /// false の合図は、AI が出す証拠（<see cref="AgentHost.CheckEvidence"/>）に挙げられない。
        /// </param>
        public AgentSignalKind(string name, AgentFace face, bool observation = true)
        {
            this.Name = name;
            this.Face = face;
            this.Observation = observation;
        }

        /// <summary>
        /// The name of the kind.
        /// 種類の名前。
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// The face that can read this kind.
        /// この種類を読める顔。
        /// </summary>
        public AgentFace Face { get; }

        /// <summary>
        /// True when the signal was written by watching the world (false: a tool wrote it by itself).
        /// 世界で起きた事を見て書いた合図か（false＝道具が自分で書いた合図）。
        /// </summary>
        public bool Observation { get; }
    }

    /// <summary>
    /// A source of signals; a game adds one for each thing it wants to report.
    /// 合図の元（世界ごと。ゲームの側の葉が DI に結ぶ）。
    /// <para>
    /// ⭐ Host が元をつなぐのは「窓口が開いている・世界ができている・使う側が 60 秒以内に頼みを送った」間だけ。
    ///    それ以外の間、元は購読も見張りもしない（窓口を使わない間の重さを 0 にするため）。
    /// ⭐ 元は、つながれた時に今の姿を initial で 1 回書き、後は変わった時だけ書く。
    /// ⚠ どの口も主スレッドで呼ばれる。コンストラクタでは画面の物を探さない（世界の束ねを作る途中に呼ばれるため）。
    /// </para>
    /// </summary>
    public interface IAgentSignalSource
    {
        /// <summary>
        /// The kinds this source writes.
        /// この元が書く合図（hello で並べる）。
        /// </summary>
        IReadOnlyList<AgentSignalKind> Kinds { get; }

        /// <summary>
        /// The abilities needed to attach this source; any one of them is enough.
        /// この元をつなぐのに要る力（どれか 1 つが開いていればつなぐ）。
        /// ⭐ 例: 画面の字の元は Look。look の無い窓口では、画面の字を見張らない（重さも 0）。
        /// ⚠ 決まった値を返すこと。
        /// </summary>
        AgentAbilities Needs { get; }

        /// <summary>
        /// Called when the source is connected.
        /// 購読を始め、今の姿を initial で書く（nowMs は <see cref="Tick"/> と同じ時計のミリ秒）。
        /// </summary>
        void Attach(IAgentSignalSink sink, long nowMs);

        /// <summary>
        /// Called every frame while the source is connected.
        /// 毎フレーム呼ばれる（PostLateUpdate か、止まっている間の EditorApplication.update）。
        /// 重い見張りは、元が自分で間引く（目安は <see cref="AgentHost.SignalPollIntervalMs"/>）。
        /// </summary>
        void Tick(IAgentSignalSink sink, long nowMs);

        /// <summary>
        /// Called when the source is disconnected.
        /// 購読をやめる（何度呼んでもよい。持ち主がもう壊れていても落ちないこと）。
        /// </summary>
        void Detach();
    }
}
